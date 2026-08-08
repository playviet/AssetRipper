using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Removes the stack protector the compiler wraps around a method that has an array on its frame.
/// </summary>
/// <remarks>
/// The prologue copies a cookie out of thread-local storage onto the frame and the epilogue checks it is
/// still there, jumping to <c>__stack_chk_fail</c> when it is not. On Android arm64 the cookie lives at
/// offset <c>0x28</c> of the thread pointer - bionic's <c>TLS_SLOT_STACK_GUARD</c>, slot five - so the test
/// reads exactly like this:
/// <code>
/// CheckNotEqual v256 (Boolean), [v7 @ X26+28], [v23 @ stackaddr_0-8]
/// ConditionalJump @b75, v256
/// </code>
/// None of it was written by anybody, and recovered C# has no stack to smash. The call it guards is already
/// dropped by <c>ImportedCall</c>, which leaves the block it jumps to empty - so what remains is a
/// comparison of two places the recovery cannot name, written out as two <c>Unmanaged memory load</c>
/// markers and an <c>if</c> around whatever followed.
///
/// Both halves of the comparison have to match, which is what makes this safe: a read at <c>+0x28</c> of
/// something on its own is an ordinary field, and a read of a stack slot on its own is an ordinary local.
/// Only the pair - a thread-local slot against a frame slot, deciding a branch into an emptied block - is
/// the protector.
/// </remarks>
public static class StackProtectorRemoval
{
    /// <summary>bionic's TLS_SLOT_STACK_GUARD: slot five of the thread pointer.</summary>
    private const int GuardSlot = 0x28;

    public static void Run(MethodAnalysisContext method)
    {
        var graph = method.ControlFlowGraph!;
        var definitions = Definitions(graph);
        var removedAny = false;

        foreach (var guard in graph.Blocks.ToList())
        {
            if (guard.BlockType != BlockType.TwoWay || guard.Successors.Count != 2)
                continue;

            var terminator = guard.Instructions.LastOrDefault(i => i.OpCode != OpCode.Nop);

            if (terminator is not { OpCode: OpCode.ConditionalJump, Operands: [_, LocalVariable condition] })
                continue;

            if (!definitions.TryGetValue(condition, out var test)
                || !IsCookieTest(test, definitions, out var jumpsWhenIntact))
                continue;

            var taken = terminator.Operands[0] switch
            {
                Block block => block,
                Instruction instruction => graph.FindBlockByInstruction(instruction),
                _ => null,
            };

            if (taken is null)
                continue;

            var fallThrough = guard.Successors.FirstOrDefault(s => !ReferenceEquals(s, taken));

            var onSmashed = jumpsWhenIntact ? fallThrough : taken;
            var otherwise = jumpsWhenIntact ? taken : fallThrough;

            //The failure side must be the emptied `__stack_chk_fail` block. Where it is not, this is some
            //other comparison that happens to read those two places and it is left alone.
            if (onSmashed is null || otherwise is null || ReferenceEquals(onSmashed, otherwise) || !IsEmptied(onSmashed))
                continue;

            terminator.OpCode = OpCode.Jump;
            terminator.Operands = [otherwise];
            test.OpCode = OpCode.Nop;
            test.Operands = [];

            guard.Successors.Remove(onSmashed);
            onSmashed.Predecessors.Remove(guard);
            guard.CalculateBlockType();
            removedAny = true;
        }

        if (removedAny)
            graph.RemoveUnreachableBlocks();

    }

    /// <summary>
    /// Whether the instruction compares the thread-local cookie against the copy on the frame, and which way
    /// it branches when the two still agree.
    /// </summary>
    private static bool IsCookieTest(Instruction test, Dictionary<LocalVariable, Instruction> definitions,
        out bool jumpsWhenIntact)
    {
        jumpsWhenIntact = test.OpCode == OpCode.CheckEqual;

        if (test.OpCode is not (OpCode.CheckEqual or OpCode.CheckNotEqual) || test.Operands.Count < 3)
            return false;

        if (Read(test.Operands[1], definitions) is not { } left || Read(test.Operands[2], definitions) is not { } right)
            return false;

        return (IsThreadCookie(left, definitions) && IsFrameSlot(right))
            || (IsThreadCookie(right, definitions) && IsFrameSlot(left));
    }

    /// <summary>
    /// The memory the operand reads: either written down in the comparison itself, or one copy away in the
    /// local that carries it. Which of the two it is depends on how far copy propagation has run, and this
    /// pass has to sit early enough that the branch it removes has not yet been folded into anything.
    /// </summary>
    private static MemoryOperand? Read(object operand, Dictionary<LocalVariable, Instruction> definitions)
    {
        for (var hop = 0; hop < 4; hop++)
        {
            if (operand is MemoryOperand memory)
                return memory;

            if (operand is not LocalVariable local
                || !definitions.TryGetValue(local, out var made)
                || made.OpCode != OpCode.Move
                || made.Operands.Count < 2)
                return null;

            operand = made.Operands[1];
        }

        return null;
    }

    /// <summary>
    /// A read at the guard slot of a register nothing in this method ever wrote. The thread pointer is set up
    /// before the lifted range, so a base with no definition here is the only shape it can have.
    /// </summary>
    private static bool IsThreadCookie(MemoryOperand operand, Dictionary<LocalVariable, Instruction> definitions) =>
        operand.Addend == GuardSlot
        && operand.Index is null
        && operand.Base is LocalVariable local
        && !IsFrameAddress(local)
        && !definitions.ContainsKey(local);

    private static bool IsFrameSlot(MemoryOperand operand) =>
        operand.Index is null && operand.Base is LocalVariable local && IsFrameAddress(local);

    /// <summary>The slot is named by the register the local sits in, not by the local's own name.</summary>
    private static bool IsFrameAddress(LocalVariable local) =>
        local.Register.Name.StartsWith(StackSlots.AddressPrefix);

    /// <summary>
    /// Whether the block is where a smashed stack ends up. Dropping <c>__stack_chk_fail</c> in the lifter
    /// leaves nothing but a jump; where the throw helper recognised it first, the block is a single
    /// <c>Throw</c> instead, and the compiler shares that block with every check in the method.
    /// </summary>
    private static bool IsEmptied(Block block)
    {
        var live = block.Instructions.Where(i => i.OpCode is not (OpCode.Nop or OpCode.Jump)).ToList();

        return live.Count == 0 || (live.Count == 1 && live[0].OpCode == OpCode.Throw);
    }

    /// <summary>Where each local is written. In SSA that is one place, which is what makes this exact.</summary>
    private static Dictionary<LocalVariable, Instruction> Definitions(ISILControlFlowGraph graph)
    {
        var definitions = new Dictionary<LocalVariable, Instruction>();

        foreach (var block in graph.Blocks)
            foreach (var instruction in block.Instructions)
                if (instruction.Destination is LocalVariable destination)
                    definitions[destination] = instruction;

        return definitions;
    }
}
