using System.Collections.Generic;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Gives back the subscript a loop over an array threw away.
/// </summary>
/// <remarks>
/// <para>
/// A loop over an array does not have to keep one. The compiler can start a pointer at the first element and
/// step it by the width of one, counting the elements down separately, which is fewer instructions and needs
/// no bounds arithmetic:
/// </para>
/// <code>
/// Add  p,  this._boardData (CF.CellData[]), 32   ; the header is 0x20, so this is &amp;array[0]
/// Move e,  [p]                                   ; the element
/// Add  p2, p, 8                                  ; and on to the next
/// </code>
/// <para>
/// Two things are lost with the subscript. The element read through <c>p</c> has no type, so the fields read
/// out of it resolve to nothing - one <c>foreach</c> over a board of cells is sixteen unresolved reads in
/// <c>BoardController.HasAnyActiveCell</c> alone. And the walk itself cannot be written down: <c>_boardData +
/// 32L</c> is not something C# will say, so even once the fields are known the statements holding them are
/// commented out anyway.
/// </para>
/// <para>
/// So both are given back. The start says what is being walked - an array plus exactly its header is the first
/// element and can be nothing else - and from there the element type follows, and a subscript can be counted
/// alongside the pointer and put back into the read. What comes out is an ordinary indexed access, which the
/// generator already knows how to write as <c>ldelem</c>.
/// </para>
/// <para>
/// The pointer arithmetic is left where it is. Nothing reads it once the reads have been rewritten, so it goes
/// the way any other dead value goes, and taking it out here would mean reasoning about a loop this pass has
/// deliberately not tried to understand.
/// </para>
/// </remarks>
public static class ArrayWalkerTyping
{
    /// <summary>Where a walk starts from, and what it is walking over.</summary>
    private readonly struct Walk(object array, TypeAnalysisContext element, LocalVariable subscript, int width, long start)
    {
        public readonly object Array = array;
        public readonly TypeAnalysisContext Element = element;
        public readonly LocalVariable Subscript = subscript;

        /// <summary>How many bytes one element takes, so a distance can be read as a number of them.</summary>
        public readonly int Width = width;

        /// <summary>Which element the walk begins at - not always the first.</summary>
        public readonly long Start = start;
    }

    public static void Run(MethodAnalysisContext method)
    {
        if (method.ControlFlowGraph is not { } graph)
            return;

        var header = method.AppContext.Binary.is32Bit ? 0x10 : 0x20;
        var walking = Chains(method, graph, header);

        //Every addition this could have started or continued a walk from, for one named method. A walk that
        //never starts is invisible from the export - the pointer arithmetic simply stays - and the two halves
        //of the question, whether the array is typed yet and whether the addend is one this recognises, are
        //not answerable from the finished ISIL either, because the pass ran long before it.
        if (System.Environment.GetEnvironmentVariable("WALK_TRACE") is { } wanted && method.Name.Contains(wanted))
            foreach (var instruction in graph.Instructions)
                if (instruction.OpCode == OpCode.Add && instruction.Operands is [LocalVariable made, ..])
                    System.Console.WriteLine($"WALK {(walking?.ContainsKey(made) == true ? "yes" : "no ")} {instruction}");

        if (walking is null)
            return;

        Count(graph, walking, header);

        foreach (var block in graph.Blocks)
        {
            for (var i = 0; i < block.Instructions.Count; i++)
            {
                var instruction = block.Instructions[i];

                for (var operand = 0; operand < instruction.Operands.Count; operand++)
                {
                    if (instruction.Operands[operand] is not MemoryOperand { Base: LocalVariable through } read
                        || !walking.TryGetValue(through, out var walk)
                        || walk.Width <= 0 || read.Addend % walk.Width != 0)
                        continue;

                    //How far from the pointer's own element the read is. A walk the compiler stepped ahead of
                    //its reads has them at negative distances - `[p - 8]`, `[p - 4]`, `[p]` is three adjacent
                    //elements read through one pointer already two on.
                    var away = (int)(read.Addend / walk.Width);

                    //A pointer read with no index is at the element its own subscript names, that far along.
                    if (read.Index is null)
                    {
                        var at = walk.Subscript;

                        if (away != 0)
                        {
                            at = new LocalVariable($"at{method.Locals.Count}", new Register(null, "WALK"), method.AppContext.SystemTypes.SystemInt32Type);
                            method.Locals.Add(at);

                            block.Instructions.Insert(i, new Instruction(instruction.Index, OpCode.Add, at, walk.Subscript, away));
                            i++;
                        }

                        instruction.Operands[operand] = new MemoryOperand(walk.Array, at, header);
                    }
                    //And one read with an index is that many elements further on - the inner loop of a walk
                    //over a jagged array indexes rather than steps, because the pointer it indexes from is set
                    //afresh for every row. So the element is the pointer's own subscript plus the index.
                    else if (read.Index is LocalVariable offset)
                    {
                        var at = new LocalVariable($"at{method.Locals.Count}", new Register(null, "WALK"), method.AppContext.SystemTypes.SystemInt32Type);
                        method.Locals.Add(at);

                        block.Instructions.Insert(i, new Instruction(instruction.Index, OpCode.Add, at, walk.Subscript, offset));
                        i++;

                        if (away != 0)
                        {
                            var further = new LocalVariable($"at{method.Locals.Count}", new Register(null, "WALK"), method.AppContext.SystemTypes.SystemInt32Type);
                            method.Locals.Add(further);

                            block.Instructions.Insert(i, new Instruction(instruction.Index, OpCode.Add, further, at, away));
                            i++;
                            at = further;
                        }

                        instruction.Operands[operand] = new MemoryOperand(walk.Array, at, header);
                    }
                    else
                    {
                        continue;
                    }

                    if (instruction.OpCode == OpCode.Move && instruction.Operands[0] is LocalVariable held && held.Type is null)
                        held.Type = walk.Element;
                }
            }
        }

        Forget(graph, walking);
    }

    /// <summary>
    /// Takes out the pointer arithmetic, now that nothing is reading it.
    /// </summary>
    /// <remarks>
    /// It will not go on its own. The walk is a cycle - the step is written from the pointer and the pointer
    /// is written back from the step - so every part of it is read by another part, and dead code elimination
    /// asks only whether something is read. Left in, it is written out as <c>_boardData + 32L</c> and
    /// <c>obj2 = obj + 8L</c>, which is not something C# will say, so the statements are commented out and the
    /// loop reads as though nothing had been recovered.
    /// </remarks>
    private static void Forget(Graphs.ISILControlFlowGraph graph, Dictionary<LocalVariable, Walk> walking)
    {
        //Only where the whole walk has been accounted for. A pointer read at some other distance is one this
        //did not rewrite, and taking the arithmetic away would take the value it still needs with it.
        foreach (var instruction in graph.Instructions)
        {
            if (instruction.Operands.Count > 0 && instruction.Operands[0] is LocalVariable written && walking.ContainsKey(written))
                continue;

            foreach (var operand in instruction.Operands)
            {
                if (operand is LocalVariable used && walking.ContainsKey(used))
                    return;

                if (operand is MemoryOperand memory
                    && ((memory.Base is LocalVariable @base && walking.ContainsKey(@base))
                        || (memory.Index is LocalVariable index && walking.ContainsKey(index))))
                    return;
            }
        }

        foreach (var instruction in graph.Instructions)
        {
            if (instruction.Operands.Count > 0 && instruction.Operands[0] is LocalVariable written && walking.ContainsKey(written))
            {
                instruction.OpCode = OpCode.Nop;
                instruction.Operands = [];
            }
        }
    }

    /// <summary>Every pointer that is walking an array, and the walk it belongs to.</summary>
    private static Dictionary<LocalVariable, Walk>? Chains(MethodAnalysisContext method, Graphs.ISILControlFlowGraph graph, int header)
    {
        Dictionary<LocalVariable, Walk>? walking = null;

        //Round and round until nothing new is learnt, because a walker is stepped inside a loop and the step
        //is written before the graph comes back to where it started.
        var learnt = true;

        while (learnt)
        {
            learnt = false;

            foreach (var instruction in graph.Instructions)
            {
                if (instruction.Operands.Count < 2 || instruction.Operands[0] is not LocalVariable destination
                    || (walking is not null && walking.ContainsKey(destination)))
                    continue;

                object array;
                TypeAnalysisContext element;
                int width;
                long start;

                //Copying a walker, or stepping it: still the same walk, over the same array.
                if (instruction.OpCode == OpCode.Move && instruction.Operands is [_, LocalVariable copied]
                    && walking is not null && walking.TryGetValue(copied, out var same))
                {
                    (array, element, width, start) = (same.Array, same.Element, same.Width, same.Start);
                }
                else if (instruction.OpCode == OpCode.Add && instruction.Operands.Count == 3 && Amount(instruction.Operands[2]) is not null
                         && instruction.Operands[1] is LocalVariable stepped
                         && walking is not null && walking.TryGetValue(stepped, out var stillWalking))
                {
                    (array, element, width, start) = (stillWalking.Array, stillWalking.Element, stillWalking.Width, stillWalking.Start);
                }
                //Or the start of one: an array plus the header is the first element, and the header plus a
                //whole number of elements is that element - clang starts a walk part-way in whenever the
                //source does, and `CellData::ComputeCats` begins at `grid[2]`.
                else if (instruction.OpCode == OpCode.Add && instruction.Operands.Count == 3
                         && Amount(instruction.Operands[2]) is { } from0 && from0 >= header
                         && ElementOf(instruction.Operands[1]) is { } starts
                         && WidthOf(starts, method) is { } elementWidth
                         && (from0 - header) % elementWidth == 0
                         && (instruction.Operands[1] is not MemoryOperand nested
                             ? instruction.Operands[1]
                             : Holding(graph, nested)) is { } from)
                {
                    (array, element, width, start) = (from, starts, elementWidth, (from0 - header) / elementWidth);
                }
                else
                {
                    continue;
                }

                //A subscript of its own for each pointer, not one for the walk. The step writes a *new*
                //pointer and the read goes through the old one - `Add p2, p, 4` then `[p]` - so a single
                //counter shared by both is stepped before the read that should have seen it unstepped.
                //`CountOf` came back reading `cells[1]` first and running off the end. Giving each pointer
                //its own subscript makes the count follow the pointer's own dataflow, whatever the order.
                var subscript = new LocalVariable($"index{method.Locals.Count}", new Register(null, "WALK"), method.AppContext.SystemTypes.SystemInt32Type);
                method.Locals.Add(subscript);

                (walking ??= [])[destination] = new Walk(array, element, subscript, width, start);
                learnt = true;
            }
        }

        return walking;
    }

    /// <summary>
    /// Puts a subscript beside the pointer: nought where the walk starts, and one more wherever it steps.
    /// </summary>
    private static void Count(Graphs.ISILControlFlowGraph graph, Dictionary<LocalVariable, Walk> walking, int header)
    {
        foreach (var block in graph.Blocks)
        {
            for (var i = 0; i < block.Instructions.Count; i++)
            {
                var instruction = block.Instructions[i];

                if (instruction.Operands.Count < 2 || instruction.Operands[0] is not LocalVariable destination
                    || !walking.TryGetValue(destination, out var walk))
                    continue;

                //Copying a pointer copies where it has got to.
                if (instruction.OpCode == OpCode.Move && instruction.Operands is [_, LocalVariable copied]
                    && walking.TryGetValue(copied, out var from))
                {
                    block.Instructions.Insert(i + 1, new Instruction(instruction.Index, OpCode.Move, walk.Subscript, from.Subscript));
                    i++;
                    continue;
                }

                if (instruction.OpCode != OpCode.Add || instruction.Operands.Count != 3 || Amount(instruction.Operands[2]) is not { } by)
                    continue;

                //A step is however many elements the distance covers - usually one, but a loop unrolled by the
                //compiler steps by several at a time.
                if (instruction.Operands[1] is LocalVariable stepped && walking.TryGetValue(stepped, out var before))
                {
                    if (before.Width <= 0 || by % before.Width != 0)
                        continue;

                    block.Instructions.Insert(i + 1, new Instruction(instruction.Index, OpCode.Add, walk.Subscript, before.Subscript, (int)(by / before.Width)));
                    i++;
                    continue;
                }

                //And the start of the walk, at whichever element it begins from.
                if (walk.Width > 0 && by == header + walk.Start * walk.Width)
                {
                    block.Instructions.Insert(i + 1, new Instruction(instruction.Index, OpCode.Move, walk.Subscript, (int)walk.Start));
                    i++;
                }
            }
        }
    }

    /// <summary>How many bytes one element of an array of this takes.</summary>
    private static int? WidthOf(TypeAnalysisContext element, MethodAnalysisContext method)
    {
        if (!element.IsValueType)
            return method.AppContext.Binary.is32Bit ? 4 : 8;

        if (element.IsEnumType)
            return 4;

        return element.FullName switch
        {
            "System.Boolean" or "System.SByte" or "System.Byte" => 1,
            "System.Int16" or "System.UInt16" or "System.Char" => 2,
            "System.Int32" or "System.UInt32" or "System.Single" => 4,
            "System.Int64" or "System.UInt64" or "System.Double" or "System.IntPtr" or "System.UIntPtr" => 8,
            _ => Aapcs64.SizeOf(element) is { } size and > 0 and < int.MaxValue ? (int)size : null,
        };
    }

    /// <summary>The element type of whatever holds an array, where it is one.</summary>
    private static TypeAnalysisContext? ElementOf(object operand) => Element(Held(operand));

    /// <summary>
    /// What a value operand holds.
    /// </summary>
    /// <remarks>
    /// An array element counts. A jagged array is an array of arrays, so the inner walk starts from
    /// <c>rows[r] + 0x20</c> - and what it walks is the elements of <c>rows[r]</c>, which is one step further
    /// in than the outer array. Without this the inner loop of every jagged walk stays unrecovered while the
    /// outer one reads perfectly.
    /// </remarks>
    private static TypeAnalysisContext? Held(object operand) => operand switch
    {
        LocalVariable local => local.Type,
        FieldReference field => field.Field.FieldType,
        MemoryOperand element => element.Base is { } held ? Element(Held(held)) : null,
        _ => null,
    };

    private static TypeAnalysisContext? Element(TypeAnalysisContext? type) => type switch
    {
        SzArrayTypeAnalysisContext szArray => szArray.ElementType,
        ArrayTypeAnalysisContext { Rank: 1 } singleDimension => singleDimension.ElementType,
        _ => null,
    };

    /// <summary>
    /// A local already holding what this operand reads, so the walk has something the generator can name.
    /// </summary>
    /// <remarks>
    /// A walk that starts from an array element cannot keep the element read as its array - the generator
    /// writes an array access from a local or a field, not from another read. The compiler has invariably put
    /// the inner array in a register of its own first, so the local is there to be found; where it is not, the
    /// walk is left alone rather than reading the array twice.
    /// </remarks>
    private static object? Holding(Graphs.ISILControlFlowGraph graph, MemoryOperand read)
    {
        foreach (var instruction in graph.Instructions)
        {
            if (instruction.OpCode == OpCode.Move
                && instruction.Operands is [LocalVariable holder, MemoryOperand candidate]
                && ReferenceEquals(candidate.Base, read.Base) && ReferenceEquals(candidate.Index, read.Index)
                && candidate.Addend == read.Addend && candidate.Scale == read.Scale)
                return holder;
        }

        return null;
    }

    private static long? Amount(object operand) => operand switch
    {
        int value => value,
        long value => value,
        uint value => value,
        ulong value => (long)value,
        _ => null,
    };
}
