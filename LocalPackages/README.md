# Local packages

`AssetRipper.Cpp2IL.Core` and `AssetRipper.LibCpp2IL` are normally taken from nuget.org, where they are published from
the `assetripper` branch of [AssetRipper/Cpp2IL](https://github.com/AssetRipper/Cpp2IL). The published 1.0.8 is that
branch exactly, and that branch is a few commits behind the upstream `development` branch — including the commits that
made Il2Cpp method body recovery work. Script content level 3 is a different feature with them than without.

So the two packages here are built locally instead, and `nuget.config` adds this folder as a package source.

## What 1.0.10 is

* `SamboyCoding/Cpp2IL` `development` (`b20ca0d`, "Lib: Fix v106")
* the three commits the `assetripper` branch adds on top, cherry picked: `Move dependencies into main projects`,
  `Change package id`, `Change version`
* `ApplicationAnalysisContext.ThrowHelperNamesByAddress` changed from `Dictionary` to `ConcurrentDictionary`.
  Throw helper recovery is new in `development` and reads that cache from the threads that build the assemblies in
  parallel, so an unsynchronised dictionary corrupts itself and every method in the assembly ends up as a body that
  throws the resulting `IndexOutOfRangeException`.

## Rebuilding

```sh
git clone https://github.com/SamboyCoding/Cpp2IL.git
cd Cpp2IL
git checkout b20ca0d
git cherry-pick 97566c8 800cc39 e3aa824   # from https://github.com/AssetRipper/Cpp2IL
# apply the ConcurrentDictionary change, set VersionPrefix in Cpp2IL.Core and LibCpp2IL
dotnet build Cpp2IL.Core/Cpp2IL.Core.csproj -c Release
dotnet build LibCpp2IL/LibCpp2IL.csproj -c Release
dotnet pack Cpp2IL.Core/Cpp2IL.Core.csproj -c Release --no-build -o <this folder>
dotnet pack LibCpp2IL/LibCpp2IL.csproj -c Release --no-build -o <this folder>
```

Once a published package carries these commits, delete this folder, drop the source from `nuget.config`, and point
`AssetRipper.Import.csproj` back at the published version.
