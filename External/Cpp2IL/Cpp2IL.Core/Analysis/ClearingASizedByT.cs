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
            else
            {
                continue;
            }

            //The answer is the buffer, and what it holds is now known - saying so keeps the value the body
            //wanted while the size, the rounding and the alloca stop being read at all, which is what lets the
            //collection take them.
            var into = call.Operands[first] as LocalVariable;

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

    /// <summary>Whether a length is a class's own size, however much arithmetic stands in the way.</summary>
    /// <remarks>
    /// The compiler rounds the size up to a multiple of sixteen before taking it off the stack, so what the
    /// call is handed is two or three steps from the read - and which steps depends only on how the frame is
    /// laid out, never on what is being cleared.
    /// </remarks>
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
