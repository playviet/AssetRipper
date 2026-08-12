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
