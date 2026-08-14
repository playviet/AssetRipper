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

            //Where the registers beyond the first of each float struct were handed over, and how far into the
            //run of eight this argument starts - both exactly as GetArgumentOperandsForCall counted them.
            var beyond = first + callee.Parameters.Count;
            var vector = 0;

            for (var i = 0; i < callee.Parameters.Count && first + i < instruction.Operands.Count; i++)
            {
                var type = callee.Parameters[i].ParameterType;

                if (type.Namespace == nameof(System))
                {
                    if (type.Name is "Single" or "Double")
                        vector++;

                    continue;
                }

                if (HomogeneousFloatStruct.Count(type) is not { } floats)
                    continue;

                var start = vector;
                var handed = beyond;

                vector += floats;
                beyond += floats - 1;

                //A struct is only being handed on unchanged if the registers beside this one are the rest of
                //it. `rb.position = new Vector3(rb.position.x, y, rb.position.z)` puts the front member of one
                //call's answer in v0 and has nothing to do with v1 and v2 - the parameter and a second call's
                //answer - and calling that operand the whole `position` wrote `rb.position = position`, which
                //compiles, carries no marker, and silently throws the `y` away. Three members of
                //`RigidbodyExtention` and `ImageExtension::SetAlpha` are that shape exactly.
                //
                //Only where the assembly below could put the struct back together from the lanes instead:
                //where it could not - an argument past the end of the run of eight, or a type with no
                //constructor taking its fields - declining here leaves a member where the struct belongs,
                //which is the invalid cast this pass exists to remove.
                if (Assemblable(instruction, type, floats, i, start)
                    && !TheRestIsThere(instruction, first + i, handed, floats, carriedBy))
                {
                    continue;
                }

                Restore(instruction, first + i, type, carriedBy);
            }
        }
    }

    /// <summary>
    /// Whether <see cref="HomogeneousFloatArguments"/> would be able to put this argument back together from
    /// its lanes - which is what makes declining to name the whole struct safe.
    /// </summary>
    private static bool Assemblable(Instruction instruction, TypeAnalysisContext type, int floats,
        int parameter, int start)
        => floats > 1
            && (start + floats <= Aapcs64.RegistersPerRun
                || StackedFloatArgument.WasCorrected(instruction, parameter))
            && type.Methods.Any(m => m is { Name: ".ctor", IsStatic: false }
                && m.Parameters.Count == floats
                && m.Parameters.All(p => p.ParameterType.FullName == "System.Single"));

    /// <summary>
    /// Whether the registers handed over beside this argument really are the rest of the same struct.
    /// </summary>
    /// <remarks>
    /// Positive confirmation, not the absence of a counter-example: a lane whose origin cannot be read here is
    /// no evidence that the struct is intact, and the answer costs nothing when it is wrong - the lanes are
    /// then assembled into the same struct one field at a time.
    /// </remarks>
    private static bool TheRestIsThere(Instruction instruction, int head, int handed, int floats,
        Dictionary<LocalVariable, FieldReference> carriedBy)
    {
        //Whatever the head is a member of - which is the value the rest have to be members of too.
        if (Member(instruction.Operands[head], carriedBy) is not { Offset: 0 } front)
            return false;

        for (var field = 1; field < floats; field++)
        {
            var at = handed + field - 1;

            if (at >= instruction.Operands.Count
                || Member(instruction.Operands[at], carriedBy) is not { } lane
                || lane.Offset != field * 4
                || !ReferenceEquals(lane.Local, front.Local))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The struct member an operand is, directly or through the local carrying it.</summary>
    private static FieldReference? Member(object operand, Dictionary<LocalVariable, FieldReference> carriedBy)
        => operand switch
        {
            FieldReference reference => reference,
            LocalVariable local when carriedBy.TryGetValue(local, out var indirect) => indirect,
            _ => null,
        };

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

            //Every offset, not only the front one: the lanes beside the head are read the same way, and
            //whether they are the rest of the same struct is what says the struct is intact.
            if (instruction is { OpCode: OpCode.Move, Operands: [_, FieldReference front] })
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
        var read = Member(instruction.Operands[operand], carriedBy) is { Offset: 0 } front ? front : null;

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
