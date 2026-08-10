using System.Collections.Generic;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Sends the answer of a libm function that writes through a pointer to the slot that pointer names.
/// </summary>
/// <remarks>
/// <para>
/// <c>sincosf</c> takes its angle in a register and hands both answers back through pointers, so a caller
/// gives it two addresses in its own frame and reads them afterwards:
/// </para>
/// <code>
/// ADD  X0, X31, 0x5C     ; &amp;sin
/// ADD  X1, X31, 0x58     ; &amp;cos
/// BL   sincosf
/// LDP  S2, S0, [X31 + 0x58]
/// </code>
/// <para>
/// <see cref="InstructionSets.NewArmV8InstructionSet"/> translates the one call into <c>Mathf.Sin</c> and
/// <c>Mathf.Cos</c>, and writes each answer into the register that held its pointer - <c>x0</c> and
/// <c>x1</c>. But the caller does not read <c>x0</c>; it reads <b>the slot</b>, off the stack pointer, and the
/// two never meet. The slots hold nothing, so every statement computed from them goes, and in
/// <c>PositionByAngle</c> that is the entire body below the call.
/// </para>
/// <para>
/// The pointer is a frame offset written down three instructions earlier, so which slot it names is not an
/// inference. Writing the answer there is what the function does. <b>55 call sites</b> in this game, 22 of
/// them in bodies that are not also waiting on a struct return.
/// </para>
/// <para>
/// Runs before <see cref="OutParameterWriteback"/>, and has to: that pass aliases an address register onto
/// the slot and takes the <c>Move</c> away, after which nothing records that <c>x0</c> was ever that slot.
/// It also renames these slots to their address form, which is what the reads use - so the destination
/// written here is the plain value form and is renamed with everything else.
/// </para>
/// </remarks>
public static class OutPointerSlotResult
{
    /// <summary>
    /// The libm functions the lifter turns into a managed call whose answer goes to a pointer register.
    /// </summary>
    /// <remarks>
    /// A real call to any of these returns a float, which aapcs64 puts in <c>v0</c> - so a destination of
    /// <c>x0</c> or <c>x1</c> is only ever the synthesised form, and no genuine call is caught by this.
    /// </remarks>
    private static readonly HashSet<string> ThroughAPointer = ["Sin", "Cos", "Truncate"];

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Instruction, object> Answered = new();

    private static readonly object Yes = new();

    /// <summary>
    /// Whether this call's result operand is a slot this pass sent an answer to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="OutParameterWriteback"/> runs immediately below and renames every <c>stack_N</c> operand to
    /// <c>stackaddr_N</c> where that slot's address was taken - which is always true here, since handing the
    /// address over is how the callee was given somewhere to write. That renames the answer straight back,
    /// nothing then reads the slot, <c>RemoveUnused</c> drops it, and whatever wanted the value reads through
    /// the address instead and gets unmanaged memory. <c>SubCellVisual::UpdateFaceTracking</c> lost its
    /// <c>Mathf.Sin</c> exactly so.
    /// </para>
    /// <para>
    /// Refusing the rename for *every* destination is what suggests itself and it is wrong: the writeback
    /// pass renames destinations on purpose, and doing that cost five of the corpus's forty-three methods -
    /// `Steps`, `SumSteps`, `Format`, `Words` and `Boxed`, every one of them an `out` parameter. So the
    /// exception is exactly this pass's own rewrite and nothing else.
    /// </para>
    /// </remarks>
    internal static bool IsTheAnswer(Instruction instruction) => Answered.TryGetValue(instruction, out _);

    public static void Run(MethodAnalysisContext method)
    {
        if (method.ControlFlowGraph is not { } graph)
            return;

        //Straight down the instructions rather than block by block. One `bl sincosf` becomes *two* calls, and
        //a call ends a basic block - so the second of the pair is already in the next block and a map kept per
        //block is empty by the time it is reached, which is exactly how `Cos` was missed while `Sin` landed.
        var pointing = new Dictionary<string, string>();

        foreach (var instruction in graph.Instructions)
        {
            if (instruction is { OpCode: OpCode.Move, Operands: [Register into, Register from] })
            {
                if (from.Name.StartsWith(StackSlots.AddressPrefix))
                    pointing[into.Name] = from.Name[StackSlots.AddressPrefix.Length..];
                else
                    pointing.Remove(into.Name);

                continue;
            }

            if (instruction is { OpCode: OpCode.Call or OpCode.CallVoid, Operands: [MethodAnalysisContext callee, ..] }
                && ThroughAPointer.Contains(callee.Name))
            {
                //A pointer is used once. Taking it out keeps a later call with the same destination register
                //from being sent to a slot whose address was handed over long ago.
                if (instruction.Operands is [_, Register answer, ..] && pointing.Remove(answer.Name, out var slot))
                {
                    instruction.Operands[1] = new Register(null, StackSlots.ValuePrefix + slot);
                    Answered.AddOrUpdate(instruction, Yes);
                }

                continue;
            }

            //Any other call destroys the caller-saved registers, so nothing held in one still points anywhere.
            //This is what makes reading straight through the instructions safe without the blocks: a pointer
            //cannot survive a call, and the arithmetic that made it is always a few instructions above.
            if (instruction.IsCall)
            {
                pointing.Clear();
                continue;
            }

            //Anything else that writes a register makes what it used to point at no longer true.
            if (instruction.Operands.Count > 0 && instruction.Operands[0] is Register written)
                pointing.Remove(written.Name);
        }
    }
}
