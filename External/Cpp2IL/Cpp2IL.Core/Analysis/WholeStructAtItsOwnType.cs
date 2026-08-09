using System.Linq;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// A struct's first field, put where that whole struct is declared, is the struct.
/// </summary>
/// <remarks>
/// <para>
/// The inverse of <see cref="StructAtADeclaredNumber"/>, and needed for the same reason: the declaration is
/// the only thing that says which of the two a register holds. Once a call's answer is named one field to a
/// vector register - see <c>VectorReturnFields</c> - the first of those fields is what reaches a place that
/// wants the whole thing, and <c>_originPos = _camTransform.position</c> comes out as
/// <c>_originPos = (Vector3)position.x</c>, which does not compile. That cost 15 whole bodies, against 13
/// wrong values the naming removed.
/// </para>
/// <para>
/// Only the field at the front, and only where the place it is going is declared as the very type it came
/// out of. Both together mean the copy is the whole struct being handed on unchanged - which is what the
/// registers beside it are doing, and what nothing else here can see.
/// </para>
/// </remarks>
public static class WholeStructAtItsOwnType
{
    public static void Run(MethodAnalysisContext method)
    {
        if (method.ControlFlowGraph is not { } graph)
            return;

        foreach (var instruction in graph.Instructions)
        {
            if (instruction.OpCode == OpCode.Move && instruction.Operands.Count == 2)
            {
                if (Declared(instruction.Operands[0]) is { } destination)
                    Restore(instruction, 1, destination);

                continue;
            }

            if (!instruction.IsCall || instruction.Operands.Count == 0
                || instruction.Operands[0] is not MethodAnalysisContext callee)
                continue;

            //The same walk the lifter made when it laid the arguments out, so the two cannot disagree about
            //which operand is which parameter.
            var first = instruction.OpCode == OpCode.Call
                ? (callee.IsStatic ? 2 : 3)
                : (callee.IsStatic ? 1 : 2);

            for (var i = 0; i < callee.Parameters.Count && first + i < instruction.Operands.Count; i++)
                Restore(instruction, first + i, callee.Parameters[i].ParameterType);
        }
    }

    /// <summary>The type a place is declared to hold, where it says so.</summary>
    private static TypeAnalysisContext? Declared(object operand) => operand switch
    {
        LocalVariable { Type: { } type } => type,
        FieldReference field => field.Field.FieldType,
        _ => null,
    };

    private static void Restore(Instruction instruction, int operand, TypeAnalysisContext destination)
    {
        if (HomogeneousFloatStruct.Count(destination) is not > 1
            || instruction.Operands[operand] is not FieldReference { Offset: 0, Local: { Type: { } held } whole }
            || held.FullName != destination.FullName)
        {
            return;
        }

        //And it must really be the first field, not a one-field struct's only one or a name that happens to
        //sit at nought in something else.
        if (HomogeneousFloatStruct.Fields(held) is not { Count: > 1 } fields
            || !ReferenceEquals(fields[0], ((FieldReference)instruction.Operands[operand]).Field))
        {
            return;
        }

        instruction.Operands[operand] = whole;
    }
}
