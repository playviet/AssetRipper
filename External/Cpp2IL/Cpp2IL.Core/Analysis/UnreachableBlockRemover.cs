using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Removes the blocks control can no longer reach.
/// </summary>
/// <remarks>
/// The passes that recover a call out of the code il2cpp inlined to find it leave that code behind reaching
/// nothing: the walk of a class's interface table is written out at the call site, and once the call names its
/// method the walk decides nothing. Detaching it is enough for a block that then has no way in at all, but a
/// loop keeps itself alive - the two blocks of the walk are each other's only predecessor, so neither is ever
/// left without one, and a check for that finds nothing to remove.
///
/// What keeps them is what they compute: the comparison that ends the walk is read by the branch that repeats
/// it, so it is not dead, and through it the whole chain the walk was reached by stays live - a runtime method,
/// its class, its generic context, the entry read out of that. None of those can be written as C#, so each is
/// one more read of unmanaged memory in the middle of a method that was otherwise recovered.
///
/// Reachability answers it where counting predecessors cannot: a block control cannot arrive at does nothing,
/// whatever points at it, and taking those away leaves the values they read used by nobody and collectable.
/// </remarks>
public static class UnreachableBlockRemover
{
    public static bool Run(MethodAnalysisContext method)
    {
        var cfg = method.ControlFlowGraph!;

        if (cfg.EntryBlock is not { } entry)
            return false;

        var reachable = new HashSet<Block>();
        var queue = new Queue<Block>();
        queue.Enqueue(entry);
        reachable.Add(entry);

        while (queue.Count > 0)
        {
            foreach (var successor in queue.Dequeue().Successors)
            {
                if (reachable.Add(successor))
                    queue.Enqueue(successor);
            }
        }

        //The exit is where returns are collected rather than somewhere control flows to, so it stays whether
        //anything reaches it or not.
        if (cfg.ExitBlock is { } exit)
            reachable.Add(exit);

        var removed = cfg.Blocks.Where(block => !reachable.Contains(block)).ToList();

        if (removed.Count == 0)
            return false;

        foreach (var block in removed)
        {
            foreach (var successor in block.Successors)
                successor.Predecessors.Remove(block);

            foreach (var predecessor in block.Predecessors)
                predecessor.Successors.Remove(block);

            block.Successors.Clear();
            block.Predecessors.Clear();
            block.Instructions.Clear();
            cfg.Blocks.Remove(block);
        }

        return true;
    }
}
