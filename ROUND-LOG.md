# Round log — the generic sharing seam (`Unknown` note slice)

Worktree `agent-ac016851c9627dd5a`, branch `worktree-agent-ac016851c9627dd5a`.
Versions **1.9.0 – 1.9.29**, exports **520 – 549**. Nothing outside those ranges.

Task (re-aimed mid-session by the coordinator; the HFA/struct-return brief was dropped before any code was
written against it): the 11 shared-generic methods carrying the ILSpy `Unknown` note, 130 commented
statements between them.

| file | member | commented (coordinator's census, 1.4.1 / export 356) |
|---|---|---|
| CFramework/IDictionaryExtension.cs | GetKeysByValue | 33 |
| CFramework/IDictionaryExtension.cs | TryGetKeyByValue | 30 |
| CFramework/IEnumerableExtension.cs | TakeLast | 16 |
| CFramework/ArrayExtension.cs | AddRange | 12 |
| CFramework/IEnumerableExtension.cs | PickRandom | 11 |
| CFramework/JsonExtension.cs | ToNewlineDelimitedJson | 9 |
| CFramework/ArrayExtension.cs | ResizeArray | 6 |
| CFramework/SlicedFilledImage.cs | SetStruct | 5 (diagnose only — shared file) |
| CFramework/ArrayExtension.cs | Remove | 4 |
| CFramework/ArrayExtension.cs | CompareArray | 3 |
| CFramework/BaseTrackingSaveData.cs | Set | 1 |

## Setup

Scratchpad `…/09e766c7…/scratchpad`, repointed at this worktree:
`riprun/riprun.csproj`, `probe2/probe.csproj`, `probe2/nuget.config` all carry the worktree's absolute path,
and `bumpz.sh` has `AR=` pinned to the worktree (its `${SP:h}` default assumes the scratchpad lives inside
the tree, which this one does not).

Branch merged up to master `09656cf38` (1.4.1) before starting, so the baseline is master.

---

## Round 0 — baseline, 1.9.0 = master 09656cf38 unmodified, export 520

| | 520 |
|---|---|
| compare2 full / partial / dead | **3252 / 151 / 108** |
| compare2 markers | commented 494, unmanaged 389, notfound 50, indirect 21, unknowncall 6, unknown 2, structvalue 1 |
| compare2, decompiled only | 2558 full of 2815 (90.9%) |
| cfscore | **609** full, 6 partial, 0 missing, 92 of 96 files clean |
| decisions | **1326 / 1382** (95.9%), 293 methods keeping all (94.8%) |
| roundtrip Assembly-CSharp | **11186 / 16264** (68.8%); whole 1043, partial 1572, dead 142 |
| seam.py genMethod | 66 members: 42 full, **24 partial**, 0 dead (36.4% partial) |
| seam.py genType / plain partial% | 6.2% / 3.5% |
| notecensus `Unknown` slices | `I8<-Unknown` 42 notes/13 methods/12 lost · `O<-Unknown` 27/14/14 · `I4<-Unknown` 8/6/6 · `F4<-Unknown` 8/4/4 |

Commented-statement shapes game-wide (`commshape.py`, written this session): 494 in 58 files. Top families are
`obj2 = obj3;` (31), `long num9 = obj - 32L;` (25), `num12 = num18;` (14), `long num22 = num19;` (10),
`val3 = val6;` (10) — i.e. an untyped local that holds an address, and the copies out of SSA that carry it.
The 11 target files hold 63 (IDictionaryExtension), 28 (IEnumerableExtension), 26 (SlicedFilledImage),
25 (ArrayExtension), 19 (JsonExtension).

### Diagnosis before any code

**`SlicedFilledImage::SetStruct` is not a typing bug and cannot be fixed as one.** It recovers as
`EqualityComparer<bool>.Default`, `newValue & 1L`, `Unsafe.As<T, object>(ref currentValue)`. il2cpp does
**not** share value-type instantiations — it compiles one body per value type — so the generic *definition*
`SetStruct<T>` has no body of its own and the address registered for it is one of the specialisations'
(`SetStruct<bool>`, from `m_FillCenter`/`m_PreserveBorder`). Every statement in it is correct code about
`bool` written under a declaration that says `T`. There is no type to fix: `bool` is what the binary says.
The only honest repairs are (a) leave it, which is what happens now, or (b) teach the export to notice that a
generic definition's body is a concrete instantiation's. `BaseTrackingSaveData::Set<TVal>` is the same shape.
**Diagnose-only, as briefed; recorded so the next session does not chase the `bool`.**

**`ArrayExtension::ResizeArray` / `AddRange` / `CompareArray` have a different and tractable root.** The ISIL
(`probe2 dump ArrayExtension ResizeArray`) is

```
b8   Add      v85,  v73 (T[]),   32          ; the new array's first element, hoisted out of the loop
b10  Add      v137, input (T[]), 32          ; the source array's, likewise
b11  Multiply v226, v159 (i), 8              ; SharedElementIsAPointer has already answered the stride
     Add      v227, v137, v226               ; &input[i]
     Call     4AFBB20 (memcpy), v230, v59, v227, v53   ; scratch = input[i]
b14  Multiply v293, v159 (i), 8
     Add      v294, v85, v293                ; &output[i]
     Call     4AFBBF0 (memcpy), v295, v294, v227, v53  ; output[i] = input[i]
```

`ArrayElementAddress` *does* build the chain (`CHAIN_TRACE=ResizeArray` says `yes` for v227, v294, v285,
v341, v161) but only rewrites `MemoryOperand` **reads**; here the address's only use is as a call argument.
`ClearingASizedByT.ElementAt`, which is the pass that reads a copy's operands, requires the step past the
header to be the **last** one (`(array + i*w) + 0x20`). Here it is the **first** and hoisted
(`(array + 0x20) + i*w`), so it matched nothing. The note on that method claimed these loops "are stepped by
a walking pointer rather than by a multiply per iteration" — that reading is wrong for this build.

---

## Round 1 — 1.9.1, export 521 — the hoisted element address, and the store

`External/Cpp2IL/Cpp2IL.Core/Analysis/ClearingASizedByT.cs` only (a fork file; no upstream file touched, so no
`FORK.md` row):

1. `ElementAt` accepts the hoisted form as well as the trailing one, via two new helpers `Hoisted` and
   `Access` (the latter is the old either-order body, lifted out).
2. **The destination half.** Where the buffer a `memcpy` fills is itself an element, the copy is a *store*:
   `Move [output[i]], value`. Recovering only the source writes the value into the destination *address*,
   which says nothing and loses the store — so both halves have to be taken in one place, and this is the
   only place both are in hand. Excluded for `memset`, which would become `array[i] = (T)0` and hit the same
   trap as the return buffer (`il2cpp-the-buffer-a-shared-body-returns-through`).

**KEPT.**

| | 520 base | **521** |
|---|---|---|
| compare2 full / partial / dead | 3252 / 151 / 108 | **same** |
| commented | 494 | **490** |
| unmanaged | 389 | **388** |
| notfound | 50 | **47** |
| cfscore full · clean files | 609 · 92/96 | **level** |
| decisions | 1326/1382 | **level** |
| roundtrip whole / facts | 1043 / 11186 | **level** |
| generation failures | 0 | **0** |
| seam genMethod partial | 24 | 24 |

Per member: `ResizeArray` commented 6→**3**, notfound 4→**2**; `CompareArray` commented 3→**2**,
unmanaged 5→**4**, notfound 1→**0**. `AddRange` did not move.

The reason to keep it is not the four statements, it is what they say. `ResizeArray`'s copy loop was

```csharp
//int num5 = (int)(input + 32L);
long num9 = num7 * 8L;
//_ = num5 + num9;
_ = "Method not found @4AFBB20";
//_ = num2 + num10;
_ = "Method not found @4AFBBF0";
```

and is now

```csharp
_ = input[num6];
if (array != null) { if (num6 >= array.Length) break;
    array[num6] = input[num6];
```

— **the source, exactly**. That is a body that copied nothing now copying the right element, which no
compilability scorer can see and which is the whole point of this family. Every correctness measure is level
and three markers went away, so nothing was traded for it.

---

## Round 2 — 1.9.2, export 522 — the salvaged `AnswerIntoTheCopyItFeeds`, measured for the first time

### Why this and not something of my own

`probe2 rawisil IEnumerableExtension TakeLast` settles what the rest of the family is:

```
260 Move X8, [X1+10]        ; the interface method's invoke_impl thunk
261 Subtract X3, X29, 16    ; the thunk's argument frame
264 Move X4, X23            ; X4 = the answer buffer - an ALLOCA, not a frame slot
266 IndirectCall X8, ...
267 Move X0, X24            ; dst: the local's own alloca
269 Move X1, X23            ; src: the buffer the thunk just wrote
271 Move X2, X22            ; sizeof(T)
273 Call 4AFBB20 (memcpy)
```

`InvokerThunk` already has two of the three shapes the answer pointer can take -
`FoldAnswerIntoTheReturn` (the method's own return buffer) and `AnswerIntoTheSlotItNames` (a named frame
slot). This is the third: **an alloca, copied once into the alloca the local is kept in**, and neither
existing rule reaches it. It is the root of `TakeLast`, `PickRandom`, `AddRange`, `TryGetKeyByValue`,
`GetKeysByValue`, `Shuffle` and `Swap` — most of the 130 statements.

The killed agent (`worktree-agent-a0ff9f5531c8d92fe`) had written exactly this rule and never measured it.
Its base commit `4e68dca82` is an ancestor of master and `git diff 4e68dca82 HEAD` over the five files is
empty, so its working copies drop in cleanly. Taken as one coherent unit:

| file | what it adds |
|---|---|
| `Analysis/InvokerThunk.cs` | `AnswerIntoTheCopyItFeeds` + 12 helpers (+389 lines), `COPYFOLD_TRACE` |
| `Analysis/KeyFunctionArguments.cs` | `Reads(method, call)` — how many operands an import really reads, so a leftover register is not counted as a use |
| `Analysis/SlotAddressRead.cs` | a call that answers into a frame word writes it, which is what names the word |
| `Analysis/RuntimeMethodCallRecovery.cs`, `Analysis/InterfaceCallRecovery.cs` | one line each, the new rule between the other two |

**Deliberately NOT taken**: their `ClearingASizedByT` addition (a separate hypothesis about a buffer the
analysis has already typed `T`, and it collides with round 1's edit to the same file). Held for round 3 so
the two are attributable apart.

### Measured

| | 521 | **522** |
|---|---|---|
| compare2 full / partial / dead | 3252 / 151 / 108 | **same** |
| **notfound** | 47 | **39** |
| unmanaged | 388 | **386** |
| **commented** | 490 | **497** |
| cfscore · decisions · roundtrip · gen failures | 609/92, 1326/1382, 11186/1043, 0 | **all level** |

The rule **works**. `IListExtension::Shuffle` and `Swap` lost five `Method not found @4AFBB20` markers and a
dozen dead alloca statements; `TryGetKeyByValue`'s loop went from

```csharp
_ = enumerator4.Current;
_ = "Method not found @4AFBB20";
_ = keyValuePair.Value;
```

to `keyValuePair = enumerator4.Current;` — the assignment the source actually has. `COPYFOLD_TRACE=1`
confirms the fold: `folded IDictionaryExtension::TryGetKeyByValue into v84 @ X27_v1`.

**The +7 commented is one missing condition, not the rule being wrong.** The declaration behind the folded
local survived as

```csharp
//KeyValuePair<T, W> keyValuePair = (KeyValuePair<T, W>)(num6 - num8);
```

`Erase` is meant to take that away and refused, because it asks the alloca's base to be a named stack slot
(`stackaddr_`/`stack_`) and this one is `82 Subtract v84 @ X27_v1, v1 @ X29, v80` — off the **frame pointer
register itself**, which stack analysis never names because it names what the frame *holds*, not what
anchors it. So the fold landed, the local was retyped `KeyValuePair<T, W>`, and its uncompilable declaration
took every use with it. All +7 are in the two `IDictionaryExtension` members.

Not yet keep-or-revert; round 3 supplies the missing condition and the two are judged together.

---

## Round 3 — 1.9.3, export 523 — the alloca taken off the frame pointer

One condition in `InvokerThunk.Erase`: an allocation may be `sub xN, x29, size` as well as
`sub xN, <named slot>, size`. What says the base is the frame rather than a value is that it has **no
definition anywhere in the body** — it is an entry value. Where it does have one, the old name test still
applies unchanged, so nothing that was refused for being a real value becomes erasable.

**KEPT — rounds 2 and 3 together.**

| | 521 | 522 | **523** |
|---|---|---|---|
| compare2 full / partial / dead | 3252 / 151 / 108 | same | **same** |
| commented | 490 | 497 | **490** |
| unmanaged | 388 | 386 | **385** |
| notfound | 47 | 39 | **39** |
| cfscore full · clean files | 609 · 92/96 | = | **=** |
| decisions | 1326/1382 | = | **=** |
| roundtrip whole / facts | 1043 / 11186 | = | **=** |
| generation failures | 0 | 0 | **0** |

So the pair is **commented level, unmanaged −3, notfound −8, everything else level** against the round-1
baseline — and the statements behind those markers are the point:

```csharp
// 521                                       // 523
_ = enumerator4.Current;                     KeyValuePair<T, W> current = enumerator4.Current;
_ = "Method not found @4AFBB20";             _ = current.Value;
_ = keyValuePair.Value;
```

`TryGetKeyByValue` and `GetKeysByValue` now have the `foreach`'s pair as a real local assigned by
`Current`, where before the call's answer was discarded and the pair was `default(KeyValuePair<T, W>)` —
a loop that compared a zeroed pair against the value, scored `full`, and was wrong. `IListExtension::Shuffle`
and `Swap` lost five `Method not found` markers and twelve dead alloca statements the same way.

Judged on the markers and on the statements, not on `commented`, exactly as `CLAUDE.md` directs: `commented`
was the only thing that moved the wrong way at 522 and it was one missing condition, which 523 supplies.

**The killed agent's work is vindicated but was one condition short of measurable.** Had it been measured as
it stood it would have read as commented +7 for notfound −8, which is the kind of result that gets reverted.

---

## Round 4 — 1.9.4, export 524 — the buffer the analysis has already called a `T`

The last piece of the killed agent's set, held back from round 2 so it could be attributed on its own:
`ClearingASizedByT` takes a copy's value into the **buffer** rather than into the call's answer register
wherever that buffer is already typed `T` — the body reads it as the value, not as somewhere to look. With
the same `Erase` step beside it (and the round-3 frame-pointer clause applied there too, which the original
lacked), so the allocation behind the local goes when the local becomes the value.

**KEPT.**

| | 523 | **524** |
|---|---|---|
| compare2 **full** / partial / dead | 3252 / 151 / 108 | **3253 / 150 / 108** |
| **commented** | 490 | **479** |
| **unmanaged** | 385 | **382** |
| notfound | 39 | **39** |
| roundtrip facts / whole | 11186 / 1043 | **11187** / 1043 |
| cfscore · decisions · gen failures | 609/92, 1326/1382, 0 | **all level** |

`IListExtension::Shuffle` goes **partial → full**, and it is right, not merely whole:

```csharp
int index = UnityEngine.Random.Range(0, maxExclusive);
T val = list[index];  T val2 = list[num2];
list[num2] = val;     list[index] = val2;
```

which is the original's `(list[i], list[j]) = (list[j], list[i])` inside
`for (int i = list.Count - 1; i > 0; i--)`. `TransformExtension::GetComponentsInChildrenFD` lost all four of
its commented statements; `GetKeysByValue` 36 → 33. `Swap` gained one (5 → 6) and is the only regression in
the file.

---

## Where the family stands, 520 → 524

| | 520 (master) | **524** |
|---|---|---|
| compare2 full / partial | 3252 / 151 | **3253 / 150** |
| commented | 494 | **479** |
| unmanaged | 389 | **382** |
| notfound | 50 | **39** |
| cfscore · decisions · roundtrip · gen failures | 609/92 · 1326/1382 · 11186/1043 · 0 | **609/92 · 1326/1382 · 11187/1043 · 0** |

Bodies that changed from doing the wrong thing to doing the right one, none of which any compilability
scorer could see: `ArrayExtension::ResizeArray` (copied nothing → `array[i] = input[i]`),
`IListExtension::Shuffle` (the whole swap), `IDictionaryExtension::TryGetKeyByValue` and `GetKeysByValue`
(compared a zeroed `KeyValuePair` → compare the real one).

### The execution oracle, run at 1.9.4 (export `ec525`, 2000 iterations)

```
79 methods run, 51 behave the same, 28 do not
rated `full` 65 · of those right 46 · whole and WRONG 19 · partial+right 5 · partial+WRONG 9
```

**Byte-identical to `corpus/BASELINE.md`** (taken at `9fa38918d` / 1.6.1). So the four rounds are
oracle-neutral: nothing that was right became wrong. It is also the honest limit of that instrument here —
the corpus has no shared-generic body that reaches a `T` through an invoker thunk's answer buffer, so the
oracle cannot see this family at all, and the per-body diffs above are the only evidence there is.

`corpus/corpus.apk` is `.gitignore`d, so it is not in a fresh worktree; copy it from the main tree before
running `oracle.sh`, and pass `REPO=<worktree> RIPRUN=$SP/riprun/bin/Release/net10.0/riprun.dll`.

---

## Round 5 — 1.9.5, export 526 — a call nothing has named reads nothing

`COPYFOLD_TRACE=1` over the three members that never moved says they share one refusal:

```
COPYFOLD refused IEnumerableExtension::TakeLast  read again by -1 Move v170 @ X4_v13, v58 @ X23_v1  | 266 get_Current
COPYFOLD refused ArrayExtension::AddRange        read by 540 Call 1E7BB80, …, v125 @ X4_v8, …       | 260 get_Current
COPYFOLD refused IEnumerableExtension::PickRandom read by 525 Call 1E7BB80, …, v139 @ X4_v8, …      | 271 get_Current
```

The thunk leaves the answer buffer in X4; several blocks later — in the tail-call block after a `throw` —
an **unresolved** call at `1E7BB80` is handed the whole speculative argument run, X4 among it, and `Dead`
counts that as a reader. A call nothing has named produces no statement at all (`Method not found
@1E7BB80`), so no value can be said to reach it. One clause in `Dead`, gated on
`KeyFunctionArguments.Reads` being null so that `memcpy` — whose arity is known and whose operands really
are read — is untouched.

**REVERTED — inert.** Export 526 is byte-identical to 524 on every marker and every scorer
(full 3253, partial 150, dead 108, commented 479, unmanaged 382, notfound 39, 0 generation failures).

The clause is *correct* and *insufficient*, which the trace says exactly: the refusal moves rather than
going away.

| member | refused by, before | refused by, after |
|---|---|---|
| `AddRange` | `Call 1E7BB80` (unnamed) | `Call 4AFBAA0` |
| `TakeLast` | `Move v170 @ X4_v13, v58` | `Move v252 @ X4_v7, v58` |
| `PickRandom` | `Call 1E7BB80` | `Call 4AFBAA0` |

`4AFBAA0` is an adjacent PLT entry that `ImportedFunctionAt` **does** name, so
`KeyFunctionArguments.Reads` returns an arity for it and the new clause correctly declines to dismiss it —
but its operands are `v196 @ X0, v139 @ X1 (Int32), v145 @ X2 (IEnumerable<T>)`, which are this method's
own arguments forwarded, not a copy's three. So either the PLT entry is mis-named or the tail call is being
read as an import; **that is the next thing to find out**, and it blocks all three members at once.

`PickRandom` additionally has a genuine second reader — the source is `T chosen = default; … chosen = item;`
and the buffer really is copied into two places, which the guard is right to refuse.

Reverted per `RECOVERY.md` ("revert what is inert"); the tree is left exactly reproducing export 524, and
the diagnosis above is the deliverable of the round.

---

## Round 6 — 1.9.6, export 527 — the same rule where it actually applies

Round 5 put the clause in `Dead`, and the trace said `read by …` rather than `read again by …`, which is the
**main loop's** refusal, not `Dead`'s. The main loop already had a clause for an unnamed call and it
insisted the leftover operand be *the very same local* as the buffer; a copy into another version of the
same register fails that. Widened: where the callee is a bare address nothing has named, **no** operand
counts, and the clause added to `Dead` as well.

`4AFBAA0` is `__cxa_begin_catch@plt` (objdump), so these registers are live only in blocks reached after a
throw — pure register-allocation residue.

**REVERTED — inert again.** Export 527 is byte-identical to 524 on every marker and scorer.

What it bought is the last of the diagnosis, and it is the reason to stop here rather than widen once more:
each widening only uncovers the next blocker, and the three are now **genuinely different**.

| member | refused by, at 1.9.6 | is it a real reader? |
|---|---|---|
| `PickRandom` | `392 Call 4AFBB20` — a real `memcpy` of the buffer | **yes.** The source is `T chosen = default; … chosen = item;` — the buffer really is copied to two places, and the guard is right |
| `AddRange` | `385 IndirectCall v470 @ X8_v44, …` | no, but an `IndirectCall`'s callee operand is not a `ulong`, so "nothing has named it" cannot be stated the same way |
| `TakeLast` | `Move v252 @ X4_v7, v58` → `Dead` still false | unresolved; the chain out of X4 is long and reaches something neither a copy nor an unnamed call |

So the remaining three need the guard **relaxed in kind**, not widened in coverage: "nothing but the one
copy may read the buffer" is too strong when the buffer register stays live into exception blocks and when
the value is legitimately copied twice. That is a design change, not a condition, and it should be measured
on its own.
