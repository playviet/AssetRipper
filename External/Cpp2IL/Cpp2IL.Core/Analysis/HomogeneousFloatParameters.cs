using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Names each register a struct of floats arrives in as the field it actually holds.
/// </summary>
/// <remarks>
/// <para>
/// Aapcs64 hands a struct whose every field is a float over in one vector register per field, so a
/// <c>Vector2</c> is v0 and v1 rather than one value anywhere. Only the first register was named, and it was
/// named after the whole struct - so <c>a.X - b.X</c> reached the generator as <c>a - b</c> over two
/// <c>Pair</c>s, which is not something that can be written down. Refusing it was the safe answer and left a
/// placeholder in the middle of the statement: 156 of them across 24 files, in a game whose types are
/// <c>Vector2</c>, <c>Vector3</c>, <c>Quaternion</c> and <c>Color</c> almost throughout.
/// </para>
/// <para>
/// Each of those registers is one field, so it is named as one. The struct is then never in a register at
/// all, which is the truth about how it was passed.
/// </para>
/// <para>
/// Only a struct whose immediate fields are all <c>float</c> is taken - which is every geometry type Unity
/// ships. One holding another struct would need a path rather than a field, and is left alone rather than
/// guessed at.
/// </para>
/// </remarks>
public static class HomogeneousFloatParameters
{
    // Where a value is one field of a struct rather than the struct: everything that computes with it.
    private static bool ReadsAValue(OpCode opCode) => opCode
        is OpCode.Add or OpCode.Subtract or OpCode.Multiply or OpCode.Divide
        or OpCode.Negate or OpCode.Not
        or OpCode.CheckEqual or OpCode.CheckNotEqual
        or OpCode.CheckGreater or OpCode.CheckLess
        or OpCode.CheckGreaterOrEqual or OpCode.CheckLessOrEqual
        //`Mathf.Max` is a compare and a select, and only the compare was rewritten - so the select kept the
        //whole `Rect` where one of its floats belonged, and `FindOverlapCell` came out choosing between a
        //struct and a number. Eight sites in this assembly, eighty-four in the game.
        or OpCode.Select;

    public static void Run(MethodAnalysisContext method)
    {
        if (method.ControlFlowGraph == null || method.Parameters.Count == 0)
            return;

        var fieldOf = FieldsByRegister(method);
        if (fieldOf.Count == 0)
            return;

        var overwrittenBefore = OverwrittenBefore(method.ControlFlowGraph.Blocks);

        //Every register single assignment form gave more than one version.
        var renamed = new HashSet<int>();
        var seen = new Dictionary<int, int>();

        foreach (var local in method.Locals)
        {
            if (seen.TryGetValue(local.Register.Number, out var version) && version != local.Register.Version)
                renamed.Add(local.Register.Number);
            else
                seen[local.Register.Number] = local.Register.Version;
        }

        foreach (var block in method.ControlFlowGraph.Blocks)
        {
            //How many vector registers an answer has already been returned into, on any path arriving here.
            var overwritten = overwrittenBefore.GetValueOrDefault(block);

            foreach (var instruction in block.Instructions)
            {
                if (ReadsAValue(instruction.OpCode))
                {
                    //Operand 0 is where the result goes, and a result is never one of these.
                    for (var i = 1; i < instruction.Operands.Count; i++)
                    {
                        if (instruction.Operands[i] is not LocalVariable local
                            || !fieldOf.TryGetValue(local, out var held))
                            continue;

                        //A struct of floats comes back one field to a vector register, so a call returning one
                        //leaves the caller's parameter nowhere - and naming it after the parameter is a
                        //confident wrong answer: `endValue - target.color` came out as
                        //`endValue.a - endValue.a`, which is zero.
                        //
                        //`VectorReturnFields` says what a **direct** call put there instead, and where it has,
                        //single assignment form has already given the register a new value and this never
                        //matches. What is left is the call whose callee is not known while lifting - a virtual
                        //or interface call, resolved long after - and for that nothing can say what is in the
                        //register, so it keeps its own name and the statement is marked rather than decided.
                        //...and only where single assignment form did not track the write itself. A register
                        //it renamed has a later version somewhere in the method, so reading version -1 here
                        //means SSA proved the entry value still reaches this point - which is exactly the
                        //parameter. Without that test the guard refused `mergeCenter.x` and `.y` in
                        //`PlayDisappear` and took `.z` (the one whose register index happened to be at the
                        //cut-off), leaving two lanes as `default(float)`.
                        if (held.Register < overwritten && !renamed.Contains(local.Register.Number))
                            continue;

                        instruction.Operands[i] = new FieldReference(held.Field, held.Struct, held.Offset);

                        //A select answers with whichever arm it took, so once an arm is one field of the
                        //struct the answer is that field and not the struct. Its destination was typed from
                        //the arm before this pass touched it - `LocalVariables.PropagateSelect` reads the
                        //first typed operand - and `SetTypeIfUnknown` only fills nulls, so nothing would
                        //correct it. `FindOverlapCell`'s `Mathf.Max(dragWorldRect.xMin, cornersBuf[0].x)`
                        //came out choosing between a `Rect` and a number.
                        if (instruction.OpCode == OpCode.Select && instruction.Operands[0] is LocalVariable answer
                            && answer.Type?.FullName == held.Struct.Type?.FullName)
                        {
                            answer.Type = held.Field.FieldType;
                        }
                    }
                }

                overwritten = System.Math.Max(overwritten, ReturnedInVectorRegisters(instruction));
            }
        }
    }

    /// <summary>
    /// How many vector registers a call fills with its answer, which is one per field of a struct of floats.
    /// </summary>
    private static int ReturnedInVectorRegisters(Instruction instruction)
        => instruction.IsCall && instruction.Operands.Count > 0
            && instruction.Operands[0] is MethodAnalysisContext callee
            && HomogeneousFloatStruct.Count(callee.ReturnType) is { } floats and > 1
            ? floats
            : 0;

    /// <summary>
    /// For each block, the widest struct of floats returned into the vector registers on any path from the
    /// method's entry to the start of it.
    /// </summary>
    /// <remarks>
    /// A "may" question, so the widest on any path in is the answer - taking a parameter's name back is the
    /// safe direction. Run to a fixpoint so a call anywhere in a loop covers the whole loop, including the
    /// blocks above it that the back edge reaches.
    /// </remarks>
    private static Dictionary<Block, int> OverwrittenBefore(List<Block> blocks)
    {
        var within = blocks.ToDictionary(block => block,
            block => block.Instructions.Count == 0 ? 0 : block.Instructions.Max(ReturnedInVectorRegisters));

        var before = blocks.ToDictionary(block => block, _ => 0);

        for (var changed = true; changed;)
        {
            changed = false;

            foreach (var block in blocks)
            {
                foreach (var predecessor in block.Predecessors)
                {
                    if (!before.TryGetValue(predecessor, out var reached))
                        continue;

                    var through = System.Math.Max(reached, within.GetValueOrDefault(predecessor));

                    if (through <= before[block])
                        continue;

                    before[block] = through;
                    changed = true;
                }
            }
        }

        return before;
    }

    /// <summary>
    /// The entry value of each vector register a float struct was passed in, and which field of which
    /// parameter it holds.
    /// </summary>
    private static Dictionary<LocalVariable,
        (FieldAnalysisContext Field, LocalVariable Struct, int Offset, int Register)>
        FieldsByRegister(MethodAnalysisContext method)
    {
        var found = new Dictionary<LocalVariable, (FieldAnalysisContext, LocalVariable, int, int)>();
        var vectorCount = 0;

        foreach (var parameter in method.Parameters)
        {
            var type = parameter.ParameterType;

            if (type.Namespace == nameof(System))
            {
                //A float or a double takes a vector register of its own; anything else takes none.
                if (type.Name is "Single" or "Double")
                    vectorCount++;
                continue;
            }

            if (HomogeneousFloatStruct.Count(type) is not { } floats)
                continue;

            var first = vectorCount;
            vectorCount += floats;

            if (floats < 2 || FloatFields(type) is not { } fields || fields.Count != floats)
                continue;

            if (LocalForVector(method, first) is not { } held)
                continue;

            for (var i = 0; i < floats; i++)
            {
                if (LocalForVector(method, first + i) is { } register)
                    found[register] = (fields[i], held, i * 4, first + i);
            }
        }

        return found;
    }

    /// <summary>The fields of a struct, where every one of them is a float and nothing else.</summary>
    private static List<FieldAnalysisContext>? FloatFields(TypeAnalysisContext type)
    {
        var fields = new List<FieldAnalysisContext>();

        foreach (var field in type.Fields)
        {
            if (field.IsStatic)
                continue;

            if (field.FieldType.FullName != "System.Single")
                return null;

            fields.Add(field);
        }

        return fields;
    }

    /// <summary>
    /// The value a vector register holds on entry. Registers are named rather than numbered, so the name is
    /// what has to be rebuilt - <see cref="Cpp2IL.Core.InstructionSets.NewArmV8InstructionSet"/> gives every
    /// width of one physical register the same name, and a vector register is `V` and its number.
    /// </summary>
    private static LocalVariable? LocalForVector(MethodAnalysisContext method, int number)
    {
        var register = new Register(null, "V" + number);

        return method.Locals.FirstOrDefault(l => l.Register.Number == register.Number && l.Register.Version == -1);
    }
}
