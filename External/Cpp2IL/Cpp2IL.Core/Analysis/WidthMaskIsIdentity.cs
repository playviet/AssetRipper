using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Removes a mask to thirty-two bits from a value that has no more than thirty-two.
/// </summary>
/// <remarks>
/// <para>
/// A write through a register's 32-bit name zeroes the top half, and the fork writes that down as
/// <c>And x, x, 0xFFFFFFFF</c> so the truncation is not silently lost. Where what is in the register is
/// **already** four bytes or fewer, the mask does nothing - and it is not harmless, because the value stops
/// looking like the type it has:
/// </para>
/// <code>
/// And v65 @ X1_v2 (UnityEngine.Color32), c @ X1 (UnityEngine.Color32), 4294967295
/// </code>
/// <para>
/// came out as <c>Color32 color = (Color32)(c &amp; 0xFFFFFFFFL);</c> - a `Color32` masked into a number and
/// cast back, which does not compile. It is a whole `Color32` and always was.
/// </para>
/// </remarks>
public static class WidthMaskIsIdentity
{
    private const long Word = 0xFFFFFFFFL;

    public static void Run(MethodAnalysisContext method)
    {
        if (method.ControlFlowGraph is not { } graph)
            return;

        foreach (var instruction in graph.Instructions)
        {
            if (instruction is not { OpCode: OpCode.And, Operands: [{ } into, LocalVariable source, { } mask] }
                || source.Type is not { } type)
                continue;

            //A `bool` holds nought or one, so masking it to its low bit changes nothing - and the mask is what
            //made the two arms of a merged return an `int` and a `bool`, which ILSpy bridged with `(int?)`.
            var identity = type.FullName == "System.Boolean"
                ? IsOne(mask) || IsWord(mask)
                : IsWord(mask) && Aapcs64.SizeOf(type) is { } size && size <= 4;

            if (!identity)
                continue;

            instruction.OpCode = OpCode.Move;
            instruction.Operands = [into, source];
        }
    }

    private static bool IsOne(object mask)
    {
        try
        {
            return mask is not string && System.Convert.ToInt64(mask) == 1;
        }
        catch (System.Exception)
        {
            return false;
        }
    }

    private static bool IsWord(object mask)
        => mask is long and Word || mask is ulong and Word || mask is uint and (uint)Word;
}
