using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Utils;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Writes a read of an array's length back as <c>Length</c>.
///
/// An array is an object with its length in it, so <c>a.Length</c> compiles to a load at a fixed offset and
/// nothing about it says which array member that is - which leaves the condition of every <c>for</c> loop
/// over an array as a read of unmanaged memory, and the loop with it.
///
/// An element is the same story one step further along: the address is the array plus the header plus the
/// index scaled by how wide an element is, and all of that arrives as arithmetic on a pointer. Put back
/// together it is an operand the generator can write as <c>ldelem</c>, which is what the source said.
///
/// The offsets are the ones the runtime's array header uses, and a value only reaches them by being an array,
/// so there is nothing to guess.
/// </summary>
public static class ArrayAccessRecovery
{
    public static bool Run(MethodAnalysisContext method)
    {
        var length = Length(method.AppContext);

        if (length == null)
            return false;

        //Il2CppArray is the object header, then the bounds pointer, then the length.
        var lengthOffset = method.AppContext.Binary.is32Bit ? 0xC : 0x18;
        var integer = method.AppContext.SystemTypes.SystemInt32Type;
        var changed = RecoverElements(method);

        foreach (var block in method.ControlFlowGraph!.Blocks)
        {
            for (var i = 0; i < block.Instructions.Count; i++)
            {
                var instruction = block.Instructions[i];

                for (var operand = 0; operand < instruction.Operands.Count; operand++)
                {
                    if (instruction.Operands[operand] is not MemoryOperand { Index: null, Scale: 0 } memory
                        || memory.Addend != lengthOffset)
                        continue;

                    //A value known only as `System.Array` counts here, though it does not for an element:
                    //`Length` is declared on that type, so asking it for one needs nothing more, while
                    //reaching an element would need the element type, which is exactly what is missing.
                    if (memory.Base is not LocalVariable { Type: { } held } array || !HasLength(held))
                        continue;

                    //Writing to it is not something the language lets you say, so only a read is given back.
                    if (operand == 0 && instruction.OpCode is not (OpCode.Call or OpCode.CallVoid or OpCode.IndirectCall))
                        continue;

                    var value = new LocalVariable($"length{method.Locals.Count}", new Register(null, "LENGTH"), integer);
                    method.Locals.Add(value);

                    block.Instructions.Insert(i, new Instruction(instruction.Index, OpCode.Call, length, value, array));
                    instruction.Operands[operand] = value;
                    i++;
                    changed = true;
                }
            }
        }

        return changed;
    }

    /// <summary>
    /// Puts an element address back together as the array, the index and the width of an element, from the
    /// arithmetic it was compiled into.
    /// </summary>
    private static bool RecoverElements(MethodAnalysisContext method)
    {
        var elements = method.AppContext.Binary.is32Bit ? 0x10 : 0x20;
        var definitions = new Dictionary<LocalVariable, Instruction>();

        foreach (var instruction in method.ControlFlowGraph!.Instructions)
            if (instruction.Destination is LocalVariable destination)
                definitions[destination] = instruction;

        var changed = false;
        var biasedAccesses = new List<(Instruction At, int Operand, object Array, LocalVariable Index, long Bias, int Scale)>();

        foreach (var instruction in method.ControlFlowGraph.Instructions)
        {
            for (var i = 0; i < instruction.Operands.Count; i++)
            {
                //The subscript is as often in the addressing mode as in a separate addition: the compiler
                //works out the elements pointer once outside the loop and lets `ldr w11, [x10, x9, lsl #2]`
                //do the rest. There is nothing to take apart there - the base is already the elements - so
                //only the array it was reached through has to be put back.
                //Whether the elements pointer carries the array's own type or none decides nothing: what
                //makes it the elements is that it was worked out by adding the header to an array, which
                //`ElementsOf` is what proves. Requiring it to be untyped meant every such access whose type
                //had propagated across the addition - which is most of them, since it is an addition - was
                //left as a read through a pointer, and the subscript with it.
                if (instruction.Operands[i] is MemoryOperand { Index: not null, Base: LocalVariable indexed } addressed
                    && definitions.GetValueOrDefault(indexed) is { OpCode: OpCode.Add, Operands.Count: 3 } offsetting)
                {
                    if (ElementsOf(offsetting, elements) is { } indexedArray)
                    {
                        instruction.Operands[i] = new MemoryOperand(indexedArray, addressed.Index, elements, addressed.Scale);
                        changed = true;
                        continue;
                    }

                    //And where the pointer starts past element zero, the subscript is that many higher. The
                    //bias cannot go in the addressing mode - the addend there is what says "this is an
                    //element" - so it is added to the index, which is what the source said anyway.
                    if (BiasedElementsOf(offsetting, elements, addressed.Scale) is { } biased
                        && addressed.Index is LocalVariable subscriptLocal)
                    {
                        biasedAccesses.Add((instruction, i, biased.Array, subscriptLocal, biased.Bias, addressed.Scale));
                        continue;
                    }
                }

                //The header can be folded into the subscript instead of into the base: `mov w22, #4` and
                //`ldr x20, [x8, x22, lsl #3]` reads `array + 4*8`, which is element zero, and the element
                //really wanted is four less. The compiler keeps that difference in a register of its own for
                //the bounds check, so it is already a local here and nothing has to be computed.
                if (instruction.Operands[i] is MemoryOperand { Index: LocalVariable folded, Addend: 0, Base: LocalVariable holder } scaled
                    && IsArray(holder.Type)
                    && scaled.Scale > 0 && elements % scaled.Scale == 0
                    && Subscript(definitions, folded, elements / scaled.Scale) is { } subscript)
                {
                    instruction.Operands[i] = new MemoryOperand(holder, subscript, elements, scaled.Scale);
                    changed = true;
                    continue;
                }

                //The header added in an addition of its own rather than folded into the read. `_colorByEnum[c]`
                //compiles to `add x8, array, c, lsl #2` and then `add x8, x8, #0x20`, so the read is at
                //distance nought and neither shape above sees it - the one below wants the header *in* the
                //addressing mode. **Ten** of the sixty-five methods still short in the 96 originals wait on
                //exactly this, across eight files, which is the largest thing left in them.
                if (instruction.Operands[i] is MemoryOperand { Index: null, Scale: 0, Addend: 0, Base: LocalVariable reached }
                    && Defining(definitions, reached) is { OpCode: OpCode.Add, Operands.Count: 3 } adding
                    && adding.Operands[2] is long or int or ulong or uint
                    && System.Convert.ToInt64(adding.Operands[2]) == elements
                    && adding.Operands[1] is LocalVariable scaledBase
                    && Defining(definitions, scaledBase) is { OpCode: OpCode.Add, Operands.Count: 3 } indexing
                    && ArrayAndOffset(indexing, definitions) is { } reachedElement)
                {
                    instruction.Operands[i] = new MemoryOperand(reachedElement.Array, reachedElement.Index,
                        elements, reachedElement.Scale);
                    changed = true;
                    continue;
                }

                if (instruction.Operands[i] is not MemoryOperand { Index: null, Scale: 0, Base: LocalVariable address } memory
                    || memory.Addend != elements)
                    continue;

                //Already the array itself: element zero, and nothing to take apart.
                if (IsArray(address.Type))
                    continue;

                if (definitions.GetValueOrDefault(address) is not { OpCode: OpCode.Add, Operands.Count: 3 } sum)
                    continue;

                if (ArrayAndOffset(sum, definitions) is not { } element)
                    continue;

                instruction.Operands[i] = new MemoryOperand(element.Array, element.Index, elements, element.Scale);
                changed = true;
            }
        }

        //The subscripts that had to be raised, applied last so that inserting instructions cannot disturb the
        //walk above. The bias is a number the compiler had already worked out; what is added here is only the
        //place to keep the sum, which the source had as the subscript itself.
        foreach (var (at, operand, array, index, bias, scale) in biasedAccesses)
        {
            foreach (var block in method.ControlFlowGraph.Blocks)
            {
                var position = block.Instructions.IndexOf(at);

                if (position < 0)
                    continue;

                var raised = new LocalVariable($"index{method.Locals.Count}", new Register(null, "BIASED"),
                    index.Type ?? method.AppContext.SystemTypes.SystemInt32Type);

                method.Locals.Add(raised);
                block.Instructions.Insert(position, new Instruction(at.Index, OpCode.Add, raised, index, bias));
                at.Operands[operand] = new MemoryOperand(array, raised, elements, scale);
                changed = true;
                break;
            }
        }

        return changed;
    }

    /// <summary>
    /// The array, out of <c>array + elements</c> - the pointer to the first element, which is what the
    /// compiler works out once before a loop and then indexes.
    /// </summary>
    /// <summary>
    /// The local holding <paramref name="offset"/> less than this one, where the method already computes it.
    /// </summary>
    /// <remarks>
    /// Only a local it already has. Working the difference out here would mean inserting an instruction into a
    /// pass that runs to a fixpoint over a flattened view of the graph, and the compiler has no reason not to
    /// have kept it: the same value is what it compares against the length.
    /// </remarks>
    private static LocalVariable? Subscript(Dictionary<LocalVariable, Instruction> definitions, LocalVariable folded, int offset)
    {
        foreach (var (local, definition) in definitions)
        {
            if (definition.OpCode != OpCode.Subtract || definition.Operands.Count != 3
                || definition.Operands[1] is not LocalVariable from || !ReferenceEquals(from, folded))
                continue;

            var taken = definition.Operands[2] switch
            {
                int i => i,
                long l => l,
                uint u => u,
                ulong ul => (long)ul,
                _ => (long?)null,
            };

            if (taken == offset)
                return local;
        }

        return null;
    }

    /// <summary>
    /// The array and the subscript this elements pointer starts at, where it starts past element zero.
    /// </summary>
    /// <remarks>
    /// A loop that begins at element one gets its pointer as <c>array + 36</c> rather than <c>array + 32</c>,
    /// and reads <c>[p + i*4]</c> - which is <c>array[i + 1]</c>. The bias is the difference divided by the
    /// width of an element, and it only means anything when the difference is a whole number of elements.
    /// </remarks>
    private static (object Array, long Bias)? BiasedElementsOf(Instruction sum, int elements, int scale)
    {
        if (scale <= 0)
            return null;

        for (var side = 1; side <= 2; side++)
        {
            var array = sum.Operands[side];

            if (!IsArray(array switch
                {
                    LocalVariable local => local.Type,
                    FieldReference field => field.Field.FieldType,
                    _ => null,
                }))
                continue;

            var addend = sum.Operands[side == 1 ? 2 : 1];

            if (addend is not (int or long or uint or ulong))
                continue;

            var distance = System.Convert.ToInt64(addend) - elements;

            if (distance > 0 && distance % scale == 0)
                return (array, distance / scale);
        }

        return null;
    }

    private static object? ElementsOf(Instruction sum, int elements)
    {
        for (var side = 1; side <= 2; side++)
        {
            var array = sum.Operands[side];

            if (!IsArray(array switch
                {
                    LocalVariable local => local.Type,
                    FieldReference field => field.Field.FieldType,
                    _ => null,
                }))
                continue;

            var addend = sum.Operands[side == 1 ? 2 : 1];

            if (addend is int i && i == elements) return array;
            if (addend is long l && l == elements) return array;
            if (addend is uint u && u == elements) return array;
            if (addend is ulong ul && ul == (ulong)elements) return array;
        }

        return null;
    }

    /// <summary>
    /// The array, the index and the width of an element, out of <c>array + index &lt;&lt; n</c>.
    /// </summary>
    /// <remarks>
    /// The array is not always carried in a local. Where the code indexes a field directly - and a board of
    /// cells is exactly that - the addition is of the field itself, and a field says what it holds as plainly
    /// as a typed local does. Reading only locals left <c>this.cells[i]</c>, which needs no local at all, as
    /// an addition to a field followed by a read of unmanaged memory.
    /// </remarks>
    /// <summary>
    /// What really wrote a local, following the plain copies that carry a value from one register to another.
    /// </summary>
    /// <remarks>
    /// The elements pointer reaches the read through a move as often as not - a phi edge, or the copy that
    /// takes it out of the block that computed it - and stopping at the move loses the addition behind it.
    /// Bounded, so a copy that goes round a loop cannot spin.
    /// </remarks>
    private static Instruction? Defining(Dictionary<LocalVariable, Instruction> definitions, LocalVariable local)
    {
        for (var depth = 0; depth < 4; depth++)
        {
            //A local written on more than one path keeps whichever definition came last, as everywhere else in
            //this pass. **Refusing those was measured and is wrong**: the elements pointer of a loop is written
            //once outside it and once round the back edge, and both are the same array - `RefillGridInto` gave
            //`src[num4]` and `dst[num4]`, which is what the source says, and guarding took it away again.
            //What a guard would have to compare is the array and index each definition resolves to, not the
            //number of them; until something needs that, this reads the map the way its neighbours do.
            if (definitions.GetValueOrDefault(local) is not { } written)
                return null;

            if (written is not { OpCode: OpCode.Move, Operands: [_, LocalVariable copied] })
                return written;

            local = copied;
        }

        return null;
    }

    /// <summary>
    /// What may stand as an array's subscript: a local, a field, or an element of another array.
    /// </summary>
    /// <remarks>
    /// The last is `outer[inner[i]]`, where the inner access has already been recovered and is in the operand
    /// as a memory read carrying its own index. Nothing downstream needs telling - the generator writes such
    /// an operand as `inner[i]` wherever else it appears.
    /// </remarks>
    private static bool CanBeASubscript(object offset)
        => offset is LocalVariable or FieldReference or MemoryOperand { Index: not null };

    private static (object Array, object Index, int Scale)? ArrayAndOffset(
        Instruction sum, Dictionary<LocalVariable, Instruction> definitions)
    {
        for (var side = 1; side <= 2; side++)
        {
            var array = sum.Operands[side];

            if (!IsArray(array switch
                {
                    LocalVariable local => local.Type,
                    FieldReference field => field.Field.FieldType,
                    _ => null,
                }))
                continue;

            var offset = sum.Operands[side == 1 ? 2 : 1];

            //The subscript is as often a field as a local - a loop that keeps its counter in the state it
            //carries, which is what a compiled iterator does with every one of its variables. Reading only
            //locals left every array access in a coroutine as arithmetic on a pointer.
            //And as often an element of another array: `highlighted[cat.subIndices[i]]` is an access whose
            //subscript is an access, already recovered and sitting in the operand as one.
            if (!CanBeASubscript(offset))
                continue;

            //A one-byte element is indexed without a shift at all.
            if (offset is not LocalVariable held
                || definitions.GetValueOrDefault(held) is not { OpCode: OpCode.ShiftLeft, Operands.Count: 3 } shift)
                return (array, offset, 1);

            var index = shift.Operands[1];

            if (!CanBeASubscript(index) || Shift(shift.Operands[2]) is not { } by)
                return null;

            return (array, index, 1 << by);
        }

        return null;
    }

    /// <summary>How far an index is shifted to reach an element, if the operand says a sensible amount.</summary>
    private static int? Shift(object operand)
        => operand switch
        {
            int i and >= 0 and <= 4 => i,
            long l and >= 0 and <= 4 => (int)l,
            uint u and <= 4 => (int)u,
            ulong ul and <= 4 => (int)ul,
            _ => null,
        };

    private static bool IsArray(TypeAnalysisContext? type)
        => type is SzArrayTypeAnalysisContext or ArrayTypeAnalysisContext;

    /// <summary>Whether a value of this type can be asked for its length.</summary>
    private static bool HasLength(TypeAnalysisContext type)
        => IsArray(type) || type.FullName == "System.Array";

    private static MethodAnalysisContext? Length(ApplicationAnalysisContext appContext)
        => appContext.GetAssemblyByName("mscorlib")?.GetTypeByFullName("System.Array")?.Methods
            .FirstOrDefault(m => m is { Name: "get_Length", IsStatic: false, Parameters.Count: 0 });
}
