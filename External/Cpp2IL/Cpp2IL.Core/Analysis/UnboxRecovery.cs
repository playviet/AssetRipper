using System.Collections.Generic;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// A cast whose answer is read through is not a cast, it is an unboxing.
/// </summary>
/// <remarks>
/// <para>
/// The runtime helper behind <c>(Colour)p</c> on a boxed value checks the class and then hands back a pointer
/// to the value inside the box, so the caller reads it at offset zero.
/// <see cref="CastHelperRecovery"/> names that helper by the shape of its call, and the shape is the same one
/// a reference cast has - so it comes out as <c>isinst</c>, whose result is an object, and the read through it
/// has nothing it can be written as:
/// </para>
/// <code>
/// //AssetRipper: commented out, this could not be kept as code.
/// //return ((obj2 is Colour) ? obj2 : null) + num;
/// </code>
/// <para>
/// What tells the two apart is not the call but what is done with the answer. **Nobody dereferences the result
/// of a reference cast** - it is an object, and its fields are named. A result that is read at offset zero, for
/// a type that is a value type, is a box being opened. So the read is the whole of it: the call becomes the
/// unboxing, its result becomes the value, and the reads through it become the value itself.
/// </para>
/// <para>
/// Only when **every** use is such a read. One use that is not means the answer is being treated as an object
/// somewhere, and then this is a cast after all and nothing is changed.
/// </para>
/// </remarks>
public static class UnboxRecovery
{
    private const string ObjectIsInstance = "il2cpp_vm_object_is_inst";

    /// <summary>The generator writes this as <c>unbox.any</c>; see IlGenerator.Fork.</summary>
    public const string ObjectUnbox = "il2cpp_vm_object_unbox";

    public static bool Run(MethodAnalysisContext method)
    {
        if (method.ControlFlowGraph is not { } graph)
            return false;

        var changed = false;

        foreach (var call in graph.Instructions)
        {
            if (call.OpCode != OpCode.Call
                || call.Operands.Count < 4
                || call.Operands[0] as string != ObjectIsInstance
                || call.Operands[1] is not LocalVariable result
                || call.Operands[3] is not TypeAnalysisContext { IsValueType: true } value)
                continue;

            var reads = new List<(Instruction Instruction, int Operand)>();

            if (!EveryUseIsARead(graph, call, result, reads) || reads.Count == 0)
                continue;

            call.Operands[0] = ObjectUnbox;
            result.Type = value;

            foreach (var (instruction, operand) in reads)
                instruction.Operands[operand] = result;

            changed = true;
        }

        return changed;
    }

    /// <summary>
    /// Whether the local is only ever read through at offset zero, collecting where. False the moment it is
    /// used as anything else.
    /// </summary>
    private static bool EveryUseIsARead(Graphs.ISILControlFlowGraph graph, Instruction call, LocalVariable result,
        List<(Instruction, int)> reads)
    {
        foreach (var instruction in graph.Instructions)
        {
            if (ReferenceEquals(instruction, call))
                continue;

            for (var operand = 0; operand < instruction.Operands.Count; operand++)
            {
                switch (instruction.Operands[operand])
                {
                    case MemoryOperand { Index: null, Scale: 0, Addend: 0, Base: LocalVariable through }
                        when ReferenceEquals(through, result):
                        reads.Add((instruction, operand));
                        break;

                    //Any other mention - passed on, compared, stored - means it is being used as the object a
                    //cast gives back.
                    case LocalVariable local when ReferenceEquals(local, result):
                        return false;
                    case MemoryOperand memory
                        when ReferenceEquals(memory.Base, result) || ReferenceEquals(memory.Index, result):
                        return false;
                    case FieldReference field when ReferenceEquals(field.Local, result):
                        return false;
                }
            }
        }

        return true;
    }
}
