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
| `decisions.py` | did the branching survive |
| `roundtrip.py` | what the binary says the method does, no source needed |
| `autodiff.py` | the ground-truth corpus, run without being told what to test |
| `owedcensus*.py` | why each owed body is owed, and the shape of its first commented statement |
| `unityverify.sh` | the gate: compile the exported project against the engine the editor has |

`tools/` holds the ones the repository itself depends on (`inventory.py` and its `csharp.py`/`markers.py`),
which are not duplicated here.
