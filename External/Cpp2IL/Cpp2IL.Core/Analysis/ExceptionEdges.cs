using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>What the exception table says one <c>catch</c> covers, once it is blocks rather than addresses.</summary>
/// <param name="From">The block holding the last protected instruction. This is the <c>try</c>.</param>
/// <param name="Pad">The landing pad, kept alive by the edge this pass adds.</param>
public readonly record struct AttachedPad(Block From, Block Pad);

/// <summary>
/// Keeps alive the one landing pad in a method that would otherwise be deleted before anything can look at
/// it, and says which block the <c>try</c> is.
/// </summary>
/// <remarks>
/// <para>
/// <b>The handler is thrown away in the first few instructions of the analysis.</b> A landing pad is entered
/// by the unwinder and by nothing else, so no branch reaches it, and <c>StackAnalyzer.Analyze</c> opens with
/// <c>graph.RemoveUnreachableBlocks()</c> - whose comment reads "Without this indirect jumps (in try catch i
/// think) cause some weird stuff". What it deletes is every <c>catch</c> body in the program. Measured on
/// <c>CFramework.SaveIO::Load</c>: the table names 22 catch pads, <b>22 of 22 are in the raw lift and 2
/// survive</b>.
/// </para>
/// <para>
/// <b>Two rules, and the first attempt at this had neither.</b>
/// </para>
/// <para>
/// <b>Attach only what would otherwise die.</b> A pad clang laid where ordinary control falls into it is
/// already reachable and needs nothing - and those are exactly the clauses the throw-anchored recognition
/// already finds. Attaching them too is pure perturbation: the first attempt attached every pad in every
/// method and cost <c>commented</c> 367 -> 434, <c>unmanaged</c> 346 -> 381, and 47 of the 77 clauses it was
/// meant to grow. Reachability at this moment is the exact test for "would be deleted", so it is the test.
/// </para>
/// <para>
/// <b>One pad per method.</b> <see cref="CatchClauses"/> emits at most one clause per method, so a second
/// attachment can never pay for the join it adds. The one chosen is the pad with the largest protected
/// extent, which is the outermost clause and the one worth the most.
/// </para>
/// <para>
/// And the <c>try</c> comes from the table rather than from the shape of the graph. That is the half nothing
/// else can supply: <c>SaveIO::Load</c>'s clauses protect ordinary <b>calls</b> and contain no <c>Throw</c>
/// at all, so a recogniser that starts from a throwing block can never see them however the pad is reached.
/// </para>
/// </remarks>
public static class ExceptionEdges
{
    private static readonly ConditionalWeakTable<MethodAnalysisContext, StrongBox<AttachedPad?>> Attached = new();

    /// <summary>The pad this pass attached, and the block that is the <c>try</c>, if it attached one.</summary>
    public static AttachedPad? AttachedTo(MethodAnalysisContext method)
        => Attached.TryGetValue(method, out var found) ? found.Value : null;

    public static void Run(MethodAnalysisContext method)
    {
        if (method.ControlFlowGraph is not { } graph || method.UnderlyingPointer == 0)
            return;

        var sites = ExceptionTable.For(method.AppContext, method.UnderlyingPointer);

        if (sites.Count == 0)
            return;

        //Which pad covers the most, and where its protection ends. An action of zero is a cleanup - a
        //`finally`, which this fork cannot write - and is left alone.
        //
        //`Covered` is the sum of the ranges, not the distance between the first and the last: one pad often
        //appears in several rows far apart, and measuring the span made a pad protecting eight bytes in two
        //places outrank one protecting forty-four in a row.
        var extent = new Dictionary<ulong, (ulong Covered, ulong Last)>();

        foreach (var site in sites)
        {
            if (site.Pad == 0 || site.Action == 0 || site.End <= site.Start)
                continue;

            var here = site.End - site.Start;

            if (!extent.TryGetValue(site.Pad, out var range))
                extent[site.Pad] = (here, site.End);
            else
                extent[site.Pad] = (range.Covered + here, System.Math.Max(range.Last, site.End));
        }

        if (extent.Count == 0)
            return;

        var live = Reachable(graph);

        //If any of this method's catch pads is reachable already, the throw-anchored recognition has a
        //candidate here and this pass has nothing to add - so it adds nothing at all, not even for the
        //method's other pads. Attaching one pad to a method that already had a working clause is how the
        //export went from 17 catch clauses to 9: the help was for methods that needed none, and the join it
        //costs is paid by the clause that was already there.
        foreach (var pad in extent.Keys)
        {
            if (BlockAt(method, graph, pad) is { } reachable && live.Contains(reachable))
                return;
        }

        AttachedPad? best = null;
        ulong widest = 0;

        foreach (var (pad, range) in extent.OrderBy(e => e.Key))
        {
            //Already reachable: ordinary control flow falls into it, nothing is going to delete it, and the
            //throw-anchored recognition can find it unaided.
            if (BlockAt(method, graph, pad) is { } existing && live.Contains(existing))
                continue;

            if (range.Covered <= widest)
                continue;

            var guarded = BlockCovering(method, graph, range.Last - 1);

            if (guarded == null || !live.Contains(guarded))
                continue;

            var padBlock = BlockStartingAt(method, graph, pad);

            if (padBlock == null || padBlock == guarded || guarded.Successors.Contains(padBlock))
                continue;

            best = new AttachedPad(guarded, padBlock);
            widest = range.Covered;
        }

        if (best is not { } chosen)
            return;

        chosen.From.Successors.Add(chosen.Pad);
        chosen.Pad.Predecessors.Add(chosen.From);
        Attached.AddOrUpdate(method, new StrongBox<AttachedPad?>(chosen));
    }

    private static HashSet<Block> Reachable(ISILControlFlowGraph graph)
    {
        var live = new HashSet<Block>();
        var pending = new Queue<Block>();
        pending.Enqueue(graph.EntryBlock);

        while (pending.Count > 0)
        {
            var block = pending.Dequeue();

            if (!live.Add(block))
                continue;

            foreach (var successor in block.Successors)
                pending.Enqueue(successor);
        }

        return live;
    }

    /// <summary>The block an address falls in, where one instruction was lifted from exactly that address.</summary>
    private static Block? BlockAt(MethodAnalysisContext method, ISILControlFlowGraph graph, ulong address)
        => graph.Blocks.FirstOrDefault(b => b != graph.EntryBlock && b != graph.ExitBlock
            && b.Instructions.Any(i => InstructionAddresses.Of(method, i) == address));

    /// <summary>The block holding the instruction lifted from the highest address at or below this one.</summary>
    private static Block? BlockCovering(MethodAnalysisContext method, ISILControlFlowGraph graph, ulong address)
    {
        Block? found = null;
        ulong best = 0;

        foreach (var block in graph.Blocks)
        {
            if (block == graph.EntryBlock || block == graph.ExitBlock)
                continue;

            foreach (var instruction in block.Instructions)
            {
                var at = InstructionAddresses.Of(method, instruction);

                if (at == 0 || at > address || at < best)
                    continue;

                best = at;
                found = block;
            }
        }

        return found;
    }

    /// <summary>
    /// The block beginning at this address, splitting one open where the address is in the middle of it.
    /// </summary>
    private static Block? BlockStartingAt(MethodAnalysisContext method, ISILControlFlowGraph graph, ulong address)
    {
        foreach (var block in graph.Blocks.ToList())
        {
            if (block == graph.EntryBlock || block == graph.ExitBlock)
                continue;

            var at = block.Instructions.FindIndex(i => InstructionAddresses.Of(method, i) == address);

            if (at < 0)
                continue;

            if (at == 0)
                return block;

            var tail = new Block { ID = graph.Blocks.Max(b => b.ID) + 1 };
            tail.Instructions.AddRange(block.Instructions.Skip(at));
            block.Instructions.RemoveRange(at, block.Instructions.Count - at);

            tail.Successors.AddRange(block.Successors);

            foreach (var successor in tail.Successors)
            {
                for (var i = 0; i < successor.Predecessors.Count; i++)
                {
                    if (successor.Predecessors[i] == block)
                        successor.Predecessors[i] = tail;
                }
            }

            block.Successors.Clear();
            block.Successors.Add(tail);
            tail.Predecessors.Add(block);

            block.CalculateBlockType();
            tail.CalculateBlockType();
            graph.Blocks.Add(tail);

            return tail;
        }

        return null;
    }
}
