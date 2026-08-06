# Local packages

`AssetRipper.Cpp2IL.Core` and `AssetRipper.LibCpp2IL` are normally taken from nuget.org, where they are published from
the `assetripper` branch of [AssetRipper/Cpp2IL](https://github.com/AssetRipper/Cpp2IL). The published 1.0.8 is that
branch exactly, and that branch is behind the upstream `development` branch — including the commits that
made Il2Cpp method body recovery work. Script content level 3 is a different feature with them than without.

So the two packages here are built locally instead, and `nuget.config` adds this folder as a package source.

The source they are built from is in `../External/Cpp2IL`, and `../External/Cpp2IL/FORK.md` says where it starts
from, what it touches in upstream's own files, and how to move it onto a newer Cpp2IL.

## What the packages are

* `SamboyCoding/Cpp2IL` `development` (`b20ca0d`, "Lib: Fix v106"), which is still its head
* the three commits the `assetripper` branch adds on top, cherry picked: `Move dependencies into main projects`,
  `Change package id`, `Change version`
* the fixes below, all for arm64, which is the architecture every Android build ships and which upstream
  exercises far less than x86:
  1. `ApplicationAnalysisContext.ThrowHelperNamesByAddress` from `Dictionary` to `ConcurrentDictionary`. Throw helper
     recovery is new in `development` and reads that cache from the threads that build the assemblies in parallel, so
     an unsynchronised dictionary corrupts itself and every method in the assembly ends up as a body that throws the
     resulting `IndexOutOfRangeException`.
  2. `NewArm64KeyFunctionAddresses.FindAllThunkFunctions` was a copy of the x86 version, looking for the `0xCC` byte
     that pads x86 functions. Arm64 pads with all zero instructions, so it never found a thunk, and every runtime
     helper reached through one stayed unknown. Rewritten against the instruction stream, and its backtrack limit
     fixed: `while (backtrack * 4 < maxBytesBack)` allowed no steps at all for the callers passing 4.
  3. `NewArmV8InstructionSet` gave a call to anything that is not a managed method no operands, because it derives
     them from the callee's signature and a runtime helper has none. Under aapcs64 the first eight arguments are in
     x0 to x7 and the result comes back in x0, so those are now handed over. Without them `KeyFunctionRecovery` could
     not rewrite `il2cpp_codegen_object_new` into `newobj`, and no object creation was recovered at all.
  4. `NewArm64KeyFunctionAddresses.AttemptInstructionAnalysisToFillGaps`, which did not exist. The metadata
     initialisation function is found on x86 by disassembling one corlib method with Iced, which cannot run here.
     It is instead found by the property that no other function has: il2cpp emits a guarded call to it at the top of
     nearly every method, so it is by far the most common branch target near the start of a method body.
  5. `NewArm64KeyFunctionAddresses.GetWriteBarrier`, which did not exist either. Ported from the x86 search: look in
     corlib methods that store a reference into a field for a call preceded by that store and by the slot's address
     being placed in x0.
  6. `NewArmV8InstructionSet` folded an ADRP based load into a load from a fixed address but did not do the same for a
     store, so the store that records that a method's metadata is initialised was never recognised and the guard
     around it was never removed.
  7. `LibCpp2IlContext.CheckForPost27GlobalAt` read the word at the address the code loads from and decoded it as a
     metadata usage. That works for an executable, which addresses its usage globals directly, but not for a shared
     object: its usage globals are extern with default visibility, so they are preemptible and reached through the
     global offset table. On `libil2cpp.so` every one of the 58000 usages was therefore missed, which is to say no
     string, type or method reference was recovered at all. It now follows the table entry.
  8. `MetadataResolver` collapses the extra dereference that table introduces, so the rest of the analysis sees the
     same shape it would on an executable, and `LocalVariables` follows single assignment copies when working out
     what type an allocation produced, since the class rarely reaches the allocation call in the same value it was
     loaded into.
  9. `IlGenerator` reconstructs a constructor call il2cpp inlined away. Where the allocation is followed by a call to
     the base constructor, or by nothing, the type being allocated still says what was built, so its own constructor
     is used with default arguments rather than emitting an untyped allocation.

  10. `IlGenerator` loaded a type used as a value by constructing an instance of it. A type used as a value is a
      runtime class handle, which is a native integer, so that said something the code never did — and it did not
      compile either, because the constructor it picked was often one the project cannot reach.

  11. `NewArmV8InstructionSet` named every floating point argument and return `v0`, but the disassembler names the
      register by the width in use, so a float is `s0` and a double `d0`. Nothing connected a float argument to where
      it was read, and every one came out as `default(float)`.
  12. `IlGenerator` returned whatever was in x0 for a method returning a struct. Aapcs64 returns a struct in the
      vector registers, in a pair of general purpose registers, or through memory the caller points x8 at - never in
      x0 alone, where the receiver usually still sits. Returning it says something plainly untrue and does not
      compile, so a method whose return could not be recovered now returns a default instead.

  13. `NewArmV8InstructionSet` lifted `b.cond` as an unconditional jump. Disarm reports a conditional branch as a
      plain `B` carrying a condition code, and nothing looked at the condition, so the fall-through path of every
      `b.eq`/`b.ne`/`b.lt`/... simply vanished: roughly ten thousand branches in the game's own assembly, and with
      them most of the code each `if` guarded. The branch is now lifted as the comparison it makes, by recording
      what the preceding compare put in the flags. This is the change that made most of the method bodies appear
      at all, so it is also why the remaining numbers below moved in both directions.
  14. Instructions the lifter had no case for became a placeholder string sitting in the middle of whatever statement
      they belonged to, which took that statement down with it. Implemented: the loads and stores that differ from
      `ldr`/`str` only in width or scaling, `blr`, `movi`, the conversions, `fdiv`/`sdiv`/`udiv`, `fneg`, the
      multiply-accumulate family, the bit-clear family, the variable shifts, `cset`, and the conditional selects -
      the last through a new ISIL opcode, since CIL has no select and it has to become the branches the compiler
      removed. System register reads and barriers now produce nothing rather than a placeholder, which is what they
      mean in managed terms.
  15. `ThrowHelperRecovery` found the helpers that exist purely to throw by looking for an x86 `lea` of a string
      constant, so on arm64 it found none and every one of them stayed an untranslated call. On arm64 a constant's
      address is built from an `adrp`/`add` pair, so the pair is followed instead. That recovered 2649 `throw`
      statements, most of them the null check in front of a field access.
  16. Stack slots were addresses. Arm64 spills through `[sp, #n]`, and a load from one is a load from memory nothing
      can say anything about - so every spilled local, and every register saved in a prologue, poisoned the statement
      that read it. A slot is a variable, not an address, and modelling it as one lets a spill and its reload become
      a copy, which type propagation follows and copy propagation then removes outright. The x86 side already worked
      this way; the arm64 lifter just never produced the operand for it.
  17. One physical register had several names. Arm64 calls the low half of `x1` `w1`, and `v0` is also `s0`, `d0`
      and `q0` - and each name was its own variable, so a value written as a 32-bit integer and read as a pointer
      was never connected to itself. An `int` parameter passed in `w1` and looked for in `x1` was the case that
      matters most: it made every iterator's generated constructor read `this.<>1__state = default(int)` instead of
      `this.<>1__state = <parameter>`, which is exactly the shape ILSpy checks for before it will write a method
      back as `yield return`.
  18. `x31` in a data-processing operand is the zero register, not the stack pointer, so reading it always yields
      zero - but it was left as a register, which made a field set to `0`, `false` or `null` read as an unexplained
      local. A zero stored where a reference is expected now emits `ldnull`, since storing an integer into a
      reference field is not verifiable IL and the whole body would be discarded for it.
  19. A value whose type was never established lowers to a native integer, and C# has no ordering operators on
      `IntPtr`/`UIntPtr`. Once (13) started producing ordered comparisons, 213 of them decompiled to source the
      editor rejects; both sides are now widened to a 64-bit integer first, which says the same thing and compiles.

  20. Virtual dispatch was never recovered. A virtual call compiles to three steps - read the runtime class out
      of the object's header, read a slot out of that class's vtable, call through it - and none of them names
      a method, so the call stayed indirect and the statement around it was lost. The object's type is right
      there in the first step, though, and with the slot it says exactly which method was meant.
      `VirtualCallRecovery` puts the call back: `FreeModifier.set_Radius` now ends in
      `_Graphic.SetVerticesDirty()`, as it does in the original source.
  21. `IlGenerator` had no case for the runtime helpers the key function search does name, so a call to one
      became a placeholder. `SzArrayNew` is `newarr`, `il2cpp_vm_object_is_inst` is `isinst`, and a metadata
      initialisation the guard remover could not reach says nothing a managed reader can see, so it is dropped.
  22. A default value for a runtime handle was `null`. A handle reports that it is not a value type, because
      there is no managed type behind it to be one, but it lowers to a native integer - and null is not one.
  23. A local typed as a managed reference or a pointer. Nothing here emits `ldloca` or `ldarga`, so such a
      local can only hold a lie, and it does not compile either: C# has no pointer to a managed type, and the
      decompiler writes the declaration out as one.

  24. A static field the project cannot name now goes through the property it is reached by. `Vector2.zero` is a
      property over a private static field, and il2cpp reads the field, so the recovered code named the field -
      which the project cannot see, and which its own build of that assembly may not even have. Which property it
      is comes out of the getter: it loads the type's static storage and takes one offset out of it, so matching
      on that offset identifies the value exactly. Matching on the field's name would only be a convention, and
      would put a wrong value in the output the moment it did not hold. The getter is read from the instruction
      stream rather than through the analysis, so it costs almost nothing, cannot recurse back into the analysis
      that asked, and cannot race another thread doing the same. `Vector2.zero`, `Vector3.one`,
      `Quaternion.identity` and the rest of that family all come back as themselves.

  25. A pre-indexed store dropped the writeback. `str xzr, [x19, #0x18]!` moves the base before writing through
      it, and the instructions after it address the frame from the new value - so losing it made every following
      store land at an offset nothing could resolve, and the store was discarded. The load side had handled this
      for a long time; the store side never had. Around 260 stores across the assembly, and the reason an
      iterator's state machine came back missing the fields it sets.
  26. An iterator's factory set its state twice. Il2cpp inlines the one-line constructor of a state machine, so
      what is left is an allocation followed by a store of the state - and the constructor was reconstructed with
      a default argument while the store was emitted as well. The store is the one that knows the real value, so
      it becomes the constructor's argument and stops being a store. It is found by tracing the object back
      through however many copies of the allocation were made, since the store is rarely made through the same
      one. Without this the decompiler's iterator transform rejected every factory and gave up silently.

  27. A value read out of memory was carried past the point where it stopped being true. The simplifier forwarded
      a field read into every later use of the local it was kept in, walking straight past writes to that same
      field and past calls - so an iterator's `MoveNext` ended in `return this.<>1__state == 0`, reading a field
      twice that the machine code read once and then overwrote twice. It now stops at a write, at a call, and at
      a join, since the substitution is made in an instruction that every path into the join shares and only one
      of them has to write the field for the value to differ. This was a wrong answer, not an untidy one.
  28. A merged return gave back no constant. A compiler routinely folds several returns into one block that works
      the value out - `cmp w21, #0; cset w0, eq` is `return num == 0` for every path at once - and while that is
      the same value, it is not the literal the source wrote, and a reader looking for an iterator needs the
      literal. Where a path reaching the return has already tested the condition the return computes, the return
      is copied into it with the answer written out. 13 iterators come back as `yield return` and 12 as `yield
      break` where none did before.
  29. `movk` writes one 16 bit field of a register and leaves the rest, which is how a constant too wide for one
      instruction is built - most often the bit pattern of a float, so this feeds the float literal recovery.

  30. `tbz`/`tbnz` - test one bit and branch, which is how a bool is tested - masked nothing and then branched on
      the zero flag, which belongs to whatever set it last. Roughly nine thousand branches in the game's own
      assembly went the wrong way, and the block structure around each one went with them.
  31. A call was only resolved when it was the last instruction of its block, so a constructor whose whole body is
      a call to its base and a return had that call left unresolved, along with every other call sharing a block.
  32. Every instantiation of a generic method compiles to one shared body, so several methods answer to one
      address. The lifter took the first candidate's signature and the *caller's* return register, which dropped
      the runtime method argument - the only thing that says which instantiation is being called - and left the
      result unconnected. `SaveManager.I` came back as `((SaveManager)0)`. Both the call and the tail call form
      now hand over every register the convention could have used, and the step that reads that argument resolves
      them. That step also no longer requires the address to name a method at all, since for a shared body it
      does not.
  33. A field a type inherits was not looked for, because only the type's own declarations were searched. Where
      the base is generic the metadata has no offsets for it - they depend on the type arguments - but a generic
      type deriving straight from object has its fields at the top of the object, so that offset is the header
      and nothing else. That is the layout rule rather than a guess, and it is the only case taken.
  34. Negating a bool held in a field was a bitwise not, because only a typed local counted as boolean. That is
      what wrote `if (~(Define.DisableTutorial ? 1u : 0u) != 0)`.
  35. A by-reference parameter and the receiver of a value type method both want an address, and a value was
      being loaded into those positions - not verifiable IL, and written back out as a pointer to a managed type,
      which C# does not have. Both now pass the address of a local, and where the argument already is a local
      that one is used, so what the callee writes lands where the code reads it.
  36. A call kept the operands it does not take. Under the unknown-callee convention a call is lifted with every
      register, and il2cpp adds a hidden trailing argument of its own - a delegate invoke is handed the
      delegate's method pointer - so the surplus kept the load that produced it alive and wrote it out as a
      placeholder.
  37. An operand carrying a shift was read as though it did not. arm64 lets an operand be shifted on the way
      into the operation, and `Mathf.Max(0, x)` compiles to `x & ~(x >> 31)` with that shift on the second
      operand - so ignoring it turned the expression into `x & ~x`, which is zero. The disassembler reports how
      far the operand is shifted but not which way, so the direction is read from the two bits the architecture
      keeps it in.
  38. A computed value was left with no type, and an untyped value lowers to a native integer - so arithmetic
      on plain ints came out as operations on `object`, which does not compile and was commented out. Types now
      travel arithmetic in both directions, restricted to the primitive integers so that address arithmetic
      cannot claim a reference type, and to the logical operations for booleans, which is what makes a negated
      condition read as a condition.
  39. An instance call lost the object it was called on. The receiver is passed in the first argument register,
      but where the callee never reads it - it only asks a singleton a question - the compiler proves the
      argument dead and stops materialising it, leaving the load, the null check that C# still requires, and
      the call. The register then held the previous call's result, and reading it as the receiver produced a
      `bool` cast to the class it was supposedly called on. The value null-checked on the way in is the
      receiver, and it is recovered before those checks are removed.
  40. A test of the sign bit stayed a test of the sign bit. arm64 asks whether a number is negative by testing
      its top bit, so `x >= 0` came back as `(x & 0x80000000) == 0` - and the mask, being a bit pattern, is a
      64-bit constant, so the comparison also stopped reading as one between ints.
  41. A value was written to a local and read straight back. Every value is given a local of its own, so a call
      whose result is used once was stored and loaded again; the decompiler folds a value it finds on the stack
      into the expression that uses it, but keeps a local as a local - which is why a method of a few lines came
      back as a dozen statements named after registers.
  42. The blocks were written out in the order they were discovered rather than the order they run in, which
      put a value and its use at opposite ends of the method. Every block ends with its jump written out in
      full, so the order is free to choose: laying them out in the order control takes lets the jump between
      neighbours be dropped and the value travel on the stack.
  43. A conditional picked between two string literals by choosing between the two slots holding them and
      reading through the choice, and only a slot read directly was being resolved - so a pair of strings came
      back as a choice between two values loaded from somewhere, with no type, written as an `object` and cast.
  44. A comparison computed into a register and branched on later became a named boolean written on one line
      and read on another. A comparison only reads, so it is moved down to its branch where nothing in between
      changes what it reads, and then needs no name at all.
  45. An instance call's receiver was read a second time from the field it had just been copied out of, where
      the source named the variable. Both read the same thing, but only the second shape is one the decompiler
      recognises.
  46. A lambda that captures nothing is built once and kept in a static field, and the value travelled from the
      field to its use in a register - which became a variable standing where the decompiler needs to see the
      field itself. Finding nothing to fold, it kept the class the compiler generated to hold the cache and
      wrote it out: a `<>c` with a static field and the lambda's body as a method, none of which was in the
      source. Naming the field instead says the same thing and leaves the shape that folds back into a lambda.
  47. A call in tail position is a branch to a register, and it was lifted as an indirect call with nothing else:
      no arguments, no result, and no return. So the call could not be recognised, and the block ran on into
      whatever followed it - usually the throw belonging to a null check it had already passed, which is what the
      recovered body then ended with. It is lifted like any other call through a register now, and returns.
  48. A virtual call was only resolved when the method it dispatched to was declared by the receiver's own type or
      a plain base. A generic base reports no base type of its own - it is a wrapper around the type it
      instantiates, and only that type has the metadata - so the search for the slot ended one type early and
      every call inherited through one stayed indirect. Here that is every singleton and every generic view.
  49. A generic method shares one body between its instantiations, so the types its body needs are read out of a
      table on the runtime method rather than from an address. Only the table belonging to a type was followed,
      and the one belonging to a method was left as a load from unmanaged memory - taking with it everything read
      through it, which is a static field of the type the entry names.
  50. The class initializer allocates a type's static storage and reports a failure to allocate, so the name of
      that exception appears in its body - which is exactly what identifies a throw helper, and it was taken for
      one. The guard around it was then a branch to a throw, and could not be removed as the boilerplate it is.
  51. A generic method's context is filled in the first time the method runs, behind a test of whether it is
      filled in already. That guard was left in place, so a body that used it opened with a test of unmanaged
      memory and a call whose result went nowhere.
  52. Calling a method whose last parameter is `params` without arguments for it passes `Array.Empty<T>()`, whose
      whole body is a read of one field - so it is inlined and the call site reads the field. The type holding it
      belongs to the runtime library and cannot be named from a game assembly, so the statement did not compile
      and was commented out. Naming the method it is the body of loses nothing.
  53. Two CIL instructions that say the same thing are equal, and before offsets are worked out a body is full of
      instructions that say the same thing - every `nop`, every `ldc.i4 0`. The passes that fold values onto the
      stack put instructions in a set and a dictionary to ask where each one is, so they could not tell one from
      another: a branch to any `nop` made every `nop` look jumped to, and removing an instruction removed the
      first one in the body equal to it rather than the one meant. They compare by identity now.
  54. Only a store whose load came next could be folded, and the load rarely does: an operation pushes its operands
      left to right, so a value computed last - the last argument of a call, the right side of a comparison - is
      read after the ones before it, and the store and the load end up a receiver apart. What sits between them is
      only ever loads of locals, arguments and literals, none of which a call can change, so those are moved ahead
      of the value and the pair meets. `TutorialMenu.I.Show()` came back as three statements and two named
      variables; a chain of them came back as one variable per link.
  55. A method whose result the caller ignores still returns one, and the lifter gives that result a register like
      any other, so the body stored it. Nothing read it, but the decompiler will not drop a store on its own
      account and wrote out a declaration - `Coroutine coroutine = self.InvokeDelay(...);` for a line the source
      wrote as a call and nothing else. The value is discarded instead of named.
  56. Clamping a number at zero is compiled without a branch: the sign spread over the whole word, complemented,
      and anded with the value - two instructions on arm64, where the complement is built into the and. No call
      survives for the analysis to name, so `Mathf.Max(0, x)` came back as two lines of arithmetic over a variable
      that existed only to hold the mask. The arithmetic is written back as the call it computes.
  57. A field is loaded into a register well before the thing that uses it, because the compiler is free to
      schedule the load early and does. With a whole cached-lambda block between the two, the value has to travel
      in a local, and a local is written out as a declaration: `MonoBehaviour self = base.context;` on one line
      and the call on the next, where the source wrote one expression. The one instruction that reads it names
      the field instead - only where nothing on the way could have changed it.
  58. Where a generic type's fields land depends on its type arguments, so il2cpp records no offsets for the
      definition and every one of them reads as zero. A body compiled against the definition - the accessors of
      a property on it, and anything else it declares - was then reading unmanaged memory at an offset matching
      nothing, so `State<T>` came back with an auto-property whose getter was a comment and a `BindContext` with
      an empty body. Where such a type derives straight from object, the field it declares first begins at the
      object header; that one is named, through the type's own parameters so that writing it is a store to the
      instance rather than through a cast to an unbound type.
  59. il2cpp gives every managed method its own runtime <c>MethodInfo</c> as a last argument, and for a body
      shared between the instantiations of a generic it is the only thing telling them apart - the type
      arguments, the runtime class and the generic context all hang off it. The arm64 lifter did not put it in
      the parameter list, so it was neither named nor typed, and every chain that starts there read as
      unmanaged memory: the base constructor of a generic type, the class it allocates, its statics.
  60. That argument then hid the one a call passes. A call's own runtime method is loaded into a register
      before it, and the search for one went backwards from the last, so it found the method's own - which
      says which instantiation this body is, not where the call goes - and gave up. Skipping it recovered
      three hundred call targets the change above had cost.
  61. A generic type records no offsets at all, because where its fields land depends on its type arguments,
      so a body compiled against the definition read unmanaged memory at an offset matching nothing. One body
      is shared between every instantiation whose arguments are references, and in it a type parameter is a
      pointer like any other - so a type deriving straight from object has its fields at the object header,
      one after another a pointer apart. `State<T>` came back with an auto-property whose getter was a
      comment; `StateMachine<T>` with four.
  62. A generic body cannot call a method its type arguments decide by address either, since one body serves
      them all, so it reads a runtime method out of the generic context and calls through the pointer at the
      front of it. The context's method entries were not followed and the call was left indirect, which is
      the whole of what a constructor doing nothing but `base()` came back as: three reads of unmanaged
      memory and a call to nowhere, in a class whose source had no constructor at all.
  63. An instance of a generic type is a wrapper around the type it instantiates, and only that type carries
      what it derives from - so a value of type `Action<string, int>` did not read as a delegate, and the
      inlined `Invoke` behind it stayed an indirect call through the delegate's own fields. That is every
      `Action<...>` and `Func<...>` in a game.
  64. An allocation in a generic body names the type it allocates through the same context table, and there
      the entry is the operand rather than a value that can be typed - so it had to be put in place. Leaving
      it also kept the whole chain that led to it alive, each link written out as a read of unmanaged memory.
  65. Every constructor calls its base one and the language writes that call back, which is why no source
      file contains it. il2cpp puts it after the field initialisers rather than before, where the decompiler
      can no longer fold it into the constructor's header, so it came out as `base..ctor()` - which C# has no
      way to say, leaving a commented line in a constructor whose source had nothing there.
  66. il2cpp compiles one body for every instantiation of a generic whose arguments are references, with
      `object` standing in for each of them, so a call resolved by address named `Dictionary<object, object>`
      whatever it was written as - and the recovered source cast the receiver to that and every argument to
      `object`. The object being called on still carries the real instantiation, and a field still carries
      what it was declared as, so that is what the call is given back.
  67. A store pair takes its two halves from the width of the registers it names, and the width was read off
      the converted operand - where the zero register is written `xzr` whichever half was meant. A
      `stp wzr, w9, [x8, #0x18]`, which is how a list is cleared, then wrote its second half eight bytes along
      instead of four, landing on the field after the one it meant.
  68. The same missing offsets that hide a generic class's own fields hide every field of a `List` or a
      `Dictionary` a caller reaches into, because those are generic too. Walking the layout the shared body
      sees - each field at the size it has there, aligned to its own - names them, and the sizes of the
      primitives are what let the walk get past the first `int`.
  69. A field of a generic type is named through the arguments the type has where it is read, and that naming
      is worked out fresh at each place - so two references to one field were never the same object, and the
      test that stops a read being carried past the write that makes it stale never fired. A list's count was
      read after it had been set to zero.
  70. `Count`, `Add` and `Clear` are small enough that il2cpp inlines them everywhere, leaving reads and writes
      of `_items`, `_size` and `_version` - fields the runtime library keeps to itself, which a game assembly
      cannot name at all. Each is recognised by the one thing in it that is still a call, or by the field it
      reads: `Count` is the read of `_size`; `Clear` is `Array.Clear(_items, 0, _size)` behind a test that
      there is anything to clear; `Add` is a test of `_size` against the array's length with the item written
      straight in on one side and `AddWithResize` called on the other. Both sides of `Add` add the item and
      both sides of `Clear` leave the list empty, so the test between them decides only which of the list's
      own bookkeeping runs.
  71. A value's type came from the shared call it was passed to, so it said `object` where the instantiation
      said something else and the recovered source cast it back and forth on every line. Where the call has
      been given back its real instantiation, the value it was called on is given the same.
  72. A generic type records no size either, so the walk above could only start a type whose base was object.
      A type that is not generic does have its size recorded, so a chain of generic types ending at one can be
      walked from there - which is what a game's own generic classes look like: `BaseSaveManager<T>` under
      `SingletonMonoBehaviour<T>` under `MonoBehaviour`. Every field of those was a read of unmanaged memory.
  73. The same is true of a generic type's statics, whose storage begins at nothing and holds only its own -
      so a singleton's `_instance` was reached at an offset matching nothing, and the property that returns it
      came back with its body commented out.
  74. Aapcs64 does not pass a small struct as a word: where every field of it is a float - and Unity's
      geometry types are nothing else - it travels in one vector register per field, so a `Vector3` arrives in
      s0, s1 and s2 rather than in x1. Counting a whole struct as one general purpose register also put every
      argument after it in the wrong place, which is how a call lost its last argument entirely.
  75. A `const` is written into the code that reads it and has no storage, but it is still recorded as a
      static field of its type with an offset of zero - so it sits in front of the field that really is at
      zero and gets named in its place. `Vector3.zero` came back as `Vector3.kEpsilon`, and with it every
      line that set a transform.
  76. A struct that travels in several registers is only named by the first of them, so what that register
      holds is one field rather than the whole thing. Taking the type from where the value is passed called a
      float a `Vector3`, and a comparison of two of those is not something C# has an operator for. The type is
      taken only from where a value is produced whole - the return of a call - and an instruction that would
      compute with a fragment of a struct is refused rather than written out.
  77. An array's length is a load at a fixed offset in the array header, and nothing about it says which
      member that is - so the condition of every loop over an array was a read of unmanaged memory, and the
      loop went with it. `Length` returns exactly what is there.
  78. Indexing an array is the array plus that header plus the index scaled by the width of an element, all
      of it arithmetic on a pointer. Put back together it is the one CIL instruction it was compiled from,
      which is the only way to say it: the fields of an array header have no names a C# file may use.
  79. Clearing a list of something with no references in it does not zero the array at all, so there is no
      call left to recognise `Clear` by - only the count going to zero and the version going up. Nothing else
      sets a list's count to nothing.
  80. A pointer is not a number. A value used as the base of a memory operand holds an address however it was
      arrived at, and giving it the type of the offset it was added to - which is exactly what indexing an
      array looks like - called the array an `int` and lost every access through it.
  81. What an element turned out to be says what the array is. A value reached through the header offset of an
      array, with an index scaled by the width of that element, is an array of it - there is no other shape
      that reads like this.
  82. A value called `object` is never called anything else afterwards, because a type that is known is left
      alone - and `object` has no fields, so every read through it fails. Where the call that produced it has
      since been given back the instantiation it was written with, what it returns is the better answer.
  83. Casting a reference is not an instruction on this architecture: it is a call into the runtime taking the
      value and the class, reached through a thunk that no method table names - so the call stayed an address
      and every statement built on it went with it. What identifies it is that its second argument is a type
      and its result is used; nothing else is handed a value and a class in that order and gives something
      back. Boxing is the same call with the two the other way round and the type a value type, which nothing
      else is either.
  84. What is put into an element says what an array is just as well as what comes out of one, and where a
      loop only writes there was nothing else to go on.
  85. il2cpp inlines a property accessor of another assembly into its caller, so what is left is a direct
      access to the private field behind it - true, but not something a project can write down:
      `button.m_OnClick.AddListener(...)` does not compile, and the line was lost even though
      `button.onClick` is the same thing and is public. The property is used where it is unambiguous: same
      declaring type, same type as the field, and named the field's name without the `m_` or `_` that marks
      it as backing.
  86. A phi was put wherever a register was written on one path into a block reachable another way, whether or
      not anything could still read it there. Compiled code writes every register it may keep a value in and
      reuses each one the moment the last read of the old value is past, so most of those joins were between a
      value and one already finished with - and merging the two says the register holds either, which loses
      what it holds. A register that held `this` until the loop that reuses it as a counter then read as an
      object of the enclosing class, and every use of the counter with it. The register is now merged only
      where something can still read it, which liveness says: the standard pruned form of the same
      construction. It is the single largest change measured here - commented-out statements in the reference
      files fell by a third, and unmanaged loads with them.
  87. A delegate is allocated, the method it is to call is worked out, and only then is its constructor called,
      which puts a block boundary between the allocation and the construction - and the two were only ever
      fused into one `newobj` when they sat in the same block. Unfused, a lambda reads as an object being
      handed a method pointer, which C# has no syntax for at all and which no decompiler will write back as
      the lambda it was. The constructor is now looked for along the path that had to be taken, stopping at
      anything that reads the object before it. Only where the delegate can stand for the method: the two must
      agree on how many parameters they take, because a decompiler names a lambda's parameters off the
      delegate's own `Invoke`, one per parameter, and stops if it runs out.
  88. A method the decompiler cannot read used to cost the rest of the assembly, since an assembly is
      decompiled as a whole and one type that throws ends the run - in one measured case, nineteen of the
      largest files in the project, none of which had anything wrong with them. The body is the only thing
      actually lost, so it is now the only thing given up: the method is left declared and empty and the run
      is repeated. Recovered bodies make this likelier than it would be for an assembly a compiler wrote,
      since they can hold shapes no compiler emits.
  89. A closure's constructor had its call to `object`'s taken out of it, because for an ordinary type that
      call is written back by the language and keeping it produces a statement C# cannot say. A closure is not
      an ordinary type: it is never written out at all - the whole class is folded away, back into the locals
      the lambda captured - but only after the decompiler has read that constructor and found it does nothing
      except call `object`'s. Without the call it reads as a class that does something, so it stays a class,
      and every captured local stays a field of it. Keeping the call costs nothing, since nothing writes it
      down. Of the display classes in the game's own assembly, ten now survive where thirty-one did.
  90. A compiler merges returns by leaving the value in a register and jumping to one `ret`: each path writes
      its own constant and none of them returns a literal. Reading `yield return` back out of a state machine
      means finding the path that sets the current value and then returns *true*, so a path that returns
      whatever is in a register does not say what it returns and the whole method is written out as the
      machinery instead. Each path now ends in the return, with the literal it already had.
  91. An iterator's state machine and a lambda's closure are classes nothing else can reach, so inside one of
      their own methods there is exactly one of them - `this`. The compiled code does not keep it in one place
      though: it lives in a register that survives a call, is read back after each one, and moves again when
      that register is wanted for something else. A decompiler only puts `yield return` back when the state
      and the current value are read off `this` itself, and one write through a copy costs the whole method.
      Since the type has only one instance there, saying so is not a guess.
  92. Which instantiation a shared body was called as is read off the object it was called on - but until the
      copies out of SSA are gone, that object is a local typed by the very call being asked about, so the
      question had no answer and the call kept the shared form. It is now asked again once the copies have
      settled, and the types worked out again after it: until then the call said it returned `object`, the
      value it produced was typed as that, every field read off it was a read of unmanaged memory rather than
      a field, and an array reached that way was never known to be an array - so its length and its elements
      went the same way, and the loop over them with it. One `list[i]` cost the method it was in.
  93. Bodies are shared between arguments laid out and passed alike. References all are, and `object` stands
      for every one of them, which was the only case recognised. Enums are too, by the integer behind them,
      and there the stand-in is a type the runtime library keeps to itself - `System.Int32Enum` for an enum
      counted in ints, one apiece for the other widths. A call against one reads as a real call against a real
      type and nothing about it looks wrong, so a `HashSet<ECellColor>` simply read as a
      `HashSet<System.Int32Enum>` whose contents, and everything reached through them, were of a type the
      project cannot name.
  94. Reads of runtime structures become the thing they stand for - a static field, an array's length, a
      list's count - and what those reads were reached through is then read by nothing. A class pointer, and
      the static storage taken out of it, have no way of being written down at all, so left behind they were
      placeholders sitting in the middle of statements that had otherwise been recovered. They are now
      collected once the recovery passes have finished, rather than only before they run.
  95. A class does not know where in its method table an interface's methods begin, so the runtime finds out:
      every class carries the interfaces it implements paired with the offset each one starts at, and a call
      on an interface walks that list, then calls through the table at the offset it found plus the method's
      own slot. il2cpp writes that walk out at the call site rather than calling a helper to do it, so one
      interface call became a read of a count, a read of a pointer, a loop that compares and steps, and an
      indirect call through whatever it landed on - none of which names a method. Reading the count alone
      accounted for nearly five hundred unmanaged loads, the most common thing left in the game.
      Everything needed is in the walk: the interface is the constant the loop compares each entry against,
      the slot is the constant added to the offset that was found, and the object is what the class was read
      out of. The call is written as the call it was, and the walk - being the runtime detail it always was -
      is then reached from nowhere and goes.
  96. A class carries a bit saying its static state has been set up, and every method that touches such a
      class opens with a test of that bit and a call to the setup that only runs the first time. The test was
      matched as a mask of one against a field read at the flag's offset, which is what a compiler emits when
      it can reach the byte in a single instruction. This build cannot: the flag sits far enough into the
      class that its address is worked out first and then read from, so the read names no offset at all and
      the mask is of a value rather than of a field. Nothing about the guard was different - only where the
      offset was written - and matched on the shape alone, not one guard in the game was found. The guard is
      now read through the arithmetic in front of it. Beyond the boilerplate it deletes, the call it guards
      hands back the class it was given, so the two paths met in a value that had a type along one and not
      the other - and a class named through a generic body's context lost its type there, which is where an
      interface call in generic code stopped being recognisable.
  97. The slot an interface call uses is a fixed distance into the interface's part of the method table, and
      the table is indexed in entries but addressed in bytes. The slot can therefore be counted before the
      offset the walk found is scaled up, or added as bytes after it - the compiler picks per call site, and
      both mean the same thing. Only the second was read, so a call compiled the other way kept its walk.
      Reading both took interface walks left in the game's own assembly from 366 to 66.
  98. A call through a register says nothing about what the callee takes, so the arguments are whatever the
      convention put in the registers, and the lifter hands those over for a later pass to pick from once it
      has worked out the signature. Only half of them were handed over. Arm64 lays arguments into two
      independent runs - x0 to x7 for integers and pointers, v0 to v7 for floating point - and a parameter's
      register depends on how many parameters *of its own kind* came before it, so `(string, double, string)`
      arrives in x0, v0 and x1. With the floating point run missing, a value passed in it had never been
      mentioned at the call, nothing carried it there, and every pass that recovers such a call had to refuse
      the whole signature rather than hand over a wrong value: an interface method taking a `float`, a virtual
      method taking one, a `Func<float, ...>`. Both runs are now handed over and one description of the
      convention decides which of them each parameter came from, which is also what lets the delegate
      recovery stop resolving arm64 calls against the x64 convention.
  100. Working out which method an interface call goes to means following the copies between a value and where
      it was assigned. Those copies can lead back to where they started: this is out of SSA, and taking a graph
      out of SSA writes a copy on each edge into a join, so a value carried around a loop is copied to the local
      the loop reads and back again. Following them without remembering where one has been does not terminate,
      and the export ends in a stack overflow rather than a bad result. It takes a particular shape to reach - a
      local held across a loop and reused for something an interface call is made on - and nothing in this game
      had it until an unrelated change altered which locals were typed. The walk now remembers where it has been.
  101. Two array-typing changes were measured and reverted rather than kept. Reading an array's type off a field
      operand rather than only off a local changed nothing at all: the shape occurs, but those elements were
      already being typed by another route. Deciding a phi's type by agreement between its inputs, rather than
      by whichever input happened to be typed first, took whole compiling bodies from 2197 to 2196 - the wrong
      type it removes is not where the damage comes from. Both are recorded because both look obviously right.

  102. Every pass that recovers something says a type as a side effect - an array's length, a list's count,
      the method an interface call goes to - and the inference that reads types had already finished before
      any of them spoke. Running it once more afterwards was the single largest change measured here: whole
      compiling bodies went from 2212 to 2251. The length is the case that shows why. Until it is recognised
      it is a read of unmanaged memory, so the index tested against it learns nothing from the test; an
      untyped index is written out as an `object`; the bounds check becomes a comparison between unrelated
      things; and the loop, with everything inside it, is dropped. Nothing about the analysis changed - only
      when it was allowed to run.
  103. A comparison says as much about a type as an assignment does, and nothing was reading it. Two values
      are only ordered against each other when they are the same kind of thing, so the index in `i < a.Length`
      is an integer for the same reason the length is. A loop counter suffers most: it is written by
      arithmetic on itself, which says nothing, and its only other mention is that test. Only numbers are
      carried across - comparing a reference against zero is a null check, not a statement that the two are
      alike.
  104. A type that is not itself generic can still inherit from one, and then its inherited fields sit at
      offsets nothing records, because a generic type's fields are all recorded as zero. Walking that layout
      was tried in an earlier session and refused, having named more fields wrongly than rightly once the
      chain above the base was itself generic. It is taken now because the walk says so itself: it starts at
      the first recorded size above the base and gives back nothing where it cannot reach one. Where a game
      type derives from a generic `ScriptableObject` - which is most of this game's configuration - the chain
      closes exactly, and `GetAbilityById` came back with nothing left in it at all.
  105. il2cpp records a shared body against a stand-in: `System.Object` for a reference, and for an enum one
      of a set named after the integer behind it. Those are real types in il2cpp's metadata but exist in no
      assembly Unity ships, so a file naming one does not compile and every statement mentioning it is
      dropped. The real type is usually right beside it - most often on the very call being made, since the
      call is resolved at the real instantiation while the value handed to it still carries the shared name.
      A stand-in is only ever a stand-in, so anything it stands next to is better information than it is.
  106. Recovering a call out of the walk il2cpp inlined to find it leaves that walk reaching nothing - but a
      walk is a loop, so its two blocks are each other's only predecessor and neither is ever left without
      one. Nothing that counts predecessors will take them, and while they stand the comparison that ends the
      walk is still read by the branch that repeats it, which holds alive the whole runtime chain it was
      reached through: a runtime method, its class, its generic context. Reachability answers what counting
      predecessors cannot.
  107. Writing to `xzr` discards, and reading it gives zero - it is the architecture's zero register. The
      stores did know that; the store-pair did not, and `stp wzr, w9, [x8, #0x18]` is exactly how a list is
      cleared, its count going to nothing and its version up in one instruction. So the count was set to a
      value rather than to zero, `Clear` was not recognised, and what was left were two writes to fields the
      runtime library keeps to itself.

  108. Taking a graph out of SSA writes, on each edge into a join, a copy of what that path had into the local
      the join reads. Compiled code takes a register back the moment the value in it is finished with, so most
      joins are between a value and something unrelated, and the copy claims one is the other - in one method
      here, sixty-three times: a runtime method assigned an integer, a class pointer assigned a struct. Such a
      copy cannot be the value moving. If the join really read what this path put there, under the type the
      other path gave it, the program would be ill-typed; so on this path the local is not read afterwards,
      and writing it is only how the register got reused. Leaving the copy out splits the two uses apart.
      Keeping it cost the declaration - a cast between unrelated types is not something the language will say -
      and with the declaration went every later statement that used it. The two largest files in the game lost
      a third of what could not be written: 281 statements to 185, and 167 to 100.

      Two earlier attempts at the same problem changed the *type* a phi is given rather than the code generated
      for it, and both measured worse. Refusing a type loses it for every use downstream that would have
      propagated correctly; refusing an impossible copy loses nothing, because that copy never carried
      anything.
  109. Preparing a class the first time something touches it is the runtime's business - a static field is
      simply there, as far as the language is concerned - but il2cpp writes the preparation out at every method
      that reaches one. Excising the whole guard does not work on this build, because the preparation sits
      behind a test of its own and the region is not the diamond that shape expects. Only the call does
      anything, and taking just the call leaves the test reading a value nobody wants, which the collection
      afterwards removes along with the class pointer it was read from. It is recognised without a name: it is
      handed a runtime class and hands back nothing that is read, and managed code never passes a runtime
      class to anything.
  110. A type named in a method arrives through a slot filled in when the assembly loads, so the constant is
      the slot's address and the class is what is in it. Only the first step was understood: the local holding
      the slot was typed as the class it would hold, and the read through it came out as a value of no type,
      which left everything reached from it - the static fields most of all - as reads of unnamed memory. The
      second read is what settles it. Static storage sits at one fixed distance from a class and the bit
      saying it is ready at another, and neither offset means anything on anything else; so a value used that
      way is a class, and a value read at offset zero from something already known to be one is the same class
      again. In the two files this was traced through, the offsets it then resolved line up exactly with the
      original's static fields in declaration order.
  111. An offset that matches no field of the type being read may name a member of a struct held in one of its
      fields, because a struct is stored where it lies rather than pointed at. Which field an offset belongs
      to is decided by where the next one starts - a field owns everything up to its successor - which needs
      no type sizes, and sizes are the thing this metadata does not record reliably enough to divide by. Where
      a struct's own members are measured from is not fixed either, since a value type is laid out both boxed
      and bare; both readings are tried and the one landing exactly on a member is the right one. Most of what
      this reaches in practice belongs to the framework and is private, so it cannot be named from recovered
      source however well it is understood.
  112. The two reads of a class that go with preparing it are not translated but removed. The bit saying a type
      is ready is not guessed at: managed code cannot run before the type it is in has been prepared, so every
      test of it that recovery can reach already has its answer, which makes the test a constant and the
      branch that would have prepared the type unreachable. The pointer to the static values is dropped rather
      than answered, because there is nothing to answer with - what reads through it have already been named
      as the fields they are, and name the field directly.
  113. Three shapes of array access were still arriving as arithmetic on a pointer: a subscript the compiler
      folded into the one immediate the load already had, an array read straight out of a field, and a
      subscript held in a field. The last is the one that mattered, and for a structural reason - a compiled
      iterator keeps every one of its variables in the state object it carries, so inside a coroutine the loop
      counter is never a local, and reading only locals missed every array access in every coroutine in the
      game. One file had forty-four of the hundred and twenty such reads, nearly all in its `MoveNext`.

  114. Nine hundred and seven method bodies - about a third of the game - were being discarded before export
      and replaced with a minimal implementation, and every one of them failed the same check: the stack did
      not balance. Nothing said so. A discarded body comes back as `return null;` with no marker and no
      commented statement, so every measure of the export counted it as a method that came back whole, and
      the largest method in the worst-recovered file was one of them. Three faults produced all of it.
      A `ret` was written only where the analysis said the method returns, so a body whose last block falls
      into the exit, or whose only instruction is a jump that could not be decoded, ended without a
      terminator and control ran off the end of it. Whether a call returns anything was taken from how the
      caller behaved - whether the result register was read afterwards - rather than from the callee's
      signature, so a void method's result was stored, popping a value that was never pushed; that alone was
      four hundred and forty-two of them. And a branch whose target could not be worked out was turned into
      nothing at all, which is right for an unconditional one and wrong for a conditional one, whose
      condition was already on the stack and stayed there, so the block after it was reached along one path
      with a value and along another without. Discarded bodies went to none, methods keeping nothing of what
      the binary says they do fell from 150 to 55, and the calls surviving into the exported source rose
      from 69.4% to 74.1%.

Also not taken. `cinc wd, wn, c` is exactly `csinc wd, wn, wn, c` and lifts through the existing conditional-
select case with one line changed, so the earlier session's revert looked like it had been about *how* rather
than *whether*. It was not: retried, the file it appears in most went from 100 statements that could not be
written to 225, and whole compiling bodies across the game fell. The condition comes from whatever comparison
last ran, which is recorded and trusted rather than proven, and a select built on a wrong guess is wrong
everywhere it flows. Untranslated it costs only its own statement.

Not taken as first written, and taken since - see the entry on moves below. Giving a struct its default where a
zero constant is assigned to one is right in itself, but it was reached by passing the destination's type into
the operand loader for *every* move - which made the existing rule for references fire on assignments it had
never seen before, turning integers into nulls. Whole compiling bodies fell from 2252 to 2243. Widening what
feeds a condition is not the same as widening the condition, and the difference is everything that was not
intended.

Also not taken. A delegate is called through the address in its own `invoke_impl`, and that load is only
recognised while it still looks like a load - a read at a fixed offset into an object. By the time the pass
runs it usually does not: the offset has been resolved to the field it names. So the better the rest of the
analysis gets, the fewer delegate calls this recognises. Reading the field form as well does work - three
indirect calls in one file became `Action.Invoke` - but some delegate it then rewrites produces an `Invoke`
whose parameters the decompiler cannot name, and since it names a lambda's parameters off `Invoke` one by one
it throws, which ends the assembly and costs every file after it: 533 exported files became 507. The score
reads this as a collapse in quality when nothing about the recovered code got worse. Landing it needs the
rewrite gated on the emitted call being nameable.

Not taken, and recorded here because it looks like it should be. A statement replaced by a placeholder pushes
the default of the type of the value it replaces, and a register the analysis never managed to type has no such
type - so asking for one throws, and the method loses its whole body over a statement that was already only a
placeholder. Around three hundred methods across the game end that way. Both ways of not throwing were measured
- standing an untyped value in as the native integer it lowers to everywhere else, and storing nothing at all -
and both made the result *worse* by the same amount: whole compiling bodies fell from 2197 to 2149. A method
that fails here is emptied, and an empty body reads as an empty method; the same method rebuilt around a
placeholder reads as a partial one, and what it does to the statements after it costs more than the body is
worth. The crash is a bad way to arrive at it, but the outcome it produces is the better one.

115. A move is loaded knowing where it is about to be stored, when where it is going is a value type. Arm64
      writes a register and nothing about the instruction says how wide the value is or whether it is a number
      at all - only the destination does. An immediate was therefore always loaded as eight bytes, so a struct
      assigned a cleared register came out as `(Dictionary<int, object>.Enumerator)0L`, which is not C#: the
      declaration was commented out and every later statement that used the local went with it. One method lost
      847 of its 849 lines to four such declarations, and got 737 of them back. The same width mismatch was
      keeping iterators from being recognised at all - the state field of a state machine is an `int`, and
      written eight bytes wide the decompiler could not see the machine for what it was and emitted its class
      verbatim; two more coroutines now come back as the `yield` methods they were written as, one of them
      matching the original line for line but for a single indirect call. Only a value-type destination is
      believed. Handing a reference one down as well is what an earlier attempt did, and it lets a zero be read
      as null on assignments where the register in fact held a number: measured again, that alone cost three
      whole compiling bodies, and dropping it gained one. Uncompilable statements across the game fell from 4076
      to 3989 and the commented ones in the measured files from 874 to 839.

116. A float carried through the arithmetic it takes part in. Only integers did, so a value the code got by
      adding two floats had no type, was written out as an `object`, and every operation on it after that was
      not something C# can say - which took the declaration, and with it every statement that used the local.
      All the geometry in the game ends that way. A float is carried through the numeric operations only: a
      logical one on a float register is working on the bits rather than on the value, and saying the result
      is a float would not be true. Nor does a float speak for a result where anything else in the expression
      is known to be something other than one - a struct of floats travels in the registers a float does, so
      dividing a `Vector2` by a `float` is two divisions and arrives as one operation over a float and
      something else. The result is the vector, and calling it a float is worse than leaving it untyped,
      because the cast the decompiler then writes does not compile: five methods lost their whole bodies to
      that before the rule was narrowed. Whole compiling bodies rose from 2221 to 2265.

117. The value a callee writes through an out or ref parameter read back. A by-reference argument is a stack
      slot the caller hands the address of, and taking a slot's address was lifted as a read of the slot - so
      nothing said the call had written anything, the zero the compiler cleared the slot with flowed across
      the call, and the read afterwards folded to that constant. `bool.TryParse(raw, out var b)` came back as
      `result = 0L != 0L`, always false. The address is now a value of its own; it and the slot are folded
      into one variable, and the clearing store is dropped, so nothing is left to propagate across the call
      and what the callee wrote is read from the one local the address was taken of - which is the local the
      generator already passes by reference. 93 discarded out parameters became 58, and whole compiling
      bodies rose from 2265 to 2279.

      Found by running the recovered code. Three of these methods compiled, carried no marker, named every
      call the binary names and still branched - so `cfscore`, `compare2`, the source-free oracle and the
      decision count all rated them whole, and all four were wrong. `scratchpad/difftest.py` compiles the
      original body and the recovered one side by side and throws random inputs at both: of ten methods it
      could run, nine rated full and two behaved the same. That is a small and deliberately unkind sample -
      pure functions are branch and arithmetic heavy - but it is the only measure that can tell a method that
      is right from one that only looks right.

118. The subscript kept where the architecture puts it. `ldr w11, [x10, x9, lsl #2]` is how every loop over
      an array reads its element - the base is the elements, x9 is the index, scaled by the width of one - and
      the lifter kept only the base, ending at a `TODO`. So the operand read element zero, and the recovery
      said so in code that compiles and runs: a method counting how many cells are a colour returned nought for
      every input, one asking whether a board is empty answered about its first square, and neither carried a
      marker to say so. The index register and its extend amount are kept now, and the recovery of an array
      access learns the shape where the subscript is in the addressing mode rather than in a separate addition.

      This is the first change here kept while the count of whole compiling bodies goes *down* - 2279 to 2277,
      and four methods in the measured files lose their `full`. Thirty-four reads that used to compile as
      element zero became placeholders instead. That is the trade being made deliberately: the decisions a
      method still makes rose from 87.7% to 88.8% of the original's, the methods keeping every one of them from
      78% to 80%, and of ten methods run against the original the ones that behave differently fell from six to
      five. Scoring an export against the binary it came from confirms nothing was actually lost - one operation
      gained across two files. A marker is a signpost and a wrong value is not.

119. A branch whose target block had been emptied is followed on rather than thrown away. The graph turns
      every jump's operand into the block it targets and the generator turns it back into an instruction, but
      only where the block still had one - and a block is emptied whenever everything in it turns out to be
      dead, most often a tail that did nothing but load the return value. The block was then dropped and the
      edges re-pointed around it; the jumps into it were not. A branch left holding a block was discarded
      whole, condition and all, which is what a lost `if` looks like in the output: the comparison still
      computed, on a line that reads it into nothing, and the code that depended on it simply gone. Following
      the emptied block's successors to the first one with an instruction took the warning to none and halved
      the discarded comparisons, from 56 to 24. The method this was found on returns the same answer as the
      original again, on every input, having been wrong on 93% of them.

      Worth noting what did not move: scoring the export against the binary it came from reads the same either
      way, because restoring a branch adds no call, no field and no literal. Only the count of decisions a
      method still makes - 88.8% of the original's to 89.4% - and running it against the original say
      anything about it at all.

120. Each register a struct of floats arrives in named as the field it holds. Aapcs64 hands such a struct over
      one field to a register, so a `Vector2` is v0 and v1 and is nowhere as a whole - but only the first
      register was named, and it was named after the whole struct. `a.X - b.X` therefore arrived as `a - b`
      over two structs, which cannot be written down, and the generator refused it rather than say something
      untrue: 156 placeholders across 24 files, in a game whose types are `Vector2`, `Vector3`, `Quaternion`
      and `Color` almost throughout. Naming each register as its field leaves 57. Where the pass runs is the
      whole of the difficulty - placed before the copies of the parameters are propagated away it binds every
      register correctly and changes nothing, because the arithmetic is reading a copy rather than the
      parameter.

      It also brought back a fault fixed three versions earlier, by a door that had not existed then. Letting
      arithmetic take its type from a field, so that `a.X - b.X` is known to be a float, meant the rule that
      stops a float speaking for a result when something else in the expression is not one - which only ever
      looked at locals - no longer blocked on a `Vector2` *field*. `eventData.position / pixelsPerUnit` read
      as a float again and one file lost five whole methods. A guard that exists because of a past failure has
      to be widened whenever what feeds it is.

121. A tail call to the function that follows is a call again. A method's body is the half-open range from its
      own address to one past its last instruction, but the test for "does this branch leave the method" asked
      whether the target was *greater* than the end rather than not less than it. A branch to exactly one past
      the last instruction - the first instruction of the next function, which is where the compiler puts the
      code it outlined out of this one - therefore looked like a branch within the body, no instruction was
      there to receive it, and the whole statement became `Jump target not found`. The conversion into a call
      followed by a return was already written directly below the test; only the comparison was wrong. Those
      markers fell from 113 to 14, one whole marker family disappeared from the measured files, and whole
      compiling bodies there rose from 245 to 262 - the largest single gain in this list, for one character.

      Found by asking which marker family appears in the most methods *with no other marker beside it*: 96 of
      the 106 methods carrying this one carried nothing else, so each was a whole method for the taking, while
      larger families sat on bodies already lost to something else.

122. Bitmasks decoded from the instruction rather than taken as given. Arm64 does not carry a mask literally;
      it encodes a repeating run of ones as three fields, and the disassembler decodes the widest run wrongly -
      `and x9, x9, #0xffffffff`, which is how a 32-bit length becomes a 64-bit loop bound, arrived as
      `and x9, x9, #0`. The loop it guarded was entered never and the method returned its initial value, in
      code that compiles and carries no marker at all. 34 such masks were live, five of them the condition of
      a loop, and seven more arrived as a value that is not an encodable mask. Decoding it here needs the
      opcode field checked first: add and subtract with an immediate reuse the same bits for something else,
      and reading those as a mask replaces the real operand in every loop counter that counts down.

123. A private field the project may not name, said as the property that forwards to it. A getter that does
      nothing but return a field is small enough that il2cpp inlines it, so the native code holds a read of the
      field; recovery writes down what it sees, the compiler answers CS0122, and the statement is commented out
      along with every later one that used the value. It was the largest single cause of a method losing its
      branching - eleven of the twenty-seven measured methods that keep fewer decisions than the original.
      Nothing was lost, only misspelled: the property is in the same file and forwards to exactly that field,
      so its name says the same thing in a way the language allows. It fires only where the compiler found the
      member and refused it, only for a read, and only where the getter is that field and nothing else - read
      off the getter's own syntax, since the field is already identified and a type cannot declare two fields
      of one name. The equality is structural, so there is no way for it to produce a statement that compiles
      and means something different.

      The decisions a method still makes rose from 89.4% of the original's to 92.2%, the methods keeping every
      one of them from 114 to 123 of 141, whole compiling bodies from 2270 to 2294, and bodies that kept
      nothing of what the binary says they do fell from 45 to 39. It is the only change here that moved every
      measure in the same direction.

124. `nop` produces nothing rather than a placeholder. An instruction that does nothing has no managed meaning
      to lose, and it sat in the existing group of system-register accesses and barriers in every respect but
      the case label. Twelve statements across six methods, and in all six it was the only marker they carried.
      Every score read the same afterwards - those six are not among the files measured against the original -
      but a placeholder standing for an instruction that does nothing is simply a false statement, and twelve of
      them are gone.

Also not taken. Rounding toward an infinity is exactly a library floor or ceiling, so `frintm` and `frintp`
route through the same machinery already built for absolute value, square root, minimum and maximum. It works:
54 placeholders became 32 named calls and the untranslated count fell from 79 to 73. It also cost 33 of the
decisions the measured methods still make - 92.2% of the original's down to 89.2%, and one more method stopped
keeping all of them. Whole compiling bodies did not move either way, and neither did the count against the
original, so only the measure of branching saw it at all. That is the third time naming an inlined instruction
has bought calls and paid in branches, after two attempts at `cinc`: the instruction is not only the method, it
is also the thing the next instruction reads, and turning it into a call changes the shape whatever recognised
that read was matching. Before adding a mnemonic, find out which statement consumes its result.

125. The interface a walk searches for, followed one dereference further. A type constant names the slot the
      class is kept in rather than the class, so the value the loop actually compares each table entry against
      is a read through that slot and carries no type of its own - and without the interface named, the walk is
      never recognised as the call it stands for, and the reads it is made of are written out as unmanaged
      memory. Unresolved memory loads fell from 2427 to 2419 and indirect calls from 152 to 149.

      Three walks, where the family is around forty. The rest are not blocked by the comparison at all: for
      `IEnumerable<T>` and its like the interface is an entry in the method's runtime generic context, which
      can only be resolved against an instantiation - and the body being analysed is the open one, shared
      between every instantiation, so there is none. The 274 placeholders that look like an interface-dispatch
      problem are a generic-sharing problem wearing its shape, and the way in is to give a shared body an
      instantiation to reason with, not to change the pass that reads the walk.

126. In a shared generic body, a type parameter stands for itself. Every instantiation of a generic method
      compiles to one body, and what that body cannot name by address - the interface it calls through, the
      class it allocates, the method it invokes - it reads out of a runtime generic context whose entries are
      written in terms of the method's own parameters. Resolving an entry meant substituting an instantiation's
      arguments for those parameters, and the body being analysed is the open one, which has no instantiation:
      every entry resolved to nothing, and the whole chain that led to it - the context, the class, the table -
      was written out as unmanaged memory. But the open body is also the one the recovered source is written
      from, and there the parameter is exactly what the source has to say: an entry written `IEnumerable<!!0>`
      inside `GetRandom<T>` is `IEnumerable<T>`. Unresolved memory loads across the game fell from 2582 to 2427,
      the context reads among them from 168 to 133, and calls through a pointer from 155 to 135.

      It was measured as a loss of seventy-five whole bodies and reverted, and the revert was wrong: the
      baseline it was judged against had been measured several builds earlier, and re-measuring it - twice, and
      once from a clean rebuild - put it ninety lower. The export is not textually deterministic, two runs of
      one source differing in 424 of 533 files in local naming, and the game-wide body count moves with that
      while the count against the original and the count of surviving decisions do not. A baseline is a number
      measured now, from this tree, not one remembered from before.

127. The runtime structures named from the header Unity ships, rather than inferred. `Il2CppClassUsefulOffsets`
      describes an older metadata and only the few fields the analysis had needed; everything else about a
      class read was worked out from how the value was used, which narrows but never settles. Unity ships
      `il2cpp-class-internals.h` with the editor, so `Il2CppClassLayout.cs` computes every field of
      `Il2CppClass` and `MethodInfo` from the struct itself. The five offsets found empirically over earlier
      rounds all agree with it exactly, which is what makes the rest of the table worth trusting - and three of
      them are ones the old table has wrong.

      Two things followed at once. The byte at 0x135 holds `initialized_and_no_error`, so a read of it can be
      answered with the value it always has by then, exactly as the flag already recognised at 0xE4 is;
      unresolved reads fell by eight. And `MethodInfo + 0x10` is `invoker_method` - one of the three pointers a
      body can reach a method's own code through, and the one a fully shared generic is entered by, where only
      the plain pointer at zero was being accepted: fourteen more calls are named, and the whole runtime
      generic context chain that found each of them goes with them. Unresolved reads 2419 to 2405, calls
      through an unnamed pointer 135 to 121.

      What the layout also settled is what *not* to do: `0xFC` is `stack_slot_size`, which an inlined
      allocation genuinely computes with, so the quick answer of replacing these reads with a constant would
      have compiled and been wrong.

128. The `is` that a walk of the class hierarchy was compiled from. Asking whether an object is of a type is
      small enough that il2cpp inlines it rather than calling the runtime, and what it inlines is a lookup in
      the chain of base classes every class carries: is this class deep enough, step along its hierarchy by the
      depth of the type being asked about, and is the class found there that type. Nothing in it names a test,
      so all four reads came out as unmanaged memory - and because the answer feeds a branch, so did everything
      that branch guarded. Named through the layout computed from Unity's own header, the comparison becomes
      the call an un-inlined cast already leaves behind, and the generator writes a real `isinst`:

          UnityEngine.Object obj = (UnityEngine.Object)((o is UnityEngine.Object) ? o : default(UnityEngine.Object));

      Unresolved memory reads fell from 2405 to 2369, and the hierarchy-depth reads with them from 22 to 11.
      The eleven that remain are not sites it missed: they are the `is it deep enough` guard in front of each
      test, which reads the field twice and feeds a branch, so it cannot go dead when the comparison after it
      becomes a call. Three guesses at why "the rest" did not match were all wrong before the lifted code was
      dumped and showed the shape identical and every site already claimed. This
      shape could not have been recognised before the structure was named rather than guessed at: the two
      fields it turns on, `typeHierarchy` and `typeHierarchyDepth`, are among those the older offset table does
      not carry.

  * **1.0.208 - the branchless `&&`, and `fabd`.** `ccmp`/`ccmn`/`fccmp` make their comparison only when an
      earlier one came out a particular way, and assert a flag immediate when it did not - which is exactly
      `a < b && c < d` with no branch in it. What a condition code makes of constant flags is itself constant,
      so the fold is exact: `&&` where the immediate does not satisfy the reader, `||` where it does. There are
      exactly three readers of a held comparison, and missing one produces the unguarded condition silently.
      `fabd` is `Math.Abs(a - b)` in one instruction. 46 markers gone, `cfscore` full 274 -> 275.
  * **1.0.214 - a lane is a register.** Every instruction the disassembler package cannot decode in this game
      is Advanced SIMD, and none of it is code anyone wrote as SIMD: it is scalar arithmetic clang noticed it
      could do two or four at a time. `VectorLanes.cs` lifts each lane as a register of its own - lane zero
      keeps the register's own name, so nothing that already worked changes - and decodes the copy, single-lane
      load/store, three-same and two-register-miscellaneous encodings from the raw instruction word, the
      disassembler having refused all of them. A `str d` over two four-byte lanes becomes the two field writes
      it always was. A word the disassembler cannot place at all arrives with **no address**, so the address is
      carried forward four bytes at a time, and the offset a vector load carries is scaled by the width it
      loads, so that too is read off the word rather than taken as reported.

      Comparisons, narrowing, the across-lane reduction and the lane permutations follow, which together are a
      vectorised `||` of four range checks - `IntersectLineSegments2D` gets all four of its comparisons back.
      They are only taken over lanes known to hold an answer rather than a number, because as numbers none of
      those steps means what it does as an answer.

      Two things this uncovered, both of which were producing wrong values rather than markers. The
      disassembler reports `fabd`'s **second source as its first**, so `fabd s0, s8, s10` lifted as the
      difference between a value and itself - zero, compiling, and wrong in all twenty places it appears; the
      register is now read off the word. And methods are lifted **in parallel**, so one shared lane model was
      being written from several at once: it surfaced as 570 bodies failing to convert from inside
      `Dictionary.TryInsert`, and any measurement taken before it was found is worth nothing.

      Markers the lifter writes, game-wide: **306 -> 172**. Decisions surviving in the measured methods
      92.2% -> **93.2%**, and the methods keeping every decision 123 -> 124. `cfscore` full is flat at 274 and
      `compare2`, the source-free oracle and the execution oracle are all flat. The four whole methods a
      mid-round build showed were measured on the racing build and did not survive fixing it.
  * **1.0.219 - two reverts that were the wrong diagnosis.** `FRINTM`/`FRINTP` and `CINC` had each been
      lifted before, measured worse, and reverted - three reverts between them, all recorded as "naming an
      instruction costs branches". Neither was that.

      **`FRINT` cost 33 decisions, and all 33 were in one method.** `BoardController.BuildInitialBoard`
      collapsed from 33 to 0 while everything else stayed exactly where it was. The cause is
      `Math.Floor(double)`: a conversion lifts as a move, so a value carries the type of what it was converted
      *from*, and putting a correctly typed call into an expression the lifting believes is integral does not
      make it wrong, it makes it uncompilable - and the decompiler then writes the whole body out as comments.
      Single precision has no such conflict. Restricted to it, and to the scalar form (a rounding over a vector
      arrangement rounds every lane, so one call claiming the register would say the low lane is the whole of
      it): **31 markers gone, every scorer unchanged.** The vector form is done properly in `VectorLanes`, one
      call per lane.

      **`CINC` was an operand-count bug.** The alias has two operands where `csinc` has three, so lifting it
      through that case read a third that was not there, and it adjusts the arm the condition *takes* rather
      than the one it does not. Both are wrong values, and a wrong value costs the method rather than the
      statement - which is exactly the 100 -> 225 unwritable statements the revert recorded. Given its own case:
      `cfscore` full 275 -> **278**, statements written out as comments 688 -> **667**, decisions 93.2% ->
      **93.4%**, methods keeping every decision 124 -> **125**.

      With `adr`, `csinv` and `csneg` in the same round, markers the lifter writes fell **172 -> 118** and the
      files with nothing left to fix went 52 -> **53**.

      **A revert is a measurement, not a diagnosis.** Both of these were reverted on a number without anyone
      finding out which method the number came from. `decisions.py` names them; one `diff` of its output
      against the previous build was the whole investigation.
  * **1.0.222 - a conversion is not a move.** ISIL carries no width, so `scvtf`, `fcvt`, `fcvtzs`, `sxtb` and
      the rest were all lifted as plain moves. That is right about where the value goes and wrong about what
      it is: a move says the two sides are the same thing, and the whole point of a conversion is that they
      are not. So a value carried the type of what it was converted *from*, and three defects followed - a
      float divided by an int count written as an int division, `Expected F4, but got I4` wherever the two
      disagreed, and any correctly typed call put into such an expression making the body uncompilable.

      `ISIL/ConversionTarget.cs` carries the type the conversion produces as a third operand of the move.
      **Marked rather than given an opcode of its own**: an opcode would be one line in the enum and fifty
      places that match `OpCode.Move` silently not matching any more, while an extra operand is ignored by
      everything that reads a move's destination and source. Four places ask: the type propagation pins the
      destination instead of copying across it, the two simplifiers refuse to forward it, and the generator now
      emits the `conv.*` the instruction actually performs instead of reinterpreting the bits.

      `Expected F4, but got I4` **24 -> 1**. Statements written out as comments 667 -> **660**. It also
      unblocked the double-precision `frintm`/`frintp`, which had been refused for exactly this reason and now
      cost nothing: markers **118 -> 89**, `compare2` full 2222 -> **2229**, decisions steady at 93.4%.
  * **1.0.223 - the float constants nothing had typed.** A floating point constant does not survive
      compilation as one: it is a word materialised into a general register and moved into a vector one, so
      recovery gets an integer. Where it goes straight into a float field or argument the type it is going to
      says so; where it is computed with first, nothing did, and it came out as its own bit pattern - `360f`
      as `1135869952`, `100f` as `1120403456`, `1000f` as `1148846080`. Each compiles, so nothing complains,
      and each is a wrong number.

      **The evidence has to be a type, not the value.** `1431655766` is `0x55555556`, the multiplier a
      division by three compiles to, and as a float it is a perfectly ordinary-looking 1.5e13. So a constant is
      reinterpreted only where something *else* in the same expression is known to be floating point - a typed
      local, a typed field, the destination, or a constant already recovered as one. The existing rule that a
      subnormal decode is a mislabelled integer covers the other direction, keeping a small count like `4` as
      itself inside an expression that really is float.

      This only became possible once a conversion stopped being an ordinary move (1.0.222): until arithmetic
      over a converted value was typed at all, there was rarely anything in these expressions to take the
      evidence from. Every scorer unchanged; the wrong constants are gone.
  * **1.0.226 - two fields written by one store, and the constant that hid it.** `Spinner()` sets an `int` and
      the `float` four bytes after it, and the compiler does both in one eight byte store. Field recovery
      resolved the offset to the first of the two and assigned it the whole sixty-four bit value, so the `int`
      got `1061997773` - which is the *other* field's `0.8f` read as an integer - and the field really being
      set did not appear at all. It compiles, and the decompiler says only `Expected I4, but got I8`.

      Three things had to be right for that to come apart. **`movk`'s immediate was in the wrong place**: the
      disassembler shifts it with a *32 bit* shift count, so `lsl #32` wrapped to zero and `lsl #48` to
      sixteen, and the fork had been inferring the position back from the value - right for the low half and
      wrong for the rest. **The chain was never folded in ISIL**, only by the decompiler at the very end, so no
      pass could read the number; `ConstantFolding.cs` folds it, carrying constants along the block itself
      rather than depending on which propagation runs next. Then `WideFieldStore.cs` splits the store, and the
      float half is recovered as a float by the pass that already knew how.

      `Spinner()` now recovers `FrameCount = 12; SpinDuration = 0.8f;`. `cfscore` full 278 -> **279**, partial
      130 -> 129, decisions steady at 93.4%, 0 errors.

      Measured against **1.0.203, the last committed build**, re-exported the same day: whole methods 274 ->
      **279**, decisions 92.2% -> **93.4%**, methods keeping every decision 123 -> **125**, files with nothing
      left 52 -> **53**, markers the lifter writes **299 -> 89**, statements commented out 690 -> **661**,
      `Expected F4, but got I4` 21 -> **1**, `compare2` whole bodies 2219 -> **2228**.
  * **1.0.227 - the commonest missing instruction was not a vector one.** Instrumenting the lane model to
      report *why* it refuses, rather than guessing, put `fcvt s0, d0` at the top by a wide margin: **387 of
      them**, 24 in the game's own assembly. The switch has handled `FCVT` all along and never saw one, because
      the disassembler reports it as `INVALID` and the mnemonic never arrives. `UndecodedScalar.cs` decodes the
      one-source floating point space from the word - the precision changes, `fmov`, `fneg`, and the `fabs`,
      `fsqrt`, `frintm`, `frintp` that are library methods - and a precision change is lifted as a *conversion*,
      so it carries the width it produces instead of quietly reinterpreting.

      Markers the lifter writes **89 -> 69**, everything else steady, 0 errors.
  * **1.0.230 - what the architecture already zeroed.** With the disassembler's own gaps closed, the lane
      model's remaining refusals were measured rather than guessed: a probe mode records every word it would
      not take together with what it knew about the registers at the time. Two causes came out of it.

      **A vector immediate establishes lanes, and nothing said so.** `movi v0.2s, #0x3f, lsl #24` is
      `[0.5f, 0.5f]`; the disassembler decodes it, so it never appeared among the refusals, and the ordinary
      lifting takes it for one value - which left the model no choice but to forget the register. Almost every
      chain ending in a refused `fmul` begins at one of these.

      **A register is very often not unknown above what last wrote it: it is zero.** Writing `s2` zeroes the
      twelve bytes above it, writing `d2` the eight above, and an instruction over a 64 bit arrangement does
      the same to the top half. A pair computed in `.2s` and then compared in `.4s` - which is what a
      vectorised pair of range checks is - reads two lanes that were computed and two that are zero, and
      without the second pair the comparison cannot be emitted at all. One watermark per register replaces the
      ad-hoc tracking, and one gate makes a known-zero lane real where something asks for it.

      The watermark has to be earned. Claiming it after an instruction the model only *partly* followed says
      lanes that hold a result are zero - which cleared nineteen more markers and put seven statements into
      comments, because the next instruction along believed it. Recorded only where every lane was followed.

      Markers the lifter writes **69 -> 60**, statements commented out 661 -> **658**, everything else steady,
      0 errors.
  * **1.0.231 - a `Vector2` spilled to the stack.** The lane model read a whole-register load only in its
      *scaled* form. A `Vector2` written to the stack and read back uses the unscaled one - `ldur d9, [sp,
      #0x14]` - and everything downstream of it was refused, because the register it loaded was forgotten at
      the first instruction. Both forms are decoded now, and the offset is read off the word either way: one is
      in units of the width it loads and the other is in bytes and signed.

      A lane of a spilled vector also has to be *named* the way the rest of the lifting names one. Register 31
      as a base is the stack pointer, and a stack slot is modelled as a variable rather than as an address -
      emitting a memory operand for it instead would have been a load from somewhere nothing can say anything
      about, which is the very thing that loses the statement.

      Whole methods **279 -> 284**, partial 129 -> 124, markers the lifter writes 60 -> **36**, unresolved
      memory reads 280 -> **270**, statements commented out 658 -> **651**, decisions steady at 93.4%,
      0 errors. Every measure moved the right way.
  * **1.0.233 - the integer half, and what a call really does.** The three-register integer instructions -
      arithmetic, the comparisons, and the bitwise family - complete the lane model's vocabulary. The bitwise
      ones are written over byte arrangements whatever they are really operating on, so the width has to come
      from what the model already knows the register holds; and `bsl`, `bit` and `bif` are a *select*, one
      register choosing lane by lane between two others, which is only a select if the register choosing holds
      answers rather than numbers.

      Two things about forgetting, both found by reading the trace rather than the code. **A call clobbers the
      first eight vector registers and preserves the rest** - which is exactly why an accumulator lives in one -
      and the model knew neither half. And the conservative rule added earlier, that a word whose register
      cannot be named has its low five bits forgotten, was firing on *every branch*: a register chosen out of a
      branch offset, forgotten on each `bl`, `b` and `cbz` in the method. Narrowed to the sentinels it was
      written for.

      Markers **36 -> 32**, `notimpl` inside the measured files 24 -> **19**, decisions 93.4% -> **93.5%**,
      whole methods steady at 284, 0 errors.
  * **1.0.234 - six thousand memory offsets that were sixteen times too small.** The immediate of a load or
      store is in units of the width it accesses, and the disassembler multiplies it back out for a byte, a
      half, a word, a double word and a `d` register. **For a 128 bit one it does not.** Counted over the whole
      binary: of the 6212 `ldr q`/`str q` instructions carrying a non-zero immediate, **every single one** was
      reported sixteen times too small, and none was reported correctly. The pair forms are all right (35417 of
      them), so the bug is exactly one addressing form.

      What it looked like in the output was a field read at an offset belonging to no field. The static
      constructor of `CFramework.ColorExtension` fills an array from its 140 static `Color` fields, sixteen
      bytes apart, and read every one of them at an offset of one, two, three - inside the first field rather
      than at the next. Those 107 reads now name their fields.

      Unresolved memory reads game-wide **2400 -> 2291**; `compare2` full 2235 -> 2236; `cfscore` and decisions
      unchanged, because the type it shows up in most is not one of the 96 measured. The source-free oracle's
      field count moved 74.7% -> 74.4%, which is the one number that went the wrong way - kept anyway, because
      the fix is not a judgement call: it was checked against the instruction word for every such instruction
      in the game.
  * **1.0.235 - the index scale on an extended register.** An index held in a register is scaled by the width
      the instruction accesses, and one bit decides whether it is. The disassembler drops that bit on the
      extended forms: `ldr x0, [x8, w1, uxtw #3]` arrives saying the index is scaled by one. Counted over the
      binary, wrong **1295** times and right 4566 - and confirmed against the toolchain's own disassembler,
      which prints the shift the other does not report.

      What it costs is not a placeholder. An index scaled by one where the elements are eight bytes apart is a
      *different element*, quietly. `TrainEngine::OnJunction` now reads
      `[Connection[] + 0x20 + index * 8]` where it read `index * 1` before.

      No counter moved: the instructions carrying it are mostly outside the 96 measured files, and a wrong
      element is exactly the kind of defect no counter sees. Kept for the same reason the 128 bit offset was -
      it was checked against the instruction word for every such instruction in the game.
  * **1.0.237 - the float constants that were never floats.** `fmov s1, #-1.0` does not carry the bits of the
      float; it carries **eight bits** that expand into one - a sign, an exponent built out of a single bit,
      six of mantissa. The disassembler hands those eight bits over as though they were the value, so `#-1.0`
      arrives as `-1` and `#1.0` as `0`. **644 of them in the game's own assembly**, every one a constant the
      recovered source states wrongly - and it compiles, because a number is a number. Worse, the integer then
      reached the float-literal pass, which reinterpreted *its* bits: `-1` became a NaN.

      Found by a differential test rather than by reading code. `probe ... alltext` dumps what the disassembler
      makes of every instruction in the game's own assembly; `scratchpad/disdiff.py` compares that against the
      toolchain's own disassembler **by the numbers each reports**, since the two write the same instruction
      differently but cannot disagree about an immediate. Nearly everything it flags is an alias (`movz`/`mov`,
      `ands`/`tst`, `ubfm`/`lsr`) or a convention (`adrp` relative against absolute); what is left is the bugs.

      Both forms are decoded, the scalar one and the vector one, which encode their eight bits in different
      places. Markers inside the measured files 19 -> **15**; the rest steady. The 644 corrected constants
      appear in no counter at all, which is the point of the ones that follow.
  * **1.0.238 - the page address that was a distance.** `adrp` names a page, and the disassembler reports the
      *distance* to it rather than the page itself. The fold that turns `adrp`+`ldr` into an absolute address
      had always corrected for that, so literal recovery worked - but the move that puts the value in the
      register did not, so wherever the fold did not apply the register held a number that is not an address,
      and negative wherever the page lies behind the instruction. Nine of them were written into the output as
      things like `_ = -23261184L`. Now the register holds the page.

      No counter moved; the reads through such a register still need the address interpreted, which is a
      different problem. Kept because a register holding a signed distance labelled as an address is simply
      wrong.

      Also measured and **not** done: 580 calls in the game resolve to no managed method, and 224 of them are
      calls into the C library through the procedure linkage table - `memcpy` 80, `memset` 42, and 79 that are
      `Mathf`/`Math` methods compiled to a real call rather than inlined (`powf`, `atan2f`, `sinf`, `modf`).
      Naming them means resolving a linkage-table slot through its relocation to a symbol, which LibCpp2IL does
      not expose. **Inside the 96 measured files it is 11 calls, 6 of them cleanly nameable** - too little to
      justify the ELF work. The rest of the 580 are thunks and runtime helpers, and following a thunk was
      measured and reverted before.
  * **1.0.244 - four attempts at the untyped bases, all reverted.** A read through a base with no type cannot
      resolve to a field and there are 2508 of them, which looks like the last large tractable family. Grouping
      them by *what defined the base* says otherwise: 739 are the stack pointer, 377 are calls whose target is
      not a managed method, and the remaining 1158 are cascades of those. Naming a stacked lane as a stack slot
      cost eleven decisions; not splitting stack stores cost exactly the same; narrowing a 64 bit value into a
      32 bit place did nothing when the source was typed and cost three whole methods when it was not. All four
      reverted, and the tree measures exactly as it did before them.

      Two avenues closed with it. **Thunk following** - of the 377 unresolved call targets only 13 are a single
      unconditional branch and *none* lands on a managed method, which is why it was catastrophic when it was
      tried before. And the **`Expected I4, but got I8` comments are not blockers**: 646 of them coexist with
      methods that count as whole, being diagnostics ILSpy writes beside code it emitted anyway.

      Recorded rather than retried. The roots are internal il2cpp helpers whose addresses lie past the last
      export, so nothing in the binary names them.
  * **1.0.245 - the load that moves its base afterwards.** Post-indexed addressing reads at the base and
      *then* advances it, which is what a loop over an array becomes once the index has been turned into a
      walking pointer: `ldr w11, [x8], #4`. The pre-indexed form was handled; this one was taken for a plain
      offset, so the access read `[x8 + 4]` - the element after the one meant - and the pointer never moved.
      `Corpus.CountOf` counts the cells equal to a colour and came back reading `cells[1]` every time round,
      returning six where the answer was one.

      The disassembler reports the offset but nothing that separates the two forms, so the two bits that do are
      read off the instruction. Both the load and the store side.

      In the game's own assembly it is 29 sites - the other 3467 post-indexed accesses are frame pops - and
      every game measure is unchanged. The corpus method now walks the pointer faithfully and fails visibly
      instead of returning a wrong number quietly, which is the trade this project takes. What it needs to
      pass is a pass that turns a walking pointer back into an indexed access; that does not exist yet.
  * **1.0.249 - the answers a switch was compiled into a table of.** A `switch` whose cases are dense and
      whose arms are constants does not become branches: the compiler puts the answers in the binary's own data
      and indexes them. That read resolves to nothing, so the method answered with its default for every input
      - `Corpus.Weight` returned zero for all five colours. Everything needed is present by the time the
      constant folding has run: the table address is a constant, the count is in the comparison that guards the
      read, and the width is the scale of the index.

      The arms become a chain of choices, since ISIL has no switch. **The fallback is nothing rather than the
      last arm**, because the guard is an *unsigned* comparison and ISIL lifts it as a signed one - so the
      block is entered for a negative index the original excluded, and answering with the last arm would be
      answering confidently with the wrong case.

      Corpus **15 -> 16 of 25** methods behaving identically. In this game it claims one read of 43: its
      indexed reads are mostly array accesses whose header offset has been folded into the base, which is a
      different problem. Every game measure unchanged, 0 errors.
  * **1.0.251 - the array header folded into the subscript.** An element is at `array + 0x20 + i * stride`,
      and the pass knew two ways the compiler writes that. There is a third: put the header *in the index*.
      `mov w22, #4` and `ldr x20, [x8, x22, lsl #3]` reads `array + 4*8`, which is element zero - and the
      element actually wanted is four less, a difference the compiler keeps in a register of its own because it
      is what the bounds check compares against. So the subscript is already a local and nothing has to be
      computed; it only had to be recognised.

      Whole methods **284 -> 288**, partial 124 -> 120, unresolved memory reads **273 -> 242**, statements
      commented out 663 -> 657, decisions steady at 93.5%, 0 errors. The best single change in this stretch,
      and it was found by reading the disassembly of one unresolved read rather than by reasoning about the
      pass.
  * **1.0.252-255 - the field initialisers that come out of the binary as one block.** Four `float` fields with
      four different defaults are not four instructions. The compiler puts the sixteen bytes in the constant
      pool and moves them in one go - `ldr q0, [x8, #0x930]` then `stur q0, [x19, #0x48]` - and the read, being
      from the binary rather than from anything managed, resolved to nothing. The store then resolved to the
      *first* of the four fields and assigned it that nothing, so `CF.BoardController` reported
      `HammerWindUpDuration = 0f` where the source says `0.2f`. A value, not a failure, which is why no counter
      had ever shown it.

      Three things were wrong at once. The **address** was sixteen times too small: the ADRP fold computes it
      from the raw immediate and never passes through the one place a 128 bit offset is scaled, so 144 of the
      224 such reads were not even at a sixteen byte boundary and none could have been right. The **width** was
      lost, ISIL having no way to say how many bytes a move moved, so it is now taken where the address becomes
      a constant and both are known. And where eight bytes came back in a `d` register the lifter called them a
      `double`, because at that point it knows the register and not the destination - `HammerShakeAngle`, whose
      value is `4`, was recovered as `3.1664965E-10`, which is `4f` and the `0.12f` beside it read together.
      That last one needs no address at all: **a four byte field cannot be assigned an eight byte number**, so
      the constant is its own evidence, the same reasoning `WideFieldStore` uses one width down.

      Answered only where the data cannot change - `.rodata`, never `.data`, and not `.data.rel.ro` either,
      whose bytes in the file are relocations rather than values - and only where the fields tile the load
      exactly. **17 float fields gained a correct default** that had none, 6 of the 8 garbage constants went,
      unresolved reads game-wide 2260 -> 2253, `compare2` **2236 -> 2238**. Whole methods, decisions and the
      corpus all held exactly, which is the expected shape: this fixes what methods *say*, not whether they
      compile, and only the enumeration can show it.
  * **1.0.256-257 - and the same sixteen bytes when they are a struct.** A `Color` is four floats, which is the
      same blob and the same one instruction. `CFramework.ColorExtension` declares a hundred and forty of them,
      the compiler folded every `new Color32(240, 248, 255, 255)` into the four floats it comes out as, and what
      was recovered said `AliceBlue = (Color)0`.

      A struct cannot simply be assigned bytes - ISIL has no way to say "these bytes are that value". What it
      has is an allocation followed by a constructor call, which the IL generator fuses back into one `newobj`,
      so the blob is given back as **the constructor the source must have started from**: `AliceBlue = new
      Color(0.9411765f, 0.972549f, 1f, 1f)`, which is `Color32(240, 248, 255, 255)` exactly. Only where the
      struct is entirely `float`, its fields tile the load from zero with no padding, and it declares one
      `float` per field - anything else keeps the placeholder, because a blob spread over the wrong arguments
      is a wrong colour stated confidently.

      Unresolved reads game-wide **2253 -> 2110**, of which the reads with no base at all went **223 -> 80**;
      **156 struct constructions** recovered. Spot-checked against the original: `Chocolate` 210/255 ->
      `0.8235294f`, `DarkOrchid` 153/255 -> `0.6f`, `Tomato` 99/255 -> `33f/85f`. Whole methods, `compare2`,
      decisions and the corpus all held - `ColorExtension` is not one of the 96, which is how a
      hundred-and-forty-read family stays invisible to the primary scorer.
  * **1.0.258 - a left shift lifted as a right shift.** `UBFM` is the encoding behind `lsr`, `lsl`, `ubfx` and
      `ubfiz`, and it means two different things: an extract when `imms >= immr`, an *insert* when it is not.
      Only the extract was translated, and with the wrong number of bits. So `h * 31`, which the compiler
      writes as `(h << 5) - h`, came back as `((h >> 27) & 0x3FFFFFF) - h` - which compiles, runs, and answers
      something else entirely.

      **38 of the 112 `UBFM` in this game's assembly are the left shift**, and the mask is too wide on most of
      the rest; where `imms` reaches 31 it was computed by shifting a 32 bit `1` that far, which is not the
      number intended either. It survived because the disassembler resolves the `lsl` alias itself 661 times
      against these 112 - the common case was right, and the rare one looked like ordinary noise. The mask is
      now dropped wherever it cannot change the answer, so an ordinary shift comes out as one operation
      instead of an expression: left shifts recovered 120 -> **137**, wrong extract-and-mask shapes 2 -> **0**.

  * **1.0.259 - say `Length`, not `_stringLength`.** The pass that names a private field by its property knew
      one convention - Unity's, where `m_OnClick` is `onClick`. The class libraries use the other: the property
      capitalises, and it drops the word the field carried to say what it was *inside* the type.
      `System.String._stringLength` is `Length`, and `get_length` matches nothing, so every string length in
      the game stayed an unwritable field and commented out the statement that used it - and, in a loop, every
      statement after it.

      Each casing and each camelCase word-suffix is now tried, still requiring the property's type to match
      the field's, which is what stops a shorter name matching something unrelated. `_stringLength` **36 ->
      0**, inaccessible fields game-wide 273 -> **237**, and on the ground-truth corpus - where every method
      has known source and is *run* against the original - **16 of 25 correct -> 17**. `Hash` was the one that
      changed, and it needed both of this pair: the shift to compute `h * 31`, and the property to be allowed
      to say so.
  * **1.0.260 - the out parameter read straight into the answer.** Coming out of single assignment form, a copy
      is written on each edge into a join. The fork refuses copies where the two ends cannot be the same value -
      a register being reused rather than a value moving - and judges that by type. But the local standing for
      an out parameter is typed as the *address* it was handed over as, `System.Int32&`, while the join is typed
      as what the callee wrote there. Different names, one of them a value type, so the copy was refused and the
      edge carried nothing.

      Where that edge is the one the answer comes back on, the answer is simply gone. `ParseOrDefault` returned
      its fallback for input it had just successfully parsed - `int.TryParse(raw, out var _)`, the value thrown
      away - and there is no marker for it, because the code that remains is well formed and compiles.

      Two types that differ by nothing but the `&` are the value moving. `compare2` **2238 -> 2253**, decisions
      93.5% -> **93.6%**, and on the corpus **17 of 25 -> 18**. This and 1.0.258-259 together took the corpus
      from 16 to 18: three separate faults, none of which any marker counted, all found by asking why a method
      that compiled gave a different answer than the source it came from.
  * **1.0.280 - the answer depended on who asked first.** Whether an address is a throw helper is worked out
      once and shared across the whole application. To stop a cycle, a `null` went into that cache *before* the
      search ran - and nothing told that placeholder, or a search that stopped at the depth limit, apart from a
      settled "this is not a throw helper". So whichever call site reached an address first, with whatever
      depth it had left, fixed the answer for every other one.

      It surfaced as two hosts disagreeing about the same binary: analysing a single method, an address
      resolved to nothing; analysing the whole assembly it resolved to `OutOfMemoryException` - **9 names found
      against 25**. Every call that failed to resolve stayed a call to a bare address, and a later pass, seeing
      an unresolved call carrying a type argument, read those as casts - **`isinst` that were really `throw`**.

      A name is a name however it was reached; a nothing is only kept when the search that produced it ran to
      the end. Whole methods **288 -> 289**, statements commented out **656 -> 600**, unresolved memory reads
      **242 -> 219**, mangled names 72 -> 69, unresolved call targets 52 -> 46, indirect calls 9 -> 6,
      `compare2` **2253 -> 2259**. Every marker moved the right way at once, which is the shape of a defect that
      was quietly costing a little everywhere rather than a lot in one place.

      The source-free oracle reads lower (calls kept 78.0% -> 77.4%) and that is the transformation working:
      a call that becomes a `throw` is one fewer call for it to find. Decisions 93.6% -> 93.5%, for the same
      reason - a block that throws has no branch to keep.
  * **1.0.284 - one field of one element of an array of structs.** A `Vector3` is twelve bytes, which is the
      one width that cannot be a shift, so an index into an array of them is a multiply the compiler folds away
      wherever the subscript is constant. What is left is a single offset: `cornersBuf[2].y` is thirty-one bytes
      past the header and nothing about that looks like an array access at all.

      Two things were missing. **A struct had no width** - the table knew the primitives and enums and answered
      `null` for everything else, so an array of any other struct could not be reasoned about at all; the
      runtime records what an instance takes up boxed, so the object header comes off and `UnityEngine.Vector3`
      is `0x1C - 0x10 = 12`. And **a field of an element was not expressible**: the fold gives an offset that is
      not a whole number of elements, which is one element plus one field, and reaching it is `ldelema` then
      `ldfld` - the same two steps the compiler folded into one.

      Unresolved memory reads in the measured files **219 -> 204**, game-wide **1835 -> 1810**. Whole methods,
      `compare2`, decisions and the corpus all held. The recovered accesses are the `Vector3[]` corner buffers
      Unity's `GetWorldCorners` fills - `cornersBuf[2].y`, `overlayCorners2[2].y` - which is what the source
      must have said.
  * **1.0.285 - the class a slot points at.** A type constant names the **slot** the runtime keeps a class in,
      not the class, so a body that wants the class reads through it once. That read produced a local with no
      type of its own - or worse, one inherited from whatever the register went on to hold - and everything past
      it went the same way: the class-initialised flag, the static field storage at `0xB8`, the runtime generic
      context at `0xC0` are read at offsets that mean nothing unless what they are read through is known to be
      a class.

      Seeded deliberately *before* the allocation results, so where a register holds a class only until an
      allocation puts a new object in it, the object still wins - it is the later of the two and what the rest
      of the body works on.

      Unresolved memory reads in the measured files **204 -> 160**, game-wide **1810 -> 1766**; unresolved call
      targets 2 -> **0**. Whole methods, `compare2`, decisions, the source-free oracle and the corpus all held
      exactly. Statements commented out 600 -> 605, which is the trade: a local that used to be claimed as a
      `List<CatData>` through a bogus cast is now honestly an `IntPtr`, and one or two statements that leaned on
      the wrong claim go with it.
  * **1.0.286-288 - the loop that kept no subscript.** A loop over an array does not have to keep one: the
      compiler can start a pointer at the first element and step it by the width of one, counting down
      separately. `Add p, this._boardData, 32` is `&array[0]`, and `Add p2, p, 8` is the next.

      Two things go with the subscript, and fixing only the first is worth nothing. The element read through the
      pointer has **no type**, so the fields read out of it resolve to nothing - sixteen unresolved reads in
      `BoardController.HasAnyActiveCell` alone. And the walk itself **cannot be written down**: `_boardData +
      32L` is not something C# will say, so even once the fields are known the statements holding them are
      commented out anyway. Typing alone measured unresolved reads -4 and `commented` **+3**.

      So the subscript is counted back - nought where the walk starts, one more wherever it steps - and put into
      the read, which becomes an ordinary indexed access. Then the arithmetic goes, which it will not do on its
      own: the walk is a *cycle*, the step written from the pointer and the pointer written back from the step,
      so every part of it is read by another part and dead-code elimination keeps all of it.

      Whole methods **289 -> 291**, partial 119 -> 117, statements commented out **605 -> 574**, unresolved
      reads 160 -> 156, `compare2` **2259 -> 2263**, decisions 93.5% -> **93.6%**. The source-free oracle is
      byte-identical and the corpus held. `HasAnyActiveCell` comes back with **no marker at all**, reading like
      the `foreach` it was.

      The earlier note that this game has "only 29 pointer walks" counted only *post-indexed* ones; this shape
      steps with an ordinary `Add` and was never in that number.
  * **1.0.289-290 - the same walk, stepped and indexed.** The other half of the pointer walk turned out to be
      already lifted: `ldr w, [p], #4` becomes an explicit `Add p2, p, 4`, which the pass from 1.0.288 saw. What
      it got wrong was **order**. The step writes a *new* pointer while the read goes through the *old* one, and
      the subscript was one mutable counter shared by the whole walk - so it was stepped before the read that
      should have seen it unstepped. `CountOf` came back reading `cells[1]` first and running off the end: a
      wrong answer, shipped in 1.0.288 and caught only by running the recovered code.

      Each pointer now carries its own subscript, mirroring the pointer's own dataflow - a copy copies it, a
      step adds one - which is right whatever the order.

      Then two shapes a jagged array needs. A walk can **start from an array element**, not only from a local:
      the inner loop of `rows[r]` begins at `rows[r] + 0x20`, and what it walks is one step further in than the
      outer array. And a walker can be **read with an index** rather than stepped, because the pointer it
      indexes from is set afresh for every row - so the element is the pointer's own subscript plus that index.

      On the ground-truth corpus, where recovered code is *run* against the original: **18 of 25 correct -> 20**
      (`CountOf`, then `SumJagged`). Whole methods 291, unresolved reads 156, `compare2` 2263 and decisions
      93.6% all held exactly, with `commented` 574 -> 576 for the extra subscripts.
  * **1.0.291-294 - a struct where a number belongs.** A struct in a field lies where it is rather than being
      pointed at, so reading one of its members is a read at a fixed distance from the outer field. Where that
      distance is not nought the offset matches nothing at the outer level and the resolver walks in to find it
      - which is how `n.A.Y` comes back right. Where it *is* nought the offset matches the outer field exactly,
      the resolver stops, and `n.A.X` comes back as `n.A`:

          Add v9, n.A (Pair), n.A.Y (System.Single)     ; a Pair plus a float

      Arithmetic is done on numbers, so an operand that resolved to a struct is a read of what lies at the front
      of it. Two things had to be got right. **Where** - the operands are still plain locals at field-resolution
      time, and only become `n.A` once the pass that names a struct-of-floats' registers has run, so this has to
      follow that. And **how narrowly**: `Add` is also how an address is worked out, and a struct is an ordinary
      thing to compute one from. Applied to every addition it cost **three whole methods** in the game to gain
      one in the corpus; narrowed to an operand sitting beside a plain number, and iterated - because an
      addition of two floats gives a float, and that is what tells the *next* addition it is arithmetic at all -
      it gains both.

      On the ground-truth corpus **20 of 25 correct -> 21** (`NestedSum`). Whole methods 291 and unresolved reads
      156 held, statements commented out **576 -> 572**, `compare2` **2263 -> 2264**, the source-free oracle's
      whole bodies 1193 -> **1194**, decisions 93.6%. Every measure improved or held.

      Of the corpus's remaining four, `Distance` and `Divide` are recorded as not defects; only `Diagonal`
      (rank-2 arrays) and `SumSteps` (an iterator chain) are real.
  * **1.0.295-296 - two dimensions, one distance.** An array of two dimensions is stored flat, so `grid[i, j]`
      is a single distance into it, worked out from the lengths the array carries: a bounds pointer at `0x10`,
      and a length per dimension `0x10` apart within that. By the time the fork sees the read it is already
      shaped like an array access - base, header, index, scale - and only the *rank* stops it being written,
      because `ldelem` takes one index and this one is flattened.

      The multiply says which index is which: the one multiplied by a dimension's length is the outer, the one
      added to it the inner. Recognising that length **by where it was read from** - through the array's own
      bounds pointer - is what makes it safe rather than a guess about any multiply that happens to be nearby.
      An array of more than one dimension is indexed through methods rather than an instruction, and Cpp2IL does
      not model those, so the reference is built from the array's own signature - which is what the language
      does too.

      Then the bounds reads themselves, as `Array.GetLength`. Without them the loop bound was zero, so
      `Diagonal` never entered its loop and threw instead of answering - the access having been recovered
      perfectly and the method still being wrong.

      On the ground-truth corpus **21 of 25 correct -> 22**. The game is **byte-identical on every measure**,
      which is the expected result and was known before the work started: Assembly-CSharp has *no* arrays of
      rank greater than one, and across every assembly there are only ten, all in engine code or generic stubs.
      This buys a corpus method and a stronger oracle, not a game method.

      Of the three corpus failures left, `Distance` and `Divide` are recorded as not defects. **`SumSteps` - an
      iterator chain - is the only real one.**
  * **1.0.301-302 - the array an allocation just made.** `Call "SzArrayNew", v70, typeof(System.Int32[]), 3` -
      the type is the call's own first argument and is not in any doubt, but the local it wrote had none. So
      everything read through the new array went unresolved: its length at `0x18`, its elements at `0x20`. A
      static constructor filling a jagged array literal does that once per row, and `BoardController`'s does it
      seventeen times.

      Seeded from the allocation, which is the one place that states the fact - nothing about a length read
      through an untyped base says the base was an array. It has to be said **twice**: once during type
      resolution, and again immediately before `ArrayAccessRecovery`, because the first does not survive to
      there. That was found by tracing rather than reasoning - the type reads back correctly at the end of the
      pipeline, and is null at the point that needs it.

      Unresolved memory reads in the 96 measured files **209 -> 183**, game-wide **1758 -> 1645**; statements
      commented out 572 -> **568**. Whole methods 291, decisions 93.6% and the corpus 22/25 all held, and the
      source-free oracle's whole bodies 1193 -> **1194**. `compare2` reads 2265 -> 2264, which is the ±1 it
      varies by on identical input.

Measured on an arm64 Android game, against the original Unity project the build came from. Over the game's own
assembly, counted on the graph the IL is generated from: instructions the lifter could not translate fell from 4702
to 474, calls left unresolved from 6097 to 2029, and unresolvable memory operands from 29680 to 12070 - while the
branch fix made roughly seventy percent more code reachable, so all three fell against a much larger denominator.
In the exported scripts the placeholders written where a call could not be translated fell from 15532 to 2824,
recovered object creation went from 11 expressions to 686, and string literals from none to 18943.

## Rebuilding

The source is in `../External/Cpp2IL`, so there is nothing to clone or re-apply:

```sh
# raise VersionPrefix in both csproj files and Version in Source/AssetRipper.Import/AssetRipper.Import.csproj
dotnet pack External/Cpp2IL/Cpp2IL.Core/Cpp2IL.Core.csproj -c Release -o LocalPackages
dotnet pack External/Cpp2IL/LibCpp2IL/LibCpp2IL.csproj -c Release -o LocalPackages
```

NuGet caches a package by version, so a rebuild that keeps the version will not be picked up. To move the fork
onto a newer upstream, see `../External/Cpp2IL/FORK.md`.

Once a published package carries these commits, delete this folder, drop the source from `nuget.config`, and point
`AssetRipper.Import.csproj` back at the published version.
