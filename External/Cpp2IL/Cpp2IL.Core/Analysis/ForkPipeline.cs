using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// The passes this fork adds to method analysis, in the four places along the existing pipeline where they
/// belong. Each of those places is one line in <see cref="MethodAnalysisContext.Analyze"/>, so that the file
/// stays as close to upstream as it can and a later version of Cpp2IL can be merged without the two sets of
/// changes meeting.
///
/// Where a pass runs matters as much as what it does - most of them read something an earlier pass produced
/// and would find nothing if they ran too early, or would be undone if they ran too late - so the reason for
/// each position is written down beside it.
/// </summary>
public static class ForkPipeline
{
    /// <summary>Runs on the stack slots, before single assignment form is built over them.</summary>
    public static void AfterStackAnalysis(MethodAnalysisContext method)
    {
        // A slot the callee writes through an out parameter is only telling apart from one the caller reads
        // while the registers are unversioned: once single assignment form is built, the zero the compiler
        // put in the slot beforehand has already been propagated into the read after the call, and the two
        // are the same constant.
        OutParameterWriteback.Run(method);
    }

    /// <summary>Runs where locals have just been given their types and fields.</summary>
    public static void AfterTypesAndFieldsResolved(MethodAnalysisContext method)
    {
        // Again, now that locals are typed: a class-initialisation guard is recognised by the runtime
        // class pointer it reads out of, and that pointer is only typed by the pass above. On builds
        // where the initialised flag is not at the offset the first pass looks for - a metadata 31
        // arm64 build tests a different byte - this is the pass that actually removes the guard.
        MetadataInitGuardRemover.Run(method);

        // Here because the array-initialiser handle is a metadata usage that upstream's resolver has no branch
        // for, so it is still an unresolved global read at this point - and because the local it lands in has
        // just been typed, which is what has to be corrected when the read becomes a token rather than a
        // pointer. Later would be too late: the read is dead by the time unused locals are dropped.
        ArrayInitialiserHandle.Run(method);

        // Where the compiler stopped passing a receiver the callee never reads, the null check it left behind
        // is the only remaining trace of which object the call was on - so this has to come first.
        CallReceiverRecovery.Run(method);

        // The null checks il2cpp writes in front of every dereference say nothing a C# reader needs: the
        // language makes the same guarantee without them, and recovering them buries the code that was written.
        NullCheckRemover.Run(method);


        // The state a state machine starts in is stored separately because il2cpp inlined the constructor that
        // set it; it belongs to the allocation, and has to be moved there before anything reads the two as
        // saying the state is set twice.
        StateMachineConstructorRecovery.Run(method);

    }

    /// <summary>Runs where a delegate's invoke has just been recognised as one.</summary>
    public static void AfterDelegateInvokeRecovery(MethodAnalysisContext method)
    {
        // A delegate invoke is only known to be one by the step above, so the arguments it does not take are
        // still on it until here - and one of them is the delegate's own method pointer, whose load would
        // otherwise be kept alive by that operand and written out as a placeholder.
        MetadataResolver.TrimResolvedCallArguments(method);
    }

    /// <summary>Runs on the shape the rest of the pipeline settled on, before unused locals are dropped.</summary>
    /// <summary>
    /// Runs once the constants have reached the instructions that use them, and before those constants are
    /// read as the floats they may be.
    /// </summary>
    public static void BeforeFloatLiteralsAreFixed(MethodAnalysisContext method)
    {
        // A constant too wide for one instruction arrives as a chain of masks and ors over immediates, which
        // the decompiler folds at the end - so the output never looked wrong, but no pass in between could
        // read the number. This is what lets the one below read it.
        ConstantFolding.Run(method);

        // A switch whose arms are constants becomes a table in the binary's own data, which resolves to
        // nothing and leaves the method answering with its default. Needs the folding above: the address is
        // only a constant once the page and the offset have been put together.
        SwitchTableRecovery.Run(method);

        // Two adjacent fields set by one wide store resolve to the first of them, which is then assigned the
        // whole of what was written - a wrong value in a field, and the other field missing entirely. It has
        // to be here: not before copy propagation, which is what carries the constant to the store, and not
        // after the float literals are fixed, because the half that belongs to a float field is one of them.
        WideFieldStore.Run(method);

        //After field recovery, which is what turns the store into the first of the fields it covers, and
        //beside WideFieldStore because it is the same defect one width up: a store that wrote more than the
        //field it resolved to. Before anything reads the constants, so the folding below sees real numbers.
        ConstantBlobStore.Run(method);
    }

    public static void BeforeUnusedLocalsAreDropped(MethodAnalysisContext method)
    {
        // An argument kept somewhere that survives a call is read back once per branch, and a local written in
        // several places is one the propagation in SSA leaves alone. First, so that everything below sees the
        // argument itself where the compiled code was passing a copy of it.
        ParameterCopyPropagation.Run(method);

        // A struct of floats arrives one field to a register, and only the first was named - after the whole
        // struct. Directly after the copies are gone, because until then the arithmetic reads a copy of the
        // parameter rather than the parameter, and there is nothing there to recognise; and well before the
        // type fixpoint at the end of this method, so that a field's type travels through the arithmetic it
        // takes part in.
        HomogeneousFloatParameters.Run(method);

        // And directly after it, because it is what names these: the first register of a struct of floats is
        // named as the whole struct while the ones after it are named as the fields they hold, so an addition
        // comes out adding a `Pair` to a `float`. Nothing else here produces that shape, and it has to be
        // after the naming rather than after field resolution, where the operands are still plain locals.
        StructInArithmetic.Run(method);


        // The same defect where a declaration settles it rather than a neighbouring number: a struct handed to
        // a float parameter, or returned where a float is declared, is the member at the front of it.
        // `Joystick.Vertical` recovers and `Joystick.Horizontal` does not, and the only difference is that one
        // reads at offset four and the other at nought. Directly after the pass above, whose narrowing it
        // shares and whose remaining positions it covers.
        StructAtADeclaredNumber.Run(method);

        // Which instantiation a shared body was called as is read off the object it was called on, and until
        // the copies are gone that object is a local typed by the very call being asked about - so this only
        // has an answer here, where the propagation out of SSA has settled them. Types are then worked out
        // again, because until now the call said it returned `object`: the value it produced was typed as
        // that, every field read off it was a read of unmanaged memory rather than a field, and an array
        // reached that way was never known to be one - so its length and its elements went the same way, and
        // the loop over them with it. One `list[i]` costs the method it is in.
        if (GenericSharingRecovery.Run(method))
            LocalVariables.ResolveTypesAndFields(method);

        // A value the shared body left named after a stand-in - `System.Int32Enum` and the rest - is named
        // after the type it really has instead. Those stand-ins are types no assembly Unity ships, so every
        // statement naming one is dropped whatever else was recovered about it. After the pass above, which
        // is what puts the real instantiation on the values this reads it from.
        SharedStandInRetyping.Run(method);

        // Every constructor calls its base one and the language writes that call back, so keeping it only
        // produces a statement C# has no way to say. Runs here, where the receiver is `this` again rather
        // than the copy of it the compiled code was passing.
        BaseConstructorCallRemover.Run(method);

        // A closure or a state machine has one instance inside its own methods, and the compiled code reads its
        // fields off whichever copy of it a register happened to be holding. Before the return is split, so
        // that what the split leaves behind is already a state machine reading its own state.
        CompilerGeneratedSelfRecovery.Run(method);

        // Give back the constants a merged return was working out. Only where a path already decided the
        // condition the return computes.
        MergedReturnSplitter.Run(method);

        // Array.Empty<T>() is inlined to the field it returns, whose type the runtime library keeps to
        // itself - so the read cannot be written down, and the statement holding it is lost.
        EmptyArrayRecovery.Run(method);

        // A constructor small enough to inline leaves its field writes at the allocation, and those fields are
        // the callee's own and often readonly - so no accessor can stand for them and the pass below cannot
        // help. First, so that the writes are still writes when this reads them, and after the state machine's
        // own recovery, whose allocation already carries its argument and is left alone here.
        InlinedConstructorRecovery.Run(method);

        // A struct the frame holds spans several slots and each got a variable of its own, so a field of it
        // has no connection to the struct - and where a callee writes that field through a pointer, nothing
        // in the caller ever assigns it. Every `foreach` element read as the zero the slot was cleared to.
        // Before the pass below, which is what then says a private field like `_current` by its property.
        StructSlotFields.Run(method);

        // A member of a struct kept in another type's static storage - `Vector3.zero.z` - matches no field at
        // the outer level and was written out as unmanaged memory. Beside the pass below because it is the
        // same answer for statics: say the value by the public property over it. After field resolution, which
        // is what leaves these unresolved, and before the pass below, which does the instance half.
        StaticStructMember.Run(method);

        // A property accessor of another assembly is inlined into its caller, leaving a direct access to the
        // private field behind it - true, but not something the project can write down.
        InaccessibleFieldRecovery.Run(method);

        // A cast is a call into the runtime reached through a thunk no method table names, so it stayed an
        // address - and every statement built on it went with it.
        CastHelperRecovery.Run(method);

        // Directly after it, because it reads what that pass named: a cast helper whose answer is only ever
        // read through at offset zero was never a cast, it was a box being opened, and only what is done with
        // the answer says which.
        UnboxRecovery.Run(method);

        // And the same question where it was not a call at all: asking whether an object is of a type is
        // small enough that il2cpp inlines it into a walk of the class hierarchy. Directly after the pass
        // above, because both end in the call it already knows how to write, and while the class the walk
        // compares against still carries its type.
        InlinedTypeTestRecovery.Run(method);

        // And the shape that walk collapses to against a type nothing can derive from: one comparison of the
        // object's class against a slot of the runtime's table of built-in classes. Beside the pass above,
        // for the same reason - both end in the call the generator already writes as an isinst - and before
        // the boolean simplifier turns the answer into something with no comparison left to match.
        ExactTypeTestRecovery.Run(method);

        // A call on an interface is compiled as a walk of the class's interface table rather than a call to a
        // helper that walks it, so nothing in it names a method. The interface it looks for, the slot it adds
        // and the object it read the class from are all right there in the walk.
        InterfaceCallRecovery.Run(method);

        // Preparing a class the first time something touches it is the runtime's business, not the program's.
        // Taking the call leaves the test that guarded it reading a value nobody wants, which the collection
        // at the end of the pipeline then removes along with the class pointer it was read from.
        ClassInitCallRemover.Run(method);

        // And the two reads of a class that go with that call: where its static values are kept, and whether
        // they are ready yet. This has to follow every pass that recognises the guard by its shape, because
        // answering the test is what makes the shape stop existing.
        RuntimeClassReadRemover.Run(method);

        // Storing a reference into an array is asked about before it is done, because an array of one thing
        // can be handed out as an array of what it derives from. The language promises that itself and says
        // nothing about it, so the question - and the store it guards - is only in the way.
        ArrayStoreCheckRemover.Run(method);

        // An array's length is a load at a fixed offset, so the condition of every loop over one was a read
        // of unmanaged memory.
        // Again here, because the seeding done during type resolution does not survive to this point - and
        // an array's length is read through the local the allocation wrote, so the pass below can only see it
        // as an array if it has been said so by now.
        LocalVariables.SeedArrayAllocations(method);

        ArrayAccessRecovery.Run(method);

        // And the same access where the array has more than one dimension: the read is already shaped like an
        // array access by now, and only its index is wrong - flattened, because the storage is. Beside the
        // pass above because it is the same question one rank up.
        RankTwoArrayAccess.Run(method);

        // Count, Add and Clear are small enough that il2cpp inlines them, leaving reads and writes of fields
        // the runtime library keeps to itself - which a game assembly cannot name at all.
        ListIdiomRecovery.Run(method);

        // Clamping at zero is compiled to a sign mask rather than a call, so the call has to be put back
        // from the arithmetic.
        MaxWithZeroRecovery.Run(method);

        // Types again, now that every pass above has had its say. Each of them turns a read of a runtime
        // structure into the thing it stands for - an array's length, a list's count, the method an interface
        // call goes to - and each of those is a statement about a type that the inference had already finished
        // before it was made. The length is the case that shows it: until it is recognised it is a read of
        // A lambda is a delegate built from a target and a method, and il2cpp generates one constructor per
        // delegate type which no method table names - so the allocation beside it had nothing to fuse with
        // and came out as `DOGetter<Color> getter = null;`. Here, where the allocation's local is typed.
        DelegateConstruction.Run(method);

        // A field whose address the compiler worked out into a register rather than leaving the offset in the
        // addressing mode - 499 additions of `this` and a constant. Nothing names the field then, and what
        // comes out is `Unsafe.AddByteOffset(ref this, 48L)` beside a call given a fresh `default(T)`. Before
        // the resolution below, so the reads it recovers are typed by it.
        FieldAddressRecovery.Run(method);

        // Directly after it, on the additions it declined: 62 of the 77 it refuses in Assembly-CSharp are
        // refused at a plain copy, because the address reaches the place it belongs through a register or
        // two first. Before the sinking below, since a use that wants the address settles the chain and the
        // sinking would only refuse it.
        FieldAddressThroughCopies.Run(method);

        // And where the address was only ever copied about and then read through - several field addresses
        // chosen between, then one load - no single field reference can speak for the choice, so the load is
        // moved to where the addresses are made and the choice picks between values instead. Straight after
        // the pass above, which has already taken every address that is wanted as one.
        FieldAddressSinking.Run(method);

        // unmanaged memory, so the index tested against it learns nothing from the test, and an untyped index
        // is written out as an object - which makes the bounds check a comparison between unrelated things,
        // and takes the loop and everything in it along with it.
        // A local assigned once, from something that says a type derived from the one it carries, holds the
        // derived one: a call's parameter type is true but weak, and `MonoBehaviour` cannot be more accurate
        // than the `LevelManager` the field it was read from declares. Directly before the resolution below,
        // which is what then finds the fields that only the derived type has.
        MostDerivedLocalType.Run(method);

        LocalVariables.ResolveTypesAndFields(method);

        // And the delegate invocations the pass that knows them could not see: by the time it ran, the
        // resolver had named the load of `invoke_impl` as the field it is and folding had put it straight
        // into the call, leaving nothing whose definition could be followed back to the delegate. Here the
        // field references exist and say which delegate it is. The trim beside it takes away the two
        // arguments an invoke does not have, the same way `AfterDelegateInvokeRecovery` does.
        DelegateInvokeRecovery.RunOnFoldedLoads(method);
        MetadataResolver.TrimResolvedCallArguments(method);

        // And the same for a runtime helper, which never becomes a managed method and so is never trimmed:
        // it keeps all eight argument registers and every load that filled one, however few it reads.
        KeyFunctionArguments.Run(method);

        // And again, on the arrays that only just became arrays. The first run is far above, and a base typed
        // by the resolution on the line before it was invisible to that one - so `array.Length` came out as
        // `Unmanaged memory load: [v397 @ X8_v20 (System.Int32[])+18]`, reading a header field of a type the
        // recovery had by then worked out. Before the arithmetic below, which would otherwise call the index
        // a number of its own and leave nothing for the subscript to be built from.
        ArrayAccessRecovery.Run(method);

        // Last of all, and only what nothing else could name: what arithmetic produces is a number, whatever
        // the destination register was called. `object obj3 = obj4 - 4L;` is not C#, and that shape begins 99
        // commented blocks in the game. **After** the resolution above, not before it - typing these early
        // stops the resolver ever recognising one as the base of a field or an element, which cost
        // `unmanaged` 128 -> 163. Here there is nothing left to lose: a local still untyped at this point is
        // one every other pass has already declined.
        ArithmeticProducesANumber.Run(method);

        // And again the fields that resolved only just now. A field is named by its property only where it
        // is known to be a field at all, and the pass that does so ran long before the line above - so a
        // field reached through something a later pass recovered was still being written by its private
        // name. `shape.Name()` becoming a real call is what makes `text._stringLength` a field reference,
        // and by then the chance to call it `Length` had gone: the statement did not compile and was lost.
        InaccessibleFieldRecovery.Run(method);
    }

    /// <summary>Runs last of all, on the copies the passes before it leave behind.</summary>
    public static void AfterUnusedLocalsAreDropped(MethodAnalysisContext method)
    {
        // Answering the class-prepared bit leaves a branch on `1 != 0` whose other arm holds il2cpp's
        // initialisation call, and reachability alone cannot see that the arm is unreachable - a conditional
        // jump points at both of them. Directly above the collection, which is what then takes the arm away,
        // and far above ConditionSinking, which folds a condition into its branch and would leave this
        // nothing to read.
        ConstantBranchFolding.Run(method);

        // Recovering an indexed access leaves the length its bounds check read feeding nothing, and dead code
        // elimination will not take a call. After the access recovery that orphans it, and above the
        // collection below, which is what then finds the array read it went through dead too.
        UnusedLengthRead.Run(method);

        // A call recovered out of the walk il2cpp inlined to find it leaves that walk reaching nothing, and a
        // walk is a loop - so its two blocks are each other's predecessor and neither is ever left without
        // one. Nothing that counts predecessors will take them, and while they stand the comparison that ends
        // the walk is still read by the branch that repeats it, which holds the whole runtime chain it was
        // reached through alive. Before the collection below, which is what then finds that chain dead.
        UnreachableBlockRemover.Run(method);

        // The passes above turn reads of runtime structures into the thing they stand for - a static field, an
        // array's length, a list's count - and what those reads were reached through is then read by nothing.
        // A class pointer and the static storage taken out of it have no way of being written down, so left
        // behind they are placeholders in the middle of statements that were otherwise recovered.
        DeadCodeEliminator.Run(method);

        // A lambda that captures nothing is cached in a static field, and the decompiler only puts the lambda
        // back when it sees that field used directly. Runs here, on the copies phi removal leaves behind.
        CachedDelegateRecovery.Run(method);

        // A field loaded well above the one thing that reads it has to travel in a local, and the local is
        // what gets a name. Runs after the lambda cache is folded, so what lies between the two is settled.
        FieldReadSinking.Run(method);
        LocalVariables.RemoveUnused(method);

        // Again, now that a receiver read from a field is the field itself rather than a copy of it: what
        // the field is declared as is what says which instantiation a shared body was called as.
        GenericSharingRecovery.Run(method);

        // The stack protector - not written by anybody, and with the `__stack_chk_fail` it guards already
        // dropped in the lifter, all that is left is a comparison of two places the recovery cannot name.
        // **Late**, because the shape only exists once the frame slots have been named and the two reads have
        // been propagated into the comparison; run beside the null check it found nothing at all.
        StackProtectorRemoval.Run(method);

        // A condition standing next to the branch that asks it needs no name, and the decompiler folds it in.
        // Last, so that it sees the copies the passes before it leave behind.
        ConditionSinking.Run(method);

        // A shared generic body opens by asking whether its own runtime context is filled in yet. It is, by
        // the time the method's code runs. **Here and not beside the class guard**: the comparison only
        // reads the field directly once the copies between them have been propagated away, and earlier it is
        // two instructions this does not match.
        RgctxGuardFolding.Run(method);

        // A mask to thirty-two bits on something that has no more than thirty-two is the register's width and
        // not arithmetic, and it makes a `Color32` stop looking like one. Late, where the value has its type.
        WidthMaskIsIdentity.Run(method);

        // One immediate broadcast into every lane of a struct of floats - `Color.white` is `FMOV V0.4S` and
        // one store, and the lifter keeps the number and loses the broadcast. Here, where the destination has
        // its declared type and before the generator has to write the number down as a cast.
        FloatStructBroadcast.Run(method);

        // A `Nullable<bool>` is two bytes read as one number and asked one question; the answer is a field of
        // it, and the type has to be resolved before that can be seen - so, here rather than in the lifter.
        NullablePackedCompare.Run(method);

        // A struct of floats travels in one register per field and the lifter named only the first, so an
        // argument reached the generator as a float where a Vector3 belongs. **After everything**, because
        // the operand it leaves is not a value anything else can reason about, and because the registers it
        // reads have to be the ones that survived every pass above rather than the ones the lifter emitted.
        HomogeneousFloatArguments.Run(method);
    }
}
