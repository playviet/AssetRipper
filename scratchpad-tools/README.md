# The measurement loop, backed up

Everything here lives in the session scratchpad while it is being used, and the scratchpad does not survive.
`riprun/Program.cs` has been lost twice; `compare2.py` and `decisions.py` went missing mid-session on
2026-08-13, between one round and the next, while their numbers were being quoted.

**This is the copy of record.** Restore with:

```sh
SP=<scratchpad>
cp scratchpad-tools/*.py scratchpad-tools/*.sh scratchpad-tools/apkpath $SP/
cp -r scratchpad-tools/riprun scratchpad-tools/probe2 $SP/
```

and back up again after changing any of them - the two copies drift silently otherwise, and a scorer that
scores differently looks exactly like a code regression.

| | what it is |
|---|---|
| `riprun/` | the export runner. `riprun <apk> <out> <log> Level3 fast` |
| `probe2/` | reads the binary through Cpp2IL without exporting: `dump`, `asm`, `bare`, `invalid`, … |
| `round.sh` | `round.sh <old> <new> <n>` - bump, build, export into the numbered follow folder |
| `bump.sh` | NuGet caches by version, so a rebuild is invisible unless the version changes |
| `cfscore.py` | the 96 files that have an original beside them |
| `compare2.py` | every body in the game, not just the 96 |
| `gamescore.py` | the whole picture of an export with **no original source** - totals, classification, rethrown generator crashes, the marker census by body, and the fix queue. `--json` for `gamescorediff.py`. See `GAMESCORE.md` |
| `gamescorediff.py` | what moved between two `gamescore --json` runs, with the verdict |
| `decisions.py` | did the branching survive |
| `roundtrip.py` | what the binary says the method does, no source needed |
| `autodiff.py` | the ground-truth corpus, run without being told what to test |
| `owedcensus*.py` | why each owed body is owed, and the shape of its first commented statement |
| `unityverify.sh` | the gate: compile the exported project against the engine the editor has |
| `seam.py` | every member cut into generic-method / generic-type / plain, with `--list=`, `--files`, `--markers` |
| `livecount.py` | live statements and branches, one export or two - the only check on "commented fell while code was deleted" |

## Working from a git worktree

Two agents in one tree clobber each other's `LocalPackages` and `riprun` (`il2cpp-measuring-while-another-agent-builds`).
These are the loop again, SP-relative and pointed at the worktree the scratchpad sits in, so nothing here
touches the main tree:

| | what it is |
|---|---|
| `bumpz.sh` | `bump.sh` for a worktree: `AR=${SP:h}`, no deleting of anyone else's package, and it **fails** unless `riprun.deps.json` names the version just built |
| `roundz.sh` | `roundz.sh <old> <new> <n>` - bump and export into `..._<n>` |
| `score.sh` | `score.sh <n>` - every scorer on export `<n>`, in one go |
| `pb.sh` | rebuild `probe2` alone against the live Cpp2IL source, no version bump - seconds, and enough to read the ISIL a change produces |
| `p2` | `probe2` against `scratchpad/bin/libil2cpp.so`, unzipped once out of the APK |
| `dis.sh` | `dis.sh <hexaddr>` - objdump at an address. `4AFBB20` is `memcpy@plt` |
| `seamtrace.sh` | `COPYFOLD_TRACE` over the seven types the generic seam lives in |

Set the scratchpad up with `cp scratchpad-tools/* $SP/` and then repoint `riprun/riprun.csproj`,
`probe2/probe.csproj` and both `nuget.config`s at the worktree - **absolute** paths, or NuGet resolves
`LocalPackages` against the process's own directory and measures the other agent's build.

`tools/` holds the ones the repository itself depends on (`inventory.py` and its `csharp.py`/`markers.py`),
which are not duplicated here.

## The roundtrip dump

`roundtrip.py` does not read the binary itself - it reads a dump of what the ISIL says every method does,
which `probe2 … roundtrip` writes. It defaulted to `/tmp/rt.jsonl`, and `/tmp` is cleaned, so the scorer
started failing mid-session with a missing file.

`rt.jsonl.gz` here is the frozen reference, taken at **1.0.993**. Restore and use it explicitly:

```sh
gunzip -c scratchpad-tools/rt.jsonl.gz > $SP/rt.jsonl
python3 $SP/roundtrip.py <export>/ExportedProject $SP/rt.jsonl
```

Keep it frozen between rounds or the denominator moves under the measurement: a pass that resolves more
calls makes the *ground truth* richer as well as the export, and the two changes cancel. Regenerate it
deliberately, and record the new baseline when you do. At 1.0.993 it is **whole 1037, partial 1567,
dead 153** — not comparable with the 972 quoted before this dump was rebuilt.

## The execution oracle is gone, and cannot be restored from here

`difftest.py` had already vanished when this backup was made, and `autodiff.py` imported it - but only
vestigially: everything `autodiff` needs it defines itself, so the import is simply removed and `autodiff`
runs again.

**What is actually lost is the corpus.** `scratchpad/corpus/` and `scratchpad/corpus6/` are empty directory
shells - no `.cs` files - and `scratchpad/ecorpus6/` has no exported project. So there is nothing to run the
oracle *on*: no original source, no recovered source, no binary.

Rebuilding it needs Unity: a small project whose every method has known source, built to arm64 il2cpp,
exported, and then

```sh
python3 autodiff.py <corpus>/Assets/.../Corpus.cs <export>/ExportedProject/.../Corpus.cs 400
```

Until then the loop has four scorers, not five, and **nothing can tell a method that is right from one that
only looks right**. Where a change is inert on the four, the check used instead has been diffing the exported
C# of the affected bodies by hand - which works, and does not scale.
