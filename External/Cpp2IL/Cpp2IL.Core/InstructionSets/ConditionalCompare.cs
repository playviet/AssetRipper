using System;
using System.Collections.Generic;
using Cpp2IL.Core.ISIL;
using Disarm;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>
/// Puts back the second half of a condition that the architecture makes without branching.
/// </summary>
/// <remarks>
/// <para>
/// <c>a &lt; b &amp;&amp; c &lt; d</c> does not have to become two branches. Arm64 has one instruction for it:
/// <c>ccmp</c> makes its comparison only when an earlier one came out a particular way, and when it did not it
/// simply asserts a set of flags instead. So the pair
/// </para>
/// <code>
/// cmp   w8, w9
/// ccmp  w10, w11, #0, lt      ; only compare if w8 &lt; w9; otherwise say "nothing is set"
/// b.lt  target
/// </code>
/// <para>
/// is <c>if (w8 &lt; w9 &amp;&amp; w10 &lt; w11)</c>, and with the <c>ccmp</c> unlifted the whole statement it
/// belonged to went with it - so the condition came back as half of itself, or not at all.
/// </para>
/// <para>
/// The recovery is exact rather than approximate, because <b>what a condition code makes of constant flags is
/// itself a constant</b>. When the guard does not hold the flags are the four-bit immediate the instruction
/// carries, and whether the reader of those flags is satisfied by them can be worked out here. If it is not,
/// the reader is only true when the guard held and the second comparison held - an <c>&amp;&amp;</c>. If it
/// is, the reader is true whenever the guard did not hold - an <c>||</c>. Nothing else can happen.
/// </para>
/// <para>
/// The guard is computed where the <c>ccmp</c> is, and folded in where the flags are next read - a branch, a
/// <c>cset</c> or a <c>csel</c>. Those are the three places <see cref="NewArmV8InstructionSet"/> turns held
/// comparison operands into a value, and each calls <see cref="Apply"/> on one line.
/// </para>
/// </remarks>
internal static class ConditionalCompare
{
    /// <summary>Holds "the earlier comparison came out the way this one needs".</summary>
    private const string GuardRegister = "CCGUARD";

    private static bool pending;
    private static long flagsWhenGuardFails;

    /// <summary>Forgotten between methods, exactly as the held comparison is.</summary>
    public static void Reset() => pending = false;

    /// <summary>
    /// Any ordinary comparison replaces the flags outright, so a guard that was never read is gone.
    /// </summary>
    public static void Clear() => pending = false;

    /// <summary>
    /// Turns the condition the <c>ccmp</c> is guarded by into a value, and says what it is now comparing.
    /// </summary>
    /// <remarks>
    /// The comparison operands handed in are the ones still held from the instruction that set the flags, so
    /// this has to run before they are replaced by this instruction's own. <c>ccmn</c> adds where <c>ccmp</c>
    /// subtracts, so what it really compares against is the negation of its second operand.
    /// </remarks>
    public static (object Left, object Right)? Guard(
        Arm64Instruction instruction, object heldLeft, object heldRight, object left, object right,
        Action<OpCode, object[]> emit)
    {
        if (!NewArmV8InstructionSet.TryGetRelationalOpCode(instruction.FinalOpConditionCode, out var relational))
            return null;

        var guard = new Register(null, GuardRegister);

        //A chain of them - `a && b && c` - is the same fold again: this one's guard is what the previous one
        //left, narrowed by the condition this one carries.
        if (pending)
        {
            var inner = new Register(null, "CCINNER");
            emit(relational, [inner, heldLeft, heldRight]);

            if (!Fold(instruction.FinalOpConditionCode, inner, guard, emit))
                return null;

            emit(OpCode.Move, [guard, inner]);
        }
        else
        {
            emit(relational, [guard, heldLeft, heldRight]);
        }

        if (instruction.Mnemonic == Arm64Mnemonic.CCMN && right is long negatable)
            right = -negatable;

        return (left, right);
    }

    /// <summary>Records that everything reading the flags from here on is guarded.</summary>
    public static void Pend(Arm64Instruction instruction)
    {
        pending = true;
        flagsWhenGuardFails = instruction.Op2Imm;
    }

    /// <summary>
    /// Folds the guard into a value just computed from the held comparison, if there is one to fold.
    /// </summary>
    /// <returns>
    /// False only when the guard cannot be folded, which leaves the caller holding a value that is right for
    /// one half of the condition and wrong for the other - it must put a marker there rather than keep it.
    /// </returns>
    public static bool Apply(Arm64ConditionCode condition, object result, Action<OpCode, object[]> emit)
    {
        if (!pending)
            return true;

        pending = false;
        return Fold(condition, result, new Register(null, GuardRegister), emit);
    }

    private static bool Fold(Arm64ConditionCode condition, object result, Register guard, Action<OpCode, object[]> emit)
    {
        if (Satisfies(condition, flagsWhenGuardFails) is not { } withoutTheGuard)
            return false;

        if (!withoutTheGuard)
        {
            //Only when the guard held and this held.
            emit(OpCode.And, [result, guard, result]);
            return true;
        }

        //Or else whenever the guard did not hold.
        var otherwise = new Register(null, "CCOTHERWISE");
        emit(OpCode.Not, [otherwise, guard]);
        emit(OpCode.Or, [result, otherwise, result]);
        return true;
    }

    /// <summary>
    /// Whether a condition code is satisfied by a fixed set of flags - the immediate a conditional compare
    /// asserts when its guard does not hold. N is bit 3, Z bit 2, C bit 1 and V bit 0.
    /// </summary>
    private static bool? Satisfies(Arm64ConditionCode condition, long nzcv)
    {
        var negative = (nzcv & 8) != 0;
        var zero = (nzcv & 4) != 0;
        var carry = (nzcv & 2) != 0;
        var overflow = (nzcv & 1) != 0;

        return condition switch
        {
            Arm64ConditionCode.EQ => zero,
            Arm64ConditionCode.NE => !zero,
            Arm64ConditionCode.CS => carry,
            Arm64ConditionCode.CC => !carry,
            Arm64ConditionCode.MI => negative,
            Arm64ConditionCode.PL => !negative,
            Arm64ConditionCode.VS => overflow,
            Arm64ConditionCode.VC => !overflow,
            Arm64ConditionCode.HI => carry && !zero,
            Arm64ConditionCode.LS => !carry || zero,
            Arm64ConditionCode.GE => negative == overflow,
            Arm64ConditionCode.LT => negative != overflow,
            Arm64ConditionCode.GT => !zero && negative == overflow,
            Arm64ConditionCode.LE => zero || negative != overflow,
            Arm64ConditionCode.AL or Arm64ConditionCode.NV => true,
            _ => null,
        };
    }
}
