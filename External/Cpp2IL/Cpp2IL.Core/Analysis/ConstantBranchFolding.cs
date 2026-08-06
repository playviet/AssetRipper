using System.Linq;
using System.Runtime.CompilerServices;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Settles a branch whose condition is a comparison between two constants, and detaches the arm it can never
/// take.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="RuntimeClassReadRemover"/> answers the bit a class carries saying it has been prepared, because
/// by the time recovered code could run the answer is always yes. That turns the test into <c>1 != 0</c> and
/// makes the arm that would have prepared the type unreachable - and its own comment says the collection
/// afterwards removes it. Nothing did: <see cref="UnreachableBlockRemover"/> decides by who points at a block,
/// and a conditional jump points at both of its arms whatever its condition says. So the arm survived, with
/// il2cpp's initialisation call inside it, and the call is not something the language can write:
/// </para>
/// <code>
/// if (1 == 0)
/// {
///     //AssetRipper: commented out, this could not be kept as code.
///     //((Dictionary&lt;ECellColor, int&gt;)(nint)intPtr14).TryGetValue((ECellColor)num39, out var _);
/// }
/// </code>
/// <para>
/// One of those per type a method touches. They are not lost decisions - the branch is boilerplate and the
/// answer is genuinely constant - but each is a statement in the recovered body that the reader has to know to
/// ignore, and each is what keeps a method from scoring as recovered whole.
/// </para>
/// <para>
/// Runs immediately before <see cref="UnreachableBlockRemover"/>, which is what then collects what this
/// detaches, and well before <see cref="ConditionSinking"/>, which folds a condition into the branch that
/// reads it and would leave nothing here to read.
/// </para>
/// </remarks>
public static class ConstantBranchFolding
{
    /// <summary>
    /// The comparisons a pass settled the answer to itself, and the only ones this will act on.
    /// </summary>
    /// <remarks>
    /// A comparison between two constants is not on its own a comparison that was always going to go one way.
    /// It is just as often what a read that resolved to nothing leaves behind - <c>if (0L == 0L) return;</c>
    /// standing where a real test was - and folding one of those deletes the rest of the method as
    /// unreachable. <c>SubCellVisual::UpdateEyeTracking</c> lost nine of its thirteen decisions that way,
    /// which is how this came to be narrowed. Only a pass that put the constant there knows the answer is
    /// genuinely settled, so only a pass that put it there may say so.
    /// </remarks>
    private static readonly ConditionalWeakTable<Instruction, object> Settled = new();

    private static readonly object Yes = new();

    /// <summary>Records that this comparison's answer was decided by analysis rather than found in the code.</summary>
    public static void HasSettledAnswer(Instruction comparison) => Settled.AddOrUpdate(comparison, Yes);

    public static bool Run(MethodAnalysisContext method)
    {
        if (method.ControlFlowGraph is not { } graph)
            return false;

        var changed = false;

        foreach (var block in graph.Blocks.ToList())
        {
            if (block.Instructions.Count == 0)
                continue;

            var terminator = block.Instructions[^1];

            if (terminator.OpCode != OpCode.ConditionalJump
                || terminator.Operands.Count < 2
                || terminator.Operands[0] is not Block target
                || terminator.Operands[1] is not LocalVariable condition)
                continue;

            //In single assignment form there is one definition; out of it there can be several, so the one
            //that decides the branch is the last before it.
            var definition = block.Instructions.LastOrDefault(i => ReferenceEquals(i.Destination, condition));

            if (definition is null || !Settled.TryGetValue(definition, out _) || Evaluate(definition) is not { } taken)
                continue;

            //A branch is between the block it names and the one it falls into, and which is which is the
            //order they are recorded in. Both being the same block is a shape that has happened, and there
            //the branch is already unconditional whichever way it is read.
            var fallthrough = block.Successors.FirstOrDefault(s => !ReferenceEquals(s, target));
            var keep = taken ? target : fallthrough;
            var drop = taken ? fallthrough : target;

            if (keep is null)
                continue;

            //Only once the values a join was merging have been written out, which by here they have. Left in,
            //an arm taken away underneath a phi would leave the slot standing for it holding nothing.
            if (drop is not null && drop.Instructions.Any(i => i.OpCode == OpCode.Phi))
                continue;

            if (keep.Instructions.Any(i => i.OpCode == OpCode.Phi))
                continue;

            terminator.OpCode = OpCode.Jump;
            terminator.Operands = [keep];

            if (drop is not null && !ReferenceEquals(drop, keep))
            {
                block.Successors.Remove(drop);
                drop.Predecessors.Remove(block);
            }

            block.CalculateBlockType();
            changed = true;
        }

        return changed;
    }

    /// <summary>Which way a comparison between two constants goes, or nothing if it is not one.</summary>
    private static bool? Evaluate(Instruction comparison)
    {
        if (comparison.Operands.Count < 3)
            return null;

        //A float and an integer are compared differently, and a comparison of two constants of different
        //kinds is not something this is here for.
        if (Real(comparison.Operands[1]) is { } leftReal && Real(comparison.Operands[2]) is { } rightReal)
        {
            return comparison.OpCode switch
            {
                OpCode.CheckEqual => leftReal == rightReal,
                OpCode.CheckNotEqual => leftReal != rightReal,
                OpCode.CheckGreater => leftReal > rightReal,
                OpCode.CheckGreaterOrEqual => leftReal >= rightReal,
                OpCode.CheckLess => leftReal < rightReal,
                OpCode.CheckLessOrEqual => leftReal <= rightReal,
                _ => null,
            };
        }

        if (Whole(comparison.Operands[1]) is not { } left || Whole(comparison.Operands[2]) is not { } right)
            return null;

        return comparison.OpCode switch
        {
            OpCode.CheckEqual => left == right,
            OpCode.CheckNotEqual => left != right,
            OpCode.CheckGreater => left > right,
            OpCode.CheckGreaterOrEqual => left >= right,
            OpCode.CheckLess => left < right,
            OpCode.CheckLessOrEqual => left <= right,
            _ => null,
        };
    }

    private static long? Whole(object operand) => operand switch
    {
        long v => v,
        ulong v => unchecked((long)v),
        int v => v,
        uint v => v,
        short v => v,
        ushort v => v,
        sbyte v => v,
        byte v => v,
        _ => null,
    };

    private static double? Real(object operand) => operand switch
    {
        float v => v,
        double v => v,
        _ => null,
    };
}
