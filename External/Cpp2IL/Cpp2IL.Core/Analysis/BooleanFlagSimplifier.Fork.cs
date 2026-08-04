using System.Collections.Generic;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// What this fork adds: writing a negated comparison as the comparison it negates, and a test of a
/// number's sign bit as the comparison against zero that it is.
///
/// Kept apart from the file it belongs to so that the file stays as close to upstream as it can,
/// and a later version of Cpp2IL can be merged without the two sets of changes meeting.
/// </summary>
public static partial class BooleanFlagSimplifier
{
    private static bool IsBoolean(object operand, TypeAnalysisContext booleanType) => operand switch
    {
        LocalVariable { Type: { } type } => type == booleanType,
        FieldReference field => field.Field.FieldType == booleanType,
        _ => false,
    };

    private static bool IsOneConstant(object operand)
    {
        try
        {
            return operand is not string && Instruction.IsConstantValue(operand) && System.Convert.ToInt64(operand) == 1;
        }
        catch (System.Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Turns the negation of a comparison into the opposite comparison.
    ///
    /// A condition arrives as a value in a register, so negating it is a separate instruction on a separate
    /// local, and a branch on the negation of a negation reads as <c>bool flag = x == null; bool flag2 = !flag;
    /// if (!flag2)</c> - three names for one question. Asking the opposite question instead says the same thing
    /// in one step, which is also the shape the decompiler recognises: the cached-lambda pattern it folds back
    /// into a lambda expression is written as a plain null test, and did not match while the test was spelled
    /// this way.
    /// </summary>
    private static void FoldNegatedComparisons(MethodAnalysisContext method)
    {
        var definitions = SingleDefinitions(method);

        foreach (var instruction in method.ControlFlowGraph!.Instructions)
        {
            if (instruction.OpCode != OpCode.Not || instruction.Operands.Count < 2)
                continue;

            if (instruction.Operands[1] is not LocalVariable source || !definitions.TryGetValue(source, out var comparison))
                continue;

            if (Opposite(comparison.OpCode) is not { } opposite || comparison.Operands.Count < 3)
                continue;

            //Recomputing the comparison is only the same question if what it asks about has not moved on.
            if (!IsStable(comparison.Operands[1], definitions) || !IsStable(comparison.Operands[2], definitions))
                continue;

            instruction.OpCode = opposite;
            instruction.Operands = [instruction.Operands[0], comparison.Operands[1], comparison.Operands[2]];
        }
    }

    /// <summary>
    /// Where each local that is written exactly once is written. A local written more than once has no single
    /// definition to reason about and is left out.
    /// </summary>
    private static Dictionary<LocalVariable, Instruction> SingleDefinitions(MethodAnalysisContext method)
    {
        var definitions = new Dictionary<LocalVariable, Instruction>();
        var rewritten = new HashSet<LocalVariable>();

        foreach (var instruction in method.ControlFlowGraph!.Instructions)
        {
            if (instruction.Destination is not LocalVariable destination)
                continue;

            if (!definitions.TryAdd(destination, instruction))
                rewritten.Add(destination);
        }

        foreach (var local in rewritten)
            definitions.Remove(local);

        return definitions;
    }

    /// <summary>Whether an operand still holds what it held where the comparison was made.</summary>
    private static bool IsStable(object operand, Dictionary<LocalVariable, Instruction> definitions)
        => operand is not LocalVariable local || definitions.ContainsKey(local);

    private static OpCode? Opposite(OpCode opCode) => opCode switch
    {
        OpCode.CheckEqual => OpCode.CheckNotEqual,
        OpCode.CheckNotEqual => OpCode.CheckEqual,
        OpCode.CheckGreater => OpCode.CheckLessOrEqual,
        OpCode.CheckLessOrEqual => OpCode.CheckGreater,
        OpCode.CheckLess => OpCode.CheckGreaterOrEqual,
        OpCode.CheckGreaterOrEqual => OpCode.CheckLess,
        _ => null,
    };

    /// <summary>
    /// Turns a test of the sign bit back into the comparison it came from.
    ///
    /// arm64 asks whether a number is negative with a test-and-branch on its top bit, so <c>x &gt;= 0</c>
    /// arrives as <c>(x &amp; 0x80000000) == 0</c> - and the mask, being a bit pattern rather than a number,
    /// is a 64-bit constant, so the comparison also stopped reading as one between ints.
    ///
    /// Only a signed number is rewritten. On an unsigned one the top bit is a bit like any other and the
    /// comparison would be trivially true, so the test is left as the bit test it is.
    /// </summary>
    private static void FoldSignTests(MethodAnalysisContext method)
    {
        var definitions = SingleDefinitions(method);

        foreach (var instruction in method.ControlFlowGraph!.Instructions)
        {
            if (instruction.OpCode is not (OpCode.CheckEqual or OpCode.CheckNotEqual) || instruction.Operands.Count < 3)
                continue;

            if (!IsZeroConstant(instruction.Operands[2]))
                continue;

            if (instruction.Operands[1] is not LocalVariable masked || !definitions.TryGetValue(masked, out var and))
                continue;

            if (and.OpCode != OpCode.And || and.Operands.Count < 3 || and.Operands[1] is not LocalVariable tested)
                continue;

            if (SignBitWidth(and.Operands[2]) is not { } width || !IsSignedOfWidth(tested.Type, width))
                continue;

            if (!IsStable(tested, definitions))
                continue;

            //The bit clear is the number being non-negative; the bit set is it being negative.
            instruction.OpCode = instruction.OpCode == OpCode.CheckEqual ? OpCode.CheckGreaterOrEqual : OpCode.CheckLess;
            instruction.Operands = [instruction.Operands[0], tested, 0];
        }
    }

    /// <summary>
    /// The width of the number whose sign bit this mask is, if that is what it is. The same bit reaches here
    /// written several ways - as a negative int, as the positive long it widens to, or as an unsigned value
    /// too large for a long - so each is matched rather than routed through one conversion.
    /// </summary>
    private static int? SignBitWidth(object operand) => operand switch
    {
        int i when i == int.MinValue => 32,
        uint u when u == 0x80000000U => 32,
        long l when l is 0x80000000L or int.MinValue => 32,
        long l when l == long.MinValue => 64,
        ulong u when u == 0x80000000UL => 32,
        ulong u when u == 0x8000000000000000UL => 64,
        _ => null,
    };

    private static bool IsSignedOfWidth(TypeAnalysisContext? type, int width) => type?.FullName switch
    {
        "System.Int32" => width == 32,
        "System.Int64" => width == 64,
        _ => false,
    };
}
