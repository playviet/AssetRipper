using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Throws the exception the method built, rather than a fresh one of whatever type the helper looked like.
/// </summary>
/// <remarks>
/// <para>
/// A <c>throw</c> is a call to a runtime helper, and the lifter turns that call back into a throw by naming
/// the type it decided the helper raises. That is right for the ones the runtime raises on its own account -
/// a null check, a bounds check - and wrong for every <c>throw new Foo(message)</c> the program wrote, where
/// the object already exists and the helper is only handed it:
/// </para>
/// <code>
/// Newobj   v114 (IndexOutOfRangeException), v100
/// CallVoid IndexOutOfRangeException..ctor,  v114, "Cannot select a random item from an empty list"
/// Throw    typeof(System.OutOfMemoryException)
/// </code>
/// <para>
/// So the object is made, given its message, and dropped - the constructor call has nowhere to go and is
/// commented out - and what is thrown is a different exception with no message at all. Both halves of the
/// statement are wrong and it still compiles, which is why no scorer has ever objected.
/// </para>
/// <para>
/// The object is right there. A <c>Newobj</c> of an exception whose constructor has been called and which
/// nothing else reads is what the throw is for, and saying so is one operand.
/// </para>
/// </remarks>
public static class ThrowTheConstructedException
{
    public static void Run(MethodAnalysisContext method)
    {
        if (method.ControlFlowGraph is not { } graph)
            return;

        var live = graph.Blocks.SelectMany(b => b.Instructions).Where(i => i.OpCode != OpCode.Nop).ToList();

        foreach (var block in graph.Blocks)
        {
            if (block.Instructions.LastOrDefault(i => i.OpCode != OpCode.Nop) is not { OpCode: OpCode.Throw } throwing)
                continue;

            if (throwing.Operands is not [TypeAnalysisContext raised] || !IsAnException(raised))
                continue;

            if (Built(block, live) is not { } built)
                continue;

            throwing.Operands = [built];
        }
    }

    /// <summary>
    /// The exception this block finished constructing, where that is the only thing it could be about to
    /// throw.
    /// </summary>
    /// <remarks>
    /// Walking back from the throw through the blocks that reach it, because the compiler puts the allocation,
    /// the message and the constructor call in blocks of their own - each one is a call, and a call ends a
    /// block. The first constructed exception found on the way back is the one being thrown; a path that
    /// reaches the entry without finding one, or two paths that found different objects, is a throw this
    /// cannot speak for.
    /// </remarks>
    private static LocalVariable? Built(Graphs.Block from, List<Instruction> live)
    {
        LocalVariable? found = null;

        var seen = new HashSet<Graphs.Block>();
        var queue = new Queue<Graphs.Block>();
        queue.Enqueue(from);

        while (queue.Count > 0)
        {
            var block = queue.Dequeue();
            LocalVariable? here = null;

            for (var i = block.Instructions.Count - 1; i >= 0 && here is null; i--)
            {
                if (block.Instructions[i] is { OpCode: OpCode.CallVoid, Operands: [MethodAnalysisContext { Name: ".ctor" } made, LocalVariable built, ..] }
                    && made.DeclaringType is { } declaring && IsAnException(declaring)
                    && built.Type is { } holds && IsAnException(holds))
                {
                    here = built;
                }
            }

            if (here != null)
            {
                if (found != null && !ReferenceEquals(found, here))
                    return null;

                found = here;
                continue;
            }

            //Nothing here, so whatever reaches this block reaches the throw. Running out of predecessors means
            //this path throws without building anything, which is the runtime's own throw and not a written one.
            if (block.Predecessors.Count == 0)
                return null;

            foreach (var predecessor in block.Predecessors)
                if (seen.Add(predecessor))
                    queue.Enqueue(predecessor);
        }

        //And it has to be the object's last word: something that reads it afterwards is using it for
        //something else, and this would be throwing a value the program still wants.
        return found is null || live.Any(i => i.OpCode is OpCode.Call or OpCode.CallVoid
            && i.Operands.Count > 0 && i.Operands[0] is MethodAnalysisContext { Name: not ".ctor" }
            && i.Operands.Skip(1).Any(o => ReferenceEquals(o, found)))
            ? null
            : found;
    }

    /// <summary>Whether a type is an exception, which is what makes a constructed object a candidate.</summary>
    private static bool IsAnException(TypeAnalysisContext? type)
    {
        for (var walk = type; walk != null; walk = walk.BaseType)
            if (walk.FullName == "System.Exception")
                return true;

        return false;
    }
}
