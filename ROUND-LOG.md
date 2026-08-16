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

---

## 1.12.10 / exports 650, 651 — `mov Xd, #bitmask`, which Disarm renders as `INVALID`

`Cpp2IL.Core/InstructionSets/NewArmV8InstructionSet.Fork.cs` — new `MovedBitmask`, one line at the `MOV` case
in `NewArmV8InstructionSet.cs`. `scratchpad/probe2/Program.cs` — new `badoperand` mode.

`mov x9, #-4294967296` is `orr x9, xzr, #0xffffffff00000000`, the same bitmask encoding `LogicalImmediate`
already decodes for `and`/`orr`/`eor`. Disarm reports the mnemonic and then hands over an operand it cannot
represent — `Op1Kind` is `Register` and `Op1Reg` is `Arm64Register.INVALID` — so the lifter moves a **local
nothing ever assigns**, and everything computed from it is arithmetic on nothing.

**65 sites in 58 methods**, all `MOV`, counted with the new `badoperand` mode (distinct from `invalid`, which
counts words Disarm refuses outright — those are all NEON). `Buffer::MemoryCopy`, `MulticastDelegate::
CombineImpl` and `BigInteger::IsProbablePrime` among them.

`Corpus::Reversed` is the shape: counting an index **down** is done in the high half of a register —
`x10 = len << 32`, `x10 += x9` each turn with `x9 = -1 << 32`, and `asr #30` reads the index back already
scaled by four. The whole chain was built on the undefined local; it now reads
`long num2 = -4294967296L + (num << 32);`.

| | 648 | 651 |
|---|---|---|
| oracle run / same | 79 / 62 | 79 / 62 |
| every game scorer | 2560 · 371 · 324 · 607/8 · 90/96 · 2120 · 1326 · 1044 · 0 | **identical** |
| livecount | — | live -1, branches 0 |

**Kept, and honestly: no scorer moved.** It is not inert — the ISIL goes from an undefined local to the right
constant at 65 sites — but all 65 are in the substituted assemblies, so nothing scoreable sees them. Same
family as the six wrong-value decoder bugs `il2cpp-differential-test-the-disassembler` records.

### `Reversed` is now half-fixed, and the other half is specified

The index chain is right; the remaining fault is one step later. The read is
`[values + 0x20 + v70]` with **no scale**, where `v70 = x10 asr #30` is a **byte offset** (index × 4).
`IlGenerator.Fork.cs`'s array path returns `memory.Index` unchanged when `memory.Addend == elements`,
whatever `memory.Scale` says — so `values[num4]` is four times too big and throws.

The rule is exact: **an index the addressing mode does not scale is a byte offset, and the subscript is that
offset divided by the element width.** It cannot be done where it was found — that function returns operands,
not instructions, and a division has to be emitted — so it belongs in `ArrayAccessRecovery`, beside the other
subscript reconstructions. Not attempted here: the subscript path is the one that cost 12 game methods the
last time it was widened (`il2cpp-a-slot-inside-a-struct-is-its-field`), and it wants its own round with its
own before/after.

---

## 1.12.10 / export 652 — the full export and the Unity gate, again

Unity gate **12 CS7069, its floor**, unchanged from master. Full export scores identically to the fast one.

## Where it stands at 1.12.10

| | master (630) | now (650/651/652) |
|---|---|---|
| **oracle run / same** | 79 / **54** | 79 / **62** |
| **`full` + WRONG** | **16** | **11** |
| `full` + right | 49 | 56 |
| `partial` + WRONG | 9 | 6 |
| compare2 full (decompiled only) | 2561 | 2560 |
| commented / unmanaged | 363 / 315 | 371 / 324 |
| cfscore full / partial · files | 609 / 6 · 91/96 | 607 / 8 · 90/96 |
| allscore | 2121 (91.2%) | 2120 (91.1%) |
| decisions · roundtrip whole · genfail | 1326 · 1044 · 0 | **1326 · 1044 · 0** |
| Unity gate | 12 CS7069 | **12 CS7069** |

Ten corpus shapes fixed — `DivMagic`, `Weight`, `SharedPick`, `ValuePick`, `NullableSum`, `NullableChain`,
`Steps`, `SumSteps` by verdict, and `Reversed` half-recovered — for one compare2 body, two cfscore bodies and
nine `unmanaged` markers, all of them from the stale-read guard and all of them the trade RECOVERY.md names.

---

## The cfscore check — reading the two bodies against their originals

`cfscore` 609/6 → 607/8 at 1.12.9 was the first downward move of the run. `scratchpad/cfdiff.py` (new) names
which **methods** changed verdict, which cfscore's per-file table cannot: `BoardController::InitBoard` and
`PowerUpBuyPopup::Show`, both `full → partial`. Read against `game-hub/Assets/AAA/CF`:

### `PowerUpBuyPopup::Show` — **not worse.** Every effect of the original is present and in order.

`base.Show` · the `initParams != null && Length >= 3` guard · all three assignments · `definition` null check
with icon / `SetNativeSize` / price / `interactable` / `OnCoinChanged` · `Time.timeScale = 0` · `GetScreen` ·
`UpdateProperties` · `TrackUiInteraction` · `PauseBgm` · `DisableBoardAction`. All there, both before and
after.

What changed is *shape*, and in the right direction:

```csharp
_abilitySlotUI = (BaseAbilitySlotUI)initParams[0];   // 646: re-indexes the array
_abilitySlotUI = baseAbilitySlotUI;                  // 651: the local the machine actually stores
```

The new markers are `Unmanaged memory load: [… Il2CppClass<BaseAbilitySlotUI>+130]` and `+C8` — the **runtime
type-check machinery** of the `isinst`, which is dead either way and was previously folded into an expression
that got eliminated. Marker count up; program unchanged.

### `BoardController::InitBoard` — **genuinely worse, one statement.** And it is now fixed.

Original: `for (int i = 0; i < _targets.Count; i++) _targetColors[i] = _targets[i].color;`

```csharp
targetColors3[num5] = targetProgress.color;                     // 646 — right
_ = "Unmanaged memory load: [… (System.Object)+10]";            // 651 — every colour written as zero
object obj = 0;  targetColors3[num5] = (ECellColor)obj;
```

**The cause was not the guard's logic; it was a type the guard stopped propagating by accident.** While copy
propagation folded `_targets` into the call, the receiver carried the field's real type. Once the copy
legitimately survives, the local keeps the **generic-sharing stand-in** `List<object>` — so `get_Item`
answers `object` and a field at `+0x10` has nothing to resolve against.

## 1.12.11 / 1.12.12 — exports 653–656: the three links that fix it

| file | what |
|---|---|
| `Cpp2IL.Core/Analysis/StandInCopyType.cs` | new `SharperInstantiation` — a local whose every definition is a copy does not keep a stand-in **instantiation** when the copy says the real one |
| `Cpp2IL.Core/Analysis/GenericSharingRecovery.cs` | also retypes the call's **result**, not only its receiver |
| `Cpp2IL.Core/Analysis/ForkPipeline.cs` | `GenericSharingRecovery` and `MetadataResolver.ResolveFieldOffsets` re-run after `StandInEdgeCopy`, for the same reason the array passes already are |
| `Cpp2IL.Core/Analysis/SharedBody.cs` | `IsAStandIn` made public and reused |

Each link alone is inert — measured. `List<object>` → `List<TargetProgress>` alone still calls
`List<object>.get_Item`; renaming the callee alone still answers `object`; retyping the result alone leaves
`[v131 + 0x10]` raw. All three together give `Move v729, v131.color`.

`SharperInstantiation` is exact, not a heuristic: same generic definition, argument for argument, differing
only where the stand-in has a stand-in and the copy has something real — and `System.Object` standing where a
**value type** is refused, since a value-type instantiation gets a body of its own and is never shared.

| | 651 | 656 |
|---|---|---|
| oracle run / same · `full`+WRONG | 79 / 62 · 11 | 79 / 62 · 11 |
| compare2 full | 2560 | **2561** — master's number |
| allscore | 2120 | **2121** — master's number |
| cfscore full / partial | 607 / 8 | **608 / 7** |
| unmanaged | 324 | **323** |
| commented · decisions · roundtrip · genfail | 371 · 1326 · 1044 · 0 | 371 · 1326 · 1044 · 0 |
| livecount | — | live +1 |
| verdicts changed | — | `InitBoard` **partial → full**, and nothing else |

**Kept.** The whole `compare2`/`allscore` cost of the stale-read guard is repaid; what is left against master
is `cfscore` 609→608 and one file off the clean list, both of them `PowerUpBuyPopup::Show`, which the reading
above shows is not worse.

---

## 1.12.13 — `ClearAndSet` and `Describe`: one root, diagnosed and **not** fixed

Both are the same thing, and `PIPETRACE` (new, in `ForkPipeline`) found it in one run by printing the body at
every hook:

```
PIPE AfterStackAnalysis          PIPE AfterTypesAndFieldsResolved
  Move X0, stackaddr_-20           Nop
  Move stack_-20, 0                Nop
  Move stack_-14, V0               Move v2 (Quad), v3 @ stackaddr_-20 (Quad)
  Call Quad::ToString, X0, X0      Call Quad::ToString, …, v2
```

**Every store into the slot is gone by the second hook.** `DeadCodeEliminator`'s own remark says "stores have
a memory or field destination and are never dead" — true of every store *except* a stack slot's, which has a
**local** destination, so nothing reads it and it is removed. `Corpus::ClearAndSet` loses `q.A = x` and
`Corpus::Describe` loses both operands of `colour.ToString() + "=" + (int)colour`.

**Two fixes were built and both reverted.**

`StructSlotFields` rewriting the *write* as well as the read: **inert** — the stores are gone long before
that pass runs. Its remark now says so, so nobody rebuilds it.

`SlotWrittenThenAddressed` — rewriting `Move stack_A, v` to `Move stackaddr_A, v` at `AfterStackAnalysis`,
the only hook before the first DCE, plus re-taking the address immediately before the call. This got
`Describe` from a silently wrong body to a **commented** one (`full`+WRONG 11 → 10, `partial`+WRONG 6 → 7,
`79 run / 62 same` unchanged) — the preferred direction, but no shape recovered, and it inserts instructions
into every method with an addressed slot. Reverted.

**Why the simple version cannot work, which is the finding worth keeping.** The compiler takes the address
**before** it fills the slot:

```
ADD X0, X31, 0x0     ; &quad          -> Move X0, stackaddr_-20
STR X31, [X31]       ; clear it       -> Move stackaddr_-20, 0
STR S0, [X31 + 0xC]  ; q.A = x
BL  Quad::ToString
```

A slot is modelled as a **variable**, not as memory (`il2cpp-the-slot-address-is-the-slot`, and the
spill/reload that model exists to keep). So single assignment form gives the store a new version and leaves
the call reading the version live where the address was taken — the empty one. Making the store survive is
not enough; the call has to read the *latest* version, which is what a pointer means and what a variable
cannot say.

## 1.12.14 / exports 658, 659 — `AsOrNull`: link one of three

`Cpp2IL.Core/Analysis/BoxedIsAnObject.cs` (**new**), one call beside `StandInCopyType` in `ForkPipeline`.

`object o = flag ? (object)"text" : (object)7;` merges a string with a boxed `int` in one register. The
string arm types it `System.String` first, that type reaches the box's result through the copies, and
`SetTypeIfUnknown` will not revisit one — so the boxed seven is written `string text2 = (string)(object)7;`
and throws on a cast the source never had. The `isinst` below it is recovered perfectly and never reached.

The static type of a box is `object`. `BoxedIsAnObject` says so, and **only** where the result claims a
reference type a boxed value can never be — `object`, `ValueType`, `Enum`, an interface and a generic
parameter are all left alone, because `il2cpp-a-cast-that-is-read-through-is-an-unbox` depends on them.

| | 655/656 | 658/659 |
|---|---|---|
| oracle · every game scorer | 62 same · 2561 · 371 · 323 · 608/7 · 2121 · 1326 · 1044 · 0 | **identical** |
| game text | — | one line, `JsonExtension.cs`: `JsonSerializer value = (JsonSerializer)(object)(T)num2;` → `object value = (T)num2;` (commented either way) |

**Kept, and it buys nothing measurable today.** The ISIL moves (`v41` `String` → `Object`) and the one line
that changed is better. It is gated `BOXEDOBJECT_OFF=1`.

### The other two links, named

1. **The merge.** `v44` is still `System.String`, so `Move v44 (String), v41 (Object)` is emitted as a cast
   and `(string)(object)7` survives. A local that receives a copy from an `object` cannot be a more derived
   reference type; the merge has to widen. Broad rule, wants its own round.
2. **The `isinst` result is not what the next statement reads.**
   `Call is_inst, instance68 @ ISINST (String), v44, typeof(String)` then
   `Call String.ToUpperInvariant, …, v44` — the receiver is the *pre-call* `x0`, not the answer. Harmless
   while both are the same object; fatal once `v44` is widened to `object`. So links 1 and 2 must land
   together.

## Where it stands at 1.12.14

| | master (630) | now |
|---|---|---|
| **oracle run / same** | 79 / **54** | 79 / **62** |
| **`full` + WRONG** | **16** | **11** |
| compare2 full · allscore | 2561 · 2121 | **2561 · 2121** |
| cfscore full / partial · files | 609 / 6 · 91/96 | 608 / 7 · 90/96 |
| commented · unmanaged | 363 · 315 | 371 · 323 |
| decisions · roundtrip · genfail · Unity | 1326 · 1044 · 0 · 12 CS7069 | **1326 · 1044 · 0 · 12 CS7069** |

The only body still behind master is `PowerUpBuyPopup::Show`, read against its original above and shown not
to be worse.

## 1.12.14 / export 659 re-run as the FULL export — the Unity gate

Unity gate **12 CS7069, its floor**, unchanged from master. Full export scores identically to fast:
compare2 full 2561, commented 371, unmanaged 323, cfscore 608/7, allscore 2121, decisions 1326,
roundtrip whole 1044, genfail 0.

Export numbers 630–659 are now all used.

---
---

# Ranges 1.13.0–1.13.29, exports 660–689

## 1.13.0 / exports 660, 661 — `AsOrNull`: links two and three, landed together

| file | what |
|---|---|
| `Cpp2IL.Core/Analysis/BoxedIsAnObject.cs` | new `WidenWhatCarriesIt` — link two |
| `Cpp2IL.Core/Analysis/TypeTestNarrowing.cs` | **new** — link three; `Run`, `SuccessOf` |
| `Cpp2IL.Core/Analysis/ExactTypeTestRecovery.cs`, `InlinedTypeTestRecovery.cs` | one call each, where the `isinst` is emitted |

**Link two — the merge.** Retyping the box alone changed nothing the output could show: the phi it feeds still
said `System.String`, so the copy into it was written as a cast and `(string)(object)7` survived exactly as it
was. A phi handed a boxed value cannot be a type that value can never have. Bounded to the **copy closure of
the boxes this pass just retyped**, not stated over every phi that sees an `object`: widening is safe for the
language and expensive for the recovery — a read through an `object` has no field to resolve against — so it
is only done where the type that is there is known to be wrong.

**Link three — the test's answer is what the next statement reads.** Both type-test passes put back the
`isinst` and give its answer a local; neither said what that answer is *for*. The machine keeps the object in
the same register either way, so `String.ToUpperInvariant` was called on the **object**. Invisible while the
tested value happened to be typed as the target; fatal the moment link two types it honestly — which is why
they had to land together.

`TypeTestNarrowing` replaces reads of the tested value with the narrowed one only in a region **entered
nowhere else**: the success successor, then any block every one of whose predecessors is already in the
region. That is dominance computed as it is needed rather than out of `DominatorInfo`, which was built before
the guard remover changed the edges.

| | 658/659 | 660/661 |
|---|---|---|
| oracle run / same | 79 / 62 | 79 / **63** |
| `full` + WRONG | 11 | **10** |
| `full` + right | 56 | **57** |
| `AsOrNull` | !InvalidCastException | **notstring — right** |
| every game scorer | 2561 · 371 · 323 · 608/7 · 90/96 · 2121 · 1326 · 1044 · 0 | **identical** |

`AsOrNull` is the only verdict that moves. **Kept.**

## 1.13.1 / exports 662, 663 — every event accessor was swapping a throwaway

`Cpp2IL.Core/Analysis/StaticStorageIsTheFirstField.cs` (**new**), one call in `BeforeUnusedLocalsAreDropped`.

`FieldAddressRecovery` already reads `Add v121, [klass + 0xB8], 8` as `&Type.someStatic` and writes
`ldsflda`, and its own remark names the caller it exists for: *"which is where
`Interlocked.CompareExchange(ref SomeEvent, ...)` gets the place it swaps. Every event accessor in the game is
that call."* **The first static field is at distance nought, so there is no addition at all** and that pass
never sees it. The storage local went straight into the call, the callee's signature retyped it
`System.Object&`, and the generator wrote:

```csharp
object location = default(object);                                    // this is `ref Corpus.m_Adjust`
object obj4 = Interlocked.CompareExchange(ref location, value2, obj);
```

The whole compare-exchange loop is recovered perfectly around it — the combine, the type test, the retry —
and the event is never written. Runs in `BeforeUnusedLocalsAreDropped`, found by `PIPETRACE`: at the last
hook the local has already been retyped `System.Object&` from the callee's signature and no longer says whose
storage it is.

**The observable, since no scorer can see this:**

| | 661 | 663 |
|---|---|---|
| `CompareExchange(ref location …)` — a throwaway | **10** | **0** |
| `CompareExchange(ref …Unsafe.As<…>(ref TheField) …)` | 50 | **60** |
| `Corpus::EventRoundTrip` | !NullReferenceException | **105** (original 1105) |
| oracle · every game scorer | 63 same · 2561 · 371 · 323 · 608/7 · 2121 · 1326 · 1044 · 0 | **identical** |

Ten event accessors in `Assembly-CSharp` were adding and removing handlers from a local nobody reads. **Kept**
— the corpus verdict does not move because a second, independent defect in `EventRoundTrip` remains, below.

### What is left in `EventRoundTrip`, and it is a rendering asymmetry

The ISIL is right: both `add_Adjust` and `remove_Adjust` are handed `<>c.<>9__86_0`, and the `+=` path stores
the new delegate into that cache before using it. But ILSpy folds the cache pattern back into a lambda only
where it sees the whole shape:

```csharp
Adjust += (int v) => v + 5;                    // folded - the recompiled assembly makes its OWN cache field
Adjust -= _003C_003Ec._003C_003E9__86_0;       // not folded - reads the ORIGINAL field, which is now null
```

So `Delegate.Remove(list, null)` removes nothing, `Adjust` stays non-null and the method answers 105 instead
of 1105. It is a real defect in the exported source, not a harness artefact: the two sites reference two
different fields once recompiled. The fix is to make the two sites render **the same way** — either fold both
or fold neither — and the checkable condition is "this cache field is also read in this method without the
pattern around it". Left specified; `CachedDelegateRecovery` is the owner.

## 1.13.2 / exports 664, 665 — `EventRoundTrip`: the fold that made two sites mean two fields

`Cpp2IL.Core/Analysis/CachedDelegateRecovery.cs` — new `ReadElsewhere`, one guard before `Replace`.

The pass makes a cached lambda's *use* name the field so the decompiler folds the whole shape back into a
lambda literal. The decompiler folds only what it can see - test, build, store, use - so a **second** read of
the same field elsewhere is left bare, and once the export is recompiled the two sites name two different
fields:

```csharp
Adjust += (int v) => v + 5;                    // folded: the new assembly makes its OWN cache field
Adjust -= _003C_003Ec._003C_003E9__86_0;       // not folded: the ORIGINAL field, which nothing now writes
```

`Delegate.Remove(list, null)` removes nothing. Declining the fold where the field is read more than once
leaves the local in place — an ordinary variable read from the field — and both sites name the one field
again. One read is the pattern's own test; anything beyond it is a use the fold cannot reach.

| | 663 | 665 |
|---|---|---|
| oracle run / same | 79 / 63 | 79 / **64** |
| `full` + WRONG | 10 | **9** |
| `full` + right | 57 | **58** |
| `EventRoundTrip` | 105 | **1105 — right** |
| **decisions** | 1326 | **1328** |
| **livecount** | — | **live +49, branches +15** |
| compare2 full | 2561 of 3255 | 2568 of **3262** |
| cfscore · allscore · commented · unmanaged · roundtrip · genfail | 608/7 · 2121 · 371 · 323 · 1044 · 0 | **all level** |
| `<>c.<>9__N` references in the export | 17 | 67 |

**Kept.** `decisions` and `livecount` both up, everything else level. The cost is legibility: 50 more places
show the compiler's cache instead of a lambda literal — about 25 methods where a cached lambda is used twice,
every one of which had the same latent two-fields bug. `compare2`'s denominator grows by the same 7 as its
`full`, so the percentage reads 91.0% → 90.9% while nothing got worse.

## 1.13.3 / exports 666, 667 — `Reversed`: the index the addressing mode does not scale

`Cpp2IL.Core/Analysis/UnscaledSubscript.cs` (**new**), one call after the two array passes are re-run.

The rule as specified: *an index the addressing mode does not scale is a byte offset, and the subscript is
that offset over the element width.* But **not** by emitting a division — by moving the scaling out of the
shift:

```
add x12, x19, x10, asr #30     ; values + (index * 4)      <- 32 - log2(stride)
```

`x >> 30` used as a byte offset with stride 4 **is** `x >> 32` used as a subscript with scale 4, exactly, and
it costs no instruction. That is also what keeps it narrow: it applies only where the index's **one**
definition is a right shift and the index has **one** use. The general case — an arbitrary byte offset —
would need the division, and the general case is where widening this path cost twelve game methods before.

| | 665 | 667 |
|---|---|---|
| oracle run / same | 79 / 64 | 79 / **65** |
| `full` + WRONG | 9 | **8** |
| `Reversed` | !IndexOutOfRangeException | **[7,1,5] — right** |
| every game scorer | 2568/3262 · 371 · 323 · 608/7 · 2121 · 1328 · 1044 · 0 | **identical** |
| files differing in `Assembly-CSharp` | — | **0** |

**Kept, and honestly: it does not fire in the game at all.** The count-down-in-the-high-half idiom is in the
corpus and in corlib; `Assembly-CSharp` has none of it. Zero cost, one shape, and the rule is now built
rather than only written down.

## 1.13.4 / exports 668, 669 — `Total` and `Tally`: the buffer is the whole struct, not its front field

`Cpp2IL.Core/Analysis/TheBufferIsTheWholeStruct.cs` (**new**), one call beside `StaticStorageIsTheFirstField`.

A composite over sixteen bytes is returned indirectly — the caller passes a slot's address in `x8`. The
binding works; what does not is that the destination is a memory write at **distance nought**, and field
resolution names distance nought of a struct as its first member:

```
Call List<int>::GetEnumerator, v42._list, values      // the whole enumerator, called `._list`
Call Enumerator<int>::MoveNext,  …, v42               // …run on a slot nothing wrote
```

so the recovered source calls `GetEnumerator()`, throws the answer away, and iterates
`default(List<int>.Enumerator)` — which is empty, so the loop never runs. Exact rather than a guess about
what a distance means: only where the callee **returns indirectly** and the slot is declared as the type it
returns. `FrontMember`, which named it, is right everywhere else.

| | 667 | 669 |
|---|---|---|
| oracle run / same | 79 / 65 | 79 / **67** |
| `full` + WRONG | 8 | **7** |
| `full` + right | 59 | **61** |
| verdicts changed | — | `Total`, `Tally`, both DIFFERS → agrees |
| **discarded `x.GetEnumerator();`** | **19** | **9** |
| **`Enumerator … = default(…)`** | **46** | **36** |
| commented | 371 | 378 |
| livecount | — | live −32, branches 0 |
| compare2 full · cfscore · allscore · decisions · roundtrip · genfail | 2568 · 608/7 · 2121 · 1328 · 1044 · 0 | **all level** |

**Kept.** Ten `foreach` loops in the game now iterate a real enumerator instead of an empty one — the
observable, since no scorer can see it. The `live −32` is junk going away, not code:
`RendererExtension.IsVisible` went from `Bounds bounds = default(Bounds); _ = bounds.center;
_ = renderer.bounds; return TestPlanesAABB(frustumPlanes, bounds);` to
`return GeometryUtility.TestPlanesAABB(_frustumPlanes, renderer.bounds);`.

The `+7 commented` is one method, `TrackingManager`, and it is the **stand-in seam again from the other
side**: `Dictionary<string, int>.Enumerator e = enumerator2;` where `enumerator2` still says
`Dictionary<object, int>.Enumerator`, which is a cast between two instantiations.

### 1.13.5 / exports 670, 671 — the mirror clause, inert, reverted

The obvious answer — `SharperInstantiation` applied to the **source** of a copy rather than its destination —
measured **byte-identical on every scorer and on the corpus**. The reason is `carriedOnly`: `enumerator2`'s
definitions are not all copies, so the guard that makes `StandInCopyType` safe refuses it. Reverted rather
than loosened; loosening `carriedOnly` is what cost forty commented statements the last time it was tried
(the pass says so in its own remarks).
