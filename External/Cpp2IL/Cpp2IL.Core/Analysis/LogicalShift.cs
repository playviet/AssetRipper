using System.Runtime.CompilerServices;
using Cpp2IL.Core.ISIL;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Which right shifts shift zeroes in rather than the sign bit.
/// </summary>
/// <remarks>
/// <para>
/// arm64 has two right shifts and they are different operations: <c>LSRV</c>/<c>UBFM</c> shift zeroes in,
/// which is what <c>ulong &gt;&gt; n</c> compiles to, and <c>ASRV</c>/<c>SBFM</c> shift the sign bit in, which
/// is what <c>long &gt;&gt; n</c> compiles to. ISIL has one <see cref="OpCode.ShiftRight"/> for both, and the
/// generator emitted <c>shr</c> for it - so every logical shift came back arithmetic. The two agree on a
/// non-negative value and differ on every negative one, which is why nothing caught it: no marker, no
/// commented statement, compiles cleanly, wrong only at runtime. Counted in this game's own assembly:
/// <b>93 logical right shifts against 17 arithmetic ones</b>.
/// </para>
/// <para>
/// Recorded beside the instruction rather than on it. An extra operand would be the obvious way and is the
/// wrong one: <see cref="ConstantFolding"/> refuses anything whose operand count is not three, so a fourth
/// would silently stop logical shifts being folded, and the folding is what several later passes read.
/// </para>
/// </remarks>
public static class LogicalShift
{
    private static readonly ConditionalWeakTable<Instruction, object> Logical = new();

    private static readonly object Yes = new();

    /// <summary>Says this shift brings in zeroes. Returns what it was given, so it can wrap an emit.</summary>
    public static Instruction? Mark(Instruction? shift)
    {
        if (shift is { OpCode: OpCode.ShiftRight })
            Logical.AddOrUpdate(shift, Yes);

        return shift;
    }

    /// <summary>Whether this shift brings in zeroes. Anything unmarked keeps the sign, as before.</summary>
    public static bool BringsInZeroes(Instruction shift) => Logical.TryGetValue(shift, out _);
}
