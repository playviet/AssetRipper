# Scoring a game this project has no source for

`cfscore.py`, `allscore.py`, `autodiff.py` and `corpusscore.py` all need an original beside the recovered
file. On Snacky Dash — and on any build after it — there is none, and those four are silent. What is left
is what the export can be asked about itself.

| | what it answers |
|---|---|
| `gamescore.py <export>/ExportedProject` | the whole picture in one command: totals, classification, crashes, markers, the fix queue |
| `gamescorediff.py <a.json> <b.json>` | what moved between two rounds, with the verdict |
| `compare2.py` | the six numbers `gamescore` opens with, and nothing else |
| `decisions.py` | needs originals — **useless without source** |
| `roundtrip.py` | source-free, but needs a `probe2 … roundtrip` dump of the same binary |

```sh
python3 gamescore.py "<export>/ExportedProject" --top 20 --json round-N.json
python3 gamescorediff.py round-N.json round-M.json
```

`--quiet` writes only the JSON. `--jobs` sets the parser threads (default `min(8, cpus)`). Paths are
forgiving: the export root, its `ExportedProject`, or `Assets/Scripts` itself all work.

Measured: **533 files in 1.4 s**, **4538 files / 31135 bodies in 9.9 s**. `compare2.py` takes 5.5 s on the
533. The gain is a bounded thread pool over `csharp.members`, which is one `ast-grep` subprocess per file
and is the entire cost of the run.

## It reuses the vocabulary rather than restating it

`markers.classify` decides what a body is, `csharp.members` decides what a member is and
`compare2.substituted` decides whether a file was decompiled at all. On export `_50` and export `_720`
`gamescore` reproduces `compare2`'s six numbers exactly — same totals, same per-marker occurrence counts —
which is the check that the reuse is real and not a second implementation that agrees today.

`compare2.py` calls `main()` at the bottom rather than guarding it, so importing it runs it. `_compare2()`
loads it from source with that one line dropped, and raises if the line is no longer there. Nothing in
`compare2.py`, `markers.py` or `csharp.py` is modified.

## What it adds

* **Every assembly**, not `Assets/Scripts/Assembly-CSharp` alone. Every game measured so far exports one,
  but nothing guarantees the next one does.
* **Types**, from one streamed `ast-grep` over the whole tree (`--json=stream`, one object per line — the
  match text of a type declaration is the whole type, so this must not be slurped). 0.2 s for 533 files.
* **The marker census by body as well as by occurrence.** 400 `commented` lines in one method and one line
  in 400 methods are the same number to `compare2` and nothing like the same problem. On `_720`:
  `commented` = 398 occurrences across **107** bodies, `unmanaged` = 377 across **98**.
* **The fix queue.** Worst files by `dead + marker-carrying bodies`, worst methods by marker count. On
  `_720` the head of the queue is `SlicedFilledImage::GenerateSlicedFilledSprite`, 75 `unmanaged` and 7
  `commented` in one method.
* **Rethrown generator crashes counted in their own right** — see below.
* A warning when a file in the tree was written in the last 90 seconds, because a half-written body is
  shorter and cleaner and therefore scores *better* (`il2cpp-score-only-a-finished-export`).

## The crash count, and what the marker vocabulary misses

`AsmResolverDllOutputFormatIlRecovery.FillMethodBody` wraps **both** `methodContext.Analyze()` and
`IlGenerator.GenerateIl` in one `try`, and on any exception emits three instructions in place of the whole
method: `ldstr <detail>`, `newobj Exception`, `throw`. `detail` is `e.Message` for a `DecompilerException`
("Decompilation failed: …") and `e.ToCollapsedString()` — `e.ToString()` with repeated lines folded — for
anything else, so the body arrives as one escaped line:

```csharp
throw new Exception("System.NullReferenceException: Object reference not set to an instance of an object.\n   at Cpp2IL.Core.Analysis.StructInArithmetic.IsNumber(TypeAnalysisContext type) in …");
```

One statement, no marker, nothing a compiler objects to.

**`il2cpp-source-free-oracle` says both scorers now treat a rethrown generator crash as dead. Verified:
that is only two thirds true, and the third case is the live one.** `markers.MARKERS` has

```python
('failed', ['at Cpp2IL.Core.IlGenerator.GenerateIl', 'Decompilation failed']),
```

which catches a `DecompilerException` and a crash *inside the generator* — and misses a crash inside
`methodContext.Analyze()`, whose trace has no `GenerateIl` frame because the generator was never reached.
Measured on export `_50`:

| | bodies |
|---|---|
| rethrown crashes, attributed to a member | **40** |
| …of which `classify` rates `dead` | 4 (all `DecompilerException`) |
| …of which `classify` rates **`full`** | **36** |
| rethrown crashes in the raw file text | **52** |

Cross-checked by `grep`: 38 `throw new Exception("System.NullReferenceException…at Cpp2IL`, 14
`"Decompilation failed`, **0** carrying a `GenerateIl` frame. So `compare2`'s `full=3150` on that export is
really 3114, and every scorer here inherits the same inflation. It is not hypothetical for the next game
either — the Snacky Dash log is throwing `System.ArgumentOutOfRangeException` out of
`MetadataResolver.ResolveFieldOffsets` ← `LocalVariables.ResolveTypesAndFields` ← `Analyze()`, which is
exactly the shape with no `GenerateIl` frame.

`gamescore` finds them by shape instead: a body that is one statement, `throw new Exception("…")`, whose
string opens with `<Type>Exception:` and names a `Cpp2IL`/`AssetRipper`/`LibCpp2IL`/`AsmResolver` frame —
or opens with `Decompilation failed`. A hand-written `throw new Exception("Not supported")` names no frame
and is correctly left alone.

**The fix, when someone wants it, is one line** in `markers.py`: add `'at Cpp2IL.Core.'` (or the frame the
`try` in `FillMethodBody` always produces) to the `failed` needles. It was left alone here deliberately —
it moves `full` on every export ever measured, and a scorer that silently changes scale makes a recovery
change indistinguishable from a scorer change. Change it as its own round, and record the new baseline.

## The second blind spot: event accessors are in no member at all

`csharp.KINDS` has no `event_declaration`, so an `add`/`remove` accessor body is returned by
`csharp.members` for nothing and is counted by `compare2`, `cfscore`, `allscore` and `decisions` alike as
though it did not exist. On export `_50` **12 of the 52 crashed bodies are event accessors** — the whole
gap between the two rows in the table above.

`gamescore` measures the gap without closing it: it counts crashes a second time over the raw file text and
prints the difference. Closing it means adding `event_declaration` to `csharp.KINDS`, which moves the
denominator of every number this project has recorded. Same reasoning as above — its own round, its own
baseline. `il2cpp-every-event-accessor-is-a-compare-exchange` says these bodies are recoverable, so the
gap is hiding real work as well as real damage.

## The JSON

`--json` writes the same numbers, plus a `files` map keyed by path — but only for files carrying damage
(`dead` or marker-carrying bodies), which is the set a round is trying to move. A file that becomes clean
drops out, and `gamescorediff.py` reads the absence as the improvement it is. 35 KB for a 533-file export.

`gamescorediff.py` prints totals, both classification scales with the share as well as the count, crashes
by exception, the marker census both ways, the files that moved each way, and a verdict over five numbers
with the direction that counts as an improvement written beside each. It says in as many words that `full`
alone does not settle it, because it does not: 19 of 65 corpus shapes rated `full` compute the wrong
answer (`il2cpp-what-full-is-actually-worth`).

## Numbers on the test export

FluffyField `_720` (the newest finished export on disk; the Snacky Dash export was still being written and
was left alone):

```
assemblies 1  files 533 (110 substituted)  types 664  bodies 3522  no body 120
full 3245 (92.1%)   partial 165 (4.7%)   dead 112 (3.2%)
decompiled only:  full 2551 (90.3%)  partial 165 (5.8%)  dead 110 (3.9%)
crashes 0
commented 107 bodies / 398   unmanaged 98 / 377   notfound 24 / 39   indirect 32 / 35
unknowncall 4 / 7   unknown 1 / 2   notimpl 0   structvalue 0   failed 0   thrown 0
```

Identical to `compare2.py` on the same tree.
