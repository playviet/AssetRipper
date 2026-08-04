using System;
using System.Collections.Generic;
using System.Linq;
using Disarm;
using Cpp2IL.Core.Api;
using Cpp2IL.Core.Il2CppApiFunctions;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using Disarm.InternalDisassembly;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>
/// What this fork adds to the arm64 lifter: the operand shapes the architecture folds into an instruction -
/// a shifted register, a bitfield move, a stack slot's address - and the register naming the rest of the
/// lifting reads those through.
///
/// Kept apart from the file it belongs to so that the file stays as close to upstream as it can, and a later
/// version of Cpp2IL can be merged without the two sets of changes meeting.
/// </summary>
public partial class NewArmV8InstructionSet
{

    /// <summary>
    /// How far apart the two halves of a load or store pair are.
    ///
    /// The width has to come from the instruction rather than from the register the operand converted to:
    /// the zero register is written <c>wzr</c> or <c>xzr</c> for the same register number, and converting it
    /// gives back the 64-bit name whichever was written. A <c>stp wzr, w9, [x8, #0x18]</c> - which is how a
    /// list is cleared, count to zero and version up in one instruction - then wrote its second half eight
    /// bytes along instead of four, landing on the field after the one it meant.
    /// </summary>
    private static int PairElementSize(Arm64Register register)
        => register switch
        {
            >= Arm64Register.V0 and <= Arm64Register.V31 => 16,
            >= Arm64Register.D0 and <= Arm64Register.D31 => 8,
            >= Arm64Register.S0 and <= Arm64Register.S31 => 4,
            >= Arm64Register.H0 and <= Arm64Register.H31 => 2,
            >= Arm64Register.W0 and <= Arm64Register.W31 => 4,
            _ => 8,
        };

    /// <summary>
    /// A call through a register, handed every register an argument could have arrived in.
    /// </summary>
    /// <remarks>
    /// Nothing at the call site says what the callee takes, so aapcs64 decides: the first eight arguments that
    /// are integers or pointers travel in x0 to x7, and the first eight that are floating point travel in v0 to
    /// v7 - two independent runs, not one. Only the general purpose half was handed over, so a call whose
    /// signature turned out to have a <c>float</c> or a <c>double</c> in it could not be rebuilt: the value had
    /// never been mentioned at the call, so nothing carried it there and there was no operand to hand to the
    /// parameter. Every pass that recovers such a call had to refuse it, and the walk it was reached through
    /// stayed in the output.
    ///
    /// Both runs are handed over here, and <see cref="Cpp2IL.Core.Analysis.Aapcs64.ArgumentsOf"/> is what picks
    /// out of them once a pass knows the signature. Naming a register the callee does not read costs nothing:
    /// an operand nothing needs is dropped with the rest of the dead code.
    /// </remarks>
    private static object[] IndirectCallOperands(object target)
    {
        var operands = new List<object> { target, RegisterFor(Arm64Register.X0) };

        for (var argument = 0; argument < Aapcs64.RegistersPerRun; argument++)
            operands.Add(RegisterFor(Arm64Register.X0 + argument));

        for (var argument = 0; argument < Aapcs64.RegistersPerRun; argument++)
            operands.Add(RegisterFor(Arm64Register.V0 + argument));

        return operands.ToArray();
    }

    /// <summary>
    /// The operands of the call an inlined floating point library method came from, or null where the method
    /// it stands for is not in this game's libraries and there is therefore nothing to name.
    /// </summary>
    /// <remarks>
    /// The architecture has one instruction apiece for absolute value, square root and the two-sided minimum
    /// and maximum, so a call to the library method that does one of those is compiled away to it entirely.
    /// Nothing in the instruction names a method - and an instruction the lifter does not translate takes the
    /// whole statement holding it, so one <c>Mathf.Abs</c> in the middle of an expression cost the expression.
    ///
    /// <c>FMAXNM</c>/<c>FMINNM</c> rather than <c>FMAX</c>/<c>FMIN</c> is what a library minimum and maximum
    /// compile to: the NM pair returns the number when one side is a NaN, which is what the managed methods
    /// promise, and the plain pair returns the NaN, which is not the same method and is not translated here.
    /// </remarks>
    private object[]? MathCallOperands(MethodAnalysisContext context, Arm64Instruction instruction)
    {
        //Both names are offered where the two libraries spell one method differently.
        //`FRINTM`/`FRINTP` are deliberately absent: routed here they recovered 32 calls and cost 33 of the
        //decisions the measured methods still make, the same trade `CINC` was reverted for.
        var (names, parameters) = instruction.Mnemonic switch
        {
            Arm64Mnemonic.FABS => (new[] { "Abs" }, 1),
            Arm64Mnemonic.FSQRT => (new[] { "Sqrt" }, 1),
            Arm64Mnemonic.FMAXNM => (new[] { "Max" }, 2),
            Arm64Mnemonic.FMINNM => (new[] { "Min" }, 2),
            _ => (null!, 0),
        };

        //The architecture has a single-precision and a double-precision form of each of these, told apart by
        //the register the result goes to. Which library method was inlined follows from that and nothing else.
        var isDouble = instruction.Op0Reg is >= Arm64Register.D0 and <= Arm64Register.D31;

        if (names == null || MathIntrinsics.Resolve(context.AppContext, names, parameters, isDouble) is not { } method)
            return null;

        var operands = new List<object> { method, ConvertOperand(instruction, 0) };

        for (var argument = 1; argument <= parameters; argument++)
            operands.Add(ConvertOperand(instruction, argument));

        return operands.ToArray();
    }

    /// <summary>
    /// Adds the register a method's own runtime <c>MethodInfo</c> arrives in.
    ///
    /// il2cpp gives every managed method one as a last argument, and for a body shared between the
    /// instantiations of a generic it is the only thing telling them apart: the type arguments, the runtime
    /// class and the generic context all hang off it. Without it in the list the parameter is neither named
    /// nor typed, and every chain that starts there - a static field, a type, the base constructor of a
    /// generic type - reads as unmanaged memory.
    ///
    /// It goes in the first general purpose register the arguments did not take. Past the eighth there is no
    /// register left for it, so nothing is added rather than naming one that means something else.
    /// </summary>
    private void AddRuntimeMethodOperand(List<object> operands, MethodAnalysisContext context)
    {
        var used = context.IsStatic ? 0 : 1;

        foreach (var parameter in context.Parameters)
        {
            //A floating point argument travels in a vector register and takes none of these.
            if (parameter.ParameterType is { Namespace: nameof(System), Name: "Single" or "Double" })
                continue;

            used++;
        }

        if (used < 8)
            operands.Add(RegisterFor(Arm64Register.X0 + used));
    }

    /// <summary>
    /// The comparison an arm64 condition code makes, given the operands of the flag-setting instruction
    /// it follows. The unsigned conditions are mapped onto the signed comparisons, matching how the x86
    /// side already recovers them: ISIL has one set of relational opcodes, and treating cs/cc/hi/ls as
    /// their signed counterparts is the existing inaccuracy rather than a new one. Conditions that test
    /// the overflow flag have no relational meaning and are refused.
    /// </summary>
    private static bool TryGetRelationalOpCode(Arm64ConditionCode condition, out OpCode opCode)
    {
        opCode = condition switch
        {
            Arm64ConditionCode.EQ => OpCode.CheckEqual,
            Arm64ConditionCode.NE => OpCode.CheckNotEqual,
            Arm64ConditionCode.GE or Arm64ConditionCode.CS or Arm64ConditionCode.PL => OpCode.CheckGreaterOrEqual,
            Arm64ConditionCode.LT or Arm64ConditionCode.CC or Arm64ConditionCode.MI => OpCode.CheckLess,
            Arm64ConditionCode.GT or Arm64ConditionCode.HI => OpCode.CheckGreater,
            Arm64ConditionCode.LE or Arm64ConditionCode.LS => OpCode.CheckLessOrEqual,
            _ => OpCode.Invalid,
        };

        return opCode != OpCode.Invalid;
    }

    /// <summary>
    /// The name a register is known by. Arm64 gives one physical register several names according to
    /// the width in use: w1 is the low half of x1, and s0, d0 and q0 are all v0. Naming them apart made
    /// each width a variable of its own, so a value written as a 32-bit integer and read as a pointer -
    /// or an int parameter passed in w1 and looked for in x1 - was never connected to itself. Since ISIL
    /// carries no width, one name per physical register is both simpler and what the analysis needs.
    /// </summary>
    private static Register RegisterFor(Arm64Register register)
    {
        var number = register switch
        {
            >= Arm64Register.X0 and <= Arm64Register.X31 => register - Arm64Register.X0,
            >= Arm64Register.W0 and <= Arm64Register.W31 => register - Arm64Register.W0,
            _ => -1,
        };

        if (number >= 0)
            return new Register(null, "X" + number);

        var vector = register switch
        {
            >= Arm64Register.V0 and <= Arm64Register.V31 => register - Arm64Register.V0,
            >= Arm64Register.D0 and <= Arm64Register.D31 => register - Arm64Register.D0,
            >= Arm64Register.S0 and <= Arm64Register.S31 => register - Arm64Register.S0,
            >= Arm64Register.H0 and <= Arm64Register.H31 => register - Arm64Register.H0,
            >= Arm64Register.B0 and <= Arm64Register.B31 => register - Arm64Register.B0,
            _ => -1,
        };

        return vector >= 0 ? new Register(null, "V" + vector) : new Register(null, register.ToString().ToUpperInvariant());
    }

    /// <summary>
    /// Which 16 bit field of the register a move-keep writes, as a mask, given the value it writes. Only the
    /// four aligned positions exist, and the lowest one the value fits in is the one it came from.
    /// </summary>
    private static long? FieldMaskOf(long value)
    {
        for (var shift = 0; shift < 64; shift += 16)
        {
            var field = (long)((ulong)value >> shift);

            if ((field & ~0xFFFFL) == 0 && field << shift == value)
                return 0xFFFFL << shift;
        }

        return null;
    }

    /// <summary>
    /// The floating point literal at an address, where the register being loaded says how wide it is. Returns
    /// null for anything that is not a floating point load, or for an address the binary does not cover.
    /// </summary>
    private static object? ReadFloatConstant(MethodAnalysisContext context, Arm64Register destination, ulong address)
    {
        var isSingle = destination is >= Arm64Register.S0 and <= Arm64Register.S31;
        var isDouble = destination is >= Arm64Register.D0 and <= Arm64Register.D31;

        if (!isSingle && !isDouble)
            return null;

        var binary = context.AppContext.Binary;

        if (!binary.TryMapVirtualAddressToRaw(address, out var raw) || raw <= 0)
            return null;

        var content = binary.GetRawBinaryContent();
        var width = isSingle ? 4 : 8;

        if (raw + width > content.Length)
            return null;

        var bytes = content.Slice((int)raw, width);

        //Boxed apart, because a conditional whose arms are float and double is a double either way, and a float
        //argument handed over as a double is a type the method does not take.
        if (isSingle)
            return BitConverter.ToSingle(bytes);

        return BitConverter.ToDouble(bytes);
    }

    /// <summary>
    /// Which way a shifted register operand is shifted. The shifted register forms of add, subtract and the
    /// logical operations all keep it in bits 23 and 22, and the disassembler does not hand it over, so it is
    /// read from the encoding. A rotate has no counterpart here and is refused rather than approximated.
    /// </summary>
    /// <summary>
    /// The immediate of a bitwise instruction, decoded from the instruction rather than taken as given.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Arm64 does not carry a bitmask literally; it encodes it as a repeating run of ones - N, immr and imms -
    /// and the disassembler decodes that wrongly for the widest run. `and x9, x9, #0xffffffff`, which is how a
    /// 32-bit length is widened to a 64-bit loop bound, arrives as `and x9, x9, #0` - so the loop it guards is
    /// entered never, the method returns its initial value, and nothing anywhere says so. 34 such masks are
    /// live in the game's own assembly, five of them the condition of a loop, and another seven arrive as
    /// `0x1ffffffff`, which is not an encodable mask at all.
    /// </para>
    /// <para>
    /// The fault is a shift by 32 of a 32-bit one, which on this architecture is a shift by nothing:
    /// <c>(1 &lt;&lt; 32) - 1</c> is 0 rather than <c>0xffffffff</c>. Disarm is a package, so it is decoded here
    /// instead, following DecodeBitMasks from the architecture manual.
    /// </para>
    /// </remarks>
    private static long? LogicalImmediate(MethodAnalysisContext context, Arm64Instruction instruction)
    {
        var binary = context.AppContext.Binary;

        if (!binary.TryMapVirtualAddressToRaw(instruction.Address, out var raw) || raw <= 0)
            return null;

        var content = binary.GetRawBinaryContent();

        if (raw + 4 > content.Length)
            return null;

        var word = BitConverter.ToUInt32(content.Slice((int)raw, 4));

        //Only the logical-immediate encodings lay their operand out as N:immr:imms. Add and subtract with an
        //immediate reuse the same bits as sh:imm12, and reading those as a bitmask parses to something
        //plausible and wholly wrong - `subs x9, x9, #1`, which is every loop counter that counts down, came
        //out as `x9 - 0x300000003`. The opcode field is what tells them apart.
        if ((word >> 23 & 0x3F) != 0b100100)
            return null;

        var sixtyFourBit = (word >> 31 & 1) == 1;
        var n = (int)(word >> 22 & 1);
        var immr = (int)(word >> 16 & 0x3F);
        var imms = (int)(word >> 10 & 0x3F);

        //The size of the repeating run is the highest set bit of N followed by the complement of imms.
        var pattern = (n << 6) | (~imms & 0x3F);
        var length = 6;
        while (length >= 0 && (pattern >> length & 1) == 0)
            length--;

        if (length < 1 || (!sixtyFourBit && n == 1))
            return null;

        var size = 1 << length;
        var levels = size - 1;
        var ones = (imms & levels) + 1;

        //All ones is the encoding's one reserved case, not a mask.
        if (ones > levels)
            return null;

        //Shifting by the whole width is what goes wrong upstream, so the run is built without ever doing it.
        var element = ones >= 64 ? ulong.MaxValue : (1UL << ones) - 1;
        var rotate = immr & levels;

        if (rotate != 0 && size < 64)
            element = (element >> rotate | element << (size - rotate)) & (size >= 64 ? ulong.MaxValue : (1UL << size) - 1);
        else if (rotate != 0)
            element = element >> rotate | element << (64 - rotate);

        var mask = element;
        for (var filled = size; filled < 64; filled *= 2)
            mask |= mask << filled;

        //A 32-bit operation writes the low half and clears the high one, so the mask widens with zeroes.
        return sixtyFourBit ? unchecked((long)mask) : unchecked((long)(uint)mask);
    }

    /// <summary>
    /// The second operand of a bitwise instruction, with an immediate decoded properly.
    /// </summary>
    private static object BitwiseOperand(MethodAnalysisContext context, Arm64Instruction instruction, object given)
    {
        if (instruction.Op2Kind != Arm64OperandKind.Immediate)
            return given;

        return LogicalImmediate(context, instruction) ?? given;
    }

    private static OpCode? ShiftDirection(MethodAnalysisContext context, Arm64Instruction instruction)
    {
        if (instruction.FinalOpShiftType != Arm64ShiftType.NONE)
        {
            return instruction.FinalOpShiftType switch
            {
                Arm64ShiftType.LSL => OpCode.ShiftLeft,
                Arm64ShiftType.LSR or Arm64ShiftType.ASR => OpCode.ShiftRight,
                _ => null,
            };
        }

        var binary = context.AppContext.Binary;

        if (!binary.TryMapVirtualAddressToRaw(instruction.Address, out var raw) || raw <= 0)
            return null;

        var content = binary.GetRawBinaryContent();

        if (raw + 4 > content.Length)
            return null;

        return (BitConverter.ToUInt32(content.Slice((int)raw, 4)) >> 22 & 3) switch
        {
            0 => OpCode.ShiftLeft,
            1 or 2 => OpCode.ShiftRight,
            _ => null,
        };
    }

    // add Xd, sp, #imm - the address of a slot in the frame, not an arithmetic result.
    private static bool IsStackSlotAddress(Arm64Instruction instruction) =>
        instruction.Mnemonic == Arm64Mnemonic.ADD
        && instruction.Op0Kind == Arm64OperandKind.Register && instruction.Op0Reg != Arm64Register.X31
        && instruction.Op1Kind == Arm64OperandKind.Register && instruction.Op1Reg == Arm64Register.X31
        && instruction.Op2Kind == Arm64OperandKind.Immediate;

    /// <summary>
    /// A memory operand, keeping the register the architecture lets an address be indexed by.
    /// </summary>
    /// <remarks>
    /// `ldr w11, [x10, x9, lsl #2]` is how every loop over an array reads its element: the base is the
    /// elements of the array, and x9 is the subscript, scaled by the width of one. Only the base was kept, so
    /// the operand read element zero - and the recovery said so, in code that compiled, ran, and answered on
    /// the first element every time round the loop. `CountOf` returned nought for every input; `AllNone`
    /// answered about `cells[0]`. Six of the fifteen corpus methods that behaved differently were this.
    ///
    /// The amount is a shift for `lsl` and an extend amount for `uxtw`/`sxtw`, which is the same number: how
    /// far the subscript moves to become a byte offset. A sign-extending index is still the same index, since
    /// a subscript is never negative by the time it reaches here.
    /// </remarks>
    private static MemoryOperand MemoryOperandFor(Arm64Instruction instruction, Arm64Register baseRegister, long offset)
    {
        if (instruction.MemAddendReg == Arm64Register.INVALID)
            return new MemoryOperand(RegisterFor(baseRegister), addend: offset);

        var amount = instruction.MemExtendOrShiftAmount;

        return new MemoryOperand(
            RegisterFor(baseRegister),
            RegisterFor(instruction.MemAddendReg),
            offset,
            amount is >= 0 and <= 4 ? 1 << amount : 1);
    }

    // add sp, sp, #imm and sub sp, sp, #imm, which move the frame rather than computing anything.
    private static bool IsStackPointerAdjustment(Arm64Instruction instruction) =>
        instruction.Op0Kind == Arm64OperandKind.Register && instruction.Op0Reg == Arm64Register.X31
        && instruction.Op1Kind == Arm64OperandKind.Register && instruction.Op1Reg == Arm64Register.X31
        && instruction.Op2Kind == Arm64OperandKind.Immediate;

    /// <summary>
    /// The value a store writes. It sits in operand 0, where <see cref="ConvertOperand"/> assumes a
    /// destination, so the zero register has to be recognised here instead.
    /// </summary>
    private object ConvertStoredValue(Arm64Instruction instruction, int operand)
    {
        var converted = ConvertOperand(instruction, operand);
        return converted is Register { Name: "X31" } ? 0L : converted;
    }
}
