using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Turns il2cpp's interface dispatch back into the call it came from.
/// </summary>
/// <remarks>
/// A class does not know where in its table an interface's methods begin, so the runtime finds out: every
/// class carries a list of the interfaces it implements paired with the offset each one starts at, and a call
/// on an interface walks that list looking for the interface, then calls through the table at the offset it
/// found plus the method's own slot. il2cpp writes the walk out at the call site rather than calling a helper
/// to do it, so what a single <c>foreach</c> or one interface method call becomes is a read of a count, a read
/// of a pointer, a loop that compares and steps, and an indirect call through whatever it landed on - none of
/// which names a method, and all of which is left as reads of unmanaged memory.
///
/// It is also the most common thing left unrecovered in this game: reading the count alone accounts for nearly
/// five hundred of them.
///
/// Everything needed is in the walk. The interface is the constant the loop compares each entry against; the
/// method is the slot added to the offset that was found, which is a constant too; and the object is what the
/// class was read out of. That is a call, and it can be written as one - after which the walk is the runtime
/// detail it always was.
/// </remarks>
public static class InterfaceCallRecovery
{
    /// <summary>Where a class's method table starts, which the found offset and the slot are added to.</summary>
    private const int VTableOffset64 = 0x138;

    /// <summary>How far back the interface sits from the offset the search walks over.</summary>
    private const int InterfaceFromOffset = -8;

    /// <summary>What one entry of a method table takes up: the method's address and its runtime method.</summary>
    private const int SlotWidth = 0x10;

    /// <summary>Where the runtime method sits inside a table entry, the method's own address being the front.</summary>
    private const int MethodInfoInEntry = 0x8;

    /// <summary>Where a class records how many interfaces it has offsets for, which opens the walk.</summary>
    private const int InterfaceCountOffset = 0x12E;

    /// <summary>What a count of table entries is shifted by to become a distance in bytes.</summary>
    private const int EntriesToBytes = 4;

    /// <summary>
    /// As many slots as an interface could plausibly declare. A constant added before the scaling is only
    /// the slot when it is one an interface could have; anything larger is arithmetic of some other kind.
    /// </summary>
    private const int MaximumSlot = 1024;

    /// <summary>
    /// The body as it stands <em>at this pass's own position</em>, which is not what a dump at the end of the
    /// pipeline shows: a hundred later passes delete what they cannot express, so a shape diagnosed from the
    /// end may already be gone here, or only exist here. Three rounds were spent fixing shapes that were not
    /// present where the fix ran before this was added.
    /// </summary>
    private static void Trace(MethodAnalysisContext method)
    {
        if (System.Environment.GetEnvironmentVariable("IFACE_TRACE") is not { } wanted || !method.Name.Contains(wanted))
            return;

        System.Console.WriteLine($"===== {method.DeclaringType?.FullName}::{method.Name} (at InterfaceCallRecovery)");

        foreach (var block in method.ControlFlowGraph!.Blocks)
        {
            System.Console.WriteLine($"  b{block.ID} [{block.BlockType}] preds={string.Join(",", block.Predecessors.Select(x => "b" + x.ID))} succs={string.Join(",", block.Successors.Select(x => "b" + x.ID))}");

            foreach (var instruction in block.Instructions)
                System.Console.WriteLine("      " + instruction);
        }
    }

    public static bool Run(MethodAnalysisContext method)
    {
        if (method.AppContext.Binary.is32Bit)
            return false;

        Trace(method);

        var voidType = method.AppContext.SystemTypes.SystemVoidType;
        var definitions = new Dictionary<LocalVariable, List<Instruction>>();

        foreach (var instruction in method.ControlFlowGraph!.Instructions)
            if (instruction.Destination is LocalVariable destination)
                (definitions.TryGetValue(destination, out var list) ? list : definitions[destination] = []).Add(instruction);

        var changed = false;

        foreach (var block in method.ControlFlowGraph.Blocks.ToList())
        foreach (var instruction in block.Instructions.ToList())
        {
            if (instruction.OpCode != OpCode.IndirectCall || instruction.Operands.Count < 3)
                continue;

            if (Dispatch(instruction.Operands[0], definitions, method) is not { } dispatch)
            {
                //One dispatch that stands for several calls, which no single answer can describe. Handled by
                //splitting rather than by choosing - see TailMerged.
                changed |= TailMerged(method, block, instruction, definitions);
                continue;
            }

            if (MethodInSlot(dispatch.Interface, dispatch.Slot) is not { } callee || callee.IsStatic)
                continue;

            List<object> arguments;
            object? answer = null;

            if (dispatch.ThroughInvoker)
            {
                //Nothing about this call is in the registers the callee would have read: the thunk was handed
                //a uniform frame and unpacks it itself, so the arguments are in memory and X1 - which the
                //convention below would hand over as the first of them - is the `MethodInfo`.
                if (InvokerThunk.Read(callee, instruction, method) is not { } unpacked)
                    continue;

                arguments = [dispatch.Receiver, .. unpacked.Arguments];
                answer = unpacked.Answer;
            }
            else
            {
                //The call was handed every register an argument could have come in, because nothing was known
                //about what it took. Now that the callee is named, the convention says which of them it read.
                if (Aapcs64.ParametersOf(callee, instruction.Operands) is not { } parameters)
                    continue;

                arguments = [dispatch.Receiver, .. parameters];
            }

            if (ReferenceEquals(callee.ReturnType, voidType))
            {
                instruction.OpCode = OpCode.CallVoid;
                instruction.Operands = [callee, .. arguments];
            }
            else if (instruction.Operands[1] is LocalVariable result)
            {
                instruction.OpCode = OpCode.Call;

                //The thunk answers through the pointer it was handed and leaves x0 holding nothing, so the
                //register the call was lifted with is not where the value is. Saying the answer goes to the
                //buffer is the same shape a big struct's indirect return already has, which is what lets the
                //copy out of the buffer be folded away rather than left as a poke at unmanaged memory.
                instruction.Operands = [callee, result, .. arguments];

                //The thunk answers through the pointer it was handed and leaves x0 holding nothing, so the
                //value is where that pointer went - and in a shared body it goes straight back out again.
                //Where it does not, the body reads the buffer itself, and the call has to answer into it.
                //And where the buffer is neither the method's own nor a slot, the body copies it into the one
                //it keeps the value in, and that copy is the assignment.
                if (!InvokerThunk.FoldAnswerIntoTheReturn(method, instruction, answer)
                    && !InvokerThunk.AnswerIntoTheCopyItFeeds(method, instruction, answer))
                {
                    InvokerThunk.AnswerIntoTheSlotItNames(method, instruction, answer);
                }
            }
            else
            {
                //A value comes back but there is nowhere left to put it, so leaving the call alone keeps the
                //stack honest rather than dropping a value the emitted IL would still push.
                continue;
            }

            //The walk was only ever working out which method to call, and the call now names it. Left in
            //place it is a loop over unmanaged memory wrapped around a statement that has been recovered.
            SkipTheWalk(method.ControlFlowGraph, block, dispatch.RuntimeClass);

            changed = true;
        }

        return changed;
    }

    /// <summary>One walk of a dispatch the compiler tail-merged, and everything needed to write its call.</summary>
    private readonly record struct Arm(Block Block, Block Guard, TypeAnalysisContext Interface, int Slot,
        LocalVariable Receiver, MethodAnalysisContext Callee);

    /// <summary>
    /// Recovers a dispatch that stands for <b>several</b> calls, by giving each of them its own.
    /// </summary>
    /// <remarks>
    /// <para>
    /// il2cpp writes the interface walk out at the call site, and where two calls on the same interface sit in
    /// different arms of the same <c>if</c>, the compiler <b>tail-merges</b> them: two complete walks, each
    /// ending at its own slot, both writing the same registers, meeting at one indirect call.
    /// <c>TrackingManager::ResolveImpressionCount</c> is the shape - <c>[offsets] + 32</c> on one arm and
    /// <c>[offsets] + 30</c> on the other.
    /// </para>
    /// <para>
    /// <b>Every previous attempt at this crossing was a rule that picked a definition, and all four were
    /// reverted</b> ([[il2cpp-object-is-not-a-declaration]]). The reason they cannot work is not that the rule
    /// was wrong: the local genuinely holds two different slots naming two different methods, so there is no
    /// answer to pick. Nothing short of separating the two paths can be right, and that is what this does.
    /// </para>
    /// <para>
    /// The separation needs no renaming, which is what keeps it small. Each arm already computes its own slot
    /// and its own class into locals of its own; what they share is the *tail*, and the tail's only lasting
    /// output is the call's result register. So the call block is cut in two at the indirect call, each arm
    /// gets the call it stood for written into it and jumps straight to the second half, and each walk's
    /// opening test is sent to its own arm. Both arms then assign the same result local - which is what the
    /// merged code did - and everything after the call is untouched and reached from both.
    /// </para>
    /// <para>
    /// Refused unless every way into the call block passes through one of the walks being replaced: anything
    /// else reaches the tail without an answer of its own.
    /// </para>
    /// </remarks>
    private static bool TailMerged(MethodAnalysisContext method, Block callBlock, Instruction call,
        Dictionary<LocalVariable, List<Instruction>> definitions)
    {
        if (method.AppContext.Binary.is32Bit || call.Operands.Count < 3)
            return false;

        foreach (var (found, fromClass, throughInvoker) in Entries(call.Operands[0], definitions))
        {
            //A callee reached through the invoker thunk is handed a frame rather than registers, and the
            //frame is built in the shared tail. That is a second question and this one does not touch it.
            if (throughInvoker || fromClass < VTableOffset64)
                continue;

            var inVtable = fromClass - Il2CppClassUsefulOffsets.GetVtableOffset(method.AppContext.MetadataVersion, false);

            if (inVtable < 0 || inVtable % SlotWidth != 0)
                continue;

            //The tail itself is singly defined - it is the shared part. What it reads is not.
            if (Single(found, definitions) is not { OpCode: OpCode.Add, Operands: [_, LocalVariable runtimeClass, LocalVariable scaled] }
                || Single(scaled, definitions) is not { OpCode: OpCode.ShiftLeft, Operands: [_, LocalVariable counted, { } by] }
                || Constant(by) != EntriesToBytes)
            {
                continue;
            }

            var arms = Arms(method, definitions, counted, runtimeClass, (int)(inVtable / SlotWidth));

            if (arms.Count >= 2 && EveryWayIn(callBlock, arms))
                return Separate(method, callBlock, call, arms);
        }

        return false;
    }

    /// <summary>
    /// One arm per block that gives <b>both</b> the slot and the class a value, which is how taking single
    /// assignment form apart lays an edge's copies out: all of one edge's, in one block.
    /// </summary>
    /// <remarks>
    /// Pairing them by block is what makes this safe. Reading the two locals' definitions separately would
    /// pair a slot from one walk with a class from the other and name a method on the wrong object - which is
    /// exactly the mistake the previous four rules made, in a different place.
    /// </remarks>
    private static List<Arm> Arms(MethodAnalysisContext method, Dictionary<LocalVariable, List<Instruction>> definitions,
        LocalVariable counted, LocalVariable runtimeClass, int baseSlot)
    {
        var arms = new List<Arm>();

        foreach (var block in method.ControlFlowGraph!.Blocks)
        {
            if (Defines(block, counted) is not { } slotCopy || Defines(block, runtimeClass) is not { } classCopy)
                continue;

            if (Reached(slotCopy, definitions) is not { } sum || Reached(classCopy, definitions) is not { } ownClass)
                continue;

            //Where the walk stopped, and how far into the interface's part of the table the method sits. The
            //slot can be counted before the scaling or added after it, exactly as `Scaling` reads it.
            LocalVariable walked;
            long slot;

            if (Single(sum, definitions) is { OpCode: OpCode.Add, Operands: [_, MemoryOperand { Index: null, Scale: 0, Addend: 0, Base: LocalVariable at }, { } added] }
                && Constant(added) is { } entries && entries >= 0 && entries <= MaximumSlot)
            {
                (walked, slot) = (at, entries);
            }
            else if (Single(sum, definitions) is { OpCode: OpCode.Move, Operands: [_, MemoryOperand { Index: null, Scale: 0, Addend: 0, Base: LocalVariable only }] })
            {
                (walked, slot) = (only, 0);
            }
            else
            {
                continue;
            }

            if (InterfaceComparedAgainst(walked, method, definitions) is not { } declared
                || LoadedFrom(ownClass, definitions) is not { } receiver
                || MethodInSlot(declared, baseSlot + (int)slot) is not { } callee || callee.IsStatic
                || Opening(method, ownClass) is not { } guard || ReferenceEquals(guard, block))
            {
                continue;
            }

            arms.Add(new Arm(block, guard, declared, baseSlot + (int)slot, receiver, callee));
        }

        return arms;
    }

    /// <summary>The instruction in this block that gives the local a value, where exactly one does.</summary>
    private static Instruction? Defines(Block block, LocalVariable local)
    {
        Instruction? only = null;

        foreach (var instruction in block.Instructions)
        {
            if (instruction.Destination is not LocalVariable written || !ReferenceEquals(written, local))
                continue;

            if (only != null)
                return null;

            only = instruction;
        }

        return only;
    }

    /// <summary>What the copy on this edge was copied from, which is where that arm's own value lives.</summary>
    private static LocalVariable? Reached(Instruction copy, Dictionary<LocalVariable, List<Instruction>> definitions)
        => copy is { OpCode: OpCode.Move, Operands: [_, LocalVariable from] } && definitions.ContainsKey(from) ? from : null;

    /// <summary>The test that opens a walk: the one asking how many interfaces this class has.</summary>
    private static Block? Opening(MethodAnalysisContext method, LocalVariable runtimeClass)
        => method.ControlFlowGraph!.Blocks.FirstOrDefault(candidate =>
            candidate.BlockType == BlockType.TwoWay
            && candidate.Instructions.Count > 0
            && candidate.Instructions[^1].OpCode == OpCode.ConditionalJump
            && candidate.Instructions.Any(instruction => instruction.Operands.Any(operand =>
                operand is MemoryOperand { Base: LocalVariable held } read
                && read.Addend == InterfaceCountOffset
                && ReferenceEquals(held, runtimeClass))));

    /// <summary>Whether every path into the shared tail comes through one of the walks being replaced.</summary>
    private static bool EveryWayIn(Block callBlock, List<Arm> arms)
        => callBlock.Predecessors.All(predecessor => arms.Any(arm => Reaches(arm.Guard, predecessor, callBlock)));

    /// <summary>
    /// Cuts the tail in two and writes each arm's own call into the arm.
    /// </summary>
    private static bool Separate(MethodAnalysisContext method, Block callBlock, Instruction call, List<Arm> arms)
    {
        var cfg = method.ControlFlowGraph!;
        var voidType = method.AppContext.SystemTypes.SystemVoidType;
        var written = new List<(Arm Arm, List<object> Operands, bool Void)>();

        foreach (var arm in arms)
        {
            var returnsNothing = ReferenceEquals(arm.Callee.ReturnType, voidType);

            if (!returnsNothing && call.Operands[1] is not LocalVariable)
                return false;

            if (Aapcs64.ParametersOf(arm.Callee, call.Operands) is not { } parameters
                || !parameters.All(operand => AvailableIn(arm.Block, operand, cfg)))
            {
                return false;
            }

            var operands = new List<object> { arm.Callee };

            if (!returnsNothing)
                operands.Add(call.Operands[1]);

            operands.Add(arm.Receiver);
            operands.AddRange(parameters);
            written.Add((arm, operands, returnsNothing));
        }

        var at = callBlock.Instructions.IndexOf(call);

        if (at < 0)
            return false;

        //The second half of the tail: everything after the call, which both arms still reach and which is
        //untouched. Its predecessors become the arms.
        var after = new Block { ID = cfg.Blocks.Max(block => block.ID) + 1 };
        after.Instructions.AddRange(callBlock.Instructions.Skip(at + 1));
        callBlock.Instructions.RemoveRange(at + 1, callBlock.Instructions.Count - at - 1);

        foreach (var successor in callBlock.Successors)
        {
            successor.Predecessors.Remove(callBlock);
            successor.Predecessors.Add(after);
            after.Successors.Add(successor);
        }

        callBlock.Successors.Clear();
        cfg.Blocks.Add(after);
        after.CalculateBlockType();

        foreach (var (arm, operands, returnsNothing) in written)
        {
            while (arm.Block.Instructions.Count > 0
                   && arm.Block.Instructions[^1].OpCode is OpCode.Jump or OpCode.ConditionalJump)
            {
                arm.Block.Instructions.RemoveAt(arm.Block.Instructions.Count - 1);
            }

            arm.Block.AddInstruction(new Instruction(call.Index, returnsNothing ? OpCode.CallVoid : OpCode.Call, operands.ToArray()));
            arm.Block.AddInstruction(new Instruction(call.Index, OpCode.Jump, after));

            foreach (var successor in arm.Block.Successors)
                successor.Predecessors.Remove(arm.Block);

            arm.Block.Successors.Clear();
            arm.Block.Successors.Add(after);
            after.Predecessors.Add(arm.Block);
            arm.Block.CalculateBlockType();

            //And the walk's opening test goes straight to the arm, which is what makes the loop, the
            //not-found exit and the runtime helper unreachable.
            foreach (var successor in arm.Guard.Successors)
                successor.Predecessors.Remove(arm.Guard);

            arm.Guard.Successors.Clear();
            arm.Guard.Instructions[^1].OpCode = OpCode.Jump;
            arm.Guard.Instructions[^1].Operands = [arm.Block];
            arm.Guard.Successors.Add(arm.Block);
            arm.Block.Predecessors.Add(arm.Guard);
            arm.Guard.CalculateBlockType();
        }

        cfg.RemoveUnreachableBlocks();
        return true;
    }

    /// <summary>
    /// Whether a value the merged call read is still in hand where the arm's own call is being written.
    /// </summary>
    /// <remarks>
    /// The arguments were laid out before the walk began, so in practice they are - but the shared tail is
    /// also a place a value can be built, and one built there is not available on the arm's own path. Asked
    /// as dominance over the graph as it stands, because the dominator tree computed at the start of analysis
    /// is several rewrites out of date by now.
    /// </remarks>
    private static bool AvailableIn(Block arm, object operand, ISILControlFlowGraph cfg)
    {
        if (operand is not LocalVariable local)
            return true;

        var dominators = Dominators(cfg);

        foreach (var block in cfg.Blocks)
            if (Defines(block, local) is not null && !dominators[arm].Contains(block) && !ReferenceEquals(block, arm))
                return false;

        return true;
    }

    /// <summary>Which blocks every path to each block must pass through.</summary>
    private static Dictionary<Block, HashSet<Block>> Dominators(ISILControlFlowGraph cfg)
    {
        var dominators = new Dictionary<Block, HashSet<Block>>();

        foreach (var block in cfg.Blocks)
            dominators[block] = ReferenceEquals(block, cfg.EntryBlock) ? [cfg.EntryBlock] : [.. cfg.Blocks];

        for (var changed = true; changed;)
        {
            changed = false;

            foreach (var block in cfg.Blocks)
            {
                if (ReferenceEquals(block, cfg.EntryBlock) || block.Predecessors.Count == 0)
                    continue;

                HashSet<Block>? meet = null;

                foreach (var predecessor in block.Predecessors)
                    meet = meet is null ? [.. dominators[predecessor]] : Meet(meet, dominators[predecessor]);

                meet ??= [];
                meet.Add(block);

                if (meet.SetEquals(dominators[block]))
                    continue;

                dominators[block] = meet;
                changed = true;
            }
        }

        return dominators;
    }

    private static HashSet<Block> Meet(HashSet<Block> one, HashSet<Block> other)
    {
        one.IntersectWith(other);
        return one;
    }

    /// <summary>
    /// Sends the path straight to the recovered call, past the walk that used to work out where to go.
    /// </summary>
    /// <remarks>
    /// The walk is a diamond: a test of whether the class implements any interfaces at all, the loop itself,
    /// and the runtime helper the loop falls back on, all meeting again at the call. Deciding that test in
    /// favour of the call leaves both arms reached from nowhere, and they go with whatever led only to them.
    /// Nothing else can be attached to them - il2cpp writes the walk out immediately before the call it is
    /// for - and the reads and the loop counter in there are read by nothing else either.
    /// </remarks>
    private static void SkipTheWalk(ISILControlFlowGraph cfg, Block callBlock, LocalVariable runtimeClass)
    {
        //The test that opens the walk is the one asking how many interfaces the class has.
        var guard = cfg.Blocks.FirstOrDefault(candidate =>
            candidate.BlockType == BlockType.TwoWay
            && candidate.Instructions.Count > 0
            && candidate.Instructions[^1].OpCode == OpCode.ConditionalJump
            && candidate.Instructions.Any(instruction => instruction.Operands.Any(operand =>
                operand is MemoryOperand { Base: LocalVariable held } read
                && read.Addend == InterfaceCountOffset
                && ReferenceEquals(held, runtimeClass))));

        if (guard == null || ReferenceEquals(guard, callBlock) || guard.Successors.Contains(callBlock))
            return;

        //Only where the whole diamond is behind the guard: if the call is reachable another way, redirecting
        //here would skip whatever that way does.
        if (!callBlock.Predecessors.All(predecessor => Reaches(guard, predecessor, callBlock)))
            return;

        foreach (var successor in guard.Successors.ToList())
            successor.Predecessors.Remove(guard);

        guard.Successors.Clear();
        guard.Instructions[^1].OpCode = OpCode.Jump;
        guard.Instructions[^1].Operands = [callBlock];
        guard.Successors.Add(callBlock);
        callBlock.Predecessors.Add(guard);
        guard.CalculateBlockType();

        foreach (var stranded in cfg.Blocks.ToList())
            if (stranded.Predecessors.Count == 0 && stranded != cfg.EntryBlock && stranded != cfg.ExitBlock)
                Prune(cfg, stranded);
    }

    /// <summary>Whether every way from a block to the call passes through the walk the guard opens.</summary>
    private static bool Reaches(Block guard, Block from, Block callBlock)
    {
        var seen = new HashSet<Block>();
        var queue = new Queue<Block>();
        queue.Enqueue(from);

        while (queue.Count > 0)
        {
            var block = queue.Dequeue();

            if (ReferenceEquals(block, guard))
                return true;

            if (ReferenceEquals(block, callBlock) || !seen.Add(block))
                continue;

            foreach (var predecessor in block.Predecessors)
                queue.Enqueue(predecessor);
        }

        return false;
    }

    private static void Prune(ISILControlFlowGraph cfg, Block block)
    {
        var queue = new Queue<Block>();
        queue.Enqueue(block);

        while (queue.Count > 0)
        {
            var unreachable = queue.Dequeue();

            if (unreachable.Predecessors.Count > 0 || unreachable == cfg.EntryBlock || unreachable == cfg.ExitBlock)
                continue;

            foreach (var successor in unreachable.Successors.ToList())
            {
                successor.Predecessors.Remove(unreachable);
                queue.Enqueue(successor);
            }

            unreachable.Successors.Clear();
            unreachable.Instructions.Clear();
            cfg.Blocks.Remove(unreachable);
        }
    }

    /// <summary>
    /// The interface, the slot and the object, taken out of the address an indirect call goes through.
    ///
    /// The address is the one the search settled on, and it is reached by two paths - the walk, and the
    /// runtime helper the walk falls back on - so it is written more than once. Only the walk says anything,
    /// and it is the one this reads.
    /// </summary>
    private static (TypeAnalysisContext Interface, int Slot, LocalVariable Receiver, LocalVariable RuntimeClass, bool ThroughInvoker)? Dispatch(
        object target, Dictionary<LocalVariable, List<Instruction>> definitions, MethodAnalysisContext method)
    {
        foreach (var (found, fromClass, throughInvoker) in Entries(target, definitions))
        {
            InterfaceCallCensus.Counted("attempted");

            if (fromClass < VTableOffset64)
            {
                InterfaceCallCensus.Counted("offset-below-vtable");
                continue;
            }

            //Worked out here rather than taken from the shared helper, which reports the first slot as no slot
            //at all: for a class that is right, since its first entry is not something a call reaches this
            //way, but an interface's first method is slot zero and is called like any other.
            var inVtable = fromClass - Il2CppClassUsefulOffsets.GetVtableOffset(method.AppContext.MetadataVersion, false);

            if (inVtable < 0 || inVtable % SlotWidth != 0)
            {
                InterfaceCallCensus.Counted("not-a-slot-boundary");
                continue;
            }

            //What the offset was added to is the class, and the class was read out of the object.
            if (Single(found, definitions) is not { OpCode: OpCode.Add, Operands: [_, LocalVariable runtimeClass, LocalVariable scaled] })
            {
                InterfaceCallCensus.Counted("no-single-add-defining-the-entry");
                continue;
            }

            //What type the object has does not matter here, and usually is not known: it arrives from a cast
            //whose result nothing typed. The interface is what says which method this is.
            if (LoadedFrom(runtimeClass, definitions) is not { } receiver)
            {
                InterfaceCallCensus.Counted("class-not-loaded-from-an-object");
                continue;
            }

            if (Scaling(scaled, definitions) is not var (walked, alreadyCounted))
            {
                InterfaceCallCensus.Counted("no-scaling");
                continue;
            }

            if (InterfaceComparedAgainst(walked, method, definitions) is not { } interfaceType)
            {
                //The one the Snacky Dash census points at: UniTask's interfaces are generic
                //instantiations, so the walk compares against a class read out of the rgctx rather
                //than against a typeof.
                InterfaceCallCensus.Counted("interface-not-named-by-the-comparison");
                continue;
            }

            InterfaceCallCensus.Counted("recovered");
            return (interfaceType, (int)(inVtable / SlotWidth) + alreadyCounted, receiver, runtimeClass, throughInvoker);
        }

        InterfaceCallCensus.Counted("no-entry-survived");
        return null;
    }

    /// <summary>
    /// Where the table entry was read from: what the distance to the slot was measured from, and that
    /// distance.
    /// </summary>
    /// <remarks>
    /// <para>
    /// il2cpp writes <c>ADD X0, X8, #0x138</c> and then <c>LDP X8, X2, [X0]</c>, so the distance to the slot
    /// begins as an addition of its own - and this pass only ever read that shape. Whether it still is one by
    /// the time the pass runs says nothing about the call.
    /// </para>
    /// <para>
    /// The address is where the walk and the runtime helper it falls back on meet. While it had a definition
    /// on both edges nothing would forward it, so the addition stood. That helper is recognised as a throw,
    /// no copy is written on its edge any more, the address became singly defined, and
    /// <c>MetadataResolver.FoldAddressArithmetic</c> - which runs again out of single assignment form, before
    /// this pass - folded it straight into the addressing mode of the read. Seven walks in the ninety-six
    /// files stopped being recovered that way, and about eighty-three across the game. Both shapes are the
    /// same address; both are read here.
    /// </para>
    /// </remarks>
    private static IEnumerable<(LocalVariable Found, long FromClass, bool ThroughInvoker)> Entries(
        object target, Dictionary<LocalVariable, List<Instruction>> definitions)
    {
        //The table entry is either folded straight into the call or still read into a local of its own.
        MemoryOperand? read = target switch
        {
            MemoryOperand { Index: null, Scale: 0, Base: LocalVariable } memory => memory,
            LocalVariable local => ReadThrough(local, definitions),
            _ => null,
        };

        //A callee whose signature mentions the shared T is not entered through the table entry's own pointer:
        //il2cpp calls the invoker thunk instead, which lives in the `MethodInfo` that is the entry's second
        //half. So the read handed to this pass is two hops from the table rather than one, and the offset it
        //carries is the `MethodInfo`'s - 0x10 - which is nothing like a distance into a method table. That is
        //why `IList<T>.get_Item` is refused where `ICollection<T>.get_Count` in the same body is not: the
        //return type decides which pointer the compiler reaches through, and nothing else differs.
        var throughInvoker = false;

        if (read is { Base: LocalVariable holder, Addend: var reached }
            && reached != Il2CppMethodInfoLayout.MethodPointer && Il2CppMethodInfoLayout.IsEntryPoint(reached)
            && ReadThrough(holder, definitions) is { Base: LocalVariable } inner)
        {
            read = inner;
            throughInvoker = reached == Il2CppMethodInfoLayout.InvokerMethod;
        }

        if (read is not { Base: LocalVariable through } entry)
            yield break;

        //The method pointer is the front of a table entry and its `MethodInfo` is the second half, so a walk
        //ending at either is measured from the same place - eight bytes apart. Taking that eight off here
        //means everything below sees one shape, whether the call went through the pointer or the invoker.
        var addend = entry.Addend == MethodInfoInEntry
            ? 0
            : entry.Addend >= VTableOffset64 && (entry.Addend - VTableOffset64) % SlotWidth == MethodInfoInEntry
                ? entry.Addend - MethodInfoInEntry
                : entry.Addend;

        //Left in the addressing mode: what it is measured from is the addition the walk ends with.
        if (addend != 0)
        {
            yield return (through, addend, throughInvoker);
            yield break;
        }

        //Or standing on its own, which is what the compiler wrote.
        foreach (var address in Sources(through, definitions))
            if (address is { OpCode: OpCode.Add, Operands: [_, LocalVariable found, { } added] }
                && Constant(added) is { } fromClass)
                yield return (found, fromClass, throughInvoker);
    }

    /// <summary>The memory a local was read from, where it was read exactly once and through an object.</summary>
    private static MemoryOperand? ReadThrough(LocalVariable local, Dictionary<LocalVariable, List<Instruction>> definitions)
        => Single(local, definitions) is { OpCode: OpCode.Move, Operands: [_, MemoryOperand { Index: null, Scale: 0, Base: LocalVariable } memory] }
            ? memory
            : null;

    /// <summary>
    /// The entry the walk stopped on, and how much of the slot the scaling in front of it already carries.
    /// </summary>
    /// <remarks>
    /// The offset the walk found is a count of table entries, so it is scaled up by the width of one to
    /// reach the method. A slot is a fixed distance in entries into the interface's part of the table, and
    /// so can just as well be counted before the scaling as added as bytes after it - which arrangement the
    /// compiler picks says nothing about the call, and both have to be read the same way.
    /// </remarks>
    private static (LocalVariable Walked, int Slot)? Scaling(
        LocalVariable scaled, Dictionary<LocalVariable, List<Instruction>> definitions)
    {
        if (Single(scaled, definitions) is not { OpCode: OpCode.ShiftLeft, Operands: [_, { } counted, { } by] }
            || Constant(by) != EntriesToBytes)
            return null;

        var slot = 0;

        if (counted is LocalVariable sum)
        {
            if (Single(sum, definitions) is not { OpCode: OpCode.Add, Operands: [_, { } offset, { } added] }
                || Constant(added) is not { } entries || entries < 0 || entries > MaximumSlot)
                return null;

            slot = (int)entries;
            counted = offset;
        }

        return counted is MemoryOperand { Index: null, Scale: 0, Addend: 0, Base: LocalVariable walked }
            ? (walked, slot)
            : null;
    }

    /// <summary>
    /// The interface the search walks the list looking for: the entry it steps over holds the offset, and the
    /// interface sits beside it, so the comparison that ends the walk names it outright.
    /// </summary>
    private static TypeAnalysisContext? InterfaceComparedAgainst(LocalVariable walked, MethodAnalysisContext method,
        Dictionary<LocalVariable, List<Instruction>> definitions)
    {
        foreach (var instruction in method.ControlFlowGraph!.Instructions)
        {
            if (instruction.OpCode is not (OpCode.CheckEqual or OpCode.CheckNotEqual) || instruction.Operands.Count < 3)
                continue;

            var compared = instruction.Operands[1] is MemoryOperand memory ? memory : instruction.Operands[2] as MemoryOperand?;
            var against = instruction.Operands[1] is MemoryOperand ? instruction.Operands[2] : instruction.Operands[1];

            if (compared is not { Index: null, Scale: 0, Base: LocalVariable entry } read || read.Addend != InterfaceFromOffset)
                continue;

            if (!ReferenceEquals(entry, walked))
                continue;

            if (against is TypeAnalysisContext named and not RuntimeClassTypeAnalysisContext)
                return named;

            if (against is LocalVariable { Type: RuntimeClassTypeAnalysisContext { RepresentedType: var represented } })
                return represented;

            //A type constant names the slot the class is kept in, not the class, so the value actually compared
            //here is one dereference further on and carries no type of its own. Following that one step is what
            //the walk needs: without it the interface is never named, the loop is never recognised as the call
            //it stands for, and the reads it is made of - the table at 0xB0, the count at 0x12E, the entry it
            //stepped over - are all written out as unmanaged memory.
            if (against is LocalVariable indirect
                && Single(indirect, definitions) is { OpCode: OpCode.Move, Operands: [_, MemoryOperand { Index: null, Scale: 0, Addend: 0 } slot] }
                && slot.Base is LocalVariable { Type: RuntimeClassTypeAnalysisContext { RepresentedType: var held } })
            {
                return held;
            }
        }

        return null;
    }

    /// <summary>The method occupying a slot of an interface.</summary>
    private static MethodAnalysisContext? MethodInSlot(TypeAnalysisContext type, int slot)
    {
        var declaring = type is GenericInstanceTypeAnalysisContext generic ? generic.GenericType : type;
        var found = declaring.Methods.FirstOrDefault(m => m.Definition?.slot == slot);

        if (found == null)
            return null;

        return type is GenericInstanceTypeAnalysisContext instance
            ? new ConcreteGenericMethodAnalysisContext(found, instance.GenericArguments, [])
            : found;
    }

    /// <summary>Everything a local is ever assigned, as instructions.</summary>
    private static IEnumerable<Instruction> Sources(LocalVariable local, Dictionary<LocalVariable, List<Instruction>> definitions)
        => Sources(local, definitions, []);

    /// <summary>
    /// Everything a local is ever assigned, following the copies that stand between it and the value.
    /// </summary>
    /// <remarks>
    /// Copies can lead back to where they started. This is out of SSA, and taking a graph out of it writes a
    /// copy on each edge into a join - so a value carried around a loop is copied to the local the loop reads
    /// and back again, and following copies without remembering where one has been does not terminate. It
    /// takes a particular shape to reach: a local held across a loop, reused for something an interface call
    /// is made on. Nothing here had it until a change elsewhere altered which locals were typed.
    /// </remarks>
    private static IEnumerable<Instruction> Sources(
        LocalVariable local, Dictionary<LocalVariable, List<Instruction>> definitions, HashSet<LocalVariable> seen)
    {
        if (!seen.Add(local) || !definitions.TryGetValue(local, out var assignments))
            yield break;

        foreach (var assignment in assignments)
        {
            //A copy stands for whatever it was copied from, and the paths meeting here are written as copies.
            if (assignment is { OpCode: OpCode.Move, Operands: [_, LocalVariable copied] })
            {
                foreach (var behind in Sources(copied, definitions, seen))
                    yield return behind;
            }
            else
            {
                yield return assignment;
            }
        }
    }

    /// <summary>The one instruction a local is assigned by, where it is assigned only once.</summary>
    private static Instruction? Single(LocalVariable local, Dictionary<LocalVariable, List<Instruction>> definitions)
    {
        var assignments = Sources(local, definitions).ToList();
        return assignments.Count == 1 ? assignments[0] : null;
    }

    /// <summary>What a local was read from, where it was read from an object at offset zero.</summary>
    private static LocalVariable? LoadedFrom(LocalVariable local, Dictionary<LocalVariable, List<Instruction>> definitions)
        => Single(local, definitions) is { OpCode: OpCode.Move, Operands: [_, MemoryOperand { Index: null, Scale: 0, Addend: 0, Base: LocalVariable held }] }
            ? held
            : null;

    private static long? Constant(object operand)
        => operand switch
        {
            int i => i,
            uint u => u,
            long l => l,
            ulong ul => (long)ul,
            _ => null,
        };

}
