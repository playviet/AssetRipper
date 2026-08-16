# Round log — the arithmetic that recovers whole and computes a different number

Worktree `agent-a019c3903782a2f1b`, versions 1.12.0–1.12.29, exports 630–659.
The oracle is the scorer: `scratchpad/o.sh <n>` (corpus export + `autodiff.py` + `corpusscore.py`).
No compilability scorer can see any of this — expect `full`/`commented`/`unmanaged` to sit still.

---

## 1.12.0 / export 630 — baseline, reproduced in this worktree

Reset the worktree from its stale `9fb59bdf5` to master `10cec4684`.

| | |
|---|---|
| oracle | **79 run / 54 same**, 65 `full`, 49 right, **16 whole-and-wrong** |
| the three shapes | `DivMagic` 22 → 1073741846 · `Shifts` 6442450872 → 2147483576 · `Weight` 0 → -305 |

Matches the stated baseline exactly.

### Diagnosis, all three, before writing any code

**`DivMagic` is not a missing magic-division fold.** The idiom is already recovered faithfully — the multiply,
the two shifts and the sign fixup are all there. What is missing is a **width**:

```
SMADDL X8, X0, X8, X31      ; a 64-bit product
UBFM   X8, X8, 0x20, 0x3F   ; lsr x8, #32 - the high word, as an UNSIGNED 64-bit number
ADD    W8, W8, W0           ; <- a w-form add. It reads the low word, so the high word is a SIGNED int here
SBFM   W12, W8, 0x2, 0x1F   ; asr w12, w8, #2
```

`ADD W8, W8, W0` lifted as a 64-bit add makes `num7` 4294967254 where it must be -42. Hand-checked at
`value = 96`: 4294967254 + 96 >> 2 = 1073741837, and 1073741837 + 9 + 0 = **1073741846** — the recovered
number to the digit. So the root is `il2cpp-a-w-register-write-is-a-truncation`, in the direction that
memory did not count: a w-form instruction **reading** a value wider than a word.

**`Weight` is a signed reading of an unsigned comparison.** clang turned the jump table into
`kind * 15 + 10` guarded by `cmp w0, #7` / `csel w0, w8, wzr, cc`. `CC` is *unsigned lower*; read as signed
it is `kind < 7`, which a negative `kind` passes — hence -305 = -21 * 15 + 10 where the source returns 0.

**`Shifts` is a 64-bit value truncated to 32.** `lsrv w8, w0, w1` zero-extends into `x8`, and
`add x8, x8, w8, uxtw #1` is a **64-bit** add of it; the recovery typed the whole chain `int`. Left for later —
see "specified but unbuilt".

### The size of the prize, measured before building (`probe2 wtrunc`, new)

97522 methods walked.

* **370 sites in 191 methods** where a w-form data-processing instruction reads a register whose last
  straight-line definition holds more than 32 bits.
* **All 370 descend from a widening multiply.** Nothing else in the binary leaves a value that wide for a
  w-form to read — so this population *is* magic division, exactly.
* By mnemonic: ADD 181, MADD 82, MSUB 71, MUL 13, SUB 12, CSEL 4, SUBS 2, one each of ORR/UBFM/ANDS/BFM/CMP.

And for the comparison half (`probe2 carry`, fixed — `CMP`/`CMN`/`TST` were missing from `SetsFlags`, so
every comparison written as a comparison was invisible to it):

* **CMP + CS 16142 · CMP + LS 7415 · CMP + CC 5371 · CMP + HI 2016 = 30944** unsigned comparisons in the
  game, every one of them currently lowered to a signed one.

---

## 1.12.1 / export 631 — the truncation, and half the comparison

**Files and functions**

| file | what |
|---|---|
| `Cpp2IL.Core/InstructionSets/NewArmV8InstructionSet.Fork.cs` | **new** `WordWidth` (Reset/Note/Narrowed/Widest/Index/IsWidening/Truncates) and **new** `RecordUnsignedComparison` |
| `Cpp2IL.Core/InstructionSets/NewArmV8InstructionSet.cs` | one-line hooks: `WordWidth.Reset()`, `WordWidth.Note()`, a `NarrowedOperand` local, `RecordUnsignedComparison` after `CMP`'s `RecordComparison` |
| `scratchpad/probe2/Program.cs` | new `wtrunc` mode; `CMP`/`CMN`/`TST` added to `carry`'s `SetsFlags` |

**Two aliasing bugs caught in `probe2 isil` before spending an export** — both temps reused one register
name, and single assignment form then resolved *both* reads of an instruction to the second definition:
`add w9, w9, w11` came out as `TRUNC + TRUNC`, which would have broken `value / 10` and `value / 100`, both
of which were right by accident before. Named per operand (`TRUNC1`/`TRUNC2`, `CARVL`/`CARVR`).

| | 630 | 631 |
|---|---|---|
| oracle run / same | 79 / 54 | 79 / **55** |
| `full` + WRONG | 16 | **15** |
| `full` + right | 49 | **50** |
| `DivMagic` | 1073741846 | **22 — right** |
| `Weight` | -305 | -305 (still) |

`DivMagic` now reads exactly as the source computes: `int num10 = (int)num7 + value;` and the
`//IL_012d: Expected I4, but got I8` note is gone with it.

**Falsifiers, both checked and both unchanged:** `Corpus::Modulo` — a division by a **non-constant** — lifts
to `Divide`/`Multiply`/`Subtract` with no truncation inserted anywhere. `Corpus::WrapLevel` — a genuine
widening multiply used for `% 20` — takes one truncation on a quotient that provably fits in 32 bits, and
stays `right`.

Only two corpus methods take a `TRUNC` at all, which matches the census: this is a narrow, exactly-bounded
family, not a flood.

**Why `Weight` did not move.** The lifter half landed — the ISIL reads
`CheckLess Z, CARVL (System.UInt32), 7` — but the export says `(int)num2 >= 7`. The IL stack carries no
unsignedness: `uint` and `int` are both int32 on it, and which comparison is meant is the *instruction*.
`IlGenerator` emits `Clt`/`Cgt` unconditionally, so the conversion was faithfully undone by a cast on the
way back out. Half a fix is not a fix.

## 1.12.2 / export 632 — the emission half of the unsigned comparison

`Cpp2IL.Core/IlGenerator.Fork.cs`: **new** `ComparesUnsigned(left, right)` — true when a value operand is an
unsigned integer and no value operand is a signed one; a constant is neutral, since it is loaded at the other
side's width and reads the same either way while it is non-negative.
`Cpp2IL.Core/IlGenerator.cs`: one `var unsignedOrder = ...` and the four ordered comparisons pick
`Clt_Un`/`Cgt_Un` instead of `Clt`/`Cgt`.

| | 631 | 632 |
|---|---|---|
| oracle run / same | 79 / 55 | 79 / **56** |
| `full` + WRONG | 15 | **14** |
| `full` + right | 50 | **51** |
| `Weight` | -305 | **0 — right** |

`Weight` reads `uint num2 = (uint)kind;` / `return (int)((num2 >= 7) ? 0L : num3);`.

## 1.12.3 / export 633 — the baseline, re-measured in this build

Both changes put behind `WORDWIDTH_OFF=1` / `UNSIGNEDCMP_OFF=1` so the *same build* can be measured with and
without them. Export 633 is that build with both off, and it reproduces the stated master baseline **to the
number**: compare2 decompiled-only 2561/2815 = 91.0%, commented 363, unmanaged 315, notfound 38, indirect 18;
cfscore 609 full / 6 partial, 91 of 96 files clean; allscore 2121/2326 = 91.2%; decisions 1326/1382;
roundtrip whole 1044; genfail 1.

So the game numbers at 631 are a real regression of mine, not a scorer difference:

| | 633 (both off) | 631 (both on) |
|---|---|---|
| compare2 full (decompiled only) | 2561 | 2549 (**-12**) |
| commented | 363 | 384 (**+21**) |
| unmanaged | 315 | 335 (**+20**) |
| cfscore full / partial | 609 / 6 | 604 / 11 |
| allscore | 2121 (91.2%) | 2112 (90.8%) |
| decisions | 1326 | 1326 |
| roundtrip whole | 1044 | 1043 |

Exports 634 (`UNSIGNEDCMP_OFF=1`, word width only) and 635 (`WORDWIDTH_OFF=1`, unsigned only) attribute it.

## `Shifts` — measured and declined

`add x8, x8, w8, uxtw #1` is a **64-bit** add of a word the recovery kept 32 bits wide. The fix is to
materialise the `uxtw`/`sxtw` extend as a widening conversion, which `ExtendedTo` deliberately does not do:
its remark says those two are how an index is widened inside an address. `probe2 xextend` (new) puts a number
on that remark — **12400 sites in the game**, and the shape of what my "feeds an address" heuristic called
*not* an address (`ADD X8, X8, W9, 0x4, SXTW`, a stride-16 element) says the heuristic is far too weak and
almost all of them are addressing. A 12400-site change to the addressing path, against one corpus shape,
with `il2cpp-the-subscript-in-the-addressing-mode` downstream of it, is not a trade worth making here.
Recorded as a measured negative rather than attempted.

## 1.12.3 / exports 634, 635 — the attribution

| | 633 both off | 634 word width only | 635 unsigned only | 631 both |
|---|---|---|---|---|
| compare2 full | 2561 | **2561** | 2549 | 2549 |
| commented | 363 | **363** | 384 | 384 |
| unmanaged | 315 | **315** | 335 | 335 |
| cfscore full / partial | 609 / 6 | **609 / 6** | 604 / 11 | 604 / 11 |
| files clean | 91/96 | **91/96** | 90/96 | 90/96 |
| allscore | 2121 | **2121** | 2112 | 2112 |
| decisions | 1326 | **1326** | 1326 | 1326 |
| roundtrip whole | 1044 | **1044** | 1043 | 1043 |

**`WordWidth` is free — byte-identical to the baseline on every game scorer — and the whole -12 is
`RecordUnsignedComparison`.** Which is what the file diff said before the numbers did: 66 files change
between 633 and 631, `BoardController.cs` alone gaining 243 comment lines, and its damage is one local going
`int num = default(int)` → `object obj = default(object)` with `(int)obj` in every array subscript below it.
Nothing about that is unsigned arithmetic: the two conversion `Move`s at **every** w-form `cmp`, wanted or
not, moved every IL offset in the method (`IL_07eb` → `IL_07e5`) and one type inference fell out differently.

## 1.12.4 / exports 636, 637 — the comparison as a marker instead of two instructions

`RecordUnsignedComparison` deleted. `Cpp2IL.Core/Analysis/UnsignedComparison.cs` (**new**, the fork's own
file) is a `ConditionalWeakTable` beside the instruction, exactly as `LogicalShift` is; the lifter marks the
comparison where `ReadsCarry(condition)` is true — the four sites where a condition becomes a relational
opcode, one line each — and `IlGenerator` reads the mark. **It emits nothing.** Three things it fixes at once:

* no instructions added anywhere, so no offsets move and no type inference is perturbed;
* it states the **64-bit** case too, which the `uint` conversion could not (`clt.un` on two int64s is the
  unsigned 64-bit comparison, and 16142 of the game's carry comparisons are `CMP X`);
* it is the exact question, because the reading is a property of the **condition**, not of the operands.

| | 630 | 636 |
|---|---|---|
| oracle run / same | 79 / 54 | 79 / **56** |
| `full` + WRONG | 16 | **14** |
| `full` + right | 49 | **51** |
| corpus verdicts changed | — | `DivMagic` and `Weight`, both DIFFERS → agrees, **and nothing else** |

`Weight` now reads `return (int)(((uint)kind >= 7u) ? 0L : num);` — and the two scratch locals the
conversion form left behind are gone with it.
