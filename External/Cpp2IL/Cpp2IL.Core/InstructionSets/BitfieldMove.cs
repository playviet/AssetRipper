using System;
using Cpp2IL.Core.ISIL;
using Disarm;
using Disarm.InternalDisassembly;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>
/// The unsigned bitfield move, which is a shift in either direction depending on which way round its two
/// immediates are.
/// </summary>
/// <remarks>
/// <para>
/// <c>UBFM</c> is the encoding behind <c>lsr</c>, <c>lsl</c>, <c>ubfx</c> and <c>ubfiz</c>, and it means two
/// different things:
/// </para>
/// <code>
/// imms >= immr :  dest = (src >> immr) &amp; ones(imms - immr + 1)      ; an extract - lsr, ubfx
/// imms &lt;  immr :  dest = (src &amp; ones(imms + 1)) &lt;&lt; (width - immr)   ; an insert  - lsl, ubfiz
/// </code>
/// <para>
/// Only the first was translated, and with the wrong number of bits: <c>ones(imms)</c> rather than
/// <c>ones(imms - immr + 1)</c>. So a left shift came out as a right shift by the complement of itself.
/// <c>h * 31</c>, which the compiler writes as <c>(h &lt;&lt; 5) - h</c>, was recovered as
/// <c>((h &gt;&gt; 27) &amp; 0x3FFFFFF) - h</c> - which compiles, runs, and answers something else. Of the 112
/// <c>UBFM</c> in this game's own assembly <b>38 are the left shift</b>, and the mask is too wide on most of
/// the rest; where <c>imms</c> reaches 31 the mask was being computed by shifting a 32 bit <c>1</c> that far,
/// which is not the number intended either.
/// </para>
/// <para>
/// Most <c>lsl</c> never arrives here - the disassembler resolves the alias itself 661 times against these
/// 112 - which is why this survived: the common case was right, and the shape that was wrong was rare enough
/// to look like ordinary noise in a body that had other problems.
/// </para>
/// <para>
/// The mask is dropped where it cannot change the answer - bits the shift is about to discard, or bits already
/// zero above a shifted value - so the ordinary <c>lsr</c> and <c>lsl</c> come out as one operation rather
/// than two. That is not only tidier: an <c>and</c> against a full-width constant is a value the decompiler
/// has to keep, and keeping it turns a shift into an expression.
/// </para>
/// </remarks>
public static class BitfieldMove
{
    /// <summary>
    /// Emits the shift this bitfield move is, and says whether it was able to.
    /// </summary>
    /// <remarks>
    /// False where either immediate is not one - nothing can be said about the widths then, and the caller's
    /// own translation stands.
    /// </remarks>
    public static bool Apply(Arm64Instruction instruction, object destination, object source, Func<OpCode, object[], Instruction?> emit)
    {
        if (instruction.Op2Kind != Arm64OperandKind.Immediate || instruction.Op3Kind != Arm64OperandKind.Immediate
            || instruction.Op0Kind != Arm64OperandKind.Register)
            return false;

        var width = instruction.Op0Reg is >= Arm64Register.W0 and <= Arm64Register.W31 ? 32 : 64;
        var immr = (int)instruction.Op2Imm;
        var imms = (int)instruction.Op3Imm;

        if (immr < 0 || imms < 0 || immr >= width || imms >= width)
            return false;

        if (imms >= immr)
        {
            //An extract. The value moves down by immr, and what is kept is the imms - immr + 1 bits that
            //were between them.
            var kept = imms - immr + 1;

            if (immr == 0)
                emit(OpCode.Move, [destination, source]);
            else if (instruction.Mnemonic == Arm64Mnemonic.UBFM)
                //Unsigned: the bits above what is kept are zeroes, not copies of the sign.
                Cpp2IL.Core.Analysis.LogicalShift.Mark(emit(OpCode.ShiftRight, [destination, source, immr]));
            else
                emit(OpCode.ShiftRight, [destination, source, immr]);

            //Above a value shifted down by immr there are already immr zeroes, so a mask that reaches at
            //least that far is asking for bits that cannot be set.
            if (kept < width - immr)
                emit(OpCode.And, [destination, destination, Ones(kept, width)]);

            return true;
        }

        //An insert: the low imms + 1 bits move up to sit at width - immr.
        var shift = width - immr;
        var taken = imms + 1;

        //And the mask goes when everything it would clear is shifted out anyway, which is the plain `lsl`.
        if (taken + shift >= width)
        {
            emit(OpCode.ShiftLeft, [destination, source, shift]);
            return true;
        }

        emit(OpCode.And, [destination, source, Ones(taken, width)]);
        emit(OpCode.ShiftLeft, [destination, destination, shift]);

        return true;
    }

    /// <summary>
    /// A run of set bits, as the width of the register it is going into - an <c>int</c> mask against a
    /// sixty-four bit value sign-extends and clears the whole top half.
    /// </summary>
    private static object Ones(int bits, int width)
    {
        var mask = bits >= 64 ? -1L : (1L << bits) - 1;

        return width == 32 ? unchecked((int)mask) : mask;
    }
}
