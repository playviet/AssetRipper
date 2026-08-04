using System.Collections.Generic;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Fixes the compare-a-flag-against-zero pairs the we get for every conditional: <c>bool != 0</c>
/// is the bool itself, and <c>bool == 0</c> is its negation.
/// </summary>
public static partial class BooleanFlagSimplifier
{
    public static void Run(MethodAnalysisContext method)
    {
        var booleanType = method.AppContext.SystemTypes.SystemBooleanType;

        FoldSignTests(method);

        foreach (var instruction in method.ControlFlowGraph!.Instructions)
        {
            //Testing bit zero of a bool is testing the bool: the mask a test-and-branch is lifted with says
            //nothing more than the value already does, and leaving it in writes `(b ? 1 : 0) & 1` where the
            //source said `b` - and the result of that is an int, so the test after it stops reading as one.
            if (instruction.OpCode == OpCode.And && instruction.Operands.Count > 2
                && IsBoolean(instruction.Operands[1], booleanType) && IsOneConstant(instruction.Operands[2]))
            {
                instruction.OpCode = OpCode.Move;
                instruction.Operands = [instruction.Operands[0], instruction.Operands[1]];

                if (instruction.Operands[0] is LocalVariable masked)
                    masked.Type ??= booleanType;

                continue;
            }

            if (instruction.OpCode is not (OpCode.CheckEqual or OpCode.CheckNotEqual) || instruction.Operands.Count < 3)
                continue;

            if (!IsZeroConstant(instruction.Operands[2]))
                continue;

            if (!IsBoolean(instruction.Operands[1], booleanType))
                continue;

            instruction.OpCode = instruction.OpCode == OpCode.CheckNotEqual ? OpCode.Move : OpCode.Not;
            instruction.Operands = [instruction.Operands[0], instruction.Operands[1]];
        }

        FoldNegatedComparisons(method);
    }

    private static bool IsZeroConstant(object operand) =>
        operand switch
        {
            int i => i == 0,
            uint ui => ui == 0,
            long l => l == 0,
            ulong ul => ul == 0,
            short s => s == 0,
            ushort us => us == 0,
            byte b => b == 0,
            sbyte sb => sb == 0,
            _ => false,
        };
}
