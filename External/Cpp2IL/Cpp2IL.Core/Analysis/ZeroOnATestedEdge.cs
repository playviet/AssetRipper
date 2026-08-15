using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// A register the branch was taken on because it tested nought <em>is</em> nought on that edge.
/// </summary>
/// <remarks>
/// <para>
/// <c>s?.GetHashCode() ?? 0</c> compiles to a <c>CBZ</c> around the call and a join that reads the answer
/// register:
/// </para>
/// <code>
/// CBZ  X0, join     ; the string was null
/// LDR  X8, [X0]     ; otherwise call GetHashCode
/// BLR  X9
/// join: SUB W8, W0, W21
/// </code>
/// <para>
/// On the taken edge <c>X0</c> holds the null pointer, which the compiler is reusing as the integer nought -
/// they are the same word and it knows it. The phi at the join therefore merges a <b>reference</b> with the
/// call's <c>Int32</c>, which is a disagreement rather than a value: the phi takes the reference's type,
/// <c>SsaForm.CannotBeTheSameValue</c> then refuses the copy from the call as a register being reused, and
/// the subtraction is left reading the string. <c>UserSegmentationManager::ActiveHash</c> is that in full,
/// and it recovers as <c>((object)text)?.GetHashCode();</c> with the answer dropped.
/// </para>
/// <para>
/// <b>This is a fact about the edge, not a guess about the register.</b> The branch was taken <i>because</i>
/// the comparison found the value nought, so on that edge it is nought and nothing else. That is ordinary
/// conditional constant propagation, stated at the one place a value is attributed to an edge - the phi's
/// operand.
/// </para>
/// <para>
/// <b>The operand, not the copy.</b> Rewriting the copy that <c>SsaForm.Remove</c> writes was built first and
/// is worse than useless: by then the phi has already been <i>typed</i> from the reference, so the other
/// edge's copy is still refused and the join keeps only the nought - <c>ActiveHash</c> came out adding zero
/// every time round. Rewriting the operand instead runs before any of that, so the phi takes its type from
/// the edge that has one and both copies survive.
/// </para>
/// <para>
/// Only where the two successors differ, so which edge this is can be told at all; a block that is its own
/// two successors says nothing about either.
/// </para>
/// </remarks>
public static class ZeroOnATestedEdge
{
    public static void Seed(MethodAnalysisContext method)
    {
        if (method.ControlFlowGraph is not { } graph)
            return;

        foreach (var block in graph.Blocks)
        {
            foreach (var phi in block.Instructions)
            {
                if (phi.OpCode != OpCode.Phi)
                    continue;

                for (var edge = 0; edge < block.Predecessors.Count && 1 + edge < phi.Operands.Count; edge++)
                    if (IsNoughtOnThisEdge(block.Predecessors[edge], block, phi.Operands[1 + edge]))
                        phi.Operands[1 + edge] = 0;
            }
        }
    }

    /// <summary>Whether this edge is the one the branch takes on finding that value nought.</summary>
    private static bool IsNoughtOnThisEdge(Block predecessor, Block block, object source)
    {
        if (source is not LocalVariable tested || predecessor.Instructions.Count == 0
            || predecessor.Successors.Count != 2
            || ReferenceEquals(predecessor.Successors[0], predecessor.Successors[1]))
        {
            return false;
        }

        if (predecessor.Instructions[^1] is not { OpCode: OpCode.ConditionalJump, Operands: [Block taken, LocalVariable condition] })
            return false;

        //The comparison the branch is on, which is written in this block. Anything else writing the condition
        //refuses outright rather than being ignored: the branch is on that instruction's answer, so a second
        //one means this reading of the edge is not the whole story.
        var equal = false;
        var found = false;

        foreach (var instruction in predecessor.Instructions)
        {
            if (instruction.Operands.Count != 3 || instruction.Operands[0] is not LocalVariable answer
                || !ReferenceEquals(answer, condition))
            {
                continue;
            }

            if (instruction.OpCode is not (OpCode.CheckEqual or OpCode.CheckNotEqual))
                return false;

            //Either way round: the value against nought, or nought against the value.
            if (!(IsZero(instruction.Operands[2]) && ReferenceEquals(instruction.Operands[1], tested))
                && !(IsZero(instruction.Operands[1]) && ReferenceEquals(instruction.Operands[2], tested)))
            {
                return false;
            }

            equal = instruction.OpCode == OpCode.CheckEqual;
            found = true;
        }

        //`CheckEqual` sends the value's nought down the taken edge; `CheckNotEqual` sends it down the other.
        return found && ReferenceEquals(block, taken) == equal;
    }

    private static bool IsZero(object operand) => operand is long and 0 or int and 0 or ulong and 0 or uint and 0;
}
