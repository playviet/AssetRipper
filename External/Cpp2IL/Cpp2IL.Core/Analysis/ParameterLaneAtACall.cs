using System.Collections.Generic;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Names a parameter's lane where a call is the only thing that reads it.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="HomogeneousFloatParameters"/> gives each vector register a struct of floats arrived in the name
/// of the field it holds, but only where something <i>computes</i> with it - its <c>ReadsAValue</c> list is
/// arithmetic, comparison, select and move, and a call is none of those. A lane that is forwarded straight on
/// as an argument is therefore never named, and since nothing else in the body writes that register there is
/// no definition of it at all: the generator declares it and reads it back, so
/// <c>GizmosDrawer::DrawCross</c> passes <c>new Vector3(x, spot.y - num3, (float)obj)</c> with
/// <c>object obj = default(object);</c> two lines above. The z of the origin is <c>spot.z</c>, sitting
/// untouched in v2 exactly where the callee expects it, which is precisely why nothing wrote it.
/// </para>
/// <para>
/// Adding <c>Call</c> to that list is not the fix, because a call's operand list is not a list of values the
/// way an addition's is. Two positions in it are the callee and its result, and - the reason this is a pass of
/// its own - <b>a register the lifter named for an argument is not always that argument</b>. Aapcs64 has eight
/// vector registers, and an aggregate that does not fit in what is left of them is copied to the stack whole;
/// the lifter's walk keeps counting past v7 regardless, so for <c>Debug.DrawRay(Vector3, Vector3, Color)</c> -
/// ten floats - the colour is named v6..v9 while it is really on the stack. Naming v6 there would turn a
/// <c>default</c> that says nothing into <c>color.b</c>, which says something false. So the walk here is the
/// callee's signature, and it stops at the same place the convention does.
/// </para>
/// </remarks>
public static class ParameterLaneAtACall
{
    public static void Run(MethodAnalysisContext method)
    {
        if (method.ControlFlowGraph is not { } graph || method.Parameters.Count == 0)
            return;

        var fieldOf = HomogeneousFloatParameters.FieldsByRegister(method);

        if (fieldOf.Count == 0)
            return;

        var overwrittenBefore = HomogeneousFloatParameters.OverwrittenBefore(graph.Blocks);
        var renamed = HomogeneousFloatParameters.RenamedRegisters(method);

        foreach (var block in graph.Blocks)
        {
            //How many vector registers an answer has already been returned into, on any path arriving here.
            var overwritten = overwrittenBefore.GetValueOrDefault(block);

            foreach (var instruction in block.Instructions)
            {
                if (instruction.OpCode is OpCode.Call or OpCode.CallVoid
                    && instruction.Operands.Count > 0
                    && instruction.Operands[0] is MethodAnalysisContext callee)
                {
                    Name(instruction, callee, fieldOf, overwritten, renamed);
                }

                overwritten = System.Math.Max(overwritten,
                    HomogeneousFloatParameters.ReturnedInVectorRegisters(instruction));
            }
        }
    }

    /// <summary>
    /// Walks the callee's signature the way the lifter allocated the registers, and names every operand that
    /// really is one of this method's own parameter lanes.
    /// </summary>
    private static void Name(Instruction instruction, MethodAnalysisContext callee,
        Dictionary<LocalVariable, (FieldAnalysisContext Field, LocalVariable Struct, int Offset, int Register)> fieldOf,
        int overwritten, HashSet<int> renamed)
    {
        //Operand 0 is the callee, operand 1 a `Call`'s result, and the receiver comes before the parameters.
        var first = instruction.OpCode == OpCode.Call
            ? (callee.IsStatic ? 2 : 3)
            : (callee.IsStatic ? 1 : 2);

        //Every vector register beyond the first that a struct of floats occupies is handed over after the
        //whole parameter list - see GetArgumentOperandsForCall.
        var beyond = first + callee.Parameters.Count;
        var vector = 0;

        for (var i = 0; i < callee.Parameters.Count; i++)
        {
            var type = callee.Parameters[i].ParameterType;

            if (type.Namespace == nameof(System))
            {
                //A float or a double takes a vector register of its own; anything else takes none.
                if (type.Name is "Single" or "Double")
                {
                    if (vector < Aapcs64.RegistersPerRun)
                        Rename(instruction, first + i, fieldOf, overwritten, renamed);

                    vector++;
                }

                continue;
            }

            if (HomogeneousFloatStruct.Count(type) is not { } floats)
                continue;

            var start = vector;
            var handed = beyond;

            vector += floats;
            beyond += floats - 1;

            //Eight is all there are. What does not fit in the rest of the run went to the stack whole, and
            //the registers named for it hold something else - very often one of this method's own parameters,
            //which is exactly what would be written here. Unless `StackedFloatArgument` has already put the
            //registers the stores read from in their place, in which case naming them is the whole point.
            if (start + floats > Aapcs64.RegistersPerRun && !StackedFloatArgument.WasCorrected(instruction, i))
                continue;

            //The first register of the run is both the struct and its first field, and where it still holds
            //the whole struct - this method's own parameter forwarded on unchanged - it is already the right
            //answer and `HomogeneousFloatArguments` is relying on it being one. Taking it apart into
            //`new Vector3(v.x, v.y, v.z)` says the same thing at best, and where a single lane later fails to
            //be recognised as a float it says `(Vector3)v.x`, which is worse than what it replaced.
            if (!ForwardsTheStructItself(instruction, first + i, fieldOf, type))
                Rename(instruction, first + i, fieldOf, overwritten, renamed);

            for (var field = 1; field < floats; field++)
                Rename(instruction, handed + field - 1, fieldOf, overwritten, renamed);
        }
    }

    /// <summary>Whether an operand is a parameter of this method being handed on as itself.</summary>
    private static bool ForwardsTheStructItself(Instruction instruction, int index,
        Dictionary<LocalVariable, (FieldAnalysisContext Field, LocalVariable Struct, int Offset, int Register)> fieldOf,
        TypeAnalysisContext wanted)
        => index < instruction.Operands.Count
            && instruction.Operands[index] is LocalVariable local
            && fieldOf.TryGetValue(local, out var held)
            && held.Offset == 0
            && held.Struct.Type?.FullName == wanted.FullName;

    private static void Rename(Instruction instruction, int index,
        Dictionary<LocalVariable, (FieldAnalysisContext Field, LocalVariable Struct, int Offset, int Register)> fieldOf,
        int overwritten, HashSet<int> renamed)
    {
        if (index < 0 || index >= instruction.Operands.Count)
            return;

        if (instruction.Operands[index] is not LocalVariable local || !fieldOf.TryGetValue(local, out var held))
            return;

        //A struct of floats comes back one field to a vector register, so once a call in front of this one has
        //returned into the register the parameter arrived in, the parameter is no longer what is there.
        //Reading version -1 means single assignment form proved the entry value still reaches this point,
        //which only says something where it tracked the write at all - and it never tracks a struct return,
        //which the lifter names x0.
        if (held.Register < overwritten && !renamed.Contains(local.Register.Number))
            return;

        instruction.Operands[index] = new FieldReference(held.Field, held.Struct, held.Offset);
    }
}
