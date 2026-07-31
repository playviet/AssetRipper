# Local packages

`AssetRipper.Cpp2IL.Core` and `AssetRipper.LibCpp2IL` are normally taken from nuget.org, where they are published from
the `assetripper` branch of [AssetRipper/Cpp2IL](https://github.com/AssetRipper/Cpp2IL). The published 1.0.8 is that
branch exactly, and that branch is behind the upstream `development` branch — including the commits that
made Il2Cpp method body recovery work. Script content level 3 is a different feature with them than without.

So the two packages here are built locally instead, and `nuget.config` adds this folder as a package source.

## What 1.0.26 is

* `SamboyCoding/Cpp2IL` `development` (`b20ca0d`, "Lib: Fix v106"), which is still its head
* the three commits the `assetripper` branch adds on top, cherry picked: `Move dependencies into main projects`,
  `Change package id`, `Change version`
* five local fixes, all for arm64, which is the architecture every Android build ships and which upstream exercises
  far less than x86:
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

Measured on an arm64 Android game: the placeholders recovery writes where it could not translate a call fell from
15532 to 6497, recovered object creation went from 11 expressions to 686, string literals from none to 18943, and the
share of methods recovered as compiling code with nothing commented out went from 12.5 to 34.3 percent.

## Rebuilding

```sh
git clone https://github.com/SamboyCoding/Cpp2IL.git
cd Cpp2IL
git checkout b20ca0d
git cherry-pick 97566c8 800cc39 e3aa824   # from https://github.com/AssetRipper/Cpp2IL
# apply the five fixes above, set VersionPrefix in Cpp2IL.Core and LibCpp2IL
dotnet build Cpp2IL.Core/Cpp2IL.Core.csproj -c Release
dotnet build LibCpp2IL/LibCpp2IL.csproj -c Release
dotnet pack Cpp2IL.Core/Cpp2IL.Core.csproj -c Release --no-build -o <this folder>
dotnet pack LibCpp2IL/LibCpp2IL.csproj -c Release --no-build -o <this folder>
```

Once a published package carries these commits, delete this folder, drop the source from `nuget.config`, and point
`AssetRipper.Import.csproj` back at the published version.
