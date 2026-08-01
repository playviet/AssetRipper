# Local packages

`AssetRipper.Cpp2IL.Core` and `AssetRipper.LibCpp2IL` are normally taken from nuget.org, where they are published from
the `assetripper` branch of [AssetRipper/Cpp2IL](https://github.com/AssetRipper/Cpp2IL). The published 1.0.8 is that
branch exactly, and that branch is behind the upstream `development` branch — including the commits that
made Il2Cpp method body recovery work. Script content level 3 is a different feature with them than without.

So the two packages here are built locally instead, and `nuget.config` adds this folder as a package source.

## What 1.0.29 is

* `SamboyCoding/Cpp2IL` `development` (`b20ca0d`, "Lib: Fix v106"), which is still its head
* the three commits the `assetripper` branch adds on top, cherry picked: `Move dependencies into main projects`,
  `Change package id`, `Change version`
* thirty-six local fixes, all for arm64, which is the architecture every Android build ships and which upstream
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

Measured on an arm64 Android game, against the original Unity project the build came from. Over the game's own
assembly, counted on the graph the IL is generated from: instructions the lifter could not translate fell from 4702
to 570, calls left unresolved from 6097 to 3683, and unresolvable memory operands from 29680 to 22384 - while the
branch fix made roughly seventy percent more code reachable, so all three fell against a much larger denominator.
In the exported scripts the placeholders written where a call could not be translated fell from 15532 to 2824,
recovered object creation went from 11 expressions to 686, and string literals from none to 18943.

## Rebuilding

```sh
git clone https://github.com/SamboyCoding/Cpp2IL.git
cd Cpp2IL
git checkout b20ca0d
git cherry-pick 97566c8 800cc39 e3aa824   # from https://github.com/AssetRipper/Cpp2IL
# apply the fixes above, set VersionPrefix in Cpp2IL.Core and LibCpp2IL
dotnet build Cpp2IL.Core/Cpp2IL.Core.csproj -c Release
dotnet build LibCpp2IL/LibCpp2IL.csproj -c Release
dotnet pack Cpp2IL.Core/Cpp2IL.Core.csproj -c Release --no-build -o <this folder>
dotnet pack LibCpp2IL/LibCpp2IL.csproj -c Release --no-build -o <this folder>
```

Once a published package carries these commits, delete this folder, drop the source from `nuget.config`, and point
`AssetRipper.Import.csproj` back at the published version.
