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
            {
                if (Trace)
                    System.Console.Error.WriteLine($"INVOKER   no store at {frame.Base.Name}+{frame.Offset + i * SlotWidth} "
                        + $"for parameter {i} of {callee.Name}");

                return null;
            }

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
    public static bool FoldAnswerIntoTheReturn(MethodAnalysisContext method, Instruction call, object? answer)
    {
        if (answer is not LocalVariable buffer || method.AppContext.Binary is not LibCpp2IL.Elf.ElfFile binary)
            return false;

        if (method.Locals.FirstOrDefault(l => l.Name == ReturnBuffer) is not { } returned)
            return false;

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
            return true;
        }

        return false;
    }

    /// <summary>
    /// Sends the answer to the slot the thunk was told to write, where the body reads it back from there.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A thunk returns nothing. It writes through the pointer in X4, so the register the call was lifted with
    /// holds whatever was in X0 when the thunk returned - which is nothing anyone put there. Where the answer
    /// buffer is a stack slot the body then reads, saying the call answers into <c>x0</c> leaves those reads
    /// with no definition at all:
    /// </para>
    /// <code>
    /// JsonUtility.FromJson&lt;WrapperArray&lt;T&gt;&gt;("{ \"array\": " + json + "}");  // answer thrown away
    /// object obj = default(object);                                          // the slot, undefined
    /// if (obj != null) { _ = "Unmanaged memory load: [stackaddr_-18+10]"; }   // and its field
    /// </code>
    /// <para>
    /// Naming the slot as the call's destination puts the value where the reads look. The versions have to be
    /// merged as well: the slot the call was handed and the slot the body reads are two locals wearing the
    /// same register, because nothing wrote the slot between them for single assignment form to join.
    /// </para>
    /// <para>
    /// The frame and the buffer are reused - <c>IDictionaryExtension::GetKeysByValue</c> writes the same one
    /// five times - so the rewriting stops at the next thing that puts something in the same slot. Past that
    /// the slot is a different value, and binding a read there to this call is the wrong-value failure this
    /// whole pass exists to stop making.
    /// </para>
    /// <para>
    /// Only where <see cref="FoldAnswerIntoTheReturn"/> did not already fold: there the answer goes straight
    /// out through the method's own return buffer and the slot is not read at all.
    /// </para>
    /// </remarks>
    public static void AnswerIntoTheSlotItNames(MethodAnalysisContext method, Instruction call, object? answer)
    {
        if (answer is not LocalVariable buffer || OutParameterWriteback.OffsetOfSlot(buffer.Register.Name) is null)
            return;

        call.Operands[1] = buffer;

        var instructions = method.ControlFlowGraph!.Instructions.ToList();
        var after = instructions.IndexOf(call);

        if (after < 0)
            return;

        for (var i = after + 1; i < instructions.Count; i++)
        {
            var later = instructions[i];

            if (later.Operands.Count == 0)
                continue;

            //Something else has been put in the slot, so everything past here is about that value.
            if (later.IsAssignment && later.Operands[0] is LocalVariable written
                && written.Register.Name == buffer.Register.Name && !ReferenceEquals(written, buffer))
            {
                return;
            }

            for (var operand = later.OpCode == OpCode.Call ? 2 : 1; operand < later.Operands.Count; operand++)
            {
                if (later.Operands[operand] is LocalVariable read
                    && read.Register.Name == buffer.Register.Name && !ReferenceEquals(read, buffer))
                {
                    later.Operands[operand] = buffer;
                }
                else if (later.Operands[operand] is MemoryOperand { Base: LocalVariable through } memory
                    && through.Register.Name == buffer.Register.Name && !ReferenceEquals(through, buffer))
                {
                    later.Operands[operand] = new MemoryOperand(buffer, memory.Index, memory.Addend, memory.Scale);
                }
            }
        }
    }

    /// <summary>
    /// Sends the answer to the place the one copy out of the thunk's buffer puts it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The third shape the answer pointer can have, and the one neither of the two above reaches. A body that
    /// keeps a <c>T</c> of its own allocas a run-time-sized buffer per local, and the thunk is handed a
    /// <em>different</em> one - so what the call wrote is copied from the thunk's buffer into the local's:
    /// </para>
    /// <code>
    /// Call IList`1&lt;T&gt;.get_Item, v287 (T), list, index   // the answer register, which the thunk never wrote
    /// Move v288, stack_-28                              // the local's own buffer, out of the frame
    /// Call memcpy, v294, v288, v57 (the thunk's), v51 (sizeof T)
    /// </code>
    /// <para>
    /// The copy is the assignment, so the call answers into what it filled and the copy goes. Nothing else may
    /// read the thunk's buffer: a second reader wants the value too, and taking the copy away would leave it
    /// reading a buffer nothing fills. That is the whole guard, and it is what makes this different from
    /// binding a call to a slot in general - the buffer is not a place the program named, it is the pointer
    /// this very call was handed, so there is no question of which call filled it.
    /// </para>
    /// <para>
    /// Where the copy fills a frame slot rather than a register, the answer goes to the slot and
    /// <see cref="AnswerIntoTheSlotItNames"/> merges the versions of it that follow - the body reads the slot
    /// again for each later use, and those reads are what the value has to reach.
    /// </para>
    /// </remarks>
    public static bool AnswerIntoTheCopyItFeeds(MethodAnalysisContext method, Instruction call, object? answer)
    {
        if (answer is not LocalVariable buffer || method.AppContext.Binary is not LibCpp2IL.Elf.ElfFile binary)
            return false;

        //A buffer that is a frame slot is the shape above, and it is read directly rather than copied out of.
        if (OutParameterWriteback.OffsetOfSlot(buffer.Register.Name) is not null)
            return false;

        var definitions = Definitions(method);

        //The pointer reaches the thunk through whatever register the convention wanted, so what identifies the
        //buffer is the alloca that made it and not the copy the call was handed.
        if (Single(buffer, definitions) is not { } made)
            return Refused(method, call, "no alloca behind the buffer");

        Instruction? copy = null;
        List<Instruction> reads = [];

        foreach (var instruction in method.ControlFlowGraph!.Instructions)
        {
            if (ReferenceEquals(instruction, call))
                continue;

            //Moving the pointer about is not reading what it points at.
            if (instruction.Destination is LocalVariable moved && ReferenceEquals(Single(moved, definitions), made))
                continue;

            if (!Reads(instruction, definitions, made, method))
                continue;

            //Putting the pointer somewhere is not reading what it points at either, and the thunk's own frame
            //carries it in a word past the arguments that nothing ever reads back. Only where nothing does:
            //a word that is read again is somebody else's way to the same buffer.
            if (instruction is { OpCode: OpCode.Move, Operands: [{ } put, LocalVariable pointer] }
                && ReferenceEquals(Single(pointer, definitions), made)
                && NothingReads(method, put))
            {
                continue;
            }

            //Clearing the buffer is a write, not a read. A shared body allocas its buffer and zeroes it before
            //the call fills it - that is `T val = default(T)` - and `ClearingASizedByT` takes the clear away
            //further down. Counting it as a reader refuses the fold for the sake of a statement that says
            //nothing about the value the call is about to put there.
            if (instruction is { OpCode: OpCode.Call or OpCode.CallVoid, Operands: [ulong cleared, ..] }
                && binary.ImportedFunctionAt(cleared) is "memset"
                && instruction.Operands.ElementAtOrDefault(instruction.OpCode == OpCode.Call ? 2 : 1) is LocalVariable zeroed
                && ReferenceEquals(Single(zeroed, definitions), made))
            {
                continue;
            }

            //The pointer stays in the register the thunk's convention put it in, and a later call nothing has
            //named is handed every register an argument could have come in - so the very local this call was
            //given turns up as somebody else's speculative operand. It is the same local, which is what says
            //it is a leftover rather than an argument: a value the program really passed there would be a
            //fresh version, written for that call. Anything reaching the buffer by another name still counts.
            //
            //**Widening this to "any call nothing has named reads nothing" is inert - measured three times**
            //(1.9.5/526, 1.9.6/527 by export, and once more in probe alone at 1.9.9). It is *true* and it
            //never unlocks a body: the refusal moves to the next reader and stops there. What the buffer
            //register is really caught on is the phi web SSA destruction leaves over **X4**, the register the
            //invoker convention passes the answer buffer in - every thunked call in a body writes it, so the
            //versions are merged at every join, and the web bottoms out at whichever unresolved
            //`IndirectCall` is handed the speculative argument run. `COPYFOLD_TRACE` prints that terminal
            //reader on a `dead-stops at` line. The answer is not to keep weakening who counts as a reader.
            if (instruction.OpCode is OpCode.Call or OpCode.CallVoid or OpCode.IndirectCall
                && KeyFunctionArguments.Reads(method, instruction) is null
                && instruction.Operands.Skip(instruction.OpCode == OpCode.Call ? 2 : 1)
                    .All(o => o is not LocalVariable carried
                        || !ReferenceEquals(Single(carried, definitions), made)
                        || ReferenceEquals(carried, buffer)))
            {
                continue;
            }

            //A phi is a name for the value on a path, not a use of it, and the buffer arrives in an argument
            //register whose versions get merged at every join in the body. It is only not a use where the
            //merged local goes nowhere itself - otherwise the value does reach a reader, by another name.
            if (instruction.Destination is LocalVariable named && Dead(method, named, []))
                continue;

            //Or it is single assignment form's own bookkeeping about an argument register, which is a
            //different question from where the value goes afterwards.
            if (ArgumentRegisterBookkeeping(instruction, definitions))
                continue;

            //Or the body does not copy the value out at all - it reads straight through the pointer, which
            //is what a shared body does when the `T` it fetched is only ever handed on. `KeyValuePair<T,W>`'s
            //`get_Value` is told to write X26 and the comparison two lines later reads `[X26]`; the local
            //`RuntimeMethodCallRecovery` attached as the call's answer is an X0 that nothing ever wrote, so
            //the value exists under two names and the wrong one is the one the statement uses. These are
            //gathered rather than refused, and answered below.
            if (instruction is { OpCode: OpCode.Move, Operands: [LocalVariable, MemoryOperand { Index: null, Scale: 0, Addend: 0, Base: LocalVariable through }] }
                && ReferenceEquals(Single(through, definitions), made))
            {
                reads.Add(instruction);
                continue;
            }

            //Anything but the one copy reading the buffer means the answer is wanted somewhere else as well,
            //and folding the copy away would leave that reader with a buffer nothing fills.
            if (copy != null)
                return Refused(method, call, $"read again by {instruction}");

            if (instruction is not { OpCode: OpCode.Call, Operands: [ulong address, _, LocalVariable, LocalVariable from, ..] }
                || !ReferenceEquals(Single(from, definitions), made)
                || binary.ImportedFunctionAt(address) is not ("memcpy" or "memmove"))
            {
                return Refused(method, call, $"read by {instruction}");
            }

            copy = instruction;
        }

        //Nothing copied it out, but the body read through it: then **the read is the value**, and that is the
        //whole of the fix. The call answers into the buffer local the reads name, each read becomes a plain
        //copy of it, and the allocation that had made it an address goes the same way it does for a copy.
        //Only where there is no `memcpy` at all - a body that does both is one this cannot speak for.
        if (copy is null && reads.Count > 0
            && reads[0].Operands[1] is MemoryOperand { Base: LocalVariable held })
        {
            if (CopyTrace)
                System.Console.Error.WriteLine($"COPYFOLD read    {method.DeclaringType?.Name}::{method.Name}  through {held}  x{reads.Count}  |  {call}");

            Erase(method, held, definitions);

            if (call.Operands[0] is MethodAnalysisContext answering && IsAnAddress(held.Type))
                held.Type = answering.ReturnType;

            foreach (var read in reads)
                read.Operands[1] = held;

            call.Operands[1] = held;
            return true;
        }

        if (copy is null)
            return Refused(method, call, "nothing reads the buffer");

        if (Filled(copy, definitions, method) is not { } into)
            return Refused(method, call, "the copy fills nothing that can be named");

        if (CopyTrace)
            System.Console.Error.WriteLine($"COPYFOLD folded  {method.DeclaringType?.Name}::{method.Name}  into {into}  |  {call}");

        copy.OpCode = OpCode.Nop;
        copy.Operands = [];

        //And with it the address that had been put where the value now goes. It is there only because the copy
        //wanted somewhere to write, it is what says the place holds a number, and the generator cannot write
        //both: `long num3 = num2 - num;` followed by the call answering there comes out as
        //`num3 = (long)list[index];`, a conversion from `T` that does not exist, and the statement is lost.
        Erase(method, into, definitions);

        if (into is not LocalVariable local)
        {
            call.Operands[1] = into;
            return true;
        }

        //The local was an address and is now the value, so what it was called wants saying again. Only where
        //what it said was an address: a local the analysis has already given a managed type to is something
        //else, and overwriting that would be a wrong value rather than a name for the right one.
        if (call.Operands[0] is MethodAnalysisContext callee && IsAnAddress(local.Type))
            local.Type = callee.ReturnType;

        //A slot is read again by a fresh version of itself at every use, so those versions have to be joined;
        //a register the compiler kept the buffer in for the whole body is already the one local.
        if (OutParameterWriteback.OffsetOfSlot(local.Register.Name) is not null)
            AnswerIntoTheSlotItNames(method, call, local);
        else
            call.Operands[1] = local;

        return true;
    }

    /// <summary>
    /// Takes away the allocation's address from the place the answer is about to be written.
    /// </summary>
    /// <remarks>
    /// Only an allocation, never anything else: a place the program put a value in is a value the program
    /// wanted, and removing it would be a statement lost rather than a statement that was never the
    /// program's. An allocation is <c>sub xN, sp, size</c> - the frame pointer less a run-time size - and it
    /// exists to give the copy somewhere to write.
    /// </remarks>
    private static void Erase(MethodAnalysisContext method, object into,
        Dictionary<LocalVariable, List<Instruction>> definitions)
    {
        foreach (var instruction in method.ControlFlowGraph!.Instructions)
        {
            if (instruction.Operands.Count == 0 || !Fills(instruction, into))
                continue;

            if (CopyTrace)
                System.Console.Error.WriteLine($"COPYFOLD   erase candidate {instruction}");

            //Either the allocation itself, where the value's home is the register it landed in, or the store
            //that put it in the frame word the value is going to.
            var alloca = instruction is { OpCode: OpCode.Move, Operands: [_, LocalVariable put] }
                ? Single(put, definitions)
                : instruction;

            //Or off the frame pointer itself, which is not a slot and has no name of that kind. Stack analysis
            //names what the frame *holds*; the register the frame is anchored by is an entry value with no
            //definition anywhere in the body, and `sub xN, x29, size` is the alloca all the same.
            //`IDictionaryExtension::TryGetKeyByValue` takes its `KeyValuePair<T, W>` buffer off `X29`, so the
            //fold landed and the declaration behind it stayed - `KeyValuePair<T, W> keyValuePair =
            //(KeyValuePair<T, W>)(num6 - num8);`, which cannot be written, and every use of the local went
            //with it. That is +7 commented statements for a fold that had worked.
            if (alloca is not { OpCode: OpCode.Subtract, Operands: [_, LocalVariable frame, _] }
                || !IsTheFrame(frame, definitions, []))
            {
                continue;
            }

            instruction.OpCode = OpCode.Nop;
            instruction.Operands = [];
        }
    }

    /// <summary>
    /// Whether what an allocation was taken off is the frame, however many copies of it stand in the way.
    /// </summary>
    /// <remarks>
    /// Three spellings, and the third is the one that hid for a round. A frame slot the stack analysis has
    /// named; the frame pointer register itself, which is an entry value with no definition anywhere in the
    /// body (stack analysis names what the frame *holds*, never what anchors it); and **a copy of that
    /// register into a scratch one**, which is what the compiler emits when it wants the frame pointer twice.
    /// `TryGetKeyByValue`'s `get_Value` buffer is `sub x26, x8, size` where `x8` was loaded from `x29` three
    /// instructions earlier, and the finished dump shows `v1 @ X29` there only because a later pass
    /// copy-propagates it - at the moment `Erase` runs the operand is still `v71 @ X8_v5`, whose name is
    /// neither prefix, so the allocation survived and the local kept a second definition saying it is an
    /// address. That is exactly the declaration the generator cannot write.
    /// </remarks>
    private static bool IsTheFrame(LocalVariable local, Dictionary<LocalVariable, List<Instruction>> definitions,
        HashSet<LocalVariable> seen)
    {
        if (!seen.Add(local))
            return false;

        if (local.Register.Name is { } named
            && (named.StartsWith(StackSlots.AddressPrefix) || named.StartsWith(StackSlots.ValuePrefix)))
        {
            return true;
        }

        //Nothing in the body writes it, so it arrived with the frame.
        if (!definitions.TryGetValue(local, out var places))
            return local.Register.Name is not null;

        //Or it is a copy of something that did. Only a copy - anything computed is a value, not the frame.
        return places.Count == 1 && places[0] is { OpCode: OpCode.Move, Operands: [_, LocalVariable from] }
            && IsTheFrame(from, definitions, seen);
    }

    /// <summary>Whether an instruction writes the place an answer is about to be sent to.</summary>
    private static bool Fills(Instruction instruction, object into) => (instruction.Destination, into) switch
    {
        (LocalVariable written, LocalVariable place) => ReferenceEquals(written, place),
        (MemoryOperand at, MemoryOperand place) =>
            ReferenceEquals(at.Base, place.Base) && at.Addend == place.Addend && at.Index is null,
        _ => false,
    };

    /// <summary>
    /// Whether an allocation's address stayed in registers, so that the register names the buffer outright.
    /// </summary>
    /// <remarks>
    /// A pointer put in the frame is loaded again wherever it is wanted, and those loads are reads this pass
    /// cannot see from here - so the register would be a name only the copy could read the value by.
    /// </remarks>
    private static bool OnlyInARegister(LocalVariable held, Dictionary<LocalVariable, List<Instruction>> definitions,
        MethodAnalysisContext method)
    {
        if (Single(held, definitions) is not { } made)
            return false;

        foreach (var instruction in method.ControlFlowGraph!.Instructions)
        {
            if (instruction is not { OpCode: OpCode.Move, Operands: [{ } destination, LocalVariable put] }
                || destination is LocalVariable { Register.Name: null })
            {
                continue;
            }

            if (ReferenceEquals(Single(put, definitions), made)
                && (destination is MemoryOperand
                    || (destination is LocalVariable slot && OutParameterWriteback.OffsetOfSlot(slot.Register.Name) is not null)))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The one instruction a local is assigned by, without looking through anything.</summary>
    private static Instruction? Assigned(LocalVariable local, Dictionary<LocalVariable, List<Instruction>> definitions)
        => definitions.TryGetValue(local, out var assignments) && assignments.Count == 1 ? assignments[0] : null;

    /// <summary>Whether a place a store went to is read back anywhere in the body.</summary>
    private static bool NothingReads(MethodAnalysisContext method, object place)
    {
        foreach (var instruction in method.ControlFlowGraph!.Instructions)
            for (var operand = instruction.IsAssignment ? 1 : 0; operand < instruction.Operands.Count; operand++)
                switch (instruction.Operands[operand], place)
                {
                    case (LocalVariable read, LocalVariable slot) when ReferenceEquals(read, slot):
                    case (MemoryOperand at, MemoryOperand held)
                        when ReferenceEquals(at.Base, held.Base) && at.Addend == held.Addend && at.Index is null:
                        return false;
                }

        return true;
    }

    /// <summary>
    /// One past the last operand an instruction really reads.
    /// </summary>
    /// <remarks>
    /// A call nothing has named is handed every register an argument could have arrived in, so the operands
    /// past what it reads are speculation. Counting one of them as a use says a buffer is read where it is
    /// not, and refuses a fold that was safe - the second half of <c>IListExtension::Shuffle</c> was refused
    /// because a <c>memcpy</c> three instructions later still carried the previous call's buffer in X4.
    /// </remarks>
    private static int Last(Instruction instruction, MethodAnalysisContext method)
        => instruction.IsCall && KeyFunctionArguments.Reads(method, instruction) is { } takes
            ? System.Math.Min(instruction.Operands.Count, (instruction.OpCode == OpCode.Call ? 2 : 1) + takes)
            : instruction.Operands.Count;

    /// <summary>
    /// Whether a local's value reaches nothing but further copies of itself, however far they go.
    /// </summary>
    /// <remarks>
    /// Written for the phis over an argument register. The buffer arrives in X4 and every join in the body
    /// merges that register's versions, so a loop puts a phi between the allocation and the copy and the
    /// buffer looks read where it is only named. A phi whose result is itself only phi'd or copied, and never
    /// looked at, is bookkeeping - and a cycle of them round a loop is bookkeeping too, which is why coming
    /// back to a local already being asked about answers yes.
    /// </remarks>
    private static bool Dead(MethodAnalysisContext method, LocalVariable local, HashSet<LocalVariable> seen)
    {
        if (!seen.Add(local))
            return true;

        foreach (var instruction in method.ControlFlowGraph!.Instructions)
        {
            var reads = false;

            for (var operand = instruction.IsAssignment ? 1 : 0; operand < Last(instruction, method) && !reads; operand++)
                reads = instruction.Operands[operand] switch
                {
                    LocalVariable other => ReferenceEquals(other, local),
                    MemoryOperand memory => ReferenceEquals(memory.Base, local) || ReferenceEquals(memory.Index, local),
                    FieldReference reference => ReferenceEquals(reference.Local, local),
                    _ => false,
                };

            if (!reads)
                continue;

            if (instruction.OpCode is not (OpCode.Move or OpCode.Phi)
                || instruction.Destination is not LocalVariable written
                || !Dead(method, written, seen))
            {
                if (CopyTrace)
                    System.Console.Error.WriteLine($"COPYFOLD   dead-stops at {instruction}  (following {local})");

                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Whether an instruction mentioning the buffer is single assignment form's bookkeeping about an
    /// argument register rather than a reading of what the buffer holds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is a different predicate from the three that failed</b>, and the difference is what it keys
    /// on. Widening by <em>instruction kind</em> - "a `Move` is not a reader", then "a call nothing has named
    /// is not a reader" - was measured inert three times (1.9.5/526, 1.9.6/527, and once in probe alone at
    /// 1.9.9): the refusal moves to the next reader and stops there. This keys on a <em>structural</em>
    /// property instead, and one the trace has already proved: a copy single assignment form invented,
    /// landing in a register the convention uses to pass arguments, every definition of which is another
    /// such copy.
    /// </para>
    /// <para>
    /// [[il2cpp-an-edge-copy-is-not-two-names]] is the general statement - the copies destruction leaves are
    /// "one register's several tenants meeting at a join", and a name that fits the value on one edge is not
    /// a name for the values on the others. An argument register under the invoker convention is the purest
    /// case: <b>every thunked call in the body redefines it</b>, so its versions are merged at every join and
    /// the web joins values that have nothing to do with one another.
    /// </para>
    /// <para>
    /// <b>Not by removing the copy.</b> Two measured results say that lever costs more than it pays -
    /// [[il2cpp-a-block-that-throws-takes-no-edge]] bought commented 185 -> 97 and paid 38 markers, and
    /// [[il2cpp-a-copy-is-where-a-lane-crosses]] shows those copies are often a parameter lane's only reader.
    /// The copy must go on existing; what changes is what it is taken to imply.
    /// </para>
    /// <para>
    /// <b>Three conditions, and the last is the one that keeps a real reader.</b> A copy of the value itself
    /// is told from bookkeeping about a register only by the destination's type - the same distinction
    /// <c>HomogeneousFloatParameters</c> has to make, where the first register of a run is both the struct
    /// and its first field - so a destination the analysis has given a real type to is a reader and is left
    /// alone. And one real store anywhere among the definitions means somebody put a value in that register
    /// on purpose.
    /// </para>
    /// </remarks>
    private static bool ArgumentRegisterBookkeeping(Instruction instruction,
        Dictionary<LocalVariable, List<Instruction>> definitions)
    {
        //A copy destruction invented carries index -1; anything the program itself wrote has a real one.
        if (instruction.Index != -1 || instruction.OpCode is not (OpCode.Move or OpCode.Phi)
            || instruction.Destination is not LocalVariable merged
            || !IsAnArgumentRegister(merged.Register.Name))
        {
            return false;
        }

        //The destination naming the value rather than a place is what says this one is a real reader.
        if (!IsAnAddress(merged.Type))
            return false;

        if (!definitions.TryGetValue(merged, out var places) || places.Count == 0)
            return false;

        foreach (var place in places)
            if (place.Index != -1 || place.OpCode is not (OpCode.Move or OpCode.Phi))
                return false;

        return true;
    }

    /// <summary>
    /// Whether a register is one the calling convention takes back at every call.
    /// </summary>
    /// <remarks>
    /// <c>X0</c> to <c>X7</c> are the argument run and <c>X8</c> is the indirect result register - this fork
    /// models an indirectly returning call as writing <c>[X8]</c> (<c>GetCallResultOperand</c>), so it is
    /// clobbered by every call in exactly the way the argument registers are, and the invoker thunk's own
    /// answer buffer is loaded through it. A callee-saved register is not here: a value kept in one is a
    /// value the compiler meant to survive a call, which is the opposite of bookkeeping.
    /// <para>
    /// **A register a call really writes is safe without a special case.** A <c>Call</c> defining <c>X0</c>
    /// is not an edge copy and has a real instruction index, so the "every definition is an edge copy" test
    /// in <see cref="ArgumentRegisterBookkeeping"/> refuses it. That is what keeps a returned value from
    /// being read as bookkeeping about the register it came back in.
    /// </para>
    /// </remarks>
    private static bool IsAnArgumentRegister(string? name)
        => name is { Length: > 1 } && name[0] is 'X' or 'x'
            && int.TryParse(name.Substring(1), out var number)
            && number >= 0 && number <= Aapcs64.RegistersPerRun;

    /// <summary>Says why a fold was refused, where the fold is being traced.</summary>
    private static bool Refused(MethodAnalysisContext method, Instruction call, string why)
    {
        if (CopyTrace)
            System.Console.Error.WriteLine($"COPYFOLD refused {method.DeclaringType?.Name}::{method.Name}  {why}  |  {call}");

        return false;
    }

    private static readonly bool CopyTrace = System.Environment.GetEnvironmentVariable("COPYFOLD_TRACE") is not null;

    /// <summary>What a copy filled, said as the frame slot it came out of where it came out of one.</summary>
    /// <remarks>
    /// The destination is read out of the frame right before the copy, and the body reads the same slot again
    /// for every later use of the value. Naming the read rather than the slot would put the answer somewhere
    /// only this one copy could see it.
    /// </remarks>
    private static object? Filled(Instruction copy, Dictionary<LocalVariable, List<Instruction>> definitions,
        MethodAnalysisContext method)
    {
        if (copy.Operands[2] is not LocalVariable into)
            return null;

        var seen = new HashSet<LocalVariable>();

        while (seen.Add(into))
        {
            //A slot the body already names is where the value belongs: every later use reads it again.
            if (OutParameterWriteback.OffsetOfSlot(into.Register.Name) is not null)
                return into;

            //One copy at a time. `Single` looks through a whole chain and lands on the allocation at the end
            //of it, which is where the buffer came from and not where the value is kept.
            //
            //Nothing behind it but the allocation itself: the register **is** the local, the compiler kept it
            //in a callee-saved one for the whole body, and every later use reads it. Only where the address
            //never went to memory as well - a second way to the same buffer is a reader this cannot see.
            if (Assigned(into, definitions) is not { OpCode: OpCode.Move, Operands: [_, { } behind] })
                return OnlyInARegister(into, definitions, method) ? into : null;

            if (behind is LocalVariable copied)
            {
                into = copied;
                continue;
            }

            //The pointer was put in the frame and is loaded again for every use, so the frame word is what the
            //value has to reach. <see cref="SlotAddressRead"/> gives that word a name of its own further down
            //the pipeline and gives the loads the same name, so answering here is answering where they look.
            return behind is MemoryOperand { Index: null, Scale: 0, Base: LocalVariable frame } place
                && frame.Register.Name is { } named && named.StartsWith(StackSlots.AddressPrefix)
                    ? place
                    : null;
        }

        return null;
    }

    /// <summary>
    /// Whether an instruction reads what an allocation made, however many copies of the pointer stand between.
    /// </summary>
    private static bool Reads(Instruction instruction, Dictionary<LocalVariable, List<Instruction>> definitions,
        Instruction made, MethodAnalysisContext method)
    {
        bool From(object? operand) => operand is LocalVariable local && ReferenceEquals(Single(local, definitions), made);

        for (var operand = instruction.IsAssignment ? 1 : 0; operand < Last(instruction, method); operand++)
            switch (instruction.Operands[operand])
            {
                case LocalVariable other when From(other):
                case MemoryOperand memory when From(memory.Base) || From(memory.Index):
                case FieldReference reference when From(reference.Local):
                    return true;
            }

        return false;
    }

    /// <summary>Whether what a local was called is a place rather than a thing.</summary>
    private static bool IsAnAddress(TypeAnalysisContext? type)
        => type is null || type is { Namespace: nameof(System), Name: "Int64" or "UInt64" or "IntPtr" or "UIntPtr" };

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

        //A local that *is* a stack slot stands for where the slot is. `OutParameterWriteback` folds a slot
        //and the address of it into one variable, so asking what assigned it lands on the value somebody put
        //*in* the frame rather than on the frame - and the frame's own first word then looks like a store
        //into whatever that value was.
        if (OutParameterWriteback.OffsetOfSlot(local.Register.Name) is not null)
            return (local, 0);

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
        if (instruction is { OpCode: OpCode.Move, Operands: [LocalVariable slot, { } put] }
            && InSlot(slot, holder, offset))
        {
            return put;
        }

        if (instruction is not { OpCode: OpCode.Move, Operands: [MemoryOperand destination, { } value] })
            return null;

        if (destination is not { Index: null, Scale: 0, Base: LocalVariable into } || destination.Addend != offset)
            return null;

        return ReferenceEquals(into, holder) || into.Name == holder.Name ? value : null;
    }

    /// <summary>
    /// Whether a store went into the frame's word at <paramref name="offset"/>, written as a named slot.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The frame is on the stack, and <c>StackAnalyzer</c> gives a stack slot a <em>name</em> rather than a
    /// memory operand - so <c>str x0, [sp, #8]</c> arrives as <c>Move stack_-28, X0</c> and there is no
    /// addend anywhere for the test above to compare. Every frame filled in from a register looked unfilled,
    /// and the call was refused with its arguments intact three instructions away.
    /// </para>
    /// <para>
    /// The name spells the offset, so the arithmetic that the addend was doing is done on the name instead:
    /// the frame's own slot plus the word index. Both prefixes are admissible, because
    /// <see cref="OutParameterWriteback"/> renames only the slots whose address is taken and a five-word
    /// frame has its address taken once - <c>AnonymousSerializableFormatter::Serialize</c> is
    /// <c>stackaddr_-28</c> followed by four <c>stack_</c> slots.
    /// </para>
    /// </remarks>
    private static bool InSlot(LocalVariable slot, LocalVariable holder, long offset) =>
        OutParameterWriteback.OffsetOfSlot(slot.Register.Name) is { } at
        && OutParameterWriteback.OffsetOfSlot(holder.Register.Name) is { } frame
        && at == frame + offset;

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
