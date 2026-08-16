using System.Collections.Generic;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// On the path where a type test succeeded, the value <b>is</b> what the test asked for.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ExactTypeTestRecovery"/> and <see cref="InlinedTypeTestRecovery"/> both put back the
/// <c>isinst</c> a comparison of class pointers was compiled from, and give its answer a local of its own.
/// Neither then says what that answer is <b>for</b>: the machine keeps the object in the same register
/// either way, so every statement below the branch still reads the tested value, at the type it had going
/// in.
/// </para>
/// <code>
/// Call is_inst, instance68 (String), v44 (Object), typeof(String)
/// CheckNotEqual answer, instance68, 0
/// ConditionalJump success, answer
/// …
/// Call String::ToUpperInvariant, …, v44          // <- the receiver is the OBJECT
/// </code>
/// <para>
/// While the tested value happens to be typed as the target this is invisible; the moment it is typed
/// honestly - <c>object o = flag ? (object)"text" : (object)7;</c> in <c>Corpus::AsOrNull</c> - the
/// statement stops compiling and the whole tail of the method is commented out. The two halves have to
/// land together, which is why this exists.
/// </para>
/// <para>
/// Only where the region is <b>entered nowhere else</b>: the success successor, and then any block every one
/// of whose predecessors is already in the region. That is dominance, computed as it is needed rather than
/// out of <c>DominatorInfo</c>, which was built before the guard remover changed the edges. A block reachable
/// on both paths is left alone, so nothing is renamed where the test may not have succeeded.
/// </para>
/// <para>Set <c>TYPETESTNARROW_OFF=1</c> to measure the same build without it.</para>
/// </remarks>
public static class TypeTestNarrowing
{
    private static readonly bool Off = System.Environment.GetEnvironmentVariable("TYPETESTNARROW_OFF") == "1";

    /// <summary>
    /// Reads the tested value as the narrowed one wherever the test having succeeded is certain.
    /// </summary>
    /// <param name="answer">The local the branch reads, true exactly when the object is an instance.</param>
    public static void Run(ISILControlFlowGraph graph, Block from, LocalVariable answer,
        object tested, LocalVariable value)
    {
        if (Off || SuccessOf(from, answer) is not { } success)
            return;

        var region = new HashSet<Block> { success };

        //Grown to a fixpoint: a block belongs to the region when control cannot reach it except through one
        //that already does. The branch's own block is never in it.
        for (var settling = true; settling;)
        {
            settling = false;

            foreach (var block in graph.Blocks)
            {
                if (region.Contains(block) || block.Predecessors.Count == 0 || ReferenceEquals(block, from))
                    continue;

                var entered = true;

                foreach (var predecessor in block.Predecessors)
                    entered &= region.Contains(predecessor);

                if (entered)
                    settling |= region.Add(block);
            }
        }

        foreach (var block in region)
            foreach (var instruction in block.Instructions)
                for (var operand = instruction.Destination is null ? 0 : 1; operand < instruction.Operands.Count; operand++)
                    if (ReferenceEquals(instruction.Operands[operand], tested))
                        instruction.Operands[operand] = value;
    }

    /// <summary>
    /// The successor control takes when the test succeeded, where the branch says so plainly.
    /// </summary>
    /// <remarks>
    /// The answer is true exactly when the object is an instance - both callers emit the check that way
    /// round - so the block the conditional jump names is the success path. Anything else in front of the
    /// branch, or a branch on something else, and nothing is claimed.
    /// </remarks>
    private static Block? SuccessOf(Block from, LocalVariable answer)
    {
        if (from.Instructions.Count == 0 || from.Successors.Count != 2)
            return null;

        var branch = from.Instructions[^1];

        if (branch is not { OpCode: OpCode.ConditionalJump, Operands: [Block taken, LocalVariable read] }
            || !ReferenceEquals(read, answer)
            || ReferenceEquals(from.Successors[0], from.Successors[1]))
        {
            return null;
        }

        return ReferenceEquals(taken, from.Successors[0]) || ReferenceEquals(taken, from.Successors[1])
            ? taken
            : null;
    }
}
