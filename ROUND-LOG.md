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

| | 633 baseline | 637 (fast, both on) | 638 (full, both on) |
|---|---|---|---|
| compare2 full (decompiled only) | 2561 | **2561** | **2561** |
| commented / unmanaged | 363 / 315 | **363 / 315** | **363 / 315** |
| notfound / indirect | 38 / 18 | **38 / 18** | **38 / 18** |
| cfscore full / partial | 609 / 6 | **609 / 6** | **609 / 6** |
| files clean | 91/96 | **91/96** | **91/96** |
| allscore | 2121 (91.2%) | **2121** | **2121** |
| decisions | 1326/1382 | **1326** | **1326** |
| roundtrip whole | 1044 | **1044** | **1044** |
| genfail | 0 | **0** | **0** |
| Unity gate | 12 CS7069 (floor) | — | **12 CS7069** |

**Kept.** Every compilability measure identical, the corpus oracle two shapes better, and the Unity gate on
its floor. 80 game files change text and none changes score — which is what a correctness fix looks like
here. The one worth naming: `CFramework/Utility::TimeFormatMS` went from
`long num5 = (long)((ulong)(0L + num4) >> 32) + (long)num3;` to
`int num5 = (int)((ulong)(0L + num4) >> 32) + num3;`, so it computes its hours right; and 800 `(uint)` casts
appear where 28 did, every one an unsigned comparison the export used to state as a signed one.

---

## Specified but unbuilt

* **`Shifts`.** Declined with a measurement, above. The right place for it is not the lifter: deciding
  whether `add x8, x8, w9, sxtw #2` is arithmetic or an element address needs the **base's type**, which the
  lifter does not have and a later ISIL pass does. That is a design change, not a round.
* **`WordWidth` at the last 8 sites.** `CSEL` (4), `UBFM`, `SBFM`, `BFM`, `CMP` and `ORR`'s operand 1 (1
  each) are not hooked; 362 of the 370 are. Each needs its own one-line call.
* **`CCMP`.** `ConditionalCompare.Guard` cannot mark its comparison because its `emit` returns `void`, so a
  carry condition folded into an `&&` chain is still read as signed. `CCMP` is 205 + 114 in the top 24 of
  `probe2 carry` and both of those are `EQ`/`NE`, which are unaffected; the carry-reading ones did not
  reach the top 24. Changing that signature is allowed — `ConditionalCompare.cs` is the fork's own file.
* **`ComparesUnsigned` on its own.** The operand-type route into `AsksUnsigned` was measured only as part of
  a build where the marker also fires, so its independent worth is unknown. It is monotone and cost nothing.

---

## 1.12.5 / exports 639, 640 — `SharedPick` and `ValuePick`: two shapes, one root

**Files and functions**

| file | what |
|---|---|
| `Cpp2IL.Core/Analysis/SharedBody.cs` | **new** — `IsASpecialisation`, `Specialised`, `IsAStandIn` |
| `Cpp2IL.Core/Analysis/LocalVariables.Fork.cs` | one guard at the top of `SeedSharedReturnBuffer` |
| `Cpp2IL.Core/InstructionSets/NewArmV8InstructionSet.Fork.cs` | one clause on `stepped` in `AddRuntimeMethodOperand` |
| `scratchpad/probe2/Program.cs` | `sharedbody`: `__Il2CppFullySharedGenericStructType` added to the stand-in list, and a count of the mis-shared bodies that return a bare type parameter |

**Zero upstream lines.** Both call sites are already fork files.

**The diagnosis.** `Pick<T>` recovered its null check, both magic divisions and its bounds check, and then
returned `default(T)`. The ISIL said why in one line: `Return returnBuffer @ X2 (T)`, `regs=[X0,X1,X3]`.
AssetRipper had applied the shared-generic **indirect return** convention — a shared body cannot know how wide
a `T` is, so it answers through a hidden buffer and the `MethodInfo` moves along a register. But
`probe2 sharedbody` says `Corpus::Pick @ 7B6330 is <System.Int32>`: il2cpp shares nothing for an ordinary
value type, so what is registered against the definition is the `int` specialisation's code, and that returns
in `w0` like anything else (`ldr w0, [x8 + 0x20]` / `ret`). The buffer was a register nothing writes.

**The census, before building.** 1604 generic definitions have a body; **605 are a specialisation's under an
open name, and 85 of those return a bare type parameter**. In `Assembly-CSharp` the mis-shared family is three
(`SoftMask::Set`, `BaseTrackingSaveData::Set`, `SetPropertyUtility::SetStruct`) and **none returns one** — so
the prediction was that every game scorer would be flat. It was.

| | 636 | 639 / 640 |
|---|---|---|
| oracle run / same | 79 / 56 | 79 / **58** |
| `full` + WRONG | 14 | **12** |
| `full` + right | 51 | **53** |
| corpus verdicts changed | — | `SharedPick`, `ValuePick`, both DIFFERS → agrees, **and nothing else** |
| compare2 full / commented / unmanaged | 2561 / 363 / 315 | **2561 / 363 / 315** |
| cfscore full / partial · files | 609 / 6 · 91/96 | **609 / 6 · 91/96** |
| allscore · decisions · roundtrip whole | 2121 · 1326 · 1044 | **2121 · 1326 · 1044** |
| genfail | 0 | **0** |

`Pick<T>` now reads `result = items[num4]; return result;` — semantically the source. **Kept.**

---

## 1.12.6 / export 642, and 1.12.7 / exports 643, 644 — `NullableSum`

**Files and functions**

| file | what |
|---|---|
| `Cpp2IL.Core/Analysis/StructSlotFields.cs` | `Structure` lets `System.Nullable\`1` past the namespace guard; new `IsANullable` |
| `Cpp2IL.Core/Analysis/MetadataResolver.Fork.cs` | new `AsInstantiated`, used by `FieldOfOpenGeneric` and `SpanningField`; and one clause on `FieldOfStructValue`'s records-from-the-value test |
| `Cpp2IL.Core/Analysis/InaccessibleFieldRecovery.cs` | `Accessor` answers `GetValueOrDefault()` for a read of `Nullable\`1.value` |

**The diagnosis, in three layers, each hiding the next.** `NullableSum([8,1,7])` returned 7 — the last
element, not the sum. The loop read `int num3 = default(int)` as its accumulator and never assigned it:

```
LDR W8, [X22, X20, LSL #2]   ; values[i]
LDR W9, [X31 + 0xC]          ; <- total.GetValueOrDefault(), four bytes into the Nullable at [sp+8]
ADD W1, W9, W8
BL  Nullable`1<int>..ctor    ; total = that
```

`SLOTFIELD_TRACE` named the slots in one line — `stack_-24 (Int32)` sitting inside
`stackaddr_-28 (Nullable\`1<Int32>)` — so `StructSlotFields` had everything it needed and refused. Three
refusals in a row, each only visible once the one above it was lifted:

1. **`Structure` excludes namespace `System`.** A cheap way of keeping `Span`, `ValueTuple` and the rest of
   the BCL out; it takes `Nullable` with them, and a `Nullable<T>` on the stack is exactly what the pass is
   for.
2. **`SizeOf` gives a bare type parameter the pointer size.** Right in a *shared* body — that is the
   convention — and wrong for a real instantiation. `hasValue` then `value : T` sized at eight aligns
   `value` to offset 8 where the machine puts it at 4, so the walk ran past the offset asked for.
   `List<int>.Enumerator._current` lands at 16 either way, which is why this went unnoticed.
3. **`FieldOfStructValue`'s "records from the value" test fires on a generic definition**, where every field
   reports offset nought because *nothing is recorded* — not because the type is laid out from zero. It then
   found no field at the distance and answered null without ever reaching the walk that exists for this case.

**And then the fix was still wrong, in the direction that matters.** With the slot named, the read came out
`num3.Value` and `NullableSum` threw `InvalidOperationException` — a marker replaced by a *worse* lie.
`Nullable<T>.Value` throws where `hasValue` is false; the field read the machine performs is exactly
`GetValueOrDefault()`. Same conclusion `NullablePackedCompare` reached for the two-byte reading.

| | 639 | 641 (layers only) | 643 / 644 |
|---|---|---|---|
| oracle run / same | 79 / 58 | 79 / 58 | 79 / **59** |
| `full` + WRONG | 12 | 13 | **12** |
| `full` + right | 53 | 53 | **54** |
| `partial` + WRONG | 9 | 8 | **8** |
| `NullableSum` | 7 | !InvalidOperationException | **16 — right** |
| compare2 full / unmanaged | 2561 / 315 | 2561 / 315 | **2561 / 315** |
| commented | 363 | 364 | **364** |
| cfscore · allscore · decisions · roundtrip · genfail | 609/6 · 2121 · 1326 · 1044 · 0 | — | **all level** |

Only `NullableSum` changes verdict. `commented` +1 on the game is the whole cost. **Kept.**

---

## 1.12.8 / exports 645, 646 — `NullableChain`: the other half of the packed pair

`Cpp2IL.Core/Analysis/NullablePackedCompare.cs` — new `AskHasValue` and `HasValue`, called from `Run`.
`Cpp2IL.Core/Analysis/FieldReadSinking.cs` — a `FIELDSINK_OFF=1` gate, added to falsify a wrong suspect
(see the `Steps` note below); the pass itself is unchanged.

The sibling of the shape `NullablePackedCompare` already handled. Where `T` is wider than a byte the compiler
cannot ask both questions with one compare, so it loads the whole `Nullable` and masks the field it wants:

```
LDR  X8, [X31 + 0x8]     ; the whole eight bytes
ANDS X31, X8, 0xFF       ; the low byte, result discarded
B.EQ ...                 ; so: if (!maybe.HasValue)
```

`hasValue` is declared first and is one byte, so the low byte **is** `HasValue`. Written out as an `and` it
was `((_003F?)num & 0xFFL) != 0`, which the language has no operator for, and `NullableChain` lost its whole
`if` body and returned "none" for every input.

| | 643 | 645 / 646 |
|---|---|---|
| oracle run / same | 79 / 59 | 79 / **60** |
| `full` + WRONG | 12 | **12** |
| `full` + right | 54 | **55** |
| `partial` + WRONG | 8 | **7** |
| `NullableChain` | none | **v160 — right** |
| compare2 full / unmanaged | 2561 / 315 | **2561 / 315** |
| commented | 364 | 369 (**+5**) |
| cfscore · allscore · decisions · roundtrip · genfail | 609/6 · 2121 · 1326 · 1044 · 0 | **all level** |
| **livecount** | — | **live +11, branches +6** |

**The +5 commented is the cascade running the other way and `livecount` is what says so.** Two files move.
`Pool.cs` had the whole `if (((_003F?)localPos & 0xFFL) != 0) { … } else { … }` commented out as one
statement; it is now `if (localPos.HasValue)` with both arms live and two of its inner statements commented
individually — the next defect down the same chain, a `>> 32` on the Nullable's value. More live code, more
branches, more comments. Chasing `commented` alone would have reverted this.

**Kept.**

---

## 1.12.9 / exports 647, 648, 649 — `Steps` and `SumSteps`: a read carried past its own store

**Files and functions**

| file | what |
|---|---|
| `Cpp2IL.Core/Analysis/StoreTarget.cs` | **new** — `Of`, `IsTheSameField` |
| `Cpp2IL.Core/Analysis/Simplifier.Fork.cs` | `Invalidates` asks `StoreTarget.Of`, and a raw memory store now invalidates a field read |
| `Cpp2IL.Core/Analysis/FieldReadSinking.cs` | `Harmless` asks `StoreTarget.Of`, and compares fields through `IsTheSameField` |

**The bug is one line of upstream, and it disarmed two guards at once.**

```csharp
public static bool IsConstantValue(object operand) => operand switch
{
    Register or StackOffset or LocalVariable => false,
    MemoryOperand memory => memory.IsConstant,
    _ => true                       // <- a FieldReference lands here
};
// Instruction.Destination:  return IsConstantValue(Operands[0]) ? null : Operands[0];
```

So `Instruction.Destination` is **null** for `Move this.<>1__state, -1` and for every other store into a
field. `Simplifier.Invalidates` and `FieldReadSinking.Harmless` both refuse to carry a field read past a
write to that field, and **both asked `instruction.Destination`** — so neither had ever fired for the
commonest shape either was written for. `IsConstantValue` itself is not touched: it drives `Sources`, dead
code elimination and the stack analysis.

`Corpus+<Steps>d__73::MoveNext` is the clean statement. The state machine loads `<>1__state` at entry, stores
-1 over it, and then compares the loop counter — which the compiler keeps in the very register the load left
it in, because at state 0 that register already holds 0. Carried past the store, the comparison read the
field again and got -1, so the first element of every `yield` loop came out `(-1) * (-1)` rather than
`0 * 0`.

**Diagnosed in four probe rebuilds, no exports**: `FIELDSINK_OFF=1` inert (clears that pass of acting
*first*), `SsaSimplifier.IsForwardable` already refuses a `FieldReference`, `FIELDFWD_OFF=1` on
`Invalidates` made the body right (conviction), and a four-line `SIMPTRACE` print showed `written=null` at
the store. Then with `Simplifier` fixed, `FIELDSINK_OFF` became decisive — the second carrier.

| | 645 / 646 | 649 (`STALEREAD_OFF`) | 647 / 648 |
|---|---|---|---|
| oracle run / same | 79 / 60 | — | 79 / **62** |
| `full` + WRONG | 12 | — | **11** |
| `full` + right | 55 | — | **56** |
| `partial` + WRONG | 7 | — | **6** |
| compare2 full | 2561 | 2560 | **2560** |
| commented | 369 | 370 | 371 |
| unmanaged | 315 | 316 | 324 |
| cfscore full / partial | 609 / 6 | 608 / 7 | 607 / 8 |
| allscore · decisions · roundtrip · genfail | 2121 · 1326 · 1044 · 0 | 2120 · 1326 · 1044 · 0 | **2120 · 1326 · 1044 · 0** |
| **livecount vs 649** | — | — | **live +7, branches +1** |

`Steps` and `SumSteps` both go DIFFERS → agrees and nothing else moves.

**Kept, following correctness over compilability and saying so.** compare2 `full`, `allscore`, `decisions`
and `roundtrip` are level; `cfscore` loses one body and `unmanaged` gains eight — which is exactly the trade
RECOVERY.md names: *a read that becomes a marker is better than a read that quietly returns the wrong value*,
and here the old behaviour was reading a field **after it had been overwritten**. `livecount` says live code
went up, and the live lines that did leave are `_ = list.Count;` discards, not statements.
