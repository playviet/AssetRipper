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
