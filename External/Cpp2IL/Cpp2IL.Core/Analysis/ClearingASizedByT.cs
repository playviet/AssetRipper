using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Says what a <c>memset</c> or <c>memcpy</c> the size of a shared <c>T</c> leaves in its buffer.
/// </summary>
/// <remarks>
/// <para>
/// A body shared between reference instantiations cannot know how big a <c>T</c> is, so <c>T val = default;</c>
/// is a run-time size read, an alloca of that size, and a <c>memset</c> to zero:
/// </para>
/// <code>
/// _ = "Unmanaged memory load: [v52 (Il2CppClass&lt;T&gt;)+FC]";
/// long num2 = 0xFL &amp; 0x1FFFFFFF0L;
/// long num3 = num4 - num2;
/// _ = "Method not found @4AFBB90";      // memset
/// </code>
/// <para>
/// The size is genuinely per-instantiation and unanswerable as a number, and it is never needed as one: the
/// export already writes <c>T val = default(T);</c>, and this is the machinery behind that, not something
/// else. Nothing can read the buffer either - it is unmanaged memory - so clearing it says nothing that
/// survives, and taking the call away is what lets everything feeding it be seen as dead.
/// </para>
/// <para>
/// A copy the same size is the same idea one step on: the buffer now holds whatever the other buffer held,
/// and where that is known - because this pass named it when it was filled - the copy says so. Where it is
/// not, the copy is between two places nothing can name and it is left alone.
/// </para>
/// <para>
/// Only where the length came from a class's own size. A <c>memset</c> of a length the program worked out is
/// somebody's <c>Array.Clear</c> and is left alone.
/// </para>
/// </remarks>
public static class ClearingASizedByT
{
    public static void Run(MethodAnalysisContext method)
    {
        if (method.ControlFlowGraph is not { } graph || method.AppContext.Binary is not LibCpp2IL.Elf.ElfFile binary)
            return;

        var definitions = new Dictionary<LocalVariable, Instruction>();

        foreach (var instruction in graph.Instructions)
            if (instruction.Destination is LocalVariable destination)
                definitions.TryAdd(destination, instruction);

        //What each scratch buffer holds, named by the value the operation that filled it answered with. The
        //buffers themselves are addresses and nothing can be said about them; what they carry is a `T`.
        var holds = new Dictionary<LocalVariable, LocalVariable>();
        var pointerSize = method.AppContext.Binary.is32Bit ? 4 : 8;

        foreach (var call in graph.Instructions)
        {
            if (call.OpCode is not (OpCode.Call or OpCode.CallVoid) || call.Operands.Count < 2
                || call.Operands[0] is not ulong address)
            {
                continue;
            }

            var named = binary.ImportedFunctionAt(address);

            if (named is not ("memset" or "memcpy" or "memmove"))
                continue;

            //`Call` puts its answer at operand one and `CallVoid` has none, so the arguments start after.
            var first = call.OpCode == OpCode.Call ? 2 : 1;

            if (call.Operands.Count < first + 3 || !SizeOfAClass(call.Operands[first + 2], definitions, 0))
                continue;

            //What the buffer is going to hold: nothing, for a clear; whatever the other buffer held, for a
            //copy - and if that is not known, this is a copy between two places nothing can name and there is
            //nothing to say about it.
            object filled;
            var fromAnElement = false;

            if (named == "memset")
            {
                if (Constant(call.Operands[first + 1]) is not 0)
                    continue;

                filled = 0;
            }
            else if (call.Operands[first + 1] is LocalVariable from && holds.TryGetValue(from, out var carried))
            {
                filled = carried;
            }
            //Or the source is not a buffer at all but the value itself, which is what a `T` handed straight to
            //the copy is: `values[^1] = value` compiles to `memcpy(&element, value, sizeof(T))` where the
            //second operand is already typed `T`, because a reference instantiation passes the object and only
            //a value type would pass its address. An integer there is an address and says nothing.
            else if (call.Operands[first + 1] is LocalVariable value && IsAValue(value.Type))
            {
                filled = value;
            }
            //Or the source is a field of the object, whose address a shared body has to ask the runtime for
            //because it cannot know the offset. <see cref="FieldFromItsRuntimeInfo"/> runs directly above this
            //and names the helper's answer wherever every use of it is a place an address belongs - a copy's
            //source being one - so by here the operand is the field itself and reading it back is `x.field`.
            else if (call.Operands[first + 1] is FieldReference source)
            {
                filled = source;
            }
            //Or it is an element of an array of `T`, whose address the compiler worked out a step at a time
            //because it could not know the stride when it compiled the body. `SharedElementIsAPointer` has
            //answered the stride by then - a shared body's `T` is a reference - so what is left is the
            //ordinary chain, and reading it back is `array[i]`. `ArrayElementAddress` folds the same chain
            //wherever it is *read through*; here the only use is this copy, so it is folded here instead.
            else if (ElementAt(call.Operands[first + 1], definitions, pointerSize) is { } element)
            {
                filled = element;
                fromAnElement = true;
            }
            else
            {
                continue;
            }

            //Or what is being *filled* is an element of an array of `T`, and then the copy is a store:
            //`output[index] = input[index]` compiles to `memcpy(&output[i], &input[i], sizeof(T))` with both
            //addresses worked out the same way. Recovering only the source writes the value into the
            //destination *address*, which says nothing and loses the store outright - so the two halves have
            //to be taken together, and this is the only place both are in hand.
            //
            //**Never for the clear.** `memset(&array[i], 0, sizeof(T))` would become `array[i] = (T)0`, and
            //nought does not convert to an unconstrained `T` - the same trap as the return buffer below.
            if (named != "memset" && ElementAt(call.Operands[first], definitions, pointerSize) is { } destination)
            {
                call.OpCode = OpCode.Move;
                call.Operands = [destination, filled];
                continue;
            }

            //Where only the *source* was an element, the value goes into the buffer the copy filled -
            //**not** into the call's answer, which is a register the copy happens to return and nothing reads.
            //Saying it there let the collection take the whole chain, and `RandomInArray` returned an empty
            //buffer.
            if (fromAnElement && call.Operands[first] is LocalVariable buffer)
            {
                call.OpCode = OpCode.Move;
                call.Operands = [buffer, filled];
                continue;
            }

            //The answer is the buffer, and what it holds is now known - saying so keeps the value the body
            //wanted while the size, the rounding and the alloca stop being read at all, which is what lets the
            //collection take them.
            var into = call.Operands[first] as LocalVariable;

            //Except where the buffer being filled is the method's **own return buffer**. A fully shared body
            //answers through a pointer its caller passed, and `SeedSharedReturnBuffer` has already made the
            //`Return` name that buffer - so writing the value into the call's answer register instead leaves
            //the returned local assigned by nothing at all, which the generator writes as `return default(T)`.
            //`InvokerThunk.FoldAnswerIntoTheReturn` states the same fact for the one copy it knows about;
            //this is it for a copy out of anywhere else. Only where the answer register is dead, since it is
            //the destination pointer that `memcpy` hands back and something may still be reading it.
            //
            //**And never for the clear.** A buffer nothing assigns is already written out as `T val =
            //default(T); return val;`, which is exactly what clearing it to nought means and is the one
            //spelling C# has for it: saying it here instead makes the generator write `return (T)null`, and
            //`null` does not convert to an unconstrained `T`, so a `return default(T)` that compiled becomes
            //a commented statement. `ArrayExtension::GetRandom` and both `IEnumerableExtension::GetRandom`
            //were lost to exactly that before this condition was added.
            if (named != "memset" && into is { Name: InvokerThunk.ReturnBuffer }
                && !ReadAnywhere(graph, call.Operands.ElementAtOrDefault(1) as LocalVariable, call))
            {
                call.OpCode = OpCode.Move;
                call.Operands = [into, filled];
                continue;
            }

            if (first == 2 && call.Operands[1] is LocalVariable answer)
            {
                call.OpCode = OpCode.Move;
                call.Operands = [answer, filled];

                if (into != null)
                    holds[into] = answer;
            }
            else
            {
                call.OpCode = OpCode.Nop;
                call.Operands = [];
            }
        }
    }

    /// <summary>Whether anything but the copy itself reads a local, in any position.</summary>
    private static bool ReadAnywhere(Graphs.ISILControlFlowGraph graph, LocalVariable? local, Instruction copy)
    {
        if (local is null)
            return false;

        foreach (var instruction in graph.Instructions)
        {
            if (ReferenceEquals(instruction, copy))
                continue;

            for (var operand = instruction.IsAssignment ? 1 : 0; operand < instruction.Operands.Count; operand++)
                switch (instruction.Operands[operand])
                {
                    case LocalVariable other when ReferenceEquals(other, local):
                    case MemoryOperand memory when ReferenceEquals(memory.Base, local) || ReferenceEquals(memory.Index, local):
                    case FieldReference reference when ReferenceEquals(reference.Local, local):
                        return true;
                }
        }

        return false;
    }

    /// <summary>
    /// Whether what a copy was handed is the value rather than somewhere to find it.
    /// </summary>
    /// <remarks>
    /// An address is untyped or wears the integer type the arithmetic that made it gave it. Anything the
    /// analysis has called a managed type is the thing itself - and for a shared body that is a `T`, which is
    /// a reference, which is passed rather than pointed at.
    /// </remarks>
    private static bool IsAValue(TypeAnalysisContext? type)
        => type is not null && type is not { Namespace: nameof(System), Name: "Int64" or "UInt64" or "IntPtr" or "UIntPtr" or "Int32" or "UInt32" };

    /// <summary>Whether a length is a class's own size, however much arithmetic stands in the way.</summary>
    /// <remarks>
    /// The compiler rounds the size up to a multiple of sixteen before taking it off the stack, so what the
    /// call is handed is two or three steps from the read - and which steps depends only on how the frame is
    /// laid out, never on what is being cleared.
    /// </remarks>
    /// <summary>
    /// The array element an address is, where it was worked out as <c>array + index*width + 0x20</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two shapes, and which one the compiler emits depends only on whether the step past the header could be
    /// lifted out of the loop:
    /// </para>
    /// <code>
    /// Add v78, array, v77      ; the scaled index      Add v137, array, 32   ; hoisted, once
    /// Add v81, v78, 32         ; past the header       Add v227, v137, v226  ; the scaled index
    /// </code>
    /// <para>
    /// Only the first was recognised, and the second is what <c>ArrayExtension::ResizeArray</c>,
    /// <c>AddRange</c> and <c>CompareArray</c> are made of - the note on this method used to say those
    /// stepped a walking pointer with no element address to find, and that reading was wrong: the multiply is
    /// there, per iteration, and only the header had moved.
    /// </para>
    /// </remarks>
    private static MemoryOperand? ElementAt(object operand, Dictionary<LocalVariable, Instruction> definitions, int pointerSize)
    {
        var header = pointerSize == 4 ? 0x10 : 0x20;

        if (operand is not LocalVariable address || !definitions.TryGetValue(address, out var made)
            || made is not { OpCode: OpCode.Add, Operands: [_, { } first, { } second] })
        {
            return null;
        }

        //Past the header as the last step, which is what the compiler emits where the index is free at the
        //access.
        if (Constant(second) == header && first is LocalVariable summed
            && definitions.TryGetValue(summed, out var sum)
            && sum is { OpCode: OpCode.Add, Operands: [_, { } left, { } right] }
            && Access(left, right, definitions, pointerSize, header) is { } straight)
        {
            return straight;
        }

        //Or past the header first and hoisted, the scaled index being all that is left inside the loop.
        return Hoisted(first, second, definitions, pointerSize, header)
            ?? Hoisted(second, first, definitions, pointerSize, header);
    }

    /// <summary>An element reached through a base that is the array's own first element, worked out once.</summary>
    private static MemoryOperand? Hoisted(object elements, object offset, Dictionary<LocalVariable, Instruction> definitions,
        int pointerSize, int header)
    {
        if (elements is not LocalVariable held || !definitions.TryGetValue(held, out var made)
            || made is not { OpCode: OpCode.Add, Operands: [_, { } one, { } other] })
        {
            return null;
        }

        //Either order again: the array plus the header, or the header plus the array.
        if (Constant(other) == header)
            return Access(one, offset, definitions, pointerSize, header);

        if (Constant(one) == header)
            return Access(other, offset, definitions, pointerSize, header);

        return null;
    }

    /// <summary>The access an array and a scaled subscript stand for, in either order.</summary>
    private static MemoryOperand? Access(object left, object right, Dictionary<LocalVariable, Instruction> definitions,
        int pointerSize, int header)
    {
        foreach (var (holder, offset) in new[] { (left, right), (right, left) })
        {
            if (holder is not LocalVariable array
                || array.Type is not (SzArrayTypeAnalysisContext or ArrayTypeAnalysisContext)
                || ArrayTypeInference.Width(Elements(array.Type)!, pointerSize) is not { } width || width <= 0)
            {
                continue;
            }

            if (offset is not LocalVariable scaled || !definitions.TryGetValue(scaled, out var step))
                continue;

            var index = step switch
            {
                { OpCode: OpCode.Multiply, Operands: [_, LocalVariable subscript, { } by] } when Constant(by) == width => subscript,
                { OpCode: OpCode.Multiply, Operands: [_, { } by, LocalVariable subscript] } when Constant(by) == width => subscript,
                { OpCode: OpCode.ShiftLeft, Operands: [_, LocalVariable subscript, { } places] }
                    when Constant(places) is { } shift && 1L << (int)shift == width => subscript,
                _ => null,
            };

            if (index != null)
                return new MemoryOperand(array, index, header, (int)width);
        }

        return null;
    }

    /// <summary>What an array holds, however it is written.</summary>
    private static TypeAnalysisContext? Elements(TypeAnalysisContext? type) => type switch
    {
        SzArrayTypeAnalysisContext single => single.ElementType,
        ArrayTypeAnalysisContext ranked => ranked.ElementType,
        _ => null,
    };

    private static bool SizeOfAClass(object operand, Dictionary<LocalVariable, Instruction> definitions, int depth)
    {
        if (depth > 4)
            return false;

        if (operand is MemoryOperand { Index: null, Scale: 0, Base: LocalVariable held } read)
        {
            return held.Type is RuntimeClassTypeAnalysisContext
                && read.Addend is Il2CppClassLayout.StackSlotSize or Il2CppClassLayout.ElementSize;
        }

        if (operand is not LocalVariable local || !definitions.TryGetValue(local, out var made))
            return false;

        return made.OpCode switch
        {
            OpCode.Move or OpCode.Add or OpCode.And or OpCode.Subtract or OpCode.Multiply
                => made.Operands.Skip(1).Any(o => SizeOfAClass(o, definitions, depth + 1)),
            _ => false,
        };
    }

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
