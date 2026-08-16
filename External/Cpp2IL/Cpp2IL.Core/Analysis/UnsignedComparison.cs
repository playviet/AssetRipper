using System.Runtime.CompilerServices;
using Cpp2IL.Core.ISIL;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Which ordered comparisons ask an unsigned question.
/// </summary>
/// <remarks>
/// <para>
/// arm64 has one set of flags and two readings of them. <c>GE</c>, <c>LT</c>, <c>GT</c> and <c>LE</c> read
/// the sign; <c>CS</c>, <c>CC</c>, <c>HI</c> and <c>LS</c> read the <b>carry</b>, and after a comparison the
/// carry says <c>(uint)a &gt;= (uint)b</c>. The two agree while both sides are non-negative and differ on
/// every negative one - and the compiler reaches for the unsigned form precisely when it wants a
/// <c>&lt; 0</c> test folded into a range check, so the disagreement is the normal case rather than the
/// exotic one. <b>30944 comparisons in this game</b> are written this way.
/// </para>
/// <para>
/// <c>Corpus::Weight</c> is the clean statement of it: a jump table clang turned into <c>kind * 15 + 10</c>
/// guarded by <c>cmp w0, #7</c> / <c>csel w0, w8, wzr, cc</c>. Read as signed the guard is <c>kind &lt; 7</c>,
/// which a negative <c>kind</c> passes, and the method returned -305 where the source returns 0 - whole, with
/// no marker, rated <c>full</c> by every scorer this project has.
/// </para>
/// <para>
/// Recorded beside the instruction, exactly as <see cref="LogicalShift"/> is and for the same two reasons:
/// ISIL has one <see cref="OpCode.CheckLess"/> for both readings, and a fourth operand would put the
/// instruction outside what <see cref="ConstantFolding"/> will look at. The alternative - converting both
/// sides to <see cref="uint"/> in the lifter - was built first and is worse in three ways: it spends two
/// instructions at every comparison whether or not a carry condition ever reads them, it cannot state the
/// 64-bit case at all, and the IL stack carries no unsignedness anyway, so the conversion was faithfully
/// undone on the way back out (<c>uint num2 = (uint)kind;</c> then <c>(int)num2 &gt;= 7</c>). The reading is
/// a property of the <b>condition</b>, and the condition is known exactly where the comparison is emitted.
/// </para>
/// </remarks>
public static class UnsignedComparison
{
    private static readonly ConditionalWeakTable<Instruction, object> Unsigned = new();

    private static readonly object Yes = new();

    /// <summary>Says this comparison is the unsigned one. Returns what it was given, so it can wrap an emit.</summary>
    public static Instruction? Mark(bool unsigned, Instruction? comparison)
    {
        if (unsigned && comparison is { OpCode: OpCode.CheckGreater or OpCode.CheckLess
            or OpCode.CheckGreaterOrEqual or OpCode.CheckLessOrEqual })
        {
            Unsigned.AddOrUpdate(comparison, Yes);
        }

        return comparison;
    }

    /// <summary>Whether this comparison asks the unsigned question. Anything unmarked stays signed, as before.</summary>
    public static bool Asks(Instruction comparison) => Unsigned.TryGetValue(comparison, out _);
}
