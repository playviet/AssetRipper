using System;
using System.Collections.Generic;
using Disarm;
using LibCpp2IL;

namespace Cpp2IL.Core.Utils;

public static partial class NewArm64Utils
{
    /// <summary>
    /// Puts back an instruction the linker moved out of the method, in the place it was taken from.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Cortex-A53 erratum 843419 makes a load that follows an <c>adrp</c> at certain page offsets return the
    /// wrong address, and the fix lld applies is to move the load somewhere else: the load is replaced with a
    /// branch to a two-word island, and the island runs the load and branches straight back to the instruction
    /// after the one it replaced.
    /// </para>
    /// <code>
    /// 0x2355004  B   0x4AFB8B0      ;  was `ldr x9, [x9, #0x400]`
    ///            ...
    /// 0x4AFB8B0  LDR X9, [X9, 0x400]
    /// 0x4AFB8B4  B   0x2355008      ;  back to the next instruction
    /// </code>
    /// <para>
    /// Nothing downstream can know that: an unconditional branch out of a method is a tail call, so the method
    /// ended at the load, and everything after it was another function's problem. <c>CellData::ComputeCats</c>
    /// came out with a call to <c>0x4AFB8B0</c> carrying <c>x0..x7</c>, which then held every register it named
    /// live - three reads of the runtime generic context among them, each one an unmanaged-memory statement
    /// that <see cref="Analysis.DeadRgctxRead"/> could not drop because this call still claimed to read them.
    /// </para>
    /// <para>
    /// The island is always one instruction and a branch, so what goes back is one instruction for one and
    /// every address downstream stays where it was. It is disassembled <i>at the branch's own address</i> rather
    /// than moved, so anything reading the result gets the address the instruction is written down at; the
    /// displaced instruction is the load of an <c>adrp</c> pair and never PC-relative itself, so that costs
    /// nothing. An island of any other shape is left alone - there is nowhere to put a second instruction
    /// without moving everything after it.
    /// </para>
    /// <para>
    /// 115 islands in this binary, all of that shape, reached from 115 places.
    /// </para>
    /// </remarks>
    private static List<Arm64Instruction> PutDisplacedInstructionsBack(List<Arm64Instruction> body, Il2CppBinary binary)
    {
        if (body.Count == 0)
            return body;

        var start = body[0].Address;
        var end = body[^1].Address + 4;

        for (var i = 0; i < body.Count; i++)
        {
            var instruction = body[i];

            if (instruction.Mnemonic != Arm64Mnemonic.B
                || instruction.MnemonicConditionCode is not (Arm64ConditionCode.NONE or Arm64ConditionCode.AL))
                continue;

            var island = instruction.BranchTarget;

            //A branch inside the method is a jump, and one to a real function is a tail call. Only somewhere
            //that branches straight back here is a piece of this method.
            if (island >= start && island < end)
                continue;

            if (DisplacedAt(binary, island, instruction.Address) is { } displaced)
                body[i] = displaced;
        }

        return body;
    }

    /// <summary>
    /// The one instruction held at <paramref name="island"/>, if what is there is that instruction and a branch
    /// back to the one after <paramref name="branch"/>, and nothing else.
    /// </summary>
    private static Arm64Instruction? DisplacedAt(Il2CppBinary binary, ulong island, ulong branch)
    {
        int raw;

        try
        {
            raw = (int)binary.MapVirtualAddressToRaw(island);
        }
        catch
        {
            return null;
        }

        if (raw <= 0 || raw + 8 > binary.RawLength)
            return null;

        List<Arm64Instruction> pair;

        try
        {
            //Read at the island's own address first, so the branch back is worked out from where it is written.
            pair = Disassemble(binary.GetRawBinaryContent().Slice(raw, 8), island);
        }
        catch
        {
            return null;
        }

        if (pair.Count != 2
            || pair[1].Mnemonic != Arm64Mnemonic.B
            || pair[1].MnemonicConditionCode is not (Arm64ConditionCode.NONE or Arm64ConditionCode.AL)
            || pair[1].BranchTarget != branch + 4)
            return null;

        var displaced = pair[0];

        //Whatever was moved out was an ordinary instruction; anything that decides where to go next would be
        //something else entirely, and putting it back at another address would be wrong.
        if (displaced.Mnemonic is Arm64Mnemonic.INVALID or Arm64Mnemonic.UNIMPLEMENTED
            or Arm64Mnemonic.B or Arm64Mnemonic.BL or Arm64Mnemonic.BR or Arm64Mnemonic.BLR
            or Arm64Mnemonic.RET or Arm64Mnemonic.CBZ or Arm64Mnemonic.CBNZ
            or Arm64Mnemonic.TBZ or Arm64Mnemonic.TBNZ)
            return null;

        //And now at the address it is being put back at, which is what everything downstream indexes by.
        try
        {
            var one = Disassemble(binary.GetRawBinaryContent().Slice(raw, 4), branch);

            return one.Count == 1 ? one[0] : null;
        }
        catch
        {
            return null;
        }
    }
}
