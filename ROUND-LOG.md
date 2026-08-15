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
