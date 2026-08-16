using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>One <c>catch</c> clause: the block whose throw it guards, what it catches, and its body.</summary>
public sealed class CatchClause
{
    /// <summary>The block whose <see cref="OpCode.Throw"/> the clause protects. The <c>try</c> is this block.</summary>
    public required Block Guarded { get; init; }

    /// <summary>The handler's blocks, the entry first. Reachable from nothing else once this pass has run.</summary>
    public required List<Block> Handler { get; init; }

    /// <summary>The managed type the landing pad tests the exception against.</summary>
    public required TypeAnalysisContext Caught { get; init; }

    /// <summary>The landing pad the clause was reached through.</summary>
    public required Block Pad { get; init; }

    /// <summary>
    /// The blocks the <c>try</c> covers, the one control enters at first. Just <see cref="Guarded"/> unless
    /// the exception table's range could be taken whole - see <c>CatchClauses.TheWholeProtectedRange</c>.
    /// </summary>
    public List<Block> Protected { get; set; } = [];
}

/// <summary>
/// Recovers a <c>catch</c> clause from the C++ landing pad il2cpp compiled it into.
/// </summary>
/// <remarks>
/// <para>
/// il2cpp turns <c>try { ... } catch (T) { ... }</c> into a raise followed by a landing pad that asks the
/// runtime whether the exception is a <c>T</c> and either falls into the handler body or re-raises. Clang
/// puts the pad in the instruction stream like any other code, so **all of it is already in the graph** -
/// <c>Corpus::Thrown</c> carries its whole handler, six blocks past the throw, ending in the <c>Return -5</c>
/// the original wrote. What is missing is only that a CIL <c>throw</c> ends the block, so the decompiler
/// discards everything after it as unreachable and the handler is lost with no marker and no warning.
/// </para>
/// <code>
/// b8   34 Throw v22 (ArgumentOutOfRangeException)          succs=b10
/// b10  43 CheckNotEqual v50, v36, 1 / ConditionalJump b20     the unwinder's selector test
/// b11  47 Call D5E510, ...                                    __cxa_begin_catch
/// b13  57 Call 6959B0, v73, v67, v71                          class_is_assignable_from(T, class_of(ex))
///      58 And v74, v73, 1 / 59 CheckEqual v75, v74, 0 / 60 ConditionalJump b17
/// b15  61 Call D5E520 / 3 Return -5                            THE CATCH BODY
/// b17..b20                                                     the re-raise
/// </code>
/// <para>
/// The one thing in that sequence the analysis already names is the class pointer -
/// <c>Il2CppClass&lt;System.ArgumentOutOfRangeException&gt;</c>, fed through
/// <c>il2cpp_codegen_initialize_runtime_metadata</c> into the dispatch call - so a clause can be both
/// **found** and **named** by it. Everything else in the pad is C++ exception plumbing with no managed
/// meaning, and this pass deletes it.
/// </para>
/// <para>
/// <b>What it deliberately does not do.</b> The <c>try</c> range is the throwing block and nothing else.
/// The real range is written down in the binary's <c>.gcc_except_table</c>, and reading that is a project of
/// its own; without it there is no sound way to say which earlier blocks the clause also protected. Guarding
/// the throw alone is the part that is certainly true, and it is what makes the difference between a body
/// that throws and a body that returns what the original returned. A clause whose handler is reached from
/// anywhere but the pad, or which leaves its own blocks for the rest of the method, is refused for the same
/// reason: the answer would be a guess.
/// </para>
/// </remarks>
public static class CatchClauses
{
    private static readonly ConditionalWeakTable<MethodAnalysisContext, List<CatchClause>> Recovered = new();

    /// <summary>The clauses recovered for a method, if any. The generator asks; nothing else does.</summary>
    public static List<CatchClause>? Of(MethodAnalysisContext method)
        => Recovered.TryGetValue(method, out var clauses) ? clauses : null;

    /// <summary>
    /// Why a clause was not recovered, counted, when CATCH_CENSUS asks. Nothing reads it in an ordinary run.
    /// </summary>
    /// <remarks>
    /// The number this exists to produce is how far short of the exception tables a structural rule falls.
    /// The <c>try</c> range is written down in <c>.gcc_except_table</c> and this pass does not read it, so it
    /// can only see a throw whose landing pad clang happened to lay directly after it. This says how often
    /// that is, and what the rest look like instead.
    /// </remarks>
    private static readonly Dictionary<string, int> Census = new();

    private static void Counted(string why, int howMany = 1)
    {
        if (System.Environment.GetEnvironmentVariable("CATCH_CENSUS") != "1" || howMany == 0)
            return;

        lock (Census)
        {
            Census[why] = Census.GetValueOrDefault(why) + howMany;

            if (Census.TryGetValue("throws", out var total) && total % 500 == 0)
            {
                System.Console.Error.WriteLine("CATCH CENSUS " + string.Join("  ",
                    Census.OrderByDescending(k => k.Value).Select(k => $"{k.Key}={k.Value}")));
            }
        }
    }

    public static void Run(MethodAnalysisContext method)
    {
        if (method.ControlFlowGraph is not { } graph)
            return;

        //MergeCallBlocks runs after the throw rewrite and can leave the Throw in the middle of a block, with
        //the landing pad's own first instructions behind it in the same list - and then nothing about the
        //block says where one ends and the other begins. Splitting there is exact (a straight-line split of
        //a single-entry, single-exit run) and it is undone below wherever it bought nothing, so a method
        //that gains no clause is left with the graph it had.
        var splits = new List<(Block Head, Block Tail)>();

        foreach (var block in graph.Blocks.ToList())
        {
            if (SplitAtTheThrow(block, graph) is { } tail)
                splits.Add((block, tail));
        }

        //Computed here, after the splits and before anything is severed, because a handler's extent is a
        //question about the graph as the program wrote it - see HandlerRegion.
        var dominators = new DominatorInfo(graph);

        var clauses = new List<CatchClause>();

        foreach (var block in graph.Blocks.ToList())
        {
            if (Recognise(block, graph, dominators) is not { } clause)
                continue;

            clauses.Add(clause);
        }

        //Only where the shape of the graph found nothing. The throw-anchored recognition is what the 77
        //clauses already rest on, and a second candidate beside it would make the count two and refuse both -
        //so the table's answer is a fallback, never a rival.
        if (clauses.Count == 0 && ExceptionEdges.AttachedTo(method) is { } attached)
        {
            if (RecogniseThrough(attached.From, attached.Pad, graph, dominators) is { } fromTheTable)
            {
                Counted("recovered from the protected range");
                clauses.Add(fromTheTable);
            }
            else
            {
                Counted("the table named the range and the pad is still not a catch");
            }
        }

        if (clauses.Count != 1)
        {
            for (var i = splits.Count - 1; i >= 0; i--)
                Unsplit(splits[i].Head, splits[i].Tail, graph);
        }

        //Still one clause emitted, but a second candidate no longer costs the method the first. Two used to
        //refuse both, on the grounds that they would have to agree about which blocks belong to which - and
        //now they do not have to: dominance makes their extents disjoint or nested, so the first is safe to
        //take on its own. Emitting both is a separate question and was measured to buy nothing.
        if (clauses.Count > 1)
        {
            Counted("more than one clause");
            clauses.RemoveRange(1, clauses.Count - 1);
        }

        if (clauses.Count != 1)
        {
            LetGoOfTheUnusedPad(method, graph, []);
            return;
        }

        Counted("recovered");

        var only = clauses[0];
        var region = Region(only.Guarded, only.Pad, graph);

        //Every other split bought nothing here, so it goes back - except one inside the clause's own blocks,
        //which is where the recognition was looking.
        for (var i = splits.Count - 1; i >= 0; i--)
        {
            var (head, tail) = splits[i];

            if (head == only.Guarded || region.Contains(tail) || only.Handler.Contains(tail)
                || region.Contains(head) || only.Handler.Contains(head))
                continue;

            Unsplit(head, tail, graph);
        }

        Counted("split at a mid-block throw", splits.Count);

        //Everything the pad region holds that the handler does not is C++ plumbing: the selector test, the
        //begin/end-catch pair, the re-raise. It has no managed meaning and would export as calls to raw
        //addresses.
        var keep = new HashSet<Block>(only.Handler);
        var body = Body(only.Guarded, graph);

        foreach (var block in region)
        {
            //Never a block the method still reaches without the throw: the pad can fall back into ordinary
            //code, and deleting that would delete a live path.
            if (keep.Contains(block) || body.Contains(block))
                continue;

            Detach(block, graph);
        }

        //A throw takes no ordinary edge - see SsaForm.Fork.NeverReachesItsSuccessors, which already refuses
        //to write phi copies on this one. Now that the pad it led to is a handler, the edge itself can go.
        foreach (var successor in only.Guarded.Successors.ToList())
            successor.Predecessors.Remove(only.Guarded);
        only.Guarded.Successors.Clear();

        DropThePlumbing(only.Handler, graph);

        //The handler is now reachable from nothing, and LayoutOrder writes an unreached block out after every
        //reached one, in `graph.Blocks` order. Putting the handler at the end of that list, entry first, is
        //therefore the whole of the layout this needs: one contiguous run, last, which is what a CIL handler
        //range has to be.
        //Only blocks the graph still has, and each exactly once: re-adding one that was merged away or
        //detached would put its instructions in the body twice.
        var moving = only.Handler.Distinct().Where(graph.Blocks.Contains).ToList();

        foreach (var block in moving)
            graph.Blocks.Remove(block);
        graph.Blocks.AddRange(moving);

        DeclareTheHandlersLocals(method, only.Handler);

        //And now the try itself, which until this point has been the single block the clause was found from.
        //The exception table says exactly which addresses the clause protects; taking them whole is the
        //difference between a `catch` that guards the call it was found at and one that guards what the
        //program actually wrote.
        if (TheWholeProtectedRange(method, only, graph) is { } wider)
        {
            Counted("the try is the whole protected range", wider.Count);
            only.Protected = wider;
        }

        LetGoOfTheUnusedPad(method, graph, [.. only.Handler, .. region, only.Guarded]);

        Recovered.AddOrUpdate(method, clauses);
    }


    /// <summary>
    /// Splits a block after the first <see cref="OpCode.Throw"/> that is not its last live instruction, and
    /// answers with the tail. Nothing else in the graph moves.
    /// </summary>
    /// <remarks>
    /// <c>MergeCallBlocks</c> runs after <c>MetadataResolver</c> rewrites a raise into a <c>Throw</c>, so the
    /// throw can end up mid-block with the landing pad's own first instructions behind it - and then the two
    /// are one list and position cannot tell them apart. The census counted <b>680</b> throwing blocks in
    /// this shape, against 2 clauses recovered, so it is the largest thing the recognition was blind to for
    /// a reason that has nothing to do with exceptions. The split is exact: a straight-line run with one way
    /// in and one way out, cut in two, which is the one graph edit that cannot change what anything means.
    /// </remarks>
    private static Block? SplitAtTheThrow(Block block, ISILControlFlowGraph graph)
    {
        if (block == graph.EntryBlock || block == graph.ExitBlock)
            return null;

        var at = block.Instructions.FindIndex(i => i.OpCode == OpCode.Throw);

        if (at < 0 || at >= block.Instructions.Count - 1)
            return null;

        //Nothing but Nops after it is not two blocks, it is one with a tail nobody reads.
        if (block.Instructions.Skip(at + 1).All(i => i.OpCode == OpCode.Nop))
            return null;

        var tail = new Block { ID = graph.Blocks.Max(b => b.ID) + 1 };
        tail.Instructions.AddRange(block.Instructions.Skip(at + 1));
        block.Instructions.RemoveRange(at + 1, block.Instructions.Count - at - 1);

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

    /// <summary>Puts a split back, exactly, where it bought nothing.</summary>
    /// <remarks>
    /// The tail gives its instructions up as well as handing them over. It is removed from the graph here, so
    /// keeping them looked harmless - until a later pass put the tail back (a handler's blocks are taken out
    /// of <c>graph.Blocks</c> and re-added at the end), and then the same ISIL instruction was in two blocks
    /// that both got emitted. The generator keys a dictionary on the instruction, so that is
    /// <c>An item with the same key has already been added</c> and the whole body is lost.
    /// </remarks>
    private static void Unsplit(Block head, Block tail, ISILControlFlowGraph graph)
    {
        head.Instructions.AddRange(tail.Instructions);
        tail.Instructions.Clear();
        head.Successors.Clear();
        head.Successors.AddRange(tail.Successors);

        foreach (var successor in tail.Successors)
        {
            for (var i = 0; i < successor.Predecessors.Count; i++)
            {
                if (successor.Predecessors[i] == tail)
                    successor.Predecessors[i] = head;
            }
        }

        head.CalculateBlockType();
        graph.Blocks.Remove(tail);
    }

    /// <summary>The clause a block's throw is guarded by, where the block's successor is a landing pad.</summary>
    private static CatchClause? Recognise(Block guarded, ISILControlFlowGraph graph, DominatorInfo dominators)
    {
        //The throw has to end the block. Where MergeCallBlocks left one mid-block the instructions after it
        //are the pad's own, and the two cannot be told apart by position.
        if (guarded.Instructions.Any(i => i.OpCode == OpCode.Throw))
            Counted("throws");

        if (guarded.Instructions.LastOrDefault(i => i.OpCode != OpCode.Nop) is not { OpCode: OpCode.Throw })
        {
            //Only a Nop-only tail can reach this now; SplitAtTheThrow has already moved every other one out.
            if (guarded.Instructions.Any(i => i.OpCode == OpCode.Throw))
                Counted("the throw does not end the block");
            return null;
        }

        var pads = guarded.Successors.Where(s => s != graph.ExitBlock && s != guarded).ToList();

        if (pads.Count != 1)
        {
            Counted(pads.Count == 0 ? "the throw has no successor - no pad was laid after it" : "several successors");
            return null;
        }

        return RecogniseThrough(guarded, pads[0], graph, dominators);
    }

    /// <summary>
    /// The clause reached from one guarded block through one landing pad, whether the pad was found by
    /// falling into it or named by the exception table.
    /// </summary>
    private static CatchClause? RecogniseThrough(Block guarded, Block pad, ISILControlFlowGraph graph, DominatorInfo dominators)
    {
        var region = Region(guarded, pad, graph);

        if (region.Count == 0)
        {
            Counted("the region past the throw is not a pad-sized one");
            return null;
        }

        var found = false;

        foreach (var block in region)
        {
            var (caught, handlerEntry) = Dispatch(block, region, graph);

            if (handlerEntry is null || caught is null)
                continue;

            found = true;

            var handler = HandlerRegion(handlerEntry, graph, dominators);

            if (handler.Count == 0)
            {
                Counted("the handler is the rest of the method");
                continue;
            }

            //A two-way branch out of the handler used to be refused here, because `leave` is unconditional.
            //The generator now inverts the test and jumps over a `leave`, which is the same thing said in the
            //two instructions CIL allows - see AddCatchClauses.
            if (handler.Any(b => b.Instructions.LastOrDefault(i => i.OpCode != OpCode.Nop) is { OpCode: OpCode.ConditionalJump }
                    && b.Successors.Any(s => s != graph.ExitBlock && !handler.Contains(s))))
            {
                Counted("a conditional branch leaves the handler");
            }

            //And nothing outside the pad may reach into it, or severing the throw's edge would take a live
            //path away with it. The guarded block itself does not count: where the table named the range,
            //the edge from it to the pad is the unwinder's, and is the only reason the pad is still here.
            if (handler.Any(b => b.Predecessors.Any(p => !handler.Contains(p) && !region.Contains(p) && p != guarded)))
            {
                Counted("something outside the pad reaches into the handler");
                continue;
            }

            return new CatchClause { Guarded = guarded, Handler = handler, Caught = caught, Pad = pad, Protected = [guarded] };
        }

        if (!found)
            Counted("no class_is_assignable_from dispatch in the region");

        return null;
    }

    /// <summary>
    /// The type this block tests the exception against and the block it falls into when the test passes,
    /// where the block ends in the landing pad's <c>class_is_assignable_from</c> dispatch.
    /// </summary>
    private static (TypeAnalysisContext? Caught, Block? Handler) Dispatch(Block block, List<Block> region, ISILControlFlowGraph graph)
    {
        if (block.Instructions.LastOrDefault(i => i.OpCode != OpCode.Nop) is not
            { OpCode: OpCode.ConditionalJump, Operands.Count: 2 } jump)
            return (null, null);

        if (jump.Operands[1] is not LocalVariable condition)
            return (null, null);

        //`CheckEqual c, t, 0` is "not assignable", so the handler is what the jump does NOT take;
        //`CheckNotEqual c, t, 0` is "assignable", and the handler is the target. Both spellings are the same
        //test read the two ways a compiler can read it.
        if (DefinitionIn(block, condition) is not
            { OpCode: OpCode.CheckEqual or OpCode.CheckNotEqual, Operands.Count: 3 } test)
            return (null, null);

        if (!IsZero(test.Operands[2]) || test.Operands[1] is not LocalVariable masked)
            return (null, null);

        var taken = test.OpCode == OpCode.CheckNotEqual;

        //The runtime answers with a byte, and the compiler masks it. The mask is not always there.
        var answer = masked;
        if (DefinitionIn(block, masked) is { OpCode: OpCode.And, Operands.Count: 3 } mask
            && mask.Operands[2] is 1 or 1L or 1u or 1ul
            && mask.Operands[1] is LocalVariable unmasked)
        {
            answer = unmasked;
        }

        if (DefinitionIn(block, answer) is not { IsCall: true } call || call.Operands.Count < 3)
            return (null, null);

        //A resolved managed method is not the runtime's own dispatch helper.
        if (call.Operands[0] is MethodAnalysisContext)
            return (null, null);

        TypeAnalysisContext? caught = null;
        foreach (var argument in call.SourcesAndConstants)
        {
            if (NamedClass(region, argument) is { } named)
                caught = named;
        }

        if (caught is null)
            return (null, null);

        var target = jump.Operands[0] as Block ?? BlockOf(jump.Operands[0], graph);
        var handler = taken
            ? target
            : block.Successors.FirstOrDefault(s => s != target && s != block);

        return (caught, handler);
    }

    /// <summary>The managed type an operand names, where it is a runtime class pointer or a copy of one.</summary>
    /// <remarks>
    /// The dispatch is not handed the class pointer directly: it is handed what
    /// <c>il2cpp_codegen_initialize_runtime_metadata</c> answered with, and that call's own argument is the
    /// pointer. One step back is enough, and more than one would start guessing.
    /// </remarks>
    private static TypeAnalysisContext? NamedClass(List<Block> region, object? operand)
    {
        if (operand is not LocalVariable local)
            return null;

        if (local.Type is RuntimeClassTypeAnalysisContext direct)
            return direct.RepresentedType;

        //Across the whole landing pad, not this block: the metadata call that answers with the pointer is
        //one block above the dispatch that reads it, which is what a block-local search missed.
        if (DefinitionIn(region, local) is not { } definition)
            return null;

        foreach (var source in definition.SourcesAndConstants)
        {
            if (source is LocalVariable { Type: RuntimeClassTypeAnalysisContext through })
                return through.RepresentedType;
        }

        return null;
    }

    /// <summary>Everything the landing pad reaches, bounded, so a runaway walk cannot swallow the method.</summary>
    private static List<Block> Region(Block guarded, Block pad, ISILControlFlowGraph graph)
    {
        var region = new List<Block>();
        var seen = new HashSet<Block> { guarded };
        var pending = new Queue<Block>();

        if (pad != graph.ExitBlock && pad != guarded)
            pending.Enqueue(pad);

        while (pending.Count > 0)
        {
            var block = pending.Dequeue();

            if (block == graph.ExitBlock || block == graph.EntryBlock || !seen.Add(block))
                continue;

            region.Add(block);

            if (region.Count > 32)
                return [];

            foreach (var successor in block.Successors)
                pending.Enqueue(successor);
        }

        return region;
    }

    /// <summary>
    /// What the method still reaches once the throw's edge into the landing pad is gone - everything that is
    /// ordinary control flow rather than a handler.
    /// </summary>
    private static HashSet<Block> Body(Block guarded, ISILControlFlowGraph graph)
    {
        var live = new HashSet<Block>();
        var pending = new Queue<Block>();
        pending.Enqueue(graph.EntryBlock);

        while (pending.Count > 0)
        {
            var block = pending.Dequeue();

            if (!live.Add(block))
                continue;

            //A throw takes no ordinary edge, so nothing past it is reached this way.
            if (block == guarded)
                continue;

            foreach (var successor in block.Successors)
                pending.Enqueue(successor);
        }

        return live;
    }

    /// <summary>
    /// The handler's own blocks: everything its entry <b>dominates</b>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This began as "what the entry reaches and the method does not", which was right for a handler that
    /// returns and wrong for every other kind. It left <b>three</b> separate things broken, and they turn out
    /// to be one thing: <b>the handler's end was unknown</b>.
    /// </para>
    /// <list type="bullet">
    /// <item>A handler that runs back into the method reaches the whole method, so the walk ran off its bound
    /// and 466 clauses were refused as "the handler is the rest of the method".</item>
    /// <item>il2cpp funnels a method's landing pads into a <b>shared tail</b> - <c>__cxa_end_catch</c> and the
    /// common continuation - so one clause's region contained the next one's entry and the two could not both
    /// be kept. That fired <b>185</b> times and is why lifting the one-clause-per-method cap bought nothing.</item>
    /// <item>And with no extent there was nothing to make a multi-block <c>try</c> out of either.</item>
    /// </list>
    /// <para>
    /// Dominance answers all three at once, and it is the same reasoning that worked for the returning case -
    /// where two walks meet is where control is handed back - stated exactly. A block reached from a second
    /// landing pad is dominated by neither, so the shared tail belongs to no handler and drops out. A block on
    /// the normal path after the region is reachable without entering the handler, so it is not dominated and
    /// is where the handler <c>leave</c>s to. And two handlers' extents are disjoint by construction, because
    /// dominance regions of distinct blocks nest or do not meet at all.
    /// </para>
    /// <para>
    /// The bound stays at 64 and is now a <b>check</b> rather than a guess: with a real extent, a handler that
    /// is half the method means the recognition is wrong, and the right answer is to refuse it and say so.
    /// </para>
    /// </remarks>
    private static List<Block> HandlerRegion(Block entry, ISILControlFlowGraph graph, DominatorInfo dominators)
    {
        var found = new List<Block> { entry };

        foreach (var block in graph.Blocks)
        {
            if (block == entry || block == graph.EntryBlock || block == graph.ExitBlock)
                continue;

            if (!dominators.Dominates(entry, block))
                continue;

            found.Add(block);

            if (found.Count > 64)
            {
                Counted("the handler dominates more than a handler can be");
                return [];
            }
        }

        return found;
    }

    /// <summary>
    /// The call the C++ code generator wrote and the program did not: a call to a raw address whose answer
    /// nothing in the handler reads. Inside a landing pad these are <c>__cxa_begin_catch</c> and its end, and
    /// they would export as a line about an unknown call target inside an otherwise whole handler.
    /// </summary>
    private static void DropThePlumbing(List<Block> handler, ISILControlFlowGraph graph)
    {
        var read = new HashSet<LocalVariable>();

        //Every block, not `graph.Instructions` - that property is a BFS from the entry block, and the
        //handler has just been made unreachable on purpose. Asking it would be asking whether anything
        //outside the handler reads the value, which is not the question.
        foreach (var instruction in graph.Blocks.SelectMany(b => b.Instructions))
        {
            foreach (var source in instruction.SourcesAndConstants)
            {
                if (source is LocalVariable local)
                    read.Add(local);
                else if (source is MemoryOperand { Base: LocalVariable through })
                    read.Add(through);
            }
        }

        foreach (var block in handler)
        {
            block.Instructions.RemoveAll(i => i.OpCode == OpCode.Call
                && i.Operands.Count >= 2
                && i.Operands[0] is not MethodAnalysisContext and not string
                && i.Operands[1] is LocalVariable answer
                && !read.Contains(answer));
        }
    }


    /// <summary>Makes sure every local the handler names is one the generator will have a slot for.</summary>
    /// <remarks>
    /// <c>ISILControlFlowGraph.Instructions</c> is a **breadth-first walk from the entry block**, so it
    /// yields only what is reachable - and this pass makes the handler unreachable on purpose, because that
    /// is what lays it out last. The generator builds its local map by sweeping that property, so a local
    /// named only inside a handler was never declared and the body died with
    /// <c>KeyNotFoundException: The given key 'v5 @ X22' was not present in the dictionary</c> -
    /// <c>TDCommonUtils::FormatDate</c>, the one generation failure this bought before it was found. A
    /// generation failure costs the whole body and leaves no marker, so it reads as a whole method on every
    /// other measure; it is the one number that has to be looked at before any other.
    /// </remarks>
    private static void DeclareTheHandlersLocals(MethodAnalysisContext method, List<Block> handler)
    {
        foreach (var operand in handler.SelectMany(b => b.Instructions).SelectMany(i => i.Operands))
        {
            var memory = operand as MemoryOperand?;

            foreach (var local in new[]
                     {
                         operand as LocalVariable,
                         (operand as FieldReference)?.Local,
                         memory?.Base as LocalVariable,
                         memory?.Index as LocalVariable,
                     })
            {
                if (local != null && !method.Locals.Contains(local))
                    method.Locals.Add(local);
            }
        }
    }


    /// <summary>
    /// Takes back the edge <see cref="ExceptionEdges"/> added, and the pad it was holding on to, where no
    /// <c>catch</c> came of it.
    /// </summary>
    /// <remarks>
    /// Keeping a landing pad alive is what lets it be recognised, and it is not free: one that stays without
    /// becoming a <c>catch</c> is C++ exception plumbing the generator then writes out. Symmetric with the
    /// block splits - keep what paid, put back what did not.
    /// </remarks>
    private static void LetGoOfTheUnusedPad(MethodAnalysisContext method, ISILControlFlowGraph graph, HashSet<Block> keep)
    {
        if (ExceptionEdges.AttachedTo(method) is not { } attached || keep.Contains(attached.Pad))
            return;

        attached.From.Successors.Remove(attached.Pad);
        attached.Pad.Predecessors.Remove(attached.From);

        //Everything the pad reached and the method does not: the pad's successors are the rest of the
        //plumbing, and leaving them would only move the problem one block along, where LayoutOrder writes
        //out whatever it never reached.
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

        var seen = new HashSet<Block>();
        var walk = new Queue<Block>();
        walk.Enqueue(attached.Pad);

        while (walk.Count > 0)
        {
            var block = walk.Dequeue();

            if (block == graph.ExitBlock || live.Contains(block) || keep.Contains(block) || !seen.Add(block))
                continue;

            foreach (var successor in block.Successors)
                walk.Enqueue(successor);

            Detach(block, graph);
        }
    }


    /// <summary>
    /// Every block the exception table says this clause protects, the entry first - or nothing, where that
    /// set is not something a CIL protected region is allowed to be.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Measured over <c>Assembly-CSharp</c> before this was written, because the answer decides whether it is
    /// worth writing: <b>942</b> pads have a protected block set, <b>778 of them are a single block</b> - which
    /// is what was already emitted - and <b>164</b> span several. Of those 164:
    /// </para>
    /// <list type="bullet">
    /// <item><b>Every exit is writable, 164 of 164.</b> A <c>ret</c>, a <c>br</c> and a two-way branch all have
    /// a <c>leave</c> spelling; a jump table does not, and not one protected range is left by one.</item>
    /// <item><b>Only 75 are single-entry.</b> ECMA-335 allows entering a protected region only at its first
    /// instruction, and a pad's protection is a <i>union</i> of call-site rows - there is no reason a union of
    /// address ranges should be single-entry in the graph, and 89 times it is not.</item>
    /// </list>
    /// <para>
    /// So the constraint that decides this is not the one about leaving, it is the one about entering, and it
    /// is a hard test rather than a guess: count the blocks in the set that anything outside it reaches. More
    /// than one and the range is refused whole, and the clause keeps the single block it already had.
    /// </para>
    /// </remarks>
    /// <summary>Every call-site row the compiler wrote for this method.</summary>
    private static List<ExceptionTable.CallSite> Sites(MethodAnalysisContext method)
        => method.UnderlyingPointer == 0 ? [] : ExceptionTable.For(method.AppContext, method.UnderlyingPointer);

    private static List<Block>? TheWholeProtectedRange(MethodAnalysisContext method, CatchClause clause, ISILControlFlowGraph graph)
    {
        var padAddress = InstructionAddresses.Of(method, clause.Pad);

        if (padAddress == 0)
            return null;

        var rows = Sites(method).Where(s => s.Pad == padAddress && s.Action != 0 && s.End > s.Start).ToList();

        if (rows.Count == 0)
            return null;

        var inside = new List<Block>();

        foreach (var block in graph.Blocks)
        {
            if (block == graph.EntryBlock || block == graph.ExitBlock)
                continue;

            foreach (var instruction in block.Instructions)
            {
                var at = InstructionAddresses.Of(method, instruction);

                if (at != 0 && rows.Any(r => at >= r.Start && at < r.End))
                {
                    inside.Add(block);
                    break;
                }
            }
        }

        //The block the clause was already found from has to be in it, or this is a different clause's range.
        if (inside.Count < 2 || inside.Count > 32 || !inside.Contains(clause.Guarded))
            return null;

        var set = new HashSet<Block>(inside);

        //Nothing may branch into a protected region except at its first instruction. Anything else the
        //decompiler would either refuse or, worse, accept and mean something else by.
        var entries = inside.Where(b => b.Predecessors.Count == 0 || b.Predecessors.Any(p => !set.Contains(p))).ToList();

        if (entries.Count != 1)
        {
            Counted("the protected range is branched into");
            return null;
        }

        //And every way out has to have a `leave` to write. A jump table has none.
        foreach (var block in inside)
        {
            if (!block.Successors.Any(s => s != graph.ExitBlock && !set.Contains(s)))
                continue;

            if (block.Instructions.LastOrDefault(i => i.OpCode != OpCode.Nop) is { OpCode: OpCode.IndirectJump })
            {
                Counted("the protected range is left by a jump table");
                return null;
            }
        }

        //A handler is not inside its own try, and the pad must not be either.
        if (clause.Handler.Any(set.Contains) || set.Contains(clause.Pad))
            return null;

        inside.Remove(entries[0]);
        inside.Insert(0, entries[0]);

        return inside;
    }

    /// <summary>Takes a block out of the graph, leaving no edge pointing at it.</summary>
    private static void Detach(Block block, ISILControlFlowGraph graph)
    {
        foreach (var predecessor in block.Predecessors.ToList())
            predecessor.Successors.RemoveAll(s => s == block);

        foreach (var successor in block.Successors.ToList())
            successor.Predecessors.RemoveAll(p => p == block);

        block.Predecessors.Clear();
        block.Successors.Clear();
        block.Instructions.Clear();
        graph.Blocks.Remove(block);
    }

    private static Instruction? DefinitionIn(List<Block> blocks, LocalVariable local)
        => DefinitionIn(blocks, local, new HashSet<LocalVariable>());

    private static Instruction? DefinitionIn(List<Block> blocks, LocalVariable local, HashSet<LocalVariable> seen)
    {
        //`a = b; b = a` is a cycle of copies, and following it without saying so overflowed the stack and
        //took the whole export down - it exits zero, writes DONE, and leaves an export with no scripts in it.
        if (!seen.Add(local))
            return null;

        Instruction? found = null;

        foreach (var block in blocks)
        {
            if (DefinitionIn(block, local) is not { } here)
                continue;

            if (found != null)
                return null;

            found = here;
        }

        //Through one copy. Where the pad has a predecessor it did not have before, destroying single
        //assignment leaves the class pointer arriving by a `Move` from the value the metadata call answered
        //with, and asking only for the one instruction that writes this local finds the copy and stops.
        if (found is { OpCode: OpCode.Move, Operands: [_, LocalVariable copied] } && !ReferenceEquals(copied, local))
            return DefinitionIn(blocks, copied, seen) ?? found;

        return found;
    }

    /// <summary>The one instruction in this block that writes the local, where there is exactly one.</summary>
    private static Instruction? DefinitionIn(Block block, LocalVariable local)
    {
        Instruction? found = null;

        foreach (var instruction in block.Instructions)
        {
            if (!ReferenceEquals(instruction.Destination, local))
                continue;

            if (found != null)
                return null;

            found = instruction;
        }

        return found;
    }

    private static Block? BlockOf(object? operand, ISILControlFlowGraph graph)
        => operand is Instruction target ? graph.FindBlockByInstruction(target) : null;

    private static bool IsZero(object operand) => operand is 0 or 0L or 0u or 0ul or (byte)0 or (short)0;
}
