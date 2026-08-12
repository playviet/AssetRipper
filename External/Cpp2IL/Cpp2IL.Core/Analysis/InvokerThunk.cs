using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Reads a call made through a runtime method's invoker thunk as the call it stands for.
/// </summary>
/// <remarks>
/// <para>
/// Where a callee's signature mentions a type the caller's body cannot know - the shared <c>T</c> of a
/// generic, most often a return type - il2cpp does not call the method at all. It calls the thunk recorded
/// at <c>MethodInfo::invoker_method</c>, whose job is to unpack a uniform frame into whatever the callee
/// actually takes, and whose own convention is nothing like the callee's:
/// </para>
/// <code>
/// X0 = the method's own code pointer     X3 = void** args, one slot per declared parameter
/// X1 = the MethodInfo                    X4 = void*  where the answer goes - the answer is NOT in X0
/// X2 = the receiver
/// </code>
/// <para>
/// Read as the callee's own convention that puts the <c>MethodInfo</c> where the first argument belongs and
/// the receiver where the callee is - and the resulting call compiles, so nothing but running it says
/// otherwise. What the arguments really are is one hop further out: each slot of the frame holds the value
/// itself where that is a reference, and its address where it is not.
/// </para>
/// </remarks>
public static class InvokerThunk
{
    /// <summary>Where the argument registers begin among a call-through-a-register's operands.</summary>
    private const int FirstRegister = 2;

    private const int Receiver = FirstRegister + 2;
    private const int Frame = FirstRegister + 3;
    private const int Answer = FirstRegister + 4;

    /// <summary>What one slot of the frame takes up, every one of them being a pointer.</summary>
    private const int SlotWidth = 8;

    /// <summary>
    /// The receiver, the arguments and where the answer is to be put, taken out of a call made through the
    /// thunk. Null where any one argument cannot be read back out of the frame, because a call missing an
    /// argument is not the call that was made.
    /// </summary>
    public static (object Receiver, List<object> Arguments, object? Answer)? Read(
        MethodAnalysisContext callee, Instruction call, MethodAnalysisContext method)
    {
        var read = Unpack(callee, call, method);

        if (Trace)
            System.Console.Error.WriteLine(
                $"INVOKER {(read == null ? "refused" : "read   ")} {callee.Parameters.Count}p {method.DeclaringType?.Name}::{method.Name} -> {callee.DeclaringType?.Name}::{callee.Name}");

        return read;
    }

    private static readonly bool Trace = System.Environment.GetEnvironmentVariable("INVOKER_TRACE") is not null;

    private static (object Receiver, List<object> Arguments, object? Answer)? Unpack(
        MethodAnalysisContext callee, Instruction call, MethodAnalysisContext method)
    {
        var operands = call.Operands;

        if (operands.Count <= Answer)
            return null;

        if (method.ControlFlowGraph!.Blocks.FirstOrDefault(b => b.Instructions.Contains(call)) is not { } made)
            return null;

        var definitions = Definitions(method);

        if (Address(operands[Frame], definitions) is not { } frame)
            return null;

        var arguments = new List<object>(callee.Parameters.Count);

        for (var i = 0; i < callee.Parameters.Count; i++)
        {
            if (Reaching(made, call, frame.Base, frame.Offset + i * SlotWidth) is not { } slot)
                return null;

            //A reference travels in the slot; anything else travels as its address, and the value is where
            //that address points. A shared body's own T is always a reference - sharing is what makes it one.
            var held = callee.Parameters[i].ParameterType;

            if (held is { IsValueType: true } and not GenericParameterTypeAnalysisContext)
            {
                if (Address(slot, definitions) is not { } at || Reaching(made, call, at.Base, at.Offset) is not { } value)
                    return null;

                slot = value;
            }

            arguments.Add(slot);
        }

        return (operands[Receiver], arguments, operands[Answer]);
    }

    /// <summary>What a fully shared body's return buffer is called once it has been recognised.</summary>
    public const string ReturnBuffer = "returnBuffer";

    /// <summary>
    /// Sends the answer straight to the method's own return buffer, where the copy out of the thunk's buffer
    /// is all that stands between the two.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The thunk returns nothing: it writes through the pointer it was handed, so a call rewritten to name its
    /// callee still leaves the value in a register nobody reads - <c>RandomItem</c> came out as
    /// <c>_ = list[index]; return default(T);</c>, the call recovered and its answer thrown away. A shared body
    /// that returns what it just fetched then copies the thunk's buffer into its own, which is
    /// <c>memcpy(returnBuffer, buffer, sizeof(T))</c>, so answering into the return buffer and taking the copy
    /// away says the same thing with nothing left over.
    /// </para>
    /// <para>
    /// This is done here rather than in <see cref="IndirectReturnCopy"/>, which folds the same shape for a big
    /// struct's <c>x8</c> return, because by the time that pass runs the buffer has been typed and its
    /// dereference resolved into a field of that type - <c>[v52]</c> becomes <c>v52.m_value</c> - and the copy
    /// no longer matches. Naming the buffer early enough for it to match was built at 1.0.990 and cost 22
    /// commented statements for one body closed: every site pays and only the ones that fold are paid for.
    /// Here nothing is touched unless the whole shape is present.
    /// </para>
    /// </remarks>
    public static void FoldAnswerIntoTheReturn(MethodAnalysisContext method, Instruction call, object? answer)
    {
        if (answer is not LocalVariable buffer || method.AppContext.Binary is not LibCpp2IL.Elf.ElfFile binary)
            return;

        if (method.Locals.FirstOrDefault(l => l.Name == ReturnBuffer) is not { } returned)
            return;

        foreach (var copy in method.ControlFlowGraph!.Instructions)
        {
            //Everything an import could have been handed is still on the call here: `KeyFunctionArguments`
            //cuts it down to the three `memcpy` takes long after this runs, so only the front is matched.
            if (copy is not { OpCode: OpCode.Call, Operands: [ulong address, _, LocalVariable into, LocalVariable from, ..] })
                continue;

            if (!ReferenceEquals(into, returned) || !ReferenceEquals(from, buffer))
                continue;

            if (binary.ImportedFunctionAt(address) is not ("memcpy" or "memmove"))
                continue;

            call.Operands[1] = returned;

            copy.OpCode = OpCode.Nop;
            copy.Operands = [];
            return;
        }
    }

    /// <summary>Everything each local is assigned by, which is what an address has to be traced through.</summary>
    private static Dictionary<LocalVariable, List<Instruction>> Definitions(MethodAnalysisContext method)
    {
        var definitions = new Dictionary<LocalVariable, List<Instruction>>();

        foreach (var instruction in method.ControlFlowGraph!.Instructions)
            if (instruction.Destination is LocalVariable destination)
                (definitions.TryGetValue(destination, out var list) ? list : definitions[destination] = []).Add(instruction);

        return definitions;
    }

    /// <summary>
    /// An address as the local it is measured from and the distance to it, which is how the frame and every
    /// slot in it are written: one register holding the frame pointer, and constants off it.
    /// </summary>
    private static (LocalVariable Base, long Offset)? Address(
        object operand, Dictionary<LocalVariable, List<Instruction>> definitions)
    {
        if (operand is MemoryOperand { Index: null, Scale: 0, Base: LocalVariable inside } memory)
            return (inside, memory.Addend);

        if (operand is not LocalVariable local)
            return null;

        return Single(local, definitions) switch
        {
            { OpCode: OpCode.Add, Operands: [_, LocalVariable from, { } by] } when Constant(by) is { } forward
                => (from, forward),
            { OpCode: OpCode.Subtract, Operands: [_, LocalVariable from, { } by] } when Constant(by) is { } back
                => (from, -back),
            //Anything else, including a frame pointer with nothing behind it, stands for itself.
            _ => (local, 0),
        };
    }

    /// <summary>
    /// What was put at an address by the last store to it that reaches a call.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The frame is a handful of stack words the compiler reuses for every thunked call in the body -
    /// <c>IDictionaryExtension::GetKeysByValue</c> writes the same one five times - so asking which
    /// instruction stores there has no answer, and asking which one stores there <em>last before this call</em>
    /// has exactly one. This walks back from the call through the blocks that can reach it and takes the first
    /// store on each path.
    /// </para>
    /// <para>
    /// A path that reaches the method's entry without passing a store means the frame was not filled in on
    /// that path, and two paths that fill it differently mean the argument depends on which one was taken.
    /// Both are refusals: an argument taken from the wrong store is a call that reads right and runs wrong,
    /// which is the failure this whole pass exists to stop making.
    /// </para>
    /// </remarks>
    private static object? Reaching(Block made, Instruction call, LocalVariable holder, long offset)
    {
        object? answer = null;

        var seen = new HashSet<Block>();
        var queue = new Queue<(Block Block, int Before)>();
        queue.Enqueue((made, made.Instructions.IndexOf(call)));

        while (queue.Count > 0)
        {
            var (block, before) = queue.Dequeue();
            object? here = null;

            for (var i = before - 1; i >= 0 && here == null; i--)
                here = Stores(block.Instructions[i], holder, offset);

            if (here != null)
            {
                if (answer != null && !ReferenceEquals(answer, here) && !answer.Equals(here))
                    return null;

                answer = here;
                continue;
            }

            //Nothing here, so whatever reaches this block reaches the call. An entry with nowhere above it
            //means the frame was never filled on this path.
            if (block.Predecessors.Count == 0)
                return null;

            foreach (var predecessor in block.Predecessors)
                if (seen.Add(predecessor))
                    queue.Enqueue((predecessor, predecessor.Instructions.Count));
        }

        return answer;
    }

    /// <summary>What an instruction puts at an address, where it puts anything there at all.</summary>
    private static object? Stores(Instruction instruction, LocalVariable holder, long offset)
    {
        if (instruction is not { OpCode: OpCode.Move, Operands: [MemoryOperand destination, { } value] })
            return null;

        if (destination is not { Index: null, Scale: 0, Base: LocalVariable into } || destination.Addend != offset)
            return null;

        return ReferenceEquals(into, holder) || into.Name == holder.Name ? value : null;
    }

    /// <summary>The one instruction a local is assigned by, looking through the copies in the way.</summary>
    private static Instruction? Single(LocalVariable local, Dictionary<LocalVariable, List<Instruction>> definitions)
    {
        var assignments = Sources(local, definitions, []).ToList();
        return assignments.Count == 1 ? assignments[0] : null;
    }

    private static IEnumerable<Instruction> Sources(
        LocalVariable local, Dictionary<LocalVariable, List<Instruction>> definitions, HashSet<LocalVariable> seen)
    {
        if (!seen.Add(local) || !definitions.TryGetValue(local, out var assignments))
            yield break;

        foreach (var assignment in assignments)
        {
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
