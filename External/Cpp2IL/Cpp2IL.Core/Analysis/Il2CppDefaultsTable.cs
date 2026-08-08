using System.Collections.Generic;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Names the classes a method reaches through the runtime's table of common types.
/// </summary>
/// <remarks>
/// The classes of the built-in types are not looked up where they are needed. The runtime keeps one table of
/// them, filled in when the process starts, and generated code reads a slot out of it - so a method that puts
/// an <c>int</c> into a string reads a global, then a fixed offset, and hands what it finds to the runtime.
/// The table lives in zeroed memory and its address is decided when the binary is laid out, so neither the
/// address nor the contents are in the file: only the offsets are, and an offset alone says nothing.
///
/// What says something is the one slot that has a static field worth reading. <c>string.Empty</c> is the only
/// member of any built-in type that generated code reaches through its class, so a slot whose value is then
/// read at the offset where static values are kept **is** <c>System.String</c> - and that fixes where the
/// table starts, because the layout after that is the order the runtime declares it in. Every other slot
/// follows from the one that was proved, rather than from how often it is used.
///
/// Five slots were checked against the source the game was built from and all five agree: bytes from a colour
/// written as hex, a boolean from an interpolated flag, integers from a pacing check, longs from a stopwatch,
/// and the string that pinned it.
///
/// The list stops at <c>string</c>, and no longer because the layout after it is uncertain - it is not.
/// <c>Il2CppDefaults</c> in the header Unity ships with 2022.3, the editor this game was built with, is byte
/// for byte the same declaration as the one in 6000, so the order after <c>string</c> is known: enum, array,
/// delegate, multicast delegate, the three handles, <c>Type</c>, exception, thread, the reflection types,
/// <c>StringBuilder</c>. It stops there because naming them was **built and measured byte-identical on every
/// scorer** at 1.0.566. This pass only types a local a slot is read *into*, and every slot past
/// <c>string</c> is read straight inside a comparison instead - which is what
/// <see cref="ExactTypeTestRecovery"/> answers for, and it refuses a class anything can derive from, which
/// all of those are. Adding them back needs a use for them first.
/// </remarks>
public static class Il2CppDefaultsTable
{
    /// <summary>
    /// The locals holding the table. Anything still loaded from a bare address is a global the metadata did
    /// not account for, which is what this table is - the resolved ones had their operand replaced with what
    /// they name long ago.
    /// </summary>
    public static HashSet<LocalVariable> Tables(Graphs.ISILControlFlowGraph graph)
    {
        var globals = new HashSet<LocalVariable>();

        foreach (var instruction in graph.Instructions)
            if (instruction is { OpCode: OpCode.Move, Operands.Count: 2 }
                && instruction.Operands[0] is LocalVariable global
                && instruction.Operands[1] is MemoryOperand { Base: null, Index: null, Scale: 0, Addend: not 0 })
                globals.Add(global);

        return globals;
    }

    public static bool Run(MethodAnalysisContext method)
    {
        var globals = Tables(method.ControlFlowGraph!);

        if (globals.Count == 0)
            return false;

        var changed = false;

        foreach (var instruction in method.ControlFlowGraph.Instructions)
        {
            if (instruction is not { OpCode: OpCode.Move, Operands.Count: 2 }
                || instruction.Operands[0] is not LocalVariable { Type: null } slot
                || instruction.Operands[1] is not MemoryOperand { Index: null, Scale: 0, Base: LocalVariable table } read
                || !globals.Contains(table))
                continue;

            if (SlotType(read.Addend, method.AppContext) is not { } named)
                continue;

            slot.Type = new RuntimeClassTypeAnalysisContext(named, named.DeclaringAssembly);
            changed = true;
        }

        return changed;
    }

    /// <summary>The built-in type a slot of the table holds the class of, if this is a slot at all.</summary>
    public static TypeAnalysisContext? SlotType(long addend, ApplicationAnalysisContext appContext)
    {
        var pointerSize = appContext.Binary.is32Bit ? 4 : 8;

        //One pointer stands in front of the first class on this build, which is what the string slot proved.
        if (addend < pointerSize || addend % pointerSize != 0)
            return null;

        var index = addend / pointerSize - 1;
        var types = appContext.SystemTypes;

        return index switch
        {
            1 => types.SystemObjectType,
            2 => types.SystemByteType,
            3 => types.SystemVoidType,
            4 => types.SystemBooleanType,
            5 => types.SystemSByteType,
            6 => types.SystemInt16Type,
            7 => types.SystemUInt16Type,
            8 => types.SystemInt32Type,
            9 => types.SystemUInt32Type,
            10 => types.SystemIntPtrType,
            11 => types.SystemUIntPtrType,
            12 => types.SystemInt64Type,
            13 => types.SystemUInt64Type,
            14 => types.SystemSingleType,
            15 => types.SystemDoubleType,
            16 => types.SystemCharType,
            17 => types.SystemStringType,
            _ => null,
        };
    }
}
