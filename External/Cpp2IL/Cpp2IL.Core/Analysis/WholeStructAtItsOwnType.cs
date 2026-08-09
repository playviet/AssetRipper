using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Graphs;
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

        var carriedBy = FrontMemberCarriers(graph);

        foreach (var instruction in graph.Instructions)
        {
            if (instruction.OpCode == OpCode.Move && instruction.Operands.Count == 2)
            {
                if (Declared(instruction.Operands[0]) is { } destination)
                    Restore(instruction, 1, destination, carriedBy);

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
                Restore(instruction, first + i, callee.Parameters[i].ParameterType, carriedBy);
        }
    }

    /// <summary>The type a place is declared to hold, where it says so.</summary>
    private static TypeAnalysisContext? Declared(object operand) => operand switch
    {
        LocalVariable { Type: { } type } => type,
        FieldReference field => field.Field.FieldType,
        _ => null,
    };

    /// <summary>
    /// Every local whose one definition is a read of a struct's front member, and what that read was.
    /// </summary>
    /// <remarks>
    /// The naming of a call's answer writes one move per returned field, and the first of those is carried on
    /// in a local of its own rather than reaching the place it is going directly - so
    /// `_lastDragSfxPos = transform.position` arrives here as a `System.Single` and nothing above can see
    /// what it came out of. Only where the local is written **once**: a value assembled from two places is
    /// not the struct either of them came from.
    /// </remarks>
    private static Dictionary<LocalVariable, FieldReference> FrontMemberCarriers(ISILControlFlowGraph graph)
    {
        var carried = new Dictionary<LocalVariable, FieldReference>();
        var written = new HashSet<LocalVariable>();

        foreach (var instruction in graph.Instructions)
        {
            if (instruction.Operands.Count == 0 || instruction.Operands[0] is not LocalVariable destination)
                continue;

            if (!written.Add(destination))
            {
                carried.Remove(destination);
                continue;
            }

            if (instruction is { OpCode: OpCode.Move, Operands: [_, FieldReference { Offset: 0 } front] })
                carried[destination] = front;
        }

        return carried;
    }

    private static void Restore(Instruction instruction, int operand, TypeAnalysisContext destination,
        Dictionary<LocalVariable, FieldReference> carriedBy)
    {
        if (HomogeneousFloatStruct.Count(destination) is not > 1)
            return;

        //The read itself, or the local that is carrying it and nothing else.
        var read = instruction.Operands[operand] switch
        {
            FieldReference { Offset: 0 } direct => direct,
            LocalVariable local when carriedBy.TryGetValue(local, out var indirect) => indirect,
            _ => null,
        };

        if (read is not { Local: { Type: { } held } whole } front2 || held.FullName != destination.FullName)
            return;

        //And it must really be the first field, not a one-field struct's only one or a name that happens to
        //sit at nought in something else. By name: a field reached through a generic instantiation is not the
        //same object as the one on the type definition, and reference equality quietly refused those.
        if (HomogeneousFloatStruct.Fields(held) is not { Count: > 1 } fields
            || fields[0].Name != front2.Field.Name)
        {
            return;
        }

        instruction.Operands[operand] = whole;
    }
}
