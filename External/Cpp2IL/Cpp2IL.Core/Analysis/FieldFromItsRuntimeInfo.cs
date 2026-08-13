using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// A field reached through the runtime's own <c>FieldInfo</c> is still that field.
/// </summary>
/// <remarks>
/// <para>
/// Where a body is shared between instantiations il2cpp cannot inline a field's offset, so it looks the field
/// up at run time: read <c>Il2CppClass&lt;X&gt;::fields</c>, step to the one it wants - each <c>FieldInfo</c>
/// being thirty-two bytes - and hand the object and that entry to a helper:
/// </para>
/// <code>
/// ldr   x9,  [x1, #16]     // FieldInfo->parent
/// ldrsw x10, [x1, #24]     // FieldInfo->offset
/// ldr   w9,  [x9, #40]     // the parent's byval_arg type bits
/// add   x9,  x0,  x10      // object + offset
/// csel  x8,  #-16, xzr, lt // less the header, where the parent is a value type
/// add   x0,  x9,  x8
/// </code>
/// <para>
/// which is <c>&amp;object.field</c> and nothing else. The class is already named - the analysis has resolved
/// the runtime generic context by the time this runs - and the step is a constant, so which field it is is
/// stated outright. Left alone the whole thing is a read of unmanaged memory, an addition nothing can name and
/// a call to an address, and the statement that wanted the field goes: fourteen sites, the whole of
/// <c>JsonExtension</c>'s iterator family.
/// </para>
/// <para>
/// Recognised by the <em>chain</em> rather than by the helper's address, so nothing here is pinned to this
/// binary. A call handed an object and a <c>klass-&gt;fields</c> entry is that helper; there is nothing else
/// with both.
/// </para>
/// </remarks>
public static class FieldFromItsRuntimeInfo
{
    /// <summary>What one <c>FieldInfo</c> takes up, which is what a step between two of them counts in.</summary>
    private const long FieldInfoWidth = 0x20;

    public static void Run(MethodAnalysisContext method)
    {
        if (method.ControlFlowGraph is not { } graph)
            return;

        var definitions = new Dictionary<LocalVariable, Instruction>();
        var assigned = new HashSet<LocalVariable>();

        foreach (var instruction in graph.Instructions)
        {
            if (instruction.Destination is not LocalVariable destination)
                continue;

            if (!assigned.Add(destination))
                definitions.Remove(destination);
            else
                definitions[destination] = instruction;
        }

        foreach (var call in graph.Instructions)
        {
            //Only a call nothing has resolved: one that names a method says what it does already.
            if (call.OpCode is not (OpCode.Call or OpCode.CallVoid) || call.Operands.Count < 2
                || call.Operands[0] is not ulong)
            {
                continue;
            }

            var first = call.OpCode == OpCode.Call ? 2 : 1;

            if (first != 2 || call.Operands.Count < first + 2
                || call.Operands[1] is not LocalVariable answer
                || call.Operands[first] is not LocalVariable held)
            {
                continue;
            }

            //Nought is not an offset here, it is the absence of one: a generic type records none, which is
            //the whole reason the compiler looked the field up at run time. Emitting `object + 0` would put a
            //confident wrong answer where a marker was. Where `StateMachineFieldLayout` has worked the layout
            //out and it agreed with the code, the offset is real and this fires.
            if (Named(call.Operands[first + 1], definitions) is not { } field || field.Offset <= 0)
                continue;

            call.OpCode = OpCode.Add;
            call.Operands = [answer, held, (long)field.Offset];
        }
    }

    /// <summary>The field a <c>FieldInfo</c> is, where it was stepped to from a class's own list.</summary>
    private static FieldAnalysisContext? Named(object operand, Dictionary<LocalVariable, Instruction> definitions)
    {
        if (operand is not LocalVariable info || !definitions.TryGetValue(info, out var made))
            return null;

        long step = 0;

        if (made is { OpCode: OpCode.Add, Operands: [_, { } stepped, { } by] })
        {
            if (Constant(by) is not { } away || away < 0 || away % FieldInfoWidth != 0)
                return null;

            step = away;

            //The list is either still a local of its own, or - where copy propagation folded the load into
            //the addition, which is the shape it usually leaves - the read itself. Only the first was matched,
            //and the whole of `JsonExtension`'s iterator family is the second.
            if (stepped is MemoryOperand folded)
                made = new Instruction(made.Index, OpCode.Move, made.Operands[0], folded);
            else if (stepped is not LocalVariable from || !definitions.TryGetValue(from, out made))
                return null;
        }

        if (made is not { OpCode: OpCode.Move, Operands: [_, MemoryOperand { Index: null, Scale: 0, Base: LocalVariable klass } read] }
            || read.Addend != Il2CppClassLayout.Fields
            || klass.Type is not RuntimeClassTypeAnalysisContext { RepresentedType: { } named })
        {
            return null;
        }

        var declaring = named is GenericInstanceTypeAnalysisContext instance ? instance.GenericType : named;
        var index = (int)(step / FieldInfoWidth);

        //The list is the type's own fields in metadata order, which is the order the runtime lays them out in.
        return index >= 0 && index < declaring.Fields.Count ? declaring.Fields[index] : null;
    }

    private static long? Constant(object operand)
        => operand switch
        {
            int i => i,
            uint u => u,
            long l => l,
            ulong ul => (long)ul,
            _ => null,
        };
}
