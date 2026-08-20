# probe — the ISIL inspector, and how to bring it back

`scratchpad/` is a session temp directory and has now lost `probe` five times. This file is what it takes
to have it back in about two minutes, and the two facts that make a probe reading trustworthy.

## What probe is

One .NET project that loads `libil2cpp.so` + `global-metadata.dat` through `Cpp2IlApi`, runs the same
processing layers the export runs, and then answers one of ~55 read-only questions about the model:

```
probe <libil2cpp.so> <global-metadata.dat> <unityVersion> <mode> [args...]
```

It never exports anything. `dump` is the ISIL of a method, `asm` the raw arm64, `roundtrip` the jsonl the
scorer eats. See [[il2cpp-probe-is-not-the-export]] for what it is *not* faithful about.

## probe and probe2 are one program, two build wirings

| | references Cpp2IL.Core as | rebuild with | when |
|---|---|---|---|
| `scratchpad/probe` | **PackageReference** to a version in `LocalPackages` | `bump.sh` / `probebump.sh` | the normal loop; a reading is then provably about the same build the export ran |
| `scratchpad/probe2` | **ProjectReference** to `External/Cpp2IL/Cpp2IL.Core.csproj` | `pb.sh` (no version bump) | fast iteration on a pass |

Same `Program.cs`. `probe2`'s copy in `scratchpad-tools/probe2/` is the canonical, newest source — the
`probe` backup beside it is a copy of it with `AssemblyName` and the reference swapped.

**A `probe2` build writes into `External/Cpp2IL/Cpp2IL.Core/obj` and `bin`.** Never run `pb.sh` while an
export or another agent is building out of that tree.

## Restoring it after a scratchpad loss

```sh
AR=/Users/playviet/Documents/_BZ/AssetRipper
mkdir -p $AR/scratchpad/probe
cp $AR/scratchpad-tools/probe/{probe.csproj,nuget.config,Program.cs} $AR/scratchpad/probe/
cp $AR/scratchpad-tools/{probeg,probebump.sh,p2,pb.sh,apkpath} $AR/scratchpad/
export DOTNET_ROOT=$HOME/.dotnet PATH=$HOME/.dotnet:$PATH
dotnet build $AR/scratchpad/probe/probe.csproj -c Release        # ~4s, no Cpp2IL rebuild
```

Set the `Version` in `probe.csproj` to whatever `External/Cpp2IL/Cpp2IL.Core/Cpp2IL.Core.csproj` says —
that nupkg is already in `LocalPackages`, so nothing has to be packed. `nuget.config` names `LocalPackages`
by **absolute** path; a relative one resolves to probe's own directory and the restore fails with NU1102
naming a version from nuget.org.

`probeg` re-extracts `$SP/il2cpp/` from the apk in `$SP/apkpath` when it is missing, so the binary comes
back with it.

## Invoking it

```sh
scratchpad/probeg <mode> [args...]        # the GAME binary (apkpath), Unity 6000.0.78f1
scratchpad/p2     <mode> [args...]        # same, through probe2
```

Both are wrappers; the real call is
`dotnet scratchpad/probe/bin/Release/net10.0/probe.dll <so> <metadata> <unityVersion> <mode> ...`.

Modes used most:

| | |
|---|---|
| `dump <typeSubstr> [methodSubstr]` | the ISIL, block by block — the level the passes operate on |
| `asm  <typeSubstr> [methodSubstr]` | raw arm64 for every method of a matching type |
| `at   <hexAddr> [n]` | raw arm64 at an address, plus which method owns it |
| `roundtrip` | one JSON line per method → `roundtrip.py` |
| `calls`, `fields`, `owner`, `slots`, `trgctx`, `mrgctx`, `keyfuncs`, `badoperand`, `refusals`, `count` | the rest |

`CLAUDE.md` says `scratchpad/probe asm <addr>`. That is shorthand and wrong twice over: `probe` is a
directory, and `asm` takes a **type substring**, not an address. For an address the mode is `at`.

**Never give `asm` or `dump` an empty type substring** — it scans every method in the binary, uses wrong
method-end bounds on corelib, and runs away (20 GB of output before it was killed).

## The two checks that make a reading trustworthy

1. **Same build as the export.** probe loads its *own* copy of `Cpp2IL.Core.dll`.
   ```sh
   cmp -s scratchpad/probe/bin/Release/net10.0/Cpp2IL.Core.dll \
          scratchpad/riprun/bin/Release/net10.0/Cpp2IL.Core.dll && echo same
   ```
   `bump.sh` does this and exits 1 on a mismatch. probe once sat 75 versions behind and every reading in
   between described code that no longer existed — [[il2cpp-probe-drifted-75-versions-behind]].
2. **The dump is the body as analysis *finished* with it**, not as your pass will see it. Print the body at
   the pass's own position (`CHAIN_TRACE`, `WALK_TRACE`, `IFACE_TRACE`, `PIPETRACE`) before writing a rule
   against a shape — [[il2cpp-the-dump-is-not-where-the-pass-runs]].

## Restored 2026-08-20 at 1.15.0

`scratchpad/probe` and `scratchpad/probe2` were both gone. Recovered from
`scratchpad-tools/probe2/Program.cs` (118 KB, tracked in git, the newest copy — a strict superset of the
91 KB one in the `AssetRipper-w7` worktree). `probebump.sh` and `probeg` existed only in `memory/tools/`
and are now in `scratchpad-tools/` too; `probebump.sh`'s version sed was matched on `$OLD`, the exact
pattern that caused the 75-version drift, and is now version-agnostic like `bump.sh`'s.

Built clean against `LocalPackages/AssetRipper.Cpp2IL.Core.1.15.0.nupkg`, 0 errors. Its `Cpp2IL.Core.dll`
is byte-identical to `scratchpad/riprun`'s, so it reports on the build the export is running.
`probeg roundtrip` was checked to still emit the exact JSON shape `roundtrip.py` parses.
