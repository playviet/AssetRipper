# External

## Cpp2IL

The fork of [Cpp2IL](https://github.com/SamboyCoding/Cpp2IL) that `LocalPackages` is built from.

Most of what the IL2CPP recovery gained lives here rather than in `Source` - the arm64 lifter, the
analysis passes that turn its output back into something resembling the method that was written, and
the metadata resolution that gives those values names. Keeping the source in the repository is what
makes those changes reviewable and rebuildable; the packages alone are not.

Upstream's `TestFiles` directory is not copied - it is a few hundred megabytes of sample game binaries
that nothing here needs.

### Rebuilding

```sh
dotnet pack External/Cpp2IL/Cpp2IL.Core/Cpp2IL.Core.csproj -c Release -o LocalPackages
dotnet pack External/Cpp2IL/LibCpp2IL/LibCpp2IL.csproj -c Release -o LocalPackages
```

Both packages carry a version that `Source/AssetRipper.Import/AssetRipper.Import.csproj` pins. NuGet
caches a package by version, so a rebuild that keeps the version will not be picked up - raise it in
all three places together.

### What changed, and staying mergeable

`LocalPackages/README.md` lists the individual fixes and what each one recovers.

`Cpp2IL/FORK.md` is about the fork rather than the fixes: which commit it starts from, which of upstream's
own files it touches and what it changes in each, and how to replay it onto a newer Cpp2IL. Almost everything
the fork adds is in files upstream does not have - a `*.Fork.cs` beside the class it extends, or a pass of its
own - so upstream is free to move without meeting it. `Cpp2IL/rebase-onto-upstream.sh` does the replay.
