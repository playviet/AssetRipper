using System.Collections.Generic;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// The float constants that are not being moved into a float field or passed to a float parameter, but
/// computed with.
/// </summary>
/// <remarks>
/// <para>
/// A floating point constant does not survive compilation as a constant: it is a word the code materialises
/// into a general register and moves into a vector one, so recovery gets an integer and has to know it is a
/// float to write it back. Where the value goes straight into a float field or a float argument, the type it
/// is going to says so. Where it is computed with first, nothing did, and the constant came out as its own bit
/// pattern: <c>360f</c> as <c>1135869952</c>, <c>100f</c> as <c>1120403456</c>, <c>1000f</c> as
/// <c>1148846080</c>. Each of those compiles, so nothing complains, and each is a wrong number.
/// </para>
/// <para>
/// <b>The evidence has to be a type, not the value.</b> <c>1431655766</c> is <c>0x55555556</c>, the multiplier
/// a division by three is compiled into, and as a float it is 1.5e13 - a perfectly ordinary looking one. So a
/// constant is only reinterpreted where something <i>else</i> in the same expression is known to be floating
/// point. The existing rule that a subnormal decode is a mislabelled integer then covers the other direction,
/// keeping a small count like <c>4</c> as itself inside an expression that really is float.
/// </para>
/// <para>
/// This became worth doing when a conversion stopped being an ordinary move: until arithmetic over a converted
/// value was typed at all, there was rarely anything in these expressions to take the evidence from.
/// </para>
/// </remarks>
public static partial class FloatLiteralRecovery
{
    /// <summary>
    /// Reinterprets the integer constants of an expression that something else in it says is floating point.
    /// </summary>
    public static void ConvertComputedLiterals(MethodAnalysisContext method, Instruction instruction)
    {
        if (FirstValueOperand(instruction) is not { } first)
            return;

        if (FloatingPointEvidence(method, instruction) is not { } type)
            return;

        for (var operand = first; operand < instruction.Operands.Count; operand++)
            TryConvert(instruction, operand, type);
    }

    /// <summary>
    /// The first operand of this instruction that is a value it computes with, for the instructions where a
    /// constant among them is arithmetic rather than a count.
    /// </summary>
    /// <remarks>
    /// A shift's second operand counts places and a jump's operand is a target, so neither is here. A select's
    /// first operand is the condition rather than one of the two values it chooses between.
    /// </remarks>
    private static int? FirstValueOperand(Instruction instruction)
    {
        var first = instruction.OpCode switch
        {
            OpCode.Add or OpCode.Subtract or OpCode.Multiply or OpCode.Divide => 1,
            >= OpCode.CheckEqual and <= OpCode.CheckLessOrEqual => 1,
            OpCode.Select => 2,
            _ => 0,
        };

        return first > 0 && instruction.Operands.Count > first ? first : null;
    }

    /// <summary>
    /// The floating point type this expression works in, taken from something in it that is not a constant
    /// waiting to be reinterpreted.
    /// </summary>
    /// <remarks>
    /// A typed local or field says it, the destination says it - a result that is a float was computed from
    /// floats, whatever the lifting made of the operands - and so does a constant already recovered as one,
    /// which is how the second half of <c>1f * 60f</c> is reached once the first half is known.
    /// </remarks>
    private static TypeAnalysisContext? FloatingPointEvidence(MethodAnalysisContext method, Instruction instruction)
    {
        var types = method.AppContext.SystemTypes;

        foreach (var operand in instruction.Operands)
        {
            switch (operand)
            {
                case float:
                    return types.SystemSingleType;
                case double:
                    return types.SystemDoubleType;
                case LocalVariable { Type: { } local } when IsFloatingPoint(local):
                    return local;
                case FieldReference { Field.FieldType: { } field } when IsFloatingPoint(field):
                    return field;
            }
        }

        return null;
    }

    //Compared by name rather than by reference, matching how the conversion itself compares them: a field on a
    //generic type resolves to its own Single context instance rather than the canonical one in SystemTypes.
    private static bool IsFloatingPoint(TypeAnalysisContext type)
        => type.FullName is "System.Single" or "System.Double";
}
