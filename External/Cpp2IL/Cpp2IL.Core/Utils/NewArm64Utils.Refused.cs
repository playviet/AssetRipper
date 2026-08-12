using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Disarm;
using Disarm.InternalDisassembly;

namespace Cpp2IL.Core.Utils;

public static partial class NewArm64Utils
{
    /// <summary>
    /// Decodes the words the disassembler refuses, for the encodings that are unambiguous.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Disarm reports an instruction it cannot decode as <see cref="Arm64Mnemonic.INVALID"/> <b>with address
    /// zero</b>, and that second part is what does the damage. Every address in the method is recorded as the
    /// ISIL is built, and a branch is matched to its target by looking that address up; an instruction that
    /// arrives with no address of its own is therefore not merely untranslated, it is <i>not there</i>, and any
    /// branch to it becomes <c>Invalid "Jump target not found in method"</c> - which takes the branch, its
    /// block, and everything the block decided, out of the method.
    /// </para>
    /// <para>
    /// Across this binary: <b>5240 refused words in 973 methods</b>, and <b>94 methods have one that is
    /// branched to</b>. Most of the words are Advanced SIMD, which is a project of its own. Three are not,
    /// and between them they hold 95 of the branch targets:
    /// </para>
    /// <list type="table">
    /// <item><description><c>FCVT Sd, Dn</c> - 393 sites, 80 of them branched to. Disarm decodes the
    /// single-to-double direction and refuses double-to-single.</description></item>
    /// <item><description><c>LDRSB</c> with an unsigned immediate - 79 sites, 14 branched to.</description></item>
    /// <item><description><c>UMULH</c>/<c>SMULH</c> - 26 sites, 1 branched to. The high half of a
    /// multiplication, which is how every division by a constant is done.</description></item>
    /// </list>
    /// <para>
    /// All three already have a case in the lifter (or, for the multiply-high, an honest marker), so nothing
    /// downstream needs to change: the whole of the fix is handing the instruction back its name, its operands
    /// and - above all - its address.
    /// </para>
    /// <para>
    /// This only ever <i>replaces</i> an instruction the disassembler gave up on, so a word it does decode is
    /// never second-guessed, and a shape not listed here is left exactly as it was.
    /// </para>
    /// </remarks>
    private static List<Arm64Instruction> DecodeWhatDisarmRefused(List<Arm64Instruction> body, ReadOnlySpan<byte> bytes, ulong virtAddress)
    {
        for (var i = 0; i < body.Count; i++)
        {
            if (body[i].Mnemonic is not (Arm64Mnemonic.INVALID or Arm64Mnemonic.UNIMPLEMENTED))
                continue;

            //Every arm64 instruction is four bytes and the list comes back in order, so the word for entry i
            //is at that distance into the same span the whole body was read from.
            var at = 4 * i;
            if (at + 4 > bytes.Length)
                break;

            var word = BitConverter.ToUInt32(bytes.Slice(at, 4));
            var address = virtAddress + (ulong)at;

            if (ConvertPrecision(word, address) is { } converted)
                body[i] = converted;
            else if (LoadSignedByte(word, address) is { } loaded)
                body[i] = loaded;
            else if (MultiplyHigh(word, address) is { } multiplied)
                body[i] = multiplied;
        }

        return body;
    }

    /// <summary>
    /// <c>FCVT</c>: floating-point data-processing, one source, whose opcode selects a destination precision
    /// different from the source's.
    /// </summary>
    /// <remarks>
    /// The encoding is <c>0001 1110 0 type 1 opcode 10000 Rn Rd</c>, with <c>type</c> naming the source
    /// precision and the low two bits of a <c>0001xx</c> opcode naming the destination: 00 single, 01 double,
    /// 11 half. The two cannot be equal - that encoding is unallocated - and the register the value lands in
    /// is what the lifter reads to decide which way the conversion goes.
    /// </remarks>
    private static Arm64Instruction? ConvertPrecision(uint word, ulong address)
    {
        if ((word & 0xFF207C00) != 0x1E204000)
            return null;

        var opcode = (word >> 15) & 0x3F;
        if ((opcode & 0x3C) != 0x04)
            return null;

        var from = PrecisionRegister((word >> 22) & 3);
        var to = PrecisionRegister(opcode & 3);

        if (from is null || to is null || from == to)
            return null;

        return Build(new()
        {
            ["Address"] = address,
            ["Mnemonic"] = Arm64Mnemonic.FCVT,
            ["Op0Kind"] = Arm64OperandKind.Register,
            ["Op0Reg"] = to.Value + (int)(word & 31),
            ["Op1Kind"] = Arm64OperandKind.Register,
            ["Op1Reg"] = from.Value + (int)((word >> 5) & 31),
        });
    }

    /// <summary>The first register of the bank a floating-point precision code names.</summary>
    private static Arm64Register? PrecisionRegister(uint precision) => precision switch
    {
        0 => Arm64Register.S0,
        1 => Arm64Register.D0,
        3 => Arm64Register.H0,
        _ => null,
    };

    /// <summary>
    /// <c>LDRSB</c> with an unsigned immediate offset: a byte load that sign-extends into a word or a
    /// doubleword.
    /// </summary>
    /// <remarks>
    /// <c>size 111 V 01 opc imm12 Rn Rt</c> with <c>size</c> 00, <c>V</c> 0 and <c>opc</c> 1x. The offset is
    /// scaled by the access size, which for a byte is one, so the immediate is the distance as written. The
    /// lifter already treats every narrow load as "what is at the address" - ISIL carries no width - so the
    /// only thing that matters here is that it arrives at all.
    /// </remarks>
    private static Arm64Instruction? LoadSignedByte(uint word, ulong address)
    {
        if ((word & 0xFF800000) != 0x39800000)
            return null;

        //opc 10 sign-extends to 64 bits and 11 to 32, so the destination is X or W accordingly.
        var wide = ((word >> 22) & 1) == 0;

        return Build(new()
        {
            ["Address"] = address,
            ["Mnemonic"] = Arm64Mnemonic.LDRSB,
            ["Op0Kind"] = Arm64OperandKind.Register,
            ["Op0Reg"] = (wide ? Arm64Register.X0 : Arm64Register.W0) + (int)(word & 31),
            ["Op1Kind"] = Arm64OperandKind.Memory,
            ["MemBase"] = Arm64Register.X0 + (int)((word >> 5) & 31),
            ["MemOffset"] = (long)((word >> 10) & 0xFFF),
            ["MemAddendReg"] = Arm64Register.INVALID,
        });
    }

    /// <summary>
    /// <c>UMULH</c> and <c>SMULH</c>: the high half of a 64x64 multiplication.
    /// </summary>
    /// <remarks>
    /// <c>1001 1011 1 U 10 Rm 0 11111 Rn Rd</c>, the <c>Ra</c> field pinned to the zero register because there
    /// is nothing to accumulate. This is the second half of every division by a constant, so it turns up in
    /// date arithmetic and in anything that takes a remainder. The lifter has no case for it and will say so;
    /// that is an honest marker where today the branch to it disappears silently.
    /// </remarks>
    private static Arm64Instruction? MultiplyHigh(uint word, ulong address)
    {
        if ((word & 0xFF608000) != 0x9B400000 || ((word >> 10) & 31) != 31)
            return null;

        return Build(new()
        {
            ["Address"] = address,
            ["Mnemonic"] = ((word >> 23) & 1) == 1 ? Arm64Mnemonic.UMULH : Arm64Mnemonic.SMULH,
            ["Op0Kind"] = Arm64OperandKind.Register,
            ["Op0Reg"] = Arm64Register.X0 + (int)(word & 31),
            ["Op1Kind"] = Arm64OperandKind.Register,
            ["Op1Reg"] = Arm64Register.X0 + (int)((word >> 5) & 31),
            ["Op2Kind"] = Arm64OperandKind.Register,
            ["Op2Reg"] = Arm64Register.X0 + (int)((word >> 16) & 31),
        });
    }

    /// <summary>An <see cref="Arm64Instruction"/> with those members set, by the fields behind them.</summary>
    /// <remarks>
    /// Every member of the type is an auto-property whose setter is <c>internal</c> to Disarm, so an instruction
    /// cannot be written from outside the assembly at all. Going to the backing fields is the only way to say
    /// what a refused word was, short of vendoring the disassembler - and the alternative is not a worse
    /// decoding but no instruction and no address, which is what loses the branch.
    /// </remarks>
    private static Arm64Instruction? Build(Dictionary<string, object> members)
    {
        object boxed = default(Arm64Instruction);

        //Zero is a real member of every one of these enums and never the absent one: `Arm64ConditionCode`
        //has EQ at zero and NONE elsewhere, `Arm64ExtendType` has UXTB, `Arm64Register` has X0. A default
        //instruction therefore reads as a conditional one with an extend applied to a register operand it
        //does not have - the first attempt printed `FCVT.EQ S1, D0, UXTB` - so what "nothing here" means has
        //to be asked of each enum by name rather than assumed.
        foreach (var field in typeof(Arm64Instruction).GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
        {
            if (!field.FieldType.IsEnum)
                continue;

            var names = Enum.GetNames(field.FieldType);

            //The index mode has no absent member - a load either writes its base back before the access, after
            //it, or not at all - so what is wanted is the third one, whichever it is called. Left at zero it
            //said pre-indexed, and `LDRSB W19, [X0]!` then had the lifter emit a writeback that is not there.
            var absent = names.FirstOrDefault(n => n is "NONE" or "None" or "INVALID" or "Invalid")
                         ?? (names.Any(n => n.Contains("PreIndex"))
                             ? names.FirstOrDefault(n => !n.Contains("PreIndex") && !n.Contains("PostIndex"))
                             : null);

            if (absent is not null)
                field.SetValue(boxed, Enum.Parse(field.FieldType, absent));
        }

        foreach (var member in members)
        {
            var field = typeof(Arm64Instruction).GetField($"<{member.Key}>k__BackingField",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

            //A Disarm that renamed or dropped one of these is a Disarm this cannot write for, and leaving the
            //word refused is exactly what happens today.
            if (field is null)
                return null;

            field.SetValue(boxed, member.Value);
        }

        return (Arm64Instruction)boxed;
    }
}
