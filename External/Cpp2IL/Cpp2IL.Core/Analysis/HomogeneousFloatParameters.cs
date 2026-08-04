using System.Collections.Generic;
using System.Linq;
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
        or OpCode.CheckGreaterOrEqual or OpCode.CheckLessOrEqual;

    public static void Run(MethodAnalysisContext method)
    {
        if (method.ControlFlowGraph == null || method.Parameters.Count == 0)
            return;

        var fieldOf = FieldsByRegister(method);
        if (fieldOf.Count == 0)
            return;

        foreach (var instruction in method.ControlFlowGraph.Instructions)
        {
            if (!ReadsAValue(instruction.OpCode))
                continue;

            //Operand 0 is where the result goes, and a result is never one of these.
            for (var i = 1; i < instruction.Operands.Count; i++)
            {
                if (instruction.Operands[i] is LocalVariable local
                    && fieldOf.TryGetValue(local, out var held))
                    instruction.Operands[i] = new FieldReference(held.Field, held.Struct, held.Offset);
            }
        }
    }

    /// <summary>
    /// The entry value of each vector register a float struct was passed in, and which field of which
    /// parameter it holds.
    /// </summary>
    private static Dictionary<LocalVariable, (FieldAnalysisContext Field, LocalVariable Struct, int Offset)>
        FieldsByRegister(MethodAnalysisContext method)
    {
        var found = new Dictionary<LocalVariable, (FieldAnalysisContext, LocalVariable, int)>();
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
                    found[register] = (fields[i], held, i * 4);
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
