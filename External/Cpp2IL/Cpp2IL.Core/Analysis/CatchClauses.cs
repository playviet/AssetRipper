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

    public static void Run(MethodAnalysisContext method)
    {
        if (method.ControlFlowGraph is not { } graph)
            return;

        var clauses = new List<CatchClause>();

        foreach (var block in graph.Blocks.ToList())
        {
            if (Recognise(block, graph) is not { } clause)
                continue;

            clauses.Add(clause);
        }

        //One clause only. Two would have to agree about which blocks belong to which, and the evidence that
        //would settle that is the exception table this does not read.
        if (clauses.Count != 1)
            return;

        var only = clauses[0];

        //Everything the pad region holds that the handler does not is C++ plumbing: the selector test, the
        //begin/end-catch pair, the re-raise. It has no managed meaning and would export as calls to raw
        //addresses.
        var keep = new HashSet<Block>(only.Handler);
        foreach (var block in Region(only.Guarded, graph))
        {
            if (keep.Contains(block))
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
        foreach (var block in only.Handler)
            graph.Blocks.Remove(block);
        graph.Blocks.AddRange(only.Handler);

        Recovered.AddOrUpdate(method, clauses);
    }

    /// <summary>The clause a block's throw is guarded by, where the block's successor is a landing pad.</summary>
    private static CatchClause? Recognise(Block guarded, ISILControlFlowGraph graph)
    {
        //The throw has to end the block. Where MergeCallBlocks left one mid-block the instructions after it
        //are the pad's own, and the two cannot be told apart by position.
        if (guarded.Instructions.LastOrDefault(i => i.OpCode != OpCode.Nop) is not { OpCode: OpCode.Throw })
            return null;

        var pads = guarded.Successors.Where(s => s != graph.ExitBlock && s != guarded).ToList();

        if (pads.Count != 1)
            return null;

        var region = Region(guarded, graph);

        if (region.Count == 0)
            return null;

        foreach (var block in region)
        {
            var (caught, handlerEntry) = Dispatch(block, graph);

            if (handlerEntry is null || caught is null)
                continue;

            var handler = Reachable(handlerEntry, graph);

            //A handler that runs back into the method needs to know where the try ended, and this does not.
            //Requiring it to leave only through the method's own exit is what keeps the recovery honest.
            if (handler.Count == 0 || handler.Any(b => b.Successors.Any(s => s != graph.ExitBlock && !handler.Contains(s))))
                continue;

            //And nothing outside the pad may reach into it, or severing the throw's edge would take a live
            //path away with it.
            if (handler.Any(b => b.Predecessors.Any(p => !handler.Contains(p) && !region.Contains(p))))
                continue;

            return new CatchClause { Guarded = guarded, Handler = handler, Caught = caught };
        }

        return null;
    }

    /// <summary>
    /// The type this block tests the exception against and the block it falls into when the test passes,
    /// where the block ends in the landing pad's <c>class_is_assignable_from</c> dispatch.
    /// </summary>
    private static (TypeAnalysisContext? Caught, Block? Handler) Dispatch(Block block, ISILControlFlowGraph graph)
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
            if (NamedClass(block, argument) is { } named)
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
    private static TypeAnalysisContext? NamedClass(Block block, object? operand)
    {
        if (operand is not LocalVariable local)
            return null;

        if (local.Type is RuntimeClassTypeAnalysisContext direct)
            return direct.RepresentedType;

        if (DefinitionIn(block, local) is not { } definition)
            return null;

        foreach (var source in definition.SourcesAndConstants)
        {
            if (source is LocalVariable { Type: RuntimeClassTypeAnalysisContext through })
                return through.RepresentedType;
        }

        return null;
    }

    /// <summary>Everything the landing pad reaches, bounded, so a runaway walk cannot swallow the method.</summary>
    private static List<Block> Region(Block guarded, ISILControlFlowGraph graph)
    {
        var region = new List<Block>();
        var seen = new HashSet<Block> { guarded };
        var pending = new Queue<Block>(guarded.Successors.Where(s => s != graph.ExitBlock && s != guarded));

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

    private static List<Block> Reachable(Block entry, ISILControlFlowGraph graph)
    {
        var found = new List<Block>();
        var seen = new HashSet<Block>();
        var pending = new Queue<Block>();
        pending.Enqueue(entry);

        while (pending.Count > 0)
        {
            var block = pending.Dequeue();

            if (block == graph.ExitBlock || block == graph.EntryBlock || !seen.Add(block))
                continue;

            found.Add(block);

            if (found.Count > 32)
                return [];

            foreach (var successor in block.Successors)
                pending.Enqueue(successor);
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

        foreach (var instruction in graph.Instructions)
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
