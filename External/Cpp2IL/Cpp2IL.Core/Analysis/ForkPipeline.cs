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
        // Before the pass below, which aliases an address register onto its slot and removes the move that
        // said so - after that there is nothing left to record which slot a libm out-pointer named.
        OutPointerSlotResult.Run(method);

        // A slot the callee writes through an out parameter is only telling apart from one the caller reads
        // while the registers are unversioned: once single assignment form is built, the zero the compiler
        // put in the slot beforehand has already been propagated into the read after the call, and the two
        // are the same constant.
        OutParameterWriteback.Run(method);

        // Here because this is the last place the evidence exists: nothing in the caller reads the outgoing
        // argument area, so the first run of `DeadCodeEliminator` - the next thing to happen after this hook -
        // removes the stores that put a stacked struct of floats there. A direct call's callee is resolvable
        // here because the lifter looked it up to lay the operands out in the first place, and substituting
        // before single assignment form is built is what lets the value be versioned and kept alive like any
        // other. After the two above, which are about slots this never looks at.
        StackedFloatArgument.Run(method);

        // Same reason as the pass above, from the other end of the convention: a struct of floats is
        // *returned* in v0..vn, the return is named x0, and so nothing reads those registers - the first
        // `DeadCodeEliminator`, which is the next thing to happen after this hook, deletes the whole chain
        // that computed the answer. This is the last place the evidence exists. Before single assignment
        // form is built, so the lanes are versioned and reach their definitions like any other operand.
        VectorReturnAssembly.Run(method);
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

        // And the lanes the pass above will not name, because the only thing that reads them is a call and a
        // call computes nothing. Here rather than inside it: which registers a call's arguments really occupy
        // is the callee's signature, not this method's, and the run of eight ends in a different place on
        // each side. `DrawCross` forwards `spot.z` straight into `Debug.DrawRay` and had nothing but a
        // `default(object)` to pass.
        ParameterLaneAtACall.Run(method);

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

        // And the ones no copy and no declaration reaches: a struct the compiler kept in the frame, read back
        // through a slot address, so nothing joins it to the call that produced it. Directly after the pass
        // above, so it only sees what that could not settle.
        LoneInstantiation.Run(method);

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

        // And the steps before it, for the same reason: `methodInfo->rgctx_data` and `methodInfo->klass` are
        // named by the type the resolver gave their destination, and what reads through them is named too, so
        // the read itself answers nothing and only says it could not be answered.
        RgctxReadRemover.Run(method);

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
        // A delegate over a `virtual` method reaches it through the vtable, so the constructor was handed an
        // unnamed pointer rather than a method. Directly above, because this is what gives it one - and above
        // the resolution that would otherwise make the vtable read look like a field.
        VirtualMethodPointer.Run(method);

        // And the same for a lambda inside a generic type, whose method the compiler cannot name at compile
        // time and reads out of the runtime generic context. The resolver already works the entry out and
        // writes it onto the local's *type*; the constructor needs it in the operand.
        RgctxMethodPointer.Run(method);

        DelegateConstruction.Run(method);

        // A field whose address the compiler worked out into a register rather than leaving the offset in the
        // addressing mode - 499 additions of `this` and a constant. Nothing names the field then, and what
        // comes out is `Unsafe.AddByteOffset(ref this, 48L)` beside a call given a fresh `default(T)`. Before
        // the resolution below, so the reads it recovers are typed by it.
        // Directly above, undoing the fold that hides an address from it: copy propagation puts a field
        // straight into the addition, and neither the recovery nor the sinking below reads that shape.
        FieldAddressBase.Run(method);

        // And beside it, the same undoing for the other base the sinking cannot read: a distance into a type's
        // static storage. `Vector2.zero.y` is `Add v121, [Il2CppClass<Vector2> + 0xB8], 4`, and one producer
        // the sinking cannot name refuses the whole chain - so `BaseButton::DoStateTransition`, which chooses
        // between a static `Color` and a field of `this`, loses both arms. Here rather than lower down,
        // because the static storage read is still in this form at this point and the resolver names it later.
        StaticFieldAddressBase.Run(method);

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

        // The same question one level down: not two addresses chosen between, but two *offsets* - which is
        // what `show ? highLightStarSprite : hideStarSprite` compiles to, a select of 40 and 48 and one load
        // at `this + <register>`. Here, where the base is typed and before the resolution below turns the two
        // constant reads this leaves behind into the fields they name.
        SelectedFieldOffset.Run(method);

        // unmanaged memory, so the index tested against it learns nothing from the test, and an untyped index
        // is written out as an object - which makes the bounds check a comparison between unrelated things,
        // and takes the loop and everything in it along with it.
        // A local assigned once, from something that says a type derived from the one it carries, holds the
        // derived one: a call's parameter type is true but weak, and `MonoBehaviour` cannot be more accurate
        // than the `LevelManager` the field it was read from declares. Directly before the resolution below,
        // which is what then finds the fields that only the derived type has.
        MostDerivedLocalType.Run(method);

        LocalVariables.ResolveTypesAndFields(method);

        // And again, on the types that only just arrived. The pass above has to run before the resolution -
        // the point of it is to let the reads *through* the local resolve - but what assigns the local is
        // often a call, and a call's answer is typed by the resolution itself. `ApplyFaceParts` is the case:
        // `visualOffset` is written from `GetCellVisualOffset`'s answer, which said nothing yet, so the local
        // kept `UnityEngine.Object` - the parameter type of the null check on it - and the five reads through
        // it, one per face part, were all unmanaged memory. Resolving again is only paid for where the second
        // look actually learned something.
        if (MostDerivedLocalType.Run(method))
            LocalVariables.ResolveTypesAndFields(method);

        // And the delegate invocations the pass that knows them could not see: by the time it ran, the
        // resolver had named the load of `invoke_impl` as the field it is and folding had put it straight
        // into the call, leaving nothing whose definition could be followed back to the delegate. Here the
        // field references exist and say which delegate it is. The trim beside it takes away the two
        // arguments an invoke does not have, the same way `AfterDelegateInvokeRecovery` does.
        DelegateInvokeRecovery.RunOnFoldedLoads(method);
        MetadataResolver.TrimResolvedCallArguments(method);

        // Directly after the trim, which is what makes these dead: a shared generic body reads the MethodInfo
        // its call needs out of the runtime generic context, and once the call is resolved and that argument
        // is gone, nothing wants the load. It is a memory read, so the collection will not take it on its own,
        // and it stayed beside a perfectly recovered call as an unmanaged-memory statement.
        DeadRgctxRead.Run(method);

        // And the same for a runtime helper, which never becomes a managed method and so is never trimmed:
        // it keeps all eight argument registers and every load that filled one, however few it reads.
        KeyFunctionArguments.Run(method);

        // And again, on the arrays that only just became arrays. The first run is far above, and a base typed
        // by the resolution on the line before it was invisible to that one - so `array.Length` came out as
        // `Unmanaged memory load: [v397 @ X8_v20 (System.Int32[])+18]`, reading a header field of a type the
        // recovery had by then worked out. Before the arithmetic below, which would otherwise call the index
        // a number of its own and leave nothing for the subscript to be built from.
        // Before the fold below, which needs the stride to be a number. Inside a shared body it is: `T` is a
        // reference there, so the element size the compiler asked the class for is the pointer size.
        SharedElementIsAPointer.Run(method);

        // Directly above it, and for the same reason one level down: where the index was not free at the load
        // the compiler works the element's address out a step at a time, and the addition chain that results
        // is an array access to nobody - not to the recovery below, and not to the generator, which reads an
        // access out of the addressing mode alone. Here, where the array is typed and before the recovery
        // that then has the whole access in one operand to work with.
        ArrayElementAddress.Run(method);

        // And the elements the fold above only just made readable. `SeedArrayElements` types the value an
        // element read produces, and it wants `[array + index*width + 0x20]` - but where the compiler added
        // the header **last** the operand is `[<address> + 0x20]` with no index until the line above puts it
        // back, and by then the last resolution has run. `GenerateSmartBlock` reaches `selectedLayout` that
        // way, and everything downstream of it - the length test, the indexed read - fell over.
        LocalVariables.ResolveTypesAndFields(method);
        ArrayElementAddress.Run(method);

        ArrayAccessRecovery.Run(method);

        // Last of all, and only what nothing else could name: what arithmetic produces is a number, whatever
        // the destination register was called. `object obj3 = obj4 - 4L;` is not C#, and that shape begins 99
        // commented blocks in the game. **After** the resolution above, not before it - typing these early
        // stops the resolver ever recognising one as the base of a field or an element, which cost
        // `unmanaged` 128 -> 163. Here there is nothing left to lose: a local still untyped at this point is
        // one every other pass has already declined.
        ArithmeticProducesANumber.Run(method);

        // And again, on the lists that only just became lists. A local narrowed from `IList<T>` to `List<T>`
        // by the second look above has its `_size` named by the resolution beside it - and `_size` is private
        // with no property of that name, so without this the read trades `unmanaged` for `commented`.
        ListIdiomRecovery.Run(method);

        // And again the fields that resolved only just now. A field is named by its property only where it
        // is known to be a field at all, and the pass that does so ran long before the line above - so a
        // field reached through something a later pass recovered was still being written by its private
        // name. `shape.Name()` becoming a real call is what makes `text._stringLength` a field reference,
        // and by then the chance to call it `Length` had gone: the statement did not compile and was lost.
        InaccessibleFieldRecovery.Run(method);

        // A big struct is answered through the buffer the caller points x8 at, and nothing comes back in x0 -
        // so the return the lifter built names a register the method never wrote, and the four `RuleValue`
        // factories all answered `default` beside a buffer they had filled correctly. Last of all, because it
        // only acts where *nothing* assigns that register and every pass above is one that could still have
        // made it so; and inside this hook rather than the next, so the local it stops returning is dropped.
        IndirectReturnBuffer.Run(method);
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

        // A struct bigger than sixteen bytes comes back through a buffer and is then copied out of it, so
        // `Touch t = Input.GetTouch(i)` is a call whose answer was discarded and a `default(Touch)` beside it.
        // Here, where the call is resolved and the destination has its type - and above the collection below,
        // which is what then takes the buffer the copy no longer reads.
        IndirectReturnCopy.Run(method);

        // And the other half of the same import: clearing a struct to nought is `t = default`, which the
        // generator can write, rather than a call to something no method table names. Beside the collapse
        // above because it is the same call shape and the same question - what did this memory operation
        // stand for.
        ClearedStruct.Run(method);

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
        // And again on the context reads that only became dead just now: the refusal at the first run is a
        // liveness that later folding and unreachable-block removal have since made stale, so a MethodInfo
        // load whose only reader has gone stays as an unmanaged-memory statement beside a recovered call.
        // Immediately above the collection, so what it leaves is taken with everything else.
        DeadRgctxRead.Run(method);

        // And the copies single assignment form left claiming a struct too big for a register is what a
        // register held. `SsaForm` refuses the ones it can tell apart, but it asks while the graph is being
        // destructed - before the passes that settle what a local holds - so a local that only becomes an
        // enumerator later keeps its copy. Here, where every typing pass has run, and above the collection so
        // what it orphans is taken with the rest.
        WiderThanARegister.Run(method);

        DeadCodeEliminator.Run(method);

        // And the values that keep each other alive. Counting uses proves a definition dead only while every
        // local has one definition; taking that form apart turns a phi into a copy per edge, and a value
        // carried round a loop is copied back to itself, so a closed cycle of copies is never collected.
        // Directly after, on what the counting could not take.
        // And the values that keep each other alive. Counting uses proves a definition dead only while every
        // local has one definition; taking that form apart turns a phi into a copy per edge, and a value
        // carried round a loop is copied back to itself, so a closed cycle of copies is never collected.
        // Directly after, on what the counting could not take.
        DeadCopyCycles.Run(method);

        // A lambda that captures nothing is cached in a static field, and the decompiler only puts the lambda
        // back when it sees that field used directly. Runs here, on the copies phi removal leaves behind.
        CachedDelegateRecovery.Run(method);

        // A field loaded well above the one thing that reads it has to travel in a local, and the local is
        // what gets a name. Runs after the lambda cache is folded, so what lies between the two is settled.
        FieldReadSinking.Run(method);

        // The other half of the pass below, on the address rather than on a read through it: a constant
        // distance from a slot's address is the field that far into the struct the slot holds. Here for the
        // same reason - before the collection, because naming the slot is what gives it a reader, and it is
        // now the only thing that names the state machine at all.
        SlotFieldAddress.Run(method);

        // A read at a distance from a slot's address is a read of the slot that distance away - both are
        // frame offsets and adding them names it. Late, where every slot the frame uses has been named -
        // but **before** the collection below, because joining the read to the slot is what gives the slot a
        // reader. `sincosf` writes two of them; the sine's only consumer reads through the address, so with
        // this after the collection the slot had been dropped by the time there was anything to point at,
        // and `SubCellVisual::UpdateFaceTracking` lost it.
        SlotAddressRead.Run(method);

        // And the same address where it is passed rather than read, which is what a `ref` argument is. Beside
        // the pass above and for the same reason: naming the slot in the argument is what gives it a reader.
        SlotAddressArgument.Run(method);

        // Immediately after the two above, because they are what makes the copies exist: a value spilled to
        // the frame and read back was a memory operand while the type fixpoint ran, so there was no copy for
        // it to carry a type across, and the local on the receiving end of the read is still untyped however
        // well the slot is known.
        TypeAcrossANamedSlot.Run(method);

        // And the runtime context read out of a `MethodInfo` that only became one on the line above. The
        // resolver ran inside the type fixpoint, where a `methodInfo` spilled to the frame and read back was
        // a memory operand carrying nothing, so every step of the chain behind it - klass, rgctx_data, the
        // entry, its fields - stayed untyped for the whole of the method. That is the whole of
        // `<FromNewlineDelimitedJson>d__2::MoveNext`, whose twelve field accesses are reached that way.
        // To a fixpoint because each step types the base the next one reads through.
        for (var again = 0; again < 8 && RgctxResolver.Run(method); again++)
        {
        }

        // And a base that has only just been given a type still reads its members as distances. The resolver
        // is monotone and only replaces an operand nothing has resolved, so asking it again here costs the
        // sites it already did and answers the ones that had no type when it ran inside the fixpoint.
        MetadataResolver.ResolveFieldOffsets(method);

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

        // A struct of floats travels in one register per field and the lifter named only the first. **After
        // everything**: the fields the argument does not name are read by nothing, so dead code elimination
        // has already taken them - and it runs three times inside `Analyze`, before any of this. Running
        // this earlier to catch them is therefore not possible without moving elimination itself, and was
        // measured: byte-identical, because the divide computing the second field is gone by then either way.
        // Before the two below, which read a field's type to decide what to do with it: a byte masked out of
        // a field that overlaps it is that byte's own field, and until that is said the value is an `int`
        // where a `byte` belongs. Needs the fields resolved, which is why it is here and not with the
        // resolver itself.
        PackedByteField.Run(method);

        // Beside the rule below, and the same shape from the other end: a struct of floats assigned to a
        // field one member at a time, where only the first member landed on the field and the rest fell
        // between two of them. Needs the fields resolved and the members typed, so it is here rather than
        // with WideFieldStore, which splits the opposite case.
        StructFieldStoreSplit.Run(method);

        // Before the assembly below, and after everything that types a place: the first field of a struct
        // put where that whole struct is declared is the struct being handed on unchanged, and naming the
        // vector registers a call's answer lands in is what makes that shape appear at all.
        WholeStructAtItsOwnType.Run(method);

        // And the other half of that: a composite too big for two registers is handed over as a pointer to
        // one, so what the register holds is an address and every pass that names a distance from an address
        // named the field at nought. Beside the pass above because both are about a struct arriving where the
        // operand says a member of it.
        IndirectArgument.Run(method);

        // And the same struct assigned rather than passed. Too big for registers, it is copied by `memcpy` -
        // an import no method table names - so the call stayed an address with a field's arithmetic in front
        // of it. Here, where what the source holds is settled, since the length agreeing with the struct's
        // own size is what identifies the shape.
        StructAssignmentCopy.Run(method);

        // And again on the fields those two just named. Both turn something that was arithmetic or an
        // unresolved call into a field, long after the pass that says a private one by its property has run -
        // so `mergeParticle.sizeOverLifetime = ...` arrived as `particleImage3._sizeOverLifetime = ...`,
        // recovered exactly and still not something the project can write.
        InaccessibleFieldRecovery.Run(method);

        // Two passes that have to run again first, in this order, because each needs what the one before it
        // produces and both need what everything above them has just typed.
        //
        // A floating point constant reaches recovery as its own bit pattern, and `FloatLiteralRecovery`
        // reinterprets one only where something *else* in the same expression is known to be floating point.
        // Upstream runs it before a single pass in this hook, so a struct's field, a named lane and a
        // static's member are all still untyped when it looks: `Vector3.one * 0.85f` came out as
        // `one.y * 1062836634L`. The evidence rule already knew that shape and had simply not been shown it.
        FloatLiteralRecovery.Run(method);

        // And then the answer that constant types. A lane the vector splitter writes has no declared type, so
        // inference settles it on the register's width - `Int64` on a multiply of two `Single`s - and the
        // assembler below refuses any part that is not a float, so the three lanes were never put back
        // together and `tf.localScale = ...` was commented out whole. This has to be after the line above:
        // until the constant is a float there is nothing in the instruction saying the arithmetic is.
        StructInArithmetic.Run(method);

        HomogeneousFloatArguments.Run(method);

        // A shared generic body opens by asking whether its own runtime context is filled in yet. It is, by
        // the time the method's code runs. **Here and not beside the class guard**: the comparison only
        // reads the field directly once the copies between them have been propagated away, and earlier it is
        // two instructions this does not match.
        RgctxGuardFolding.Run(method);

        // And again here, because the answer above arrives long after the earlier pair ran at line 389 and
        // 413. Without this the branch stands, its arm stays reachable, and the
        // `il2cpp_codegen_initialize_method` call inside it is an honest-looking marker on a body that has
        // nothing else wrong with it.
        ConstantBranchFolding.Run(method);
        UnreachableBlockRemover.Run(method);


        // A mask to thirty-two bits on something that has no more than thirty-two is the register's width and
        // not arithmetic, and it makes a `Color32` stop looking like one. Late, where the value has its type.
        WidthMaskIsIdentity.Run(method);

        // A struct of two words travels in one register, and its halves are its fields: a shift of thirty-two
        // brings the top one down, and an array subscript in such a register is the bottom one. Late, beside
        // the mask above, where the local carries the struct's type and the accesses are already recovered.
        PackedPairField.Run(method);

        // And the same register read the other way: a call answers with a small struct in `w0`, and every
        // field of it is a shift and a mask off that number. Beside the pass above because it is the same
        // fact about the same register, and here rather than earlier because it needs the callee resolved to
        // know what the struct is.
        PackedStructFieldRead.Run(method);

        // Again, and last. A struct of floats standing where a number belongs is only recognisable once
        // something beside it *is* a number, and several passes below the first run are what make one: the
        // ternary between two field offsets produces the `float` that tells `Vector3.one * num` which of its
        // operands is the struct. `ButtonGroup::Select` kept "Part of a struct used as a value" for exactly
        // that reason - the number arrived after the question had been asked.
        StructInArithmetic.Run(method);

        // One immediate broadcast into every lane of a struct of floats - `Color.white` is `FMOV V0.4S` and
        // one store, and the lifter keeps the number and loses the broadcast. Here, where the destination has
        // its declared type and before the generator has to write the number down as a cast.
        FloatStructBroadcast.Run(method);

        // A `Nullable<bool>` is two bytes read as one number and asked one question; the answer is a field of
        // it, and the type has to be resolved before that can be seen - so, here rather than in the lifter.
        NullablePackedCompare.Run(method);

        // Last of all, and only about what a copy says: a register that once held a class pointer keeps that
        // stand-in for the rest of the method, and where the same register later carries a managed value the
        // copies between the two come out as `array = (CellData[])num17;`. Here, so that everything which
        // resolves against a stand-in has already run and only the generator sees the correction.
        // Beside the pass below and for the same kind of reason: a local carrying a number is not always
        // carrying one. A type test writes its answer on one arm and a nought on the other, and the join
        // takes whatever width the register had - so an `isinst` result was stored into a `long`.
        NullMergedLocal.Run(method);

        StandInCopyType.Run(method);

        // And where it declined, the copy itself is what is wrong. A register carrying a `MethodInfo*` on
        // one edge and an array on the next is two values, not one; `SsaForm` states that rule already but
        // asks before either end has a type. Directly after the pass above, which has first refusal.
        StandInEdgeCopy.Run(method);

        // And once more on what that just named. A read at 0x18 of an array is its length and a read at
        // 0x20 plus a scaled index is an element, and both are decided from the type the base carries - so a
        // base that only became an array on the line above was refused when the pass ran the first time.
        // `BoardController::BuildLevelPool` reads `_shuffleColors.Length` four times through a local the
        // initialisation guard had left holding `Il2CppClass<BoardSettingSO>`.
        ArrayAccessRecovery.Run(method);
        ArrayElementAddress.Run(method);

        // Last, because it is a question about what everything above left behind: every pass that recovers a
        // call replaces machinery with the statement it stood for, and the machinery does not go anywhere.
        // Dead code elimination cannot take it because a store into the frame counts as a use of what it
        // stores, so the whole chain behind it stays alive - the runtime context read, the class read, the
        // size of a `T`, the alloca, the frame the invoker thunk was handed. Reading a frame slot nothing
        // reads back as dead is what lets the chain unwind.
        // A generic state machine records no field offsets at all, so the layout has to be worked out - and
        // it is only used where every distance this method reaches for is one the layout puts a field at.
        // Late, because those distances are what it is checked against, and they are only all here by now;
        // the field recovery is then run again over what the offsets have made nameable.
        if (StateMachineFieldLayout.Run(method))
        {
            FieldAddressRecovery.Run(method);
            FieldAddressThroughCopies.Run(method);
        }

        // Before the two below, because settling the value-type question is what takes the class read - and
        // with it the runtime generic context - out of the way of everything else.
        SharingMeansAReference.Run(method);

        // And the branches that question was deciding, which are now decided.
        ConstantBranchFolding.Run(method);
        UnreachableBlockRemover.Run(method);

        // Here because this is after the LAST of the three foldings, so every edge that is going to die has
        // died. A phi copy is written into the predecessor, before its terminator, so it runs whichever way
        // the branch goes - which is right while both edges exist and wrong the moment one is taken away: the
        // copy for the dead arm sits in a block that is still reached and overwrites what the live edge put
        // there. `MetadataInitGuardRemover` has no such problem because it folds *before* single assignment
        // form is destroyed and drops the phi operand with the edge; these two run long after, when there are
        // only copies left and nothing was tracking which edge each belonged to.
        SsaForm.DropCopiesOnDeadEdges(method);

        // A field looked up through the runtime's own `FieldInfo` is still that field, and the step from
        // `klass->fields` says which one. Late, because the class pointer has to be named for the step to
        // mean anything - and after the folding above, which is what leaves the read and the step as one
        // addition.
        FieldFromItsRuntimeInfo.Run(method);

        // Before the collection below, which is what actually takes the size read, the rounding and the
        // alloca away once nothing is left calling for them.
        ClearingASizedByT.Run(method);

        // Before the collection below, so that the class pointer the handle was carrying is read by nobody
        // and goes with it. After everything that names a runtime class, which is what there is to say.
        TypeHandleIsTheClassItNames.Run(method);

        // Before the collection below, and after everything that could still turn a call into a throw: the
        // object a `throw new Foo(message)` built is right there, and only the type the helper looked like
        // was being thrown.
        ThrowTheConstructedException.Run(method);

        DeadFrameArithmetic.Run(method);

        // Counts and changes nothing, and only when ADDBASE_CENSUS names a file: the surviving additions of a
        // reference and a byte offset, which is the CS0019 family. Last of all, so what it sees is what the
        // generator sees.
        UntypedBaseCensus.Run(method);
    }
}
