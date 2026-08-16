# Exception flow — round log

Worktree `agent-ac60f81c20ec878d3`, branch `worktree-agent-ac60f81c20ec878d3`, merged forward to master
`10cec4684` before the first round. Versions 1.11.0–1.11.29, exports 600–629.

**The oracle is the scorer for this task.** None of the 96 reference files contains a `catch` or a `using (`
— checked, `grep -rl catch <ORIGIN>` is 0 of 96 files — so `cfscore`, `compare2`, `allscore`, `decisions`
and `roundtrip` are all structurally blind to the family. They are run every round only to prove that
ordinary control flow did not move.

## Baseline — 1.11.0 / export 600 (== master 10cec4684)

| | |
|---|---|
| oracle | **79 run / 54 same / 25 differ**; 65 `full`, 49 right, **16 whole-and-wrong** |
| compare2 | full 3255/3511 (decompiled-only 2561/2815 = 91.0%), commented 363, unmanaged 315, notfound 38, indirect 18 |
| cfscore | 609 full / 6 partial / 0 missing, 91 of 96 files clean, unmanaged 19, commented 4 |
| allscore | 2121/2326 = 91.2% (CF 610/616, CFramework 1409/1608, Common 102/102) |
| decisions | 1326 / 1382 (95.9%), keeping all of them 293 (94.8%) |
| roundtrip | whole 1044 (37.9%), partial 1571, dead 142; 11196 of 16264 = 68.8% |
| gen failures | 0 |

Reproduced byte for byte from the numbers in the task, so the instrument is calibrated.

## What the four assigned shapes actually are

Read off the corpus binary with `probe2 dump` before writing a line of code. Two of the four are **not
defects**:

* **`Divide`** — the whole body in the binary is
  `0 Divide returnVal1, a, b` / `1 Return returnVal1`. There is no handler to recover: `sdiv` does not trap,
  so clang deleted the `try`/`catch` outright. The recovered `return a / b;` is a *faithful* recovery; the
  oracle calls it wrong only because C#'s `/` throws on zero where arm64's `sdiv` returns 0.
* **`Guarded`** — likewise: `0 Divide v0, a, b` / `1 ShiftLeft returnVal1, v0, 1` / `2 Return`. The `catch`
  *and* the `finally` are both gone from the binary; only the `finally`'s `* 2` survives, folded into the
  `<< 1`. Again a faithful recovery of a body clang emptied.

  **Neither is recoverable from this binary and neither should be chased.** `corpus/BASELINE.md` half-said
  this ("UDIV does not trap, so clang deleted two handlers - but `Thrown` ... is not covered by that");
  the dumps make it exact, and they say the *same* thing about `Guarded`, which BASELINE.md did not.

* **`Using`** — real, and small. See round 1.
* **`Thrown`** — real, and the whole feature. See round 2.

---

## Round 1 — 1.11.1 / export 601 — the exception slot a copy carried. **KEPT**

**File and function:** `External/Cpp2IL/Cpp2IL.Core/IlGenerator.Fork.cs`, `OnlyAskedIfItIsNull` —
split into a two-argument overload that follows the question through a copy, with a `HashSet` cycle guard.
No other file changed; no upstream file touched.

**The diagnosis, from `probe2 dump Corpus Using` and not from the export.** Destroying SSA puts the answer a
block away from the register that was cleared:

```
b8   -1 Move v57 @ X19_v7, 0            <- the compiler's exception slot, cleared
b27  -1 Move v76 @ X19_v8, v57 @ X19_v7 <- the edge copy
b30  93 CheckNotEqual v148, v76, 0      <- and only the copy is asked
```

`OnlyAskedIfItIsNull` (landed 1.0.493 for `SumSteps`) asked only about **direct** reads of the local, so the
`Move` into `v76` was a read that is not a zero-comparison, the predicate said no, `0L` was stored into an
`object` slot, `if (obj2 == null)` was false, and the recovered `using` threw `OutOfMemoryException` where
the original returned. A copy carries the value unchanged, so the question is the copy's as much as the
local's. The destination must be untyped too — where there is a declaration, the declaration decides.

**Predicted before the export**, and this is what came out: `object obj = 0L;` becomes `object obj = null;`,
the `//IL_0046: Expected O, but got I8` note disappears, and `Using` becomes `full` + `right`.

**The case named as must-not-move, before measuring**: the risk of this rule is a genuine integer zero read
as `null`, which costs whole bodies. Named in advance: `Bits`, `Modulo`, `Overflow`, `Narrow`, `Clamp` and
`Ternary` must stay `full` + `right`, `decisions` must not fall below 1326, and `cfscore full` must stay 609.
**All held.**

| | 600 (base) | 601 |
|---|---|---|
| **oracle: run / same** | 79 / **54** | 79 / **55** |
| **oracle: full + WRONG** | **16** | **15** |
| oracle: full + right | 49 | 50 |
| compare2 full / commented / unmanaged / notfound / indirect | 3255 / 363 / 315 / 38 / 18 | identical |
| compare2 decompiled-only | 2561/2815 = 91.0% | identical |
| cfscore full / partial / files clean | 609 / 6 / 91 | identical |
| allscore | 2121/2326 = 91.2% | identical |
| decisions | 1326 / 1382 | identical |
| roundtrip whole | 1044 | identical |
| gen failures | 0 | 0 |

**Keep.** Correctness up by one shape, every compilability measure byte-identical. Nothing to weigh against
anything — the two families do not disagree here.

---

## Round 2 — 1.11.4 / exports 604 (corpus) + 605 (game) — `try`/`catch` from the landing pad

**Files and functions**

| file | what |
|---|---|
| `Cpp2IL.Core/Analysis/CatchClauses.cs` | **new.** `Run`, `Recognise`, `Dispatch`, `NamedClass`, `Region`, `Reachable`, `DropThePlumbing`, `Detach`, `DefinitionIn` |
| `Cpp2IL.Core/IlGenerator.Fork.cs` | **new:** `AddCatchClauses`, `EmittedRange`, `At`, `Refused` |
| `Cpp2IL.Core/IlGenerator.cs` | **one line**, last of `GenerateIl`: `AddCatchClauses(...)` |
| `Cpp2IL.Core/Analysis/ForkPipeline.cs` | **one line** + its reason, last of `AfterUnusedLocalsAreDropped`: `CatchClauses.Run(method)` |

**What it does.** il2cpp compiles `catch (T)` into a raise followed by a C++ landing pad, and clang puts the
pad in the instruction stream like any other code — so **all of it is already in the graph**. `Corpus::Thrown`
carries its whole handler six blocks past the throw, ending in the `Return -5` the original wrote. What was
missing is only that a CIL `throw` ends the block, so the decompiler discarded the handler as unreachable,
with no marker and no warning on any scorer.

The pass finds a block whose `Throw` ends it and whose single successor is a pad, walks the pad for the
`class_is_assignable_from` dispatch, **names the caught type from the `Il2CppClass<T>` operand the analysis
already types**, takes the handler as the arm the test passes into, deletes the C++ plumbing around it
(`__cxa_begin_catch`, the selector test, the re-raise) and moves the handler blocks to the end of
`graph.Blocks`. That last step is the whole of the layout work: `LayoutOrder` already writes an unreached
block out after every reached one, so the handler comes out as one contiguous run at the end, which is what a
CIL handler range has to be. The generator then adds the `pop` a handler is entered with, turns each `ret` in
the handler into a store and a `leave`, and writes the `CilExceptionHandler`.

**What it deliberately does not do.** The `try` range is the throwing block and nothing else. The real range
is in the binary's `.gcc_except_table`, and reading that is a project of its own — see *specified but
unbuilt* at the end of this log. Guarding the throw alone is the part that is certainly true, and it is the
part that turns a body that throws into a body that returns what the original returned. One clause per
method; a handler that runs back into the method, or that anything outside the pad reaches, is refused.

**Falsified in `probe2` first, twice, before spending an export** — which is what kept this to one game
export instead of three:

1. The pass did not fire at all. `DefinitionIn` searched **one block**, and
   `il2cpp_codegen_initialize_runtime_metadata` — the call that answers with the class pointer — is in `b11`
   while the dispatch that reads it is in `b13`. Widened to the pad region.
2. It fired, deleted the plumbing correctly, and then the **emission** silently refused: `CATCH_TRACE=1`
   said `the ranges do not hold: try 19..21, handler 22`. The guard demanded a *gap* between the try and the
   handler, where the ordinary shape has the handler begin at the very next instruction. Off by one.

A third bug was headed off by construction: instruction identity is `ReferenceEquals`, not `IndexOf`, because
this handler opens on a `nop` and `IndexOf` compares values — it would have found the first `nop` in the
method.

**Predicted before the export**, and this is what came out verbatim:

```csharp
public static int Thrown(int value)
{
    if ((value & 0x80000000L) == 0) return value << 1;
    string paramName = default(string);
    ArgumentOutOfRangeException ex = new ArgumentOutOfRangeException(paramName);
    paramName = null;
    try { throw ex; }
    catch (ArgumentOutOfRangeException) { return -5; }
}
```

`value >= 0` gives `value * 2`, `value < 0` gives `-5`: the original's answer on both arms.

**The cases named as must-not-move, before measuring.** For exception recovery the falsifier is a body with
no handler, whose control flow must not move at all — checked in `probe2`, which costs seconds:

* `Corpus::Using` — *has* a `Throw` and a full landing pad, and must **not** be recognised (its throw is
  mid-block; it is already right after round 1). Its `b28`/`b30` plumbing is present and unchanged.
* `Corpus::Divide`, `Corpus::Guarded` — no handler in the binary at all. Byte-identical dumps.
* And on the game: **0 generation failures** is the test that no invalid handler was written, because an
  unverifiable body makes the decompiler throw and that shows up nowhere else.

### Numbers

| | 600 (base) | 601 | **604 / 605** |
|---|---|---|---|
| **oracle: run / same** | 79 / **54** | 79 / 55 | 79 / **56** |
| **oracle: full + WRONG** | **16** | 15 | **14** |
| oracle: full + right | 49 | 50 | **51** |
| compare2 full / partial / dead | 3255 / 148 / 108 | same | **same** |
| compare2 decompiled-only | 2561/2815 = 91.0% | same | **same** |
| compare2 commented / notfound | 363 / 38 | same | **same** |
| compare2 **unmanaged** | 315 | 315 | **317** (+2) |
| compare2 **indirect** | 18 | 18 | **19** (+1) |
| cfscore full / partial / files clean | 609 / 6 / 91 | same | **same** |
| allscore | 2121/2326 = 91.2% | same | **same** |
| decisions | 1326 / 1382 | same | **same** |
| roundtrip whole | 1044 | same | **same** |
| **gen failures** | 0 | 0 | **0** |

**Keep.** Correctness up by one shape on the oracle; every compilability measure level except three markers.

**Where the three markers came from, and why they are the good kind.** The pass fires on exactly **one**
method in the whole game, `CFramework::AudienceSegmentConfig`. At 601 that body ended:

```csharp
segments.Add(segmentRule); ... num++; }
throw new NullReferenceException();          // and nothing after it, ever
```

and at 605 it ends:

```csharp
try { throw new NullReferenceException(); }
catch (Exception)
{
    ... _ = "Unmanaged memory load: [v265 @ X8_v26+188]";
        _ = "Indirect call: 451 IndirectCall v269 ..."; ...
}
```

All three new markers are **inside that handler**, which did not exist in the export before. They are not a
read that got worse; they are code that was wholly absent and is now present with its unrecovered parts
honestly named. `RECOVERY.md`'s rule — *a read that becomes a marker beats a read that quietly returns
element zero* — applies a fortiori to a statement that beats no statement at all. `full` did not fall,
because the body was already `partial`.

**Correctness weighed above compilability, and here they do not disagree**: the oracle is the only scorer
that can see this family at all and it moved by two, while nothing that measures compiling-whole moved
except three markers that are new visibility.

*(`SoftMasking/SoftMask.cs` also contains a `catch` in this export, but it is one of the 110 substituted
never-decompiled originals — brace style gives it away — and is not this pass's work.)*

**A hazard that cost one export.** Export 602 was destroyed silently: `bumpz.sh` was run for the *next*
round while 602's game export was still going, and its `rm -rf $SP/riprun/obj $SP/riprun/bin` pulled the
binaries out from under the running process. It exited **0**, wrote `DONE`, produced an `ExportedProject`
directory with **no scripts in it**, and every scorer reported zeroes — which is `il2cpp-score-only-a-
finished-export` arriving from a new direction. **Never bump while an export is running.**

---

## Round 3 — 1.11.5 / export 606 — the census. **Behaviour unchanged; this measures the ceiling**

`CATCH_CENSUS=1` puts a counter behind every `return null` in `CatchClauses.Recognise` and prints on a
modulus, which is the *instrument the refusal* rule from `RECOVERY.md` §6.4. It changes nothing else — the
same two clauses are written, and it is off unless the variable is set.

**7500 throwing blocks in the game.** Where each one goes:

| | | |
|---|---|---|
| 3033 | 40% | **no `class_is_assignable_from` dispatch in the region** — a runtime null/bounds check with no handler at all. Correctly refused; these are not clauses. |
| 2372 | 32% | **the throw has no successor — no pad was laid after it.** clang put the landing pad somewhere else. **This is the `.gcc_except_table` gap** and nothing structural can close it. |
| 849 | 11% | the region past the throw is bigger than a pad (>32 blocks) |
| 680 | 9% | **the throw does not end the block** — `MergeCallBlocks` left it mid-block. This is `Corpus::Using`'s shape. |
| 464 | 6% | **a dispatch WAS found and the handler is not closed** — it runs back into the method |
| 22 | 0.3% | more than one clause in the method |
| **2** | | **recovered**: `CFramework::AudienceSegmentConfig::OnParse` and `Newtonsoft.Json…::JsonSerializerInternalReader::SetExtensionData` |

So **486 methods contain a landing-pad dispatch this pass finds and then refuses for a stated reason** —
that is the reachable pool, and it is two orders of magnitude larger than what is recovered today. The 464
need only two things this round did not build: a `try` range wider than one block, and `ret`/`br` out of the
**try** rewritten to `leave` the same way the handler's already are.

---

## Round 4 — 1.11.6 / exports 607 (game) + 608 (corpus) — split at the throw. **KEPT: 2 clauses → 22**

**File and function:** `Cpp2IL.Core/Analysis/CatchClauses.cs` — new `SplitAtTheThrow` and `Unsplit`, called
from `Run` before recognition. Nothing else changed.

**Why.** `MergeCallBlocks` runs *after* `MetadataResolver` rewrites a raise into a `Throw`, so the throw can
end up mid-block with the landing pad's own first instructions behind it in the same list — and then nothing
about the block says where one ends and the other begins. The round-3 census counted **680** throwing blocks
in that shape against 2 clauses recovered, making it the largest thing the recognition was blind to for a
reason that has nothing to do with exceptions.

**Why it is safe.** The split is a straight-line cut of a single-entry, single-exit run — the one graph edit
that cannot change what anything means. And it is **undone wherever it bought nothing**: if the method ends
up with no clause, every split is put back exactly, so a method that gains no handler is left with the graph
it had. Only 35 splits survive across the whole game, all inside the 22 methods that gained a clause.

**The case named as must-not-move, before measuring.** `Corpus::Using` is *exactly* this shape — a mid-block
`Throw` with a landing pad behind it — and it must **not** be recognised, because a `using` compiles to
`try`/`finally` and its pad is a cleanup pad with no `class_is_assignable_from` dispatch at all. If the split
were kept there it would perturb a body round 1 had just got right. Checked in `probe2` before the export:
**byte-identical**, `b16` still `[Throw, CheckNotEqual, ConditionalJump]`. The split was made and undone.

### The census moved as predicted

| | 606 | **607** |
|---|---|---|
| **recovered** | **2** | **22** |
| the throw does not end the block | 680 | **0** — the bucket is gone |
| no dispatch in the region (correctly refused) | 3033 | 4143 |
| a dispatch found, handler not closed | 464 | **631** (+167 newly visible) |
| the throw has no successor (needs the LSDA) | 2372 | 2390 |
| splits kept | — | 35 |

Most of the 680 turn out to be cleanup pads with no `catch` — `Using`'s shape — and are correctly refused.
But **167 more real clauses** became visible and now sit in the open-handler pool, which is the next item.

### Numbers

| | 605 | **607 / 608** |
|---|---|---|
| oracle: run / same | 79 / 56 | 79 / **56** |
| oracle: full + WRONG | 14 | **14** |
| **catch clauses written game-wide** | **2** | **22** |
| compare2 full | 3255 | **3251** (−4) |
| compare2 partial / dead | 148 / 108 | **152** / 108 |
| compare2 commented / notfound | 363 / 38 | **same** |
| compare2 unmanaged | 317 | **323** (+6) |
| compare2 indirect | 19 | **22** (+3) |
| cfscore full / partial / files clean | 609 / 6 / 91 | **same** |
| allscore | 2121/2326 = 91.2% | **2118**/2326 = 91.1% (−3) |
| decisions | 1326 / 1382 | **same** |
| roundtrip whole | 1044 | **same** |
| gen failures | 0 | **0** |

### Where the cost landed — checked, not asserted

`scratchpad/mdiff.py` diffs marker counts file by file between the two exports. **Five files moved, and
that is all:**

```
  -3      3 ->   0   CFramework/AudienceSegmentConfig.cs
  +3      0 ->   3   CFramework/ThinkingDataTracking.cs
  +3      0 ->   3   CFramework/FirebaseTracking.cs
  +3      4 ->   7   CFramework/UserSegmentationManager.cs
  +3      0 ->   3   CFramework/FacebookTracking.cs
```

All four that gained markers are on the list of methods that gained a clause —
`ThinkingDataTracking::SyncUmpConsent`, `FirebaseTracking::ApplyConsent`,
`FacebookTracking::SyncUmpConsent`, `UserSegmentationManager::TryGetDeviceCountry` — and they are the four
bodies that went `full` → `partial`. **There is no collateral: every marker and every lost `full` is inside
a handler that did not exist in the export before.** `AudienceSegmentConfig::OnParse`, the one clause round 2
already recovered, *lost* three markers because the split gives it a better one.

**Keep, and this is the one place correctness and compilability disagree.** `full` fell by 4 and I followed
correctness: those four bodies were scoring as whole **only because their handler was missing**, which is
`il2cpp-a-thrown-body-scores-as-a-whole-one` seen from the other side — a body with nothing in it has
nothing to mark. Every correctness measure is level (decisions 1326, roundtrip whole 1044, cfscore 609,
corpus oracle 56 same / 14 wrong, 0 generation failures) while eleven times as many methods now carry the
handler the program wrote. A marker on a recovered `catch` beats a `catch` that was silently deleted.

---

## Round 5 — 1.11.7 then 1.11.8 / exports 609, 610 (game) + 611 (corpus) — the handler that does not return. **KEPT: 22 → 77**

**Files and functions:** `Analysis/CatchClauses.cs` — `Reachable` replaced by `Body` + `HandlerRegion`, new
`DeclareTheHandlersLocals`, `DropThePlumbing` fixed, two new census buckets. `IlGenerator.Fork.cs` —
`AddCatchClauses` gains the branch-out-of-the-handler sweep and a `handlerEnd` bound.

**The diagnosis was wrong in the round-3 census, and the fix is what that revealed.** The bucket was named
*"a dispatch was found but its handler is not closed"* — but the handler set was built by `Reachable`, which
is transitively closed **by construction**. What was actually happening is that a `catch` which does not
return runs back into the method, so "what the handler entry reaches" is the whole method and the walk ran
off its 32-block bound. Where the two walks meet is not a failure: **it is the point the handler hands
control back, which CIL spells `leave`.**

So the handler is now *what its entry reaches and the method does not* — `HandlerRegion(entry, Body(...))`,
where `Body` is reachability from the graph entry with the throw's edge into the pad cut. Subtracting the
body is the whole difference. On the emission side, every `br` inside the handler range whose target lies
outside it becomes `leave`; a **conditional** branch out of a handler has no CIL spelling and is refused
(19 methods).

### The generation failure, and what it taught

Export 609 came back with **1 generation failure** where every previous export had 0 — the number
`RECOVERY.md` says to check before believing any other. `TDCommonUtils::FormatDate`:

```
KeyNotFoundException: The given key 'v5 @ X22' was not present in the dictionary
```

**`ISILControlFlowGraph.Instructions` is a breadth-first walk from the entry block.** It yields only what is
*reachable* — and this pass makes the handler unreachable on purpose, because that is exactly what lays it
out last. So:

* the generator builds its local map by sweeping that property, and a local named **only** inside a handler
  was never declared. `DeclareTheHandlersLocals` now adds them (in the fork's file, not upstream's).
* and `DropThePlumbing` computed "is this call's answer read by anything?" from the same property, so it was
  really asking whether anything *outside* the handler reads it. Fixed to walk `graph.Blocks`.

Both were latent from round 2 and only fired once the handler population grew. A generation failure costs
the whole body and leaves **no marker**, so it scores as a whole method everywhere else — this is
`il2cpp-a-thrown-body-scores-as-a-whole-one`, and it is why that count is looked at first.

At 1.11.8 with both fixed: **0 generation failures, 77 clauses written.**

### Numbers

| | 607 | **610 / 611** |
|---|---|---|
| **catch clauses written game-wide** | 22 | **77** |
| **gen failures** | 0 | 1 at 1.11.7 → **0** at 1.11.8 |
| oracle: run / same | 79 / 56 | 79 / **56** |
| oracle: full + WRONG | 14 | **14** |
| compare2 full | 3251 | **3247** (−4) |
| compare2 partial / dead | 152 / 108 | **156** / 108 |
| compare2 commented | 363 | **367** (+4) |
| compare2 unmanaged / indirect | 323 / 22 | **346** (+23) / **27** (+5) |
| compare2 notfound | 38 | **same** |
| cfscore full / partial / files clean | 609 / 6 / 91 | **same** |
| allscore | 2118/2326 | **2114**/2326 = 90.9% |
| decisions | 1326 / 1382 | **same** |
| roundtrip whole | 1044 | **same** |
| **Unity gate** | 12 CS7069 (its floor) | **12 CS7069 — unchanged** |

**The Unity gate was run on export 610**, which is the check `CLAUDE.md` reserves for before staging and the
only one that says the export is a project someone can open: `unityverify.sh` over 285 assemblies reports
**12 `error CS7069` and nothing else**, which is the floor stated in the baseline. **77 exception handlers,
`leave` instructions, protected regions and all, and not one new compile error.**

**Census:** recovered 22 → **80 recognised, 77 written**; *the handler is the rest of the method* 466
remaining; *a conditional branch leaves the handler* 19; *more than one clause* 28; splits kept 96.

### Where the cost landed — checked again, file by file

`mdiff.py` says **seven files move and that is all**: `AssetLoader` +8, `AESUtils` +6, `AdjustTracking` +4,
`RemoteConfigManager` +4, `AudienceSegmentConfig` +3, `SaveIO` +2, `GoogleSheetHelper` +1. Every one gained
a clause. `AssetLoader` looked like collateral until checked — its three clauses are in nested display
classes, so the census printed the display class's name and not the file's.

**Keep**, on the same reasoning as round 4 and now at three and a half times the scale: every correctness
measure level (oracle 56/14 with `Thrown` and `Using` both right, decisions 1326, roundtrip whole 1044,
cfscore 609, 0 generation failures), −4 `full` and +32 markers **entirely inside handlers that did not exist
in the export before**. Correctness followed over compilability, and named as such.

### The honest caveat on what a recovered handler contains

`AESUtils::DecryptAES` is the fair example. The clause is right — `catch (FormatException)` is the type the
original wrote — and the body is the program's own `Debug.LogError(...)`. But it ends:

```csharp
catch (FormatException) { ...; Debug.LogError(string.Concat(null, text)); throw new OutOfMemoryException(); }
```

That trailing throw is the C++ **re-raise tail**, swallowed into the handler region — the same shape
`Corpus::Using` had before round 1, where it is guarded by the cleared exception slot. So a recovered handler
today gives back the *statements the program wrote*, and does not yet give back the handler's **exit**.
Before this work the whole handler was absent, so this is strictly more of the program than there was; it is
not a claim that the handlers are right end to end, and no scorer in this project can currently tell.
Trimming the re-raise is listed below.

---

## Round 6 — 1.11.9 to 1.11.13 / exports 612–617 — `.gcc_except_table`. **BUILT, MEASURED, REVERTED**

**What was kept:** `Cpp2IL.Core/Analysis/ExceptionTable.cs` and `Analysis/InstructionAddresses.cs` (new), and
**one line** in `InstructionSets/NewArmV8InstructionSet.cs`, gated off unless `CATCH_ADDRESSES=1`. They are
an **instrument**, not a pass — nothing in the recovery reads them. `scratchpad-tools/lsda.py` is the same
reader in Python, and `probe2 padcheck` is the diagnostic.

**What was reverted:** `Analysis/ExceptionEdges.cs`, the `AfterTheGraphIsBuilt` hook, the one line in
`MethodAnalysisContext.Analyze`, and every change to `CatchClauses`. Export 617 is **byte-identical to 610**
on every scorer — 77 clauses, full 3247, commented 367, unmanaged 346, notfound 38, indirect 27, cfscore
609 / 19 / 4, decisions 1326, roundtrip whole 1044, 0 generation failures — so the revert is exact.

### The tables are there, and they say what they should

`.gcc_except_table` 714 KB, `.eh_frame_hdr` 2.4 MB, `.eh_frame` 10.3 MB — **not stripped**. The reader was
written in Python first, on purpose, and worked on the first method it was pointed at. For `Corpus::Thrown`:

```
try [7231CC, 723214)  ->  pad 723214  action 3   catch
```

and `objdump` at those addresses shows the range ending exactly after the raise, the pad being the
`cmp w1, #1` selector test, and `bl __cxa_begin_catch@plt` four instructions later — which independently
confirms the `D5E510` this fork had already identified structurally. **The LSDA call-site table IS the `try`
range**, as predicted.

Game-wide: 297,836 functions have an FDE, **9,634 have an LSDA, 8,632 have at least one catch call site**,
81,436 catch call sites in all.

### The finding that matters, and it is not what I went looking for

`CFramework.SaveIO::Load` is the shape the whole exercise was aimed at — 22 distinct catch pads, laid as
twenty four-byte stubs *after* the body, with no edge to any of them. Pointing the reader at it:

```
distinct catch pads=22   in RAW lift=22   present after passes=2   start a block=0
```

**The handler is not missing from the binary and it is not missing from the lift. It is deleted in the first
few instructions of the analysis.** A landing pad is entered by the unwinder and by nothing else, so nothing
branches to it, and `StackAnalyzer.Analyze` opens with `graph.RemoveUnreachableBlocks()` — whose comment
reads *"Without this indirect jumps (in try catch i think) cause some weird stuff"*. It is right about where
the weird stuff comes from and wrong about what to do with it: what it deletes is every `catch` body in the
program. **The 77 clauses recovered in rounds 4 and 5 are exactly the ones whose pad clang happened to lay
where a fall-through edge reached it anyway.**

### What was built on that, and what it measured

`ExceptionEdges`, at the earliest hook there is — the moment the graph exists, before stack analysis: split
a block at each pad the table names, and add the edge the unwinder represents. It **works as a mechanism**:
`SaveIO::Load` goes from 2 surviving pads to **18 of 22, all beginning a block**.

And it costs more than it buys. Four rounds of it, each fixing a real bug found by measurement:

| | clauses | what was wrong |
|---|---|---|
| 1.11.9 — table lookup, no edges | 77 | inert: the pads are gone by then, so the table names blocks that do not exist |
| 1.11.10 — edges, table pad **overrides** the fall-through | **9** | where ranges nest, the tightest row covering a throw names an **outer** clause's pad, and following it walks away from the handler that was right there |
| 1.11.11 — try each candidate pad in turn; detach pads nothing came of | 11 | `Body` followed the attached edge, so the handler looked like ordinary code the method reaches anyway |
| 1.11.12 — `Body` skips unwind edges; the predecessor check allows them | **30** | still less than half of 77 |

At 1.11.12, against 1.11.8: clauses **77 → 30**, `full` 3247 → **3244**, `commented` 367 → **434**,
`unmanaged` 346 → **381**, `notfound` 38 → **47**, and the 96 reference files — which contain **no `catch`
at all**, so every marker there is pure loss — went `unmanaged` 19 → 23 and `commented` 4 → 6. The corpus
oracle went 56 → **55** the same, `TotalSides` flipping from `partial right` to `partial WRONG`.

**Worse on every axis, so it goes.** The cause of the residual 77 → 30 is legible in the census and I am
recording it rather than guessing at it: attaching pads globally adds blocks and joins to *every* method
with a catch site, and the recognition's remaining refusals move accordingly — *no dispatch in the region*
4143 → 5587, *region not pad-sized* 1190 → 3119. A method with ten pads gets all ten attached when one
matters. The next step is not another knob on the bound; it is to attach **selectively**, and nothing yet
says which pad to pick before the analysis that needs it has run.

### What must not move, named before measuring, and what happened

`Corpus::Thrown` had to stay recovered — it did, at every version. `Corpus::Using` I named as
**byte-identical**, and that was the wrong control: attaching pads legitimately splits its block at the pad,
and the split is an improvement (`115 CheckNotEqual v92, v82 @ X1_v3, 1` reads a real register where it had
read a bogus constant). The control that was actually load-bearing — *does `Using` still compute the right
answer* — held at every version. Naming the observable rather than the byte is the lesson.


---

## Round 7 — 1.11.14 to 1.11.16 / exports 618–624 — anchor the `try` on the protected range. **KEPT**

**Files and functions:** `Analysis/ExceptionEdges.cs` (rewritten from round 6's reverted version) — `Run`,
`Reachable`, `BlockAt`, `BlockCovering`, `BlockStartingAt`. `Analysis/CatchClauses.cs` — `RecogniseThrough`
split out and reused, `LetGoOfTheUnusedPad`, `DefinitionIn` follows one copy, `Region` takes the pad.
`IlGenerator.Fork.cs` — `AddCatchClauses` writes the try's exit as `leave`.
`Analysis/ForkPipeline.cs` — the `AfterTheGraphIsBuilt` hook. **One line** in
`MethodAnalysisContext.Analyze`. `InstructionAddresses.Record` ungated, since the recovery now reads it.

**What it recovers that nothing before could.** `CFramework.SegmentRuleEvaluator::Evaluate`:

```csharp
try { _tokens = GetTokens(rule); }
catch (Exception) { … Logger.LogError(message); num = 0L; goto IL_001e; }
```

**There is no `throw` in that `try`** — it is an ordinary call, and only the LSDA can say it was protected —
**and the handler does not return**, it logs and falls out. Both halves were out of reach one round ago.

### The three selection rules, each of which cost a measurement

Round 6 attached every pad in every method and lost 47 of the 77 clauses it meant to grow. What replaces it:

1. **Attach only what would otherwise die.** A pad clang laid where ordinary control falls into it is already
   reachable and needs no help — and those are exactly the clauses the throw-anchored recognition already
   finds. Reachability at graph-build time is the exact test for *"`RemoveUnreachableBlocks` is about to
   delete this"*, so it is the test.
2. **And not at all in a method that already has one.** If any of a method's catch pads is reachable, this
   pass adds nothing — not even for the method's *other* pads. Attaching one pad to a method whose clause
   already worked is what took the export from **17 catch clauses to 9**: the help went to methods that
   needed none, and the join it costs was paid by the clause that was already there.
3. **One pad per method, the one covering the most bytes.** `CatchClauses` emits at most one clause per
   method, so a second attachment can never pay for its join. *Covered* is the **sum** of the pad's ranges,
   not the span between first and last — a pad often appears in rows far apart, and measuring the span
   ranked eight bytes in two places above forty-four in a row.

### The emission half

A protected region may not be fallen out of, returned out of, or branched out of. The try's exit is now
written as `leave`: `Ret` → `stloc` + `leave` to the epilogue, `Br` → `Leave`, a fall-out gets an explicit
`leave` to where it was going to fall, and a **conditional** exit is refused because `leave` is
unconditional. Where the try ends in a `throw` — every clause found by following a throw — none of this
fires, so those bodies are untouched by construction.

### A failure mode worth more than the fix

`DefinitionIn` following a copy had no visited set, and a cycle of copies overflowed the stack. The symptom
is the thing to remember: **the export exited `0`, wrote `DONE`, produced no scripts, and every scorer read
it as zeroes.** The stack trace went to stderr, which `census.sh` was filtering with `grep -E "^CATCH"`.
*Never filter the stream you are also using to tell whether the run finished.* This is the third distinct way
this loop has produced a plausible-looking zero.

### Numbers, against 617

| | 617 | **623 / 624** |
|---|---|---|
| **catch clauses in the export** | 17 | **21** |
| **corpus oracle: run / same / whole-and-wrong** | 79 / 56 / 14 | **identical** |
| `Thrown`, `Using`, `TotalSides` | right, right, partial-right | **identical** |
| cfscore full / partial / files clean / markers | 609 / 6 / 91 / 19,4 | **identical** |
| decisions | 1326 / 1382 | **identical** |
| roundtrip whole | 1044 | **identical** |
| commented / notfound / dead | 367 / 38 / 108 | **identical** |
| gen failures | 0 | **0** |
| **Unity gate** | 12 CS7069 | **12 CS7069 — its floor** |
| compare2 full | 3247 | 3244 (−3) |
| compare2 unmanaged / indirect | 346 / 27 | 350 (+4) / 28 (+1) |

**Where the cost landed:** `mdiff.py` says three files move. `SegmentRuleEvaluator` +3 and `StateMachine` +1
**both gained a `catch` that was not there before**; `GoogleDesignConfigSo` +1 gained nothing and is the
**one marker of genuine collateral** in the whole change. The 96 reference files are untouched.

**Keep.** Every correctness measure identical, four more handlers, and the cost is the same shape as rounds
4 and 5 — markers on newly visible code — with a single marker of collateral.


---

## Specified but unbuilt

Written down so the next session starts where this one stopped rather than re-deriving it.

1. ~~`.gcc_except_table`.~~ **Done in round 7**: read, anchored on, and kept. What is left of it is the
   *scale*: 8,632 functions in the binary have a catch call site and this recovers **21** clauses. The three
   selection rules that made it safe are also what make it small — one pad per method, only where the pad
   would otherwise die, and only in methods that have no working clause already. Every one of those is a
   deliberate under-reach, and each is where the next gain is:
   * **More than one clause per method.** `CatchClauses` refuses at two, so `ExceptionEdges` attaches one.
     Lifting the emitter's limit is the precondition for lifting the attacher's.
   * **A `try` of more than one block.** The range is known exactly; the try emitted from it is still the
     single block holding the last protected instruction, because a multi-block try must be contiguous in
     the CIL and that needs `LayoutOrder` partitioned into before-try / try / rest / handler.
   * **A conditional exit from a `try`.** Refused, because `leave` is unconditional. `brtrue X` where `X` is
     outside could become `brfalse next; leave X` — untried, and it is a real population.
   * **The 466 handlers still bigger than their 64-block bound.** The LSDA gives the pad's *start*, not the
     handler's end, so this is still structural. Do not widen the bound.

2. ~~A handler that is not closed (464).~~ **Done in round 5** — and the diagnosis was wrong: the handler
   set was closed by construction, and the real problem was that a `catch` which does not return reaches the
   whole method. What is left of that bucket is **466 methods where the handler region still exceeds its
   64-block bound**, plus **19 where a conditional branch leaves the handler** and has no `leave` spelling.
   The 466 want a proper end-of-handler, which is again the `.gcc_except_table` answer: the handler's extent
   is in the LSDA too.

3. ~~The throw that does not end its block (680).~~ **Done in round 4** — `SplitAtTheThrow`, undone
   wherever it bought nothing. Most of the 680 turned out to be cleanup pads with no `catch`, correctly
   refused; 167 were real clauses.

3b. **Trim the re-raise tail from a recovered handler.** *(Checked in round 7 and it does **not** close as a
   by-product: the LSDA gives the landing pad's start, not the handler's extent, so a genuine rethrow and a
   fall-out still look alike. `SegmentRuleEvaluator::Evaluate` falls out and shows it — `num = 0L; goto`
   rather than a throw — but that is the graph saying so, not the table.)* Original note: A handler currently ends with the C++
   `__cxa_end_catch` + re-raise, which exports as `throw new OutOfMemoryException();` after the program's own
   statements — see the caveat at the end of round 5. It is the same shape `Corpus::Using` has, where it is
   guarded by the cleared exception slot, so it may already be answered by `OnlyAskedIfItIsNull` where the
   guard survives and needs trimming where it does not. **Do not guess at this**: C#'s bare `throw;` is the
   correct recovery of a genuine rethrow, and telling a rethrow from a fall-out needs the handler's real
   extent, which is item 1. Deleting it blind would turn a rethrowing handler into a swallowing one.

4. **`finally` is not attempted at all**, and there is **no ground truth for it anywhere in the corpus**
   because `Corpus::Guarded`'s was deleted by clang. Written into `corpus/README.md` as the specified next
   shape, with the code: a `finally` that appends to a list the method then reads, so the optimiser must
   keep it and the oracle can see whether it ran. Two more shapes are specified there beside it — a
   `try`/`finally` with no `catch`, and **a `catch` that falls out instead of returning**, which is the
   single largest population in the game (466) and which the corpus contains no instance of.

5. **`Divide` and `Guarded` can never be closed.** See `corpus/BASELINE.md`, corrected this session, and
   `il2cpp-the-oracles-denominator-has-a-floor`. `full + WRONG` bottoms out at **2**, so from 14 there are
   twelve reachable, none of them in this family.

