# Local packages

`AssetRipper.Cpp2IL.Core` and `AssetRipper.LibCpp2IL` are normally taken from nuget.org, where they are published from
the `assetripper` branch of [AssetRipper/Cpp2IL](https://github.com/AssetRipper/Cpp2IL). The published 1.0.8 is that
branch exactly, and that branch is behind the upstream `development` branch — including the commits that
made Il2Cpp method body recovery work. Script content level 3 is a different feature with them than without.

So the two packages here are built locally instead, and `nuget.config` adds this folder as a package source.

## What 1.0.14 is

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

Measured on an arm64 Android game, the placeholders recovery writes where it could not translate a call fell from
15532 to 6332, and recovered object creation went from 11 expressions to 549.

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
