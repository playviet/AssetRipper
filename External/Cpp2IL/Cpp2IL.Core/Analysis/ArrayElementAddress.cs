using System;
using System.Collections.Generic;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Folds an element's address, worked out into a register a step at a time, back into the one addressing mode
/// the generator reads an array access out of.
/// </summary>
/// <remarks>
/// <para>
/// The generator already knows what <c>[array + index*width + 0x20]</c> is - <c>ArrayElement</c> in
/// <c>IlGenerator.Fork</c> takes exactly that operand and writes the subscript, and the field within the
/// element where the element is a struct. What it never sees is the same address arrived at in pieces, which
/// is what the compiler emits whenever the index is not free at the point of the load:
/// </para>
/// <code>
/// ShiftLeft v44, c, 2                     ; the subscript scaled by sizeof(Color32)
/// Add       v45, _colorByEnum, v44
/// Add       v46, v45, 32                  ; past the header
/// Return    [v46]
/// </code>
/// <para>
/// Nothing along that chain is an array access to any pass, so <c>BoardSettingSO::GetColor</c> came out as
/// <c>return (Color32)(colorByEnum + ((int)c &lt;&lt; 2) + 32L);</c> - commented, and the method with it.
/// </para>
/// <para>
/// So the pieces are put back together: an addition onto something typed as an array starts a chain, constants
/// accumulate, and a shift or a multiply by exactly the element's width is the subscript. A read through the
/// end of the chain becomes the memory operand the whole of it stood for, and the additions are left to be
/// collected once nothing reads them.
/// </para>
/// <para>
/// The width has to match the element's own, which is what refuses a chain that only happens to rest on an
/// array: a pointer stepped by twelve through a <c>Vector3[]</c> is this shape, but a pointer stepped by
/// eight is a walk over something else that was reached from it.
/// </para>
/// <para>
/// <b>A known risk, measured and left standing.</b> A chain is carried into a local without asking how many
/// places write it, so a pointer stepped round a loop - written once with the first element's address and
/// once with itself plus the stride - would have its *first* address taken for the whole loop, and every
/// iteration would read the same element. Requiring a single definition, and then requiring every definition
/// to agree, were both built and both measured **worse**: `full` 597 -> 596 and `commented` 68 -> 75, while
/// `BoardLogic::CheckAndMergeAll`, the method the guard was for, did not move either way. So the hazard is
/// real in principle and has not been shown to happen here. What would settle it is a corpus shape that walks
/// an array by pointer and checks the values, which is what the execution oracle is for.
/// </para>
/// </remarks>
public static class ArrayElementAddress
{
    private readonly record struct Address(object Array, TypeAnalysisContext Element, object? Index, int Scale, long Constant);

    public static void Run(MethodAnalysisContext method)
    {
        if (method.ControlFlowGraph is not { } graph)
            return;

        var pointerSize = method.AppContext.Binary.is32Bit ? 4 : 8;
        var header = method.AppContext.Binary.is32Bit ? 0x10 : 0x20;

        Dictionary<LocalVariable, Instruction> definitions = new();
        Dictionary<LocalVariable, List<Instruction>> everyDefinition = new();
        Dictionary<Instruction, int> inBlock = new();
        Dictionary<LocalVariable, Address> chains = new();

        var index = 0;

        foreach (var block in graph.Blocks)
        {
            foreach (var instruction in block.Instructions)
            {
                inBlock[instruction] = index;

                if (instruction.Operands.Count > 0 && instruction.Operands[0] is LocalVariable written)
                {
                    definitions.TryAdd(written, instruction);

                    if (!everyDefinition.TryGetValue(written, out var places))
                        everyDefinition[written] = places = [];

                    places.Add(instruction);
                }
            }

            index++;
        }

        var walk = new Walkers(everyDefinition, inBlock);

        foreach (var instruction in graph.Instructions)
        {
            switch (instruction)
            {
                case { OpCode: OpCode.Add, Operands: [LocalVariable made, { } left, { } right] }:
                    if (Extend(left, right, chains, definitions, walk, pointerSize) is { } forwards)
                        chains[made] = forwards;
                    else if (Extend(right, left, chains, definitions, walk, pointerSize) is { } backwards)
                        chains[made] = backwards;
                    break;

                case { OpCode: OpCode.Move, Operands: [LocalVariable copy, LocalVariable from] }
                    when chains.TryGetValue(from, out var carried):
                    chains[copy] = carried;
                    break;
            }
        }

        if (System.Environment.GetEnvironmentVariable("CHAIN_TRACE") is { } wanted && method.Name.Contains(wanted))
            foreach (var instruction in graph.Instructions)
                if (instruction.OpCode == OpCode.Add && instruction.Operands is [LocalVariable made, ..])
                    System.Console.WriteLine($"CHAIN {(chains.ContainsKey(made) ? "yes" : "no ")} {instruction}");

        if (chains.Count == 0)
            return;

        foreach (var block in graph.Blocks)
        for (var at = 0; at < block.Instructions.Count; at++)
        {
            var instruction = block.Instructions[at];

            for (var i = 0; i < instruction.Operands.Count; i++)
            {
                //An index that is really a byte offset. Where the compiler kept one instead of a subscript the
                //addressing mode already names the array, so nothing here has a chain to fold - but the index
                //still steps by the element's width and still is not a subscript. `ArrayAccessRecovery` takes
                //it for one, which is why `_cornersCache[c].x` read elements 0, 12, 24 and 36 of a four-element
                //array: it compiled, it scored whole, and it was wrong.
                if (instruction.Operands[i] is MemoryOperand { Index: LocalVariable stepping, Scale: 0 or 1 } stepped
                    && ElementOf(stepped.Base ?? "") is { } walked
                    && ArrayTypeInference.Width(walked, pointerSize) is { } wide && wide > 1
                    && walk.Subscript(stepping, wide) is { } counter)
                {
                    instruction.Operands[i] = new MemoryOperand(stepped.Base, counter, stepped.Addend, wide);
                    continue;
                }

                Address address;
                long addend;

                switch (instruction.Operands[i])
                {
                    case MemoryOperand { Index: null, Base: LocalVariable through } read
                        when chains.TryGetValue(through, out var reached):
                        address = reached;
                        addend = reached.Constant + read.Addend;
                        break;

                    //The subscript in two halves, which is how `a[i + j]` over an array of bytes is compiled:
                    //one term is added into the address and the other stays in the addressing mode, because
                    //at a width of one there is nothing to scale and no reason to put them together first.
                    //`BoardLogic::CheckAndMergeAll` reads `_mergeRemoveMask[baseIdx + s]` that way, and the
                    //address came out as `_mergeRemoveMask + num8` in front of an unmanaged-memory read.
                    //Only at a step of exactly one element, where an unscaled term can be nothing else.
                    case MemoryOperand { Index: not null, Base: LocalVariable carrying } indexed
                        when chains.TryGetValue(carrying, out var partial)
                            && ArrayTypeInference.Width(partial.Element, pointerSize) is 1
                            && indexed.Scale is 0 or 1:
                    {
                        var subscript = indexed.Index;

                        //Both halves present: the sum of them is the subscript, and it has to be worked out
                        //somewhere - immediately in front of the read, where both are in hand.
                        if (partial.Index is { } already)
                        {
                            var sum = new LocalVariable($"index{method.Locals.Count}", new Register(null, "SUM"),
                                method.AppContext.SystemTypes.SystemInt32Type);

                            method.Locals.Add(sum);
                            block.Instructions.Insert(at, new Instruction(instruction.Index, OpCode.Add, sum, already, subscript!));
                            at++;
                            subscript = sum;
                        }

                        address = partial with { Index = subscript, Scale = 1 };
                        addend = partial.Constant + indexed.Addend;
                        break;
                    }

                    //And the same address already named as a member of something else. The chain's local is
                    //typed by whatever the offsets happened to fit, and where that lands on a real type the
                    //resolver names the read before this pass ever sees it - `v208.isEmpty`, a `CellData` at
                    //0x20 - and there is no memory operand left to fold. The chain is the stronger evidence:
                    //it rests on something declared as an array and the distance is measured from the array's
                    //own header. `BoardController::PowerUp_Shuffle` swaps two elements of an `ECellColor[][]`
                    //this way, and the swap and every statement after it was commented out.
                    case FieldReference { Local: { } named } member
                        when chains.TryGetValue(named, out var renamed):
                        address = renamed;
                        addend = renamed.Constant + member.Offset;
                        break;

                    default:
                        //Every address this could have folded and did not, for one named method. Both halves
                        //of the question - is there a chain, and does the read reach it - have been wrong
                        //here in turn, and neither is visible from the export.
                        if (System.Environment.GetEnvironmentVariable("CHAIN_TRACE") is { } asked && method.Name.Contains(asked)
                            && instruction.Operands[i] is MemoryOperand { Base: LocalVariable why } refused)
                        {
                            System.Console.WriteLine($"CHAIN refused {instruction} operand {i} chain={chains.ContainsKey(why)}"
                                + $" index={refused.Index} scale={refused.Scale}");
                        }

                        continue;
                }

                //With a constant subscript the addend carries both the element and the distance into it, and
                //the generator takes them apart again. With the subscript in a register the scale has already
                //carried the element, so what is left over is the distance - and it has to stay inside one
                //element, and inside a struct, or it is not a member of anything.
                var within = addend - header;

                if (within < 0
                    || (address.Index != null && within > 0
                        && (!address.Element.IsValueType || within >= address.Scale)))
                {
                    continue;
                }

                instruction.Operands[i] = new MemoryOperand(address.Array, address.Index, addend, address.Scale);

                //What a whole element is read into is one of those, whatever it was called before the chain
                //was put back together. The read was named as a member of the type the chain's local had been
                //given, and its destination took that member's type with it - `bool isEmpty = grids[i]`, which
                //then refuses the store that puts it back. Only where this is the one place the local is
                //written, so nothing else is being overruled.
                if (i == 1 && addend == header && instruction is { OpCode: OpCode.Move, Operands: [LocalVariable into, ..] }
                    && everyDefinition.TryGetValue(into, out var only) && only is [var single] && single == instruction)
                {
                    into.Type = address.Element;
                }
            }
        }
    }

    /// <summary>
    /// The address <paramref name="onto"/> becomes once <paramref name="added"/> is added to it, where that is
    /// still an address into the same array.
    /// </summary>
    private static Address? Extend(object onto, object added, Dictionary<LocalVariable, Address> chains,
        Dictionary<LocalVariable, Instruction> definitions, Walkers walk, int pointerSize)
    {
        Address running;

        if (onto is LocalVariable held && chains.TryGetValue(held, out var carried))
            running = carried;
        else if (ElementOf(onto) is { } element && ArrayTypeInference.Width(element, pointerSize) is { } width && width > 0)
            running = new Address(onto, element, null, 0, 0);
        else
            return null;

        if (Constant(added) is { } distance)
            return running with { Constant = running.Constant + distance };

        //One subscript, and only at the element's own width. A second scaled term is not an access to this
        //array at all, and neither is a step of some other size that happens to rest on it.
        if (ArrayTypeInference.Width(running.Element, pointerSize) is not { } stride || running.Index != null)
            return null;

        if (Scaled(added, definitions, stride) is { } scaled && stride == scaled.Width)
        {
            //A constant the subscript is carrying belongs on the address, not in the subscript. The compiler
            //works `array + 0x20 + i` out as `array + (i + 0x20)` when the header and the index end up in the
            //same register - `BoardLogic::CheckAndMergeAll` does, over a `byte[]` - and then nothing here can
            //see a header at all and the fold is refused for being before the first element.
            if (scaled.Index is LocalVariable subscript && definitions.TryGetValue(subscript, out var made)
                && made is { OpCode: OpCode.Add, Operands: [_, { } one, { } other] })
            {
                if (Constant(other) is { } after && one is LocalVariable)
                    return running with { Index = one, Scale = scaled.Width, Constant = running.Constant + after * scaled.Width };

                if (Constant(one) is { } before && other is LocalVariable)
                    return running with { Index = other, Scale = scaled.Width, Constant = running.Constant + before * scaled.Width };
            }

            return running with { Index = scaled.Index, Scale = scaled.Width };
        }

        //Or the subscript is not there at all, because the compiler kept a byte offset instead and stepped it
        //by the element's width once round the loop. The counter it moves with is the subscript.
        if (walk.Subscript(added, stride) is { } counted)
            return running with { Index = counted, Scale = stride };

        return null;
    }

    /// <summary>What an array of these holds, where the operand is one.</summary>
    private static TypeAnalysisContext? ElementOf(object operand) => Holds(operand switch
    {
        LocalVariable { Type: { } held } => held,
        FieldReference { Field.FieldType: { } declared } => declared,
        _ => null,
    });

    /// <summary>
    /// The element type behind an array, through a by-reference to one. A parameter declared <c>ref T[]</c>
    /// arrives as <c>System.Boolean[]&amp;</c> and is indexed exactly as the array it refers to -
    /// <c>BoardController::ComputeHighlights</c> reaches its highlight buffer that way.
    /// </summary>
    private static TypeAnalysisContext? Holds(TypeAnalysisContext? type) => type switch
    {
        SzArrayTypeAnalysisContext array => array.ElementType,
        ArrayTypeAnalysisContext { Rank: 1 } single => single.ElementType,
        ByRefTypeAnalysisContext { ElementType: { } referenced } => Holds(referenced),
        _ => null,
    };

    /// <summary>
    /// Pairs a byte offset stepped once round a loop with the counter that moves beside it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Where the subscript is not free at the load, the compiler keeps a byte offset instead and steps it by
    /// the element's width. <c>Cell::GetCatBoundsInContainer</c> walks <c>_cornersCache</c> that way: X21 is
    /// seeded nought and stepped by twelve, X24 is seeded nought and stepped by one, so
    /// <c>X21 == 12 * X24</c> exactly and the reads at <c>+0x24</c> and <c>+0x28</c> are
    /// <c>_cornersCache[c].y</c> and <c>.z</c>.
    /// </para>
    /// <para>
    /// <b>Both same-block constraints are load-bearing.</b> The same method has a decoy - X23, the index over
    /// <c>subIndices</c>, is also seeded nought and also stepped by one, but its seed is in the outer
    /// preheader and its step in another block. A search that only looks for "some local seeded nought and
    /// stepped by one" picks it, and the result compiles, scores whole, and reads the wrong element. Only an
    /// execution oracle would ever catch that, so the constraints stay.
    /// </para>
    /// </remarks>
    private sealed class Walkers(Dictionary<LocalVariable, List<Instruction>> definitions, Dictionary<Instruction, int> inBlock)
    {
        public object? Subscript(object operand, int stride)
        {
            if (operand is not LocalVariable offset || Steps(offset, stride) is not { } mine)
                return null;

            foreach (var (candidate, places) in definitions)
            {
                if (ReferenceEquals(candidate, offset) || Steps(candidate, 1) is not { } theirs)
                    continue;

                //The two have to move together - seeded in one block and stepped in another single block -
                //and the offset has to be the counter scaled by the element.
                if (inBlock[mine.Seed] == inBlock[theirs.Seed] && inBlock[mine.Step] == inBlock[theirs.Step]
                    && mine.Start == theirs.Start * stride)
                {
                    return candidate;
                }

                _ = places;
            }

            return null;
        }

        /// <summary>The two writes of a local that is seeded once and then stepped by a constant, and the seed.</summary>
        private (Instruction Seed, Instruction Step, long Start)? Steps(LocalVariable local, long by)
        {
            if (!definitions.TryGetValue(local, out var places) || places.Count != 2)
                return null;

            Instruction? seed = null, step = null;
            long start = 0;

            foreach (var place in places)
            {
                if (place is { OpCode: OpCode.Move, Operands: [_, { } from] } && Constant(from) is { } settled)
                {
                    seed = place;
                    start = settled;
                    continue;
                }

                //The step is written back through a copy, which is how single assignment form was taken apart.
                if (place is { OpCode: OpCode.Move, Operands: [_, LocalVariable stepped] }
                    && definitions.TryGetValue(stepped, out var made) && made.Count == 1
                    && made[0] is { OpCode: OpCode.Add, Operands: [_, LocalVariable again, { } amount] }
                    && ReferenceEquals(again, local) && Constant(amount) == by)
                {
                    step = made[0];
                }
            }

            return seed != null && step != null ? (seed, step, start) : null;
        }
    }

    /// <summary>A subscript and the width it was scaled by, out of the shift or the multiply that scaled it.</summary>
    /// <remarks>
    /// Internal because <see cref="ArrayWalkerTyping"/> asks the same question of the same shape - a walk can
    /// be started at a variable element as well as at a constant one - and the rule is worth stating once.
    /// </remarks>
    internal static (object Index, int Width)? Scaled(object operand, Dictionary<LocalVariable, Instruction> definitions,
        int elementWidth)
    {
        if (operand is not LocalVariable offset)
            return null;

        //An array of bytes is indexed by the subscript itself: there is nothing to scale it by, so no shift
        //and no multiply were emitted and there is nothing to recognise except the width of the element.
        if (elementWidth == 1)
            return (offset, 1);

        if (!definitions.TryGetValue(offset, out var made))
            return null;

        return made switch
        {
            { OpCode: OpCode.ShiftLeft, Operands: [_, { } value, { } by] } when Constant(by) is { } places && places is > 0 and < 8
                => (value, 1 << (int)places),
            { OpCode: OpCode.Multiply, Operands: [_, { } value, { } times] } when Constant(times) is { } width && width is > 1 and <= 128
                => (value, (int)width),
            { OpCode: OpCode.Multiply, Operands: [_, { } times, { } value] } when Constant(times) is { } width && width is > 1 and <= 128
                => (value, (int)width),
            _ => null,
        };
    }

    private static long? Constant(object operand)
    {
        try
        {
            return operand is string or LocalVariable or Register or MemoryOperand or FieldReference ? null : Convert.ToInt64(operand);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
