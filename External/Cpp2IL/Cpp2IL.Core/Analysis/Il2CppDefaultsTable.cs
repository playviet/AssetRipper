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

        return TypeHandles(method) || changed;
    }

    /// <summary>
    /// The type a class pointer stands for, which is the class plus the offset of its <c>byval_arg</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>typeof(int)</c> has no metadata usage of its own where the type is a built-in one. The compiler
    /// reads the class out of the table this file is about and then takes the address of its
    /// <c>byval_arg</c>, which is the <c>Il2CppType*</c> that <c>Type.GetTypeFromHandle</c> is handed:
    /// </para>
    /// <code>
    /// Move v56, [4DCD108]                              // il2cpp_defaults
    /// Move v59 (Il2CppClass&lt;bool&gt;), [v56 + 0x28]  // the class, which `Run` above names
    /// Add  v69 (RuntimeTypeHandle), v59, 32            // and its byval_arg
    /// Call Type.GetTypeFromHandle, v72, v69
    /// </code>
    /// <para>
    /// so the handle is an <em>addition</em> rather than a load, and <c>IlGenerator</c>'s
    /// <c>TryLoadTypeToken</c> emits <c>ldtoken</c> only for an operand that <b>is</b> a type - a local
    /// merely typed <c>System.RuntimeTypeHandle</c> is not one. The statement came out as a cast from an
    /// integer to a handle and went, and took the assignment it was part of with it. In
    /// <c>TypeExtensions</c>'s one static constructor the thirteen <c>typeof(Vector2)</c>…
    /// <c>typeof(GUIStyle)</c> recover, because those have a metadata usage, and the thirteen
    /// <c>typeof(bool)</c>…<c>typeof(string)</c> do not. This addition is the whole difference.
    /// </para>
    /// <para>
    /// The rule is <b>not</b> restricted to the defaults table, although that is where it was found: a
    /// class pointer plus <c>byval_arg</c> is that class's type wherever the pointer came from, and the
    /// class has already been named by the time this runs - <c>Run</c> above, <see cref="RgctxResolver"/>
    /// and the metadata resolver all put a <c>RuntimeClassTypeAnalysisContext</c> on the local. Asking only
    /// which class it is, rather than where it was read from, is both simpler and wider.
    /// </para>
    /// <para>
    /// Monotone: afterwards the instruction is a <c>Move</c>, so it cannot fire twice, and the fixpoint
    /// still settles.
    /// </para>
    /// </remarks>
    private static bool TypeHandles(MethodAnalysisContext method)
    {
        var changed = false;

        foreach (var instruction in method.ControlFlowGraph!.Instructions)
        {
            if (instruction is not { OpCode: OpCode.Add, Operands: [LocalVariable handle, LocalVariable klass, { } addend] }
                || Number(addend) != Il2CppClassLayout.ByValArg)
            {
                continue;
            }

            //Only where the destination is the handle itself. The same addition with anything else on the
            //left is address arithmetic on a class, which is a different question with its own passes.
            if (handle.Type?.FullName != "System.RuntimeTypeHandle")
                continue;

            if (klass.Type is not RuntimeClassTypeAnalysisContext { RepresentedType: { } named })
                continue;

            instruction.OpCode = OpCode.Move;
            instruction.Operands = [handle, named];
            changed = true;
        }

        return changed;
    }

    /// <summary>The constant an operand spells, whichever width the lifter recorded it at.</summary>
    private static long? Number(object operand) => operand switch
    {
        long value => value,
        ulong value => (long)value,
        int value => value,
        uint value => value,
        _ => null,
    };

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
