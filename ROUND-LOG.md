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

## Specified but unbuilt

Written down so the next session starts where this one stopped rather than re-deriving it.

1. **The `try` range needs `.gcc_except_table`.** Everything this pass guesses at, the binary states. The
   LSDA's call-site table maps a PC range to a landing pad and an action record; that *is* the `try`, exactly,
   including every call inside it that can throw. Reaching it means walking `.eh_frame_hdr` to the FDE for the
   function, then its LSDA pointer, then decoding the call-site and action tables. It closes the 2372 blocks
   whose pad clang laid elsewhere, and it turns the 464 open handlers from a guess into a fact. Note the
   LSDA's *type* filters are useless here — il2cpp throws one C++ type, `Il2CppExceptionWrapper`, and does the
   managed dispatch itself in the pad — so the caught type still has to come from the `Il2CppClass<T>`
   operand, which this pass already reads correctly.

2. **A handler that is not closed (464).** Needs `ret` and `br` **out of the try** rewritten to `leave`, not
   just those in the handler, plus a layout that keeps a multi-block try contiguous. `AddCatchClauses`
   already does the handler half and the other half is the same shape.

3. **The throw that does not end its block (680).** `MergeCallBlocks` runs after the throw rewrite and leaves
   the `Throw` mid-block, so the pad's instructions and the guarded block's are in one list and position
   cannot tell them apart. Splitting the block at the `Throw` before `CatchClauses` runs would hand all 680
   to the recognition that already exists. `Corpus::Using` is this shape.

4. **`finally` is not attempted at all.** `Corpus::Guarded`'s would have been the test and clang deleted it,
   so there is currently **no ground truth for `finally` anywhere in the corpus**. Adding a shape whose
   `finally` has a side effect the optimiser cannot delete — appending to a list, not multiplying a local —
   is the cheapest useful thing anyone could do to this corpus.

5. **`Divide` and `Guarded` can never be closed.** See `corpus/BASELINE.md`, corrected this session, and
   `il2cpp-the-oracles-denominator-has-a-floor`. `full + WRONG` bottoms out at **2**, so from 14 there are
   twelve reachable, none of them in this family.

