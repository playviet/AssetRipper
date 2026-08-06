using System.Collections.Generic;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Takes the flattened index of a two-dimensional array back apart.
/// </summary>
/// <remarks>
/// <para>
/// An array of two dimensions is stored flat, so <c>grid[i, j]</c> is one distance into it. The compiler works
/// that distance out from the lengths the array carries with it - a pointer to its bounds at <c>0x10</c>, and a
/// length per dimension <c>0x10</c> apart within that:
/// </para>
/// <code>
/// Move     bounds, [grid + 0x10]
/// Multiply m,      [bounds + 0x10], i     ; the second dimension's length, times the first index
/// Add      flat,   j, m
/// ...      [grid + 0x20 + flat * 4]
/// </code>
/// <para>
/// The read is already an array access by the time it gets here - base, header, index, scale - and everything
/// about it is right except that the index is flat. Nothing in C# takes a flat index into such an array, so the
/// access could not be written and the statement went with it.
/// </para>
/// <para>
/// The multiply says which index is which: the one multiplied by a dimension's length is the outer index, the
/// one added to it is the inner. Recognising the length by where it was read from - through the array's own
/// bounds pointer - is what makes that safe rather than a guess about any multiply that happens to be nearby.
/// </para>
/// </remarks>
public static class RankTwoArrayAccess
{
    /// <summary>Where an array keeps the pointer to its bounds.</summary>
    private const int Bounds = 0x10;

    /// <summary>How far apart the bounds of one dimension and the next are.</summary>
    private const int PerDimension = 0x10;

    public static void Run(MethodAnalysisContext method)
    {
        if (method.ControlFlowGraph is not { } graph)
            return;

        var header = method.AppContext.Binary.is32Bit ? 0x10 : 0x20;
        Dictionary<LocalVariable, Instruction>? definitions = null;

        foreach (var instruction in graph.Instructions)
        {
            for (var operand = 0; operand < instruction.Operands.Count; operand++)
            {
                if (instruction.Operands[operand] is not MemoryOperand { Index: LocalVariable flat, Scale: > 0 } read
                    || read.Addend != header
                    || Rank2(read.Base) is not { } array)
                    continue;

                definitions ??= Definitions(graph);

                if (Indices(definitions, flat, read.Base) is not { } indices)
                    continue;

                instruction.Operands[operand] = new MultiDimensionalElement(read.Base!, indices, array);
            }
        }

        Lengths(method, graph, definitions ??= Definitions(graph));
    }

    /// <summary>
    /// Says a dimension's length by asking the array for it.
    /// </summary>
    /// <remarks>
    /// The lengths live in the bounds the array carries, and the compiler reads them straight out to check an
    /// index against them. Read that way they resolve to nothing, so the check reads as a comparison against
    /// zero - which in <c>Diagonal</c> meant the loop never ran at all and the method threw instead of
    /// answering. <c>Array.GetLength</c> is the same number by a name the language has.
    /// </remarks>
    private static void Lengths(MethodAnalysisContext method, Graphs.ISILControlFlowGraph graph, Dictionary<LocalVariable, Instruction> definitions)
    {
        if (GetLength(method) is not { } getLength)
            return;

        foreach (var block in graph.Blocks)
        {
            for (var i = 0; i < block.Instructions.Count; i++)
            {
                var instruction = block.Instructions[i];

                for (var operand = 0; operand < instruction.Operands.Count; operand++)
                {
                    if (instruction.Operands[operand] is not MemoryOperand { Index: null, Base: LocalVariable bounds, Addend: var at } length
                        || at % PerDimension != 0
                        || definitions.GetValueOrDefault(bounds) is not { OpCode: OpCode.Move, Operands.Count: 2 } made
                        || made.Operands[1] is not MemoryOperand { Index: null, Addend: Bounds, Base: { } from }
                        || Rank2(from) is null)
                        continue;

                    var known = new LocalVariable($"length{method.Locals.Count}", new Register(null, "RANK"), method.AppContext.SystemTypes.SystemInt32Type);
                    method.Locals.Add(known);

                    block.Instructions.Insert(i, new Instruction(instruction.Index, OpCode.Call, getLength, known, from, (int)(at / PerDimension)));
                    i++;

                    instruction.Operands[operand] = known;
                }
            }
        }
    }

    private static MethodAnalysisContext? GetLength(MethodAnalysisContext method)
    {
        if (method.AppContext.GetAssemblyByName("mscorlib")?.GetTypeByFullName("System.Array") is not { } array)
            return null;

        foreach (var candidate in array.Methods)
        {
            if (candidate is { Name: "GetLength", IsStatic: false, Parameters.Count: 1 })
                return candidate;
        }

        return null;
    }

    /// <summary>The two indices a flattened one was made of, in the order the language writes them.</summary>
    private static object[]? Indices(Dictionary<LocalVariable, Instruction> definitions, LocalVariable flat, object? array)
    {
        if (definitions.GetValueOrDefault(flat) is not { OpCode: OpCode.Add, Operands.Count: 3 } sum)
            return null;

        //Either way round: the compiler is free to put the product on either side of the addition.
        for (var side = 1; side <= 2; side++)
        {
            if (sum.Operands[side] is not LocalVariable product
                || definitions.GetValueOrDefault(product) is not { OpCode: OpCode.Multiply, Operands.Count: 3 } times)
                continue;

            for (var factor = 1; factor <= 2; factor++)
            {
                //One factor has to be a dimension's length, read through this array's own bounds - that is
                //what distinguishes this from any other multiply feeding an index.
                if (!IsDimensionLength(definitions, times.Operands[factor], array))
                    continue;

                var outer = times.Operands[factor == 1 ? 2 : 1];
                var inner = sum.Operands[side == 1 ? 2 : 1];

                return [outer, inner];
            }
        }

        return null;
    }

    /// <summary>Whether a value is a length the array carries for one of its dimensions.</summary>
    private static bool IsDimensionLength(Dictionary<LocalVariable, Instruction> definitions, object operand, object? array)
    {
        //Read out of the bounds, a whole number of dimensions along, and the bounds read out of this array.
        return operand is MemoryOperand { Index: null, Base: LocalVariable bounds, Addend: var at }
               && at % PerDimension == 0
               && definitions.GetValueOrDefault(bounds) is { OpCode: OpCode.Move, Operands.Count: 2 } made
               && made.Operands[1] is MemoryOperand { Index: null, Addend: Bounds, Base: { } from }
               && Same(from, array);
    }

    private static bool Same(object a, object? b) => a switch
    {
        LocalVariable local => ReferenceEquals(local, b),
        FieldReference field => b is FieldReference other && ReferenceEquals(field.Field, other.Field) && ReferenceEquals(field.Local, other.Local),
        _ => false,
    };

    /// <summary>The array being indexed, where it has more than one dimension.</summary>
    private static ArrayTypeAnalysisContext? Rank2(object? held) => held switch
    {
        LocalVariable { Type: ArrayTypeAnalysisContext { Rank: > 1 } array } => array,
        FieldReference { Field.FieldType: ArrayTypeAnalysisContext { Rank: > 1 } array } => array,
        _ => null,
    };

    private static Dictionary<LocalVariable, Instruction> Definitions(Graphs.ISILControlFlowGraph graph)
    {
        Dictionary<LocalVariable, Instruction> definitions = [];

        foreach (var instruction in graph.Instructions)
        {
            if (instruction.Operands.Count > 0 && instruction.Operands[0] is LocalVariable written)
                definitions[written] = instruction;
        }

        return definitions;
    }
}
