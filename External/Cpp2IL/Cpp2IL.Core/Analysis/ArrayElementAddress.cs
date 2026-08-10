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
        Dictionary<LocalVariable, Address> chains = new();

        foreach (var instruction in graph.Instructions)
        {
            if (instruction.Operands.Count > 0 && instruction.Operands[0] is LocalVariable written)
                definitions.TryAdd(written, instruction);
        }

        foreach (var instruction in graph.Instructions)
        {
            switch (instruction)
            {
                case { OpCode: OpCode.Add, Operands: [LocalVariable made, { } left, { } right] }:
                    if (Extend(left, right, chains, definitions, pointerSize) is { } forwards)
                        chains[made] = forwards;
                    else if (Extend(right, left, chains, definitions, pointerSize) is { } backwards)
                        chains[made] = backwards;
                    break;

                case { OpCode: OpCode.Move, Operands: [LocalVariable copy, LocalVariable from] }
                    when chains.TryGetValue(from, out var carried):
                    chains[copy] = carried;
                    break;
            }
        }

        if (chains.Count == 0)
            return;

        foreach (var instruction in graph.Instructions)
        {
            for (var i = 0; i < instruction.Operands.Count; i++)
            {
                if (instruction.Operands[i] is not MemoryOperand { Index: null, Base: LocalVariable through } read)
                    continue;

                if (!chains.TryGetValue(through, out var address))
                    continue;

                var addend = address.Constant + read.Addend;

                //With the subscript in a register there is nowhere left to put a distance into the element, so
                //only an access to the element itself can be written; with a constant subscript the addend
                //carries both and the generator takes it apart again.
                if (address.Index != null ? addend != header : addend < header)
                    continue;

                instruction.Operands[i] = new MemoryOperand(address.Array, address.Index, addend, address.Scale);
            }
        }
    }

    /// <summary>
    /// The address <paramref name="onto"/> becomes once <paramref name="added"/> is added to it, where that is
    /// still an address into the same array.
    /// </summary>
    private static Address? Extend(object onto, object added, Dictionary<LocalVariable, Address> chains,
        Dictionary<LocalVariable, Instruction> definitions, int pointerSize)
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
        if (ArrayTypeInference.Width(running.Element, pointerSize) is not { } stride
            || running.Index != null || Scaled(added, definitions, stride) is not { } scaled
            || stride != scaled.Width)
        {
            return null;
        }

        return running with { Index = scaled.Index, Scale = scaled.Width };
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

    /// <summary>A subscript and the width it was scaled by, out of the shift or the multiply that scaled it.</summary>
    private static (object Index, int Width)? Scaled(object operand, Dictionary<LocalVariable, Instruction> definitions,
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
