using System.Collections.Generic;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// How wide an element of <c>T[]</c> is has an answer inside a shared body: one pointer.
/// </summary>
/// <remarks>
/// <para>
/// il2cpp compiles one body for every reference instantiation of a generic and a separate one for each value
/// instantiation - that is what sharing is - so inside a shared body <c>T</c> is always a reference, and a
/// reference is one machine word wide. The compiler cannot know that when it emits the body, so it asks the
/// class at run time and multiplies by the answer:
/// </para>
/// <code>
/// Move     v67, [array (T[])]                  // array-&gt;klass
/// Multiply v77, [v67 + 0x104], i               // element_size * i
/// Add      v78, array, v77
/// Add      v81, v78, 32                        // past the header
/// Call     memmove, answer, buffer, v81, [Il2CppClass&lt;T&gt; + 0xFC]
/// </code>
/// <para>
/// which is <c>array[i]</c> and nothing else. <see cref="ArrayElementAddress"/> already folds exactly that
/// chain back into the one addressing mode the generator reads an access out of - but only where the stride
/// is a <b>number</b>, and here it is a read of unmanaged memory. So the chain was left as arithmetic, and
/// with the read removed as runtime bookkeeping the stride quietly became nought: every one of
/// <c>ArrayExtension</c>'s six members came out reading element zero, which compiles, scores whole, and is
/// wrong.
/// </para>
/// <para>
/// Answering it is not a guess. <c>stack_slot_size</c> and <c>element_size</c> of a reference type are both
/// the pointer size, and a shared body is only ever entered with reference instantiations - the same fact
/// <see cref="SharingMeansAReference"/> already turns the value-type question on.
/// </para>
/// <para>
/// Only where the class is a generic <em>parameter's</em>, reached either as a named
/// <c>Il2CppClass&lt;T&gt;</c> or as the class of an array whose elements are one. A real type's element size
/// is a fact about that type and is not this question.
/// </para>
/// </remarks>
public static class SharedElementIsAPointer
{
    /// <summary>What a value of the type takes on the evaluation stack.</summary>
    private const long StackSlotSize = 0xFC;

    /// <summary>What one element of an array of the type takes.</summary>
    private const long ElementSize = 0x104;

    public static void Run(MethodAnalysisContext method)
    {
        if (method.ControlFlowGraph is not { } graph)
            return;

        var classOfAShared = new HashSet<LocalVariable>();

        //The class read out of an array, which is the form the stride comes through: the addressing the
        //compiler emits never goes near the runtime generic context for it.
        foreach (var instruction in graph.Instructions)
        {
            if (instruction is { OpCode: OpCode.Move, Operands: [LocalVariable held, MemoryOperand { Index: null, Scale: 0, Addend: 0, Base: LocalVariable array }] }
                && array.Type is SzArrayTypeAnalysisContext { ElementType: GenericParameterTypeAnalysisContext }
                    or ArrayTypeAnalysisContext { ElementType: GenericParameterTypeAnalysisContext })
            {
                classOfAShared.Add(held);
            }
        }

        var pointerSize = method.AppContext.Binary.is32Bit ? 4L : 8L;

        foreach (var instruction in graph.Instructions)
        {
            //Only where the size is a **stride**. The same read is also what a copy of a `T` is sized by, and
            //`ClearingASizedByT` recognises that copy by tracing its size operand back to a class - so
            //answering it there would take the copy's own recovery with it.
            if (instruction.OpCode != OpCode.Multiply)
                continue;

            for (var i = 0; i < instruction.Operands.Count; i++)
            {
                if (instruction.Operands[i] is not MemoryOperand { Index: null, Scale: 0, Base: LocalVariable klass } read
                    || read.Addend is not (StackSlotSize or ElementSize))
                {
                    continue;
                }

                if (klass.Type is RuntimeClassTypeAnalysisContext { RepresentedType: GenericParameterTypeAnalysisContext }
                    || classOfAShared.Contains(klass))
                {
                    instruction.Operands[i] = pointerSize;
                }
            }
        }
    }
}
