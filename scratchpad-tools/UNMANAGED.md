# `Unmanaged memory` — what the 13457 occurrences are made of

Round 1 of Snacky Dash 1.11.0: `unmanaged` is **1454 bodies (9.22%), 13457 occurrences** — the largest
*causal* marker family in the export (`commented` is bigger but is mostly cascade collateral).

Instrument: **`scratchpad-tools/unmanagedcensus.py`** (written this round).

    python3 unmanagedcensus.py <export>                    # ranked buckets, split by gamefilter tier
    python3 unmanagedcensus.py <export> --tier gameplay
    python3 unmanagedcensus.py <export> --bucket "load local untyped +small"
    python3 unmanagedcensus.py <export> --methods
    python3 unmanagedcensus.py --selftest                  # 12 parse cases, no files needed

It parses the operand rather than matching its text, so `[worldBounds @ X2 (UnityEngine.Bounds&)+8]` and
`[bounds @ X3 (UnityEngine.Bounds&)+4]` land in one bucket. ~0.8 s over a whole export.

## What the marker actually is

`IlGenerator.cs:855` (load) and `:1041` (store) emit it when an ISIL **`MemoryOperand` is still a
`MemoryOperand`** at IL generation — i.e. nothing upstream turned it into a `FieldReference`, a
`NestedFieldReference`, an array element (`TryLoadArrayElement`), or a read through a by-ref parameter
(`TryLoadThroughByRef`). It says nothing about the instruction; it is entirely about **the address**.

The operand grammar is `ISIL/MemoryOperand.ToString()`:

    [ base? (+|-)ADDEND_HEX? +index (*scale)? ]

`base`/`index` print as `name @ REG (Full.Type.Name)` (a `LocalVariable`), `X8_v20` / `stackaddr_-D0` /
`X8#pin0` (a bare `Register`), or nothing at all with a bare hex addend (a constant address). Note two
traps the census handles and a text split cannot: `stackaddr_0-8` is the register `stackaddr_0` with
addend `-8`, and a nested type prints a `+` inside its own name
(`AssetLoader+<LoadFromAddressables>d__7`1`).

### The bucket key

`<load|store> <base kind> <type class> <offset class>` plus `[i]` if there is an index register.

| base kind | |
|---|---|
| `const` | no base: a fixed address — static data or a hard-coded pointer |
| `this` | the base local is `this` |
| `param` | a named local that is neither `this` nor `vN` — a parameter or a named variable |
| `local` | `vN`, a name analysis made up |
| `reg` | a bare register: no local was ever made for it |
| `stack` | the base register is `stackaddr_N` — the frame, a stack slot's address |

| type class | |
|---|---|
| `untyped` | the base carries **no type at all** — the wall |
| `byref` | `Type&`, a struct reached through a reference |
| `runtime` | `Il2CppClass<>`, `Il2CppStaticFields<>`, `Il2CppMethodRgctx<>`, `MethodInfo` — a runtime structure with no managed field to name |
| `statemachine` | a compiler-generated `<...>d__N` |
| `array` | `T[]` |
| `type` | an ordinary managed type — a field *should* have been nameable |

Offsets: `+0`, `+small` (<0x20), `+mid` (<0x100), `+large`, `-neg`.

## Where the refusal is, in one place

The pass that turns `[base+offset]` into a field is upstream's
**`Analysis/MetadataResolver.cs` `ResolveGetter`**, and it has exactly three refusals:

| `file:line` | refusal | which buckets it produces |
|---|---|---|
| `MetadataResolver.cs:108` | `memory.Index != null \|\| memory.Scale != 0` | every `[i]` bucket (handed to the array passes instead) |
| `MetadataResolver.cs:111` | `memory.Base is not LocalVariable local \|\| local.Type == null` | **every `untyped` bucket, and every `reg`/`stack` bucket** |
| `MetadataResolver.cs:193` | `field == null` — nothing at that offset | the `type` / `runtime` / `statemachine` buckets |

Everything the fork has built around address recovery (`FieldAddressRecovery`,
`FieldAddressThroughCopies`, `StructSlotFields`, `StaticStructMember`, `SlotFieldAddress`,
`StateMachineFieldLayout`, `ArrayElementAddress`, `UnscaledSubscript`, …) exists to make one of those three
refusals not fire — by giving the base a type, by laying out a type that records no offsets, or by naming
an element instead of a field. So a bucket's diagnosis is always *which of the three*, and *why the pass
that should have prevented it did not*.

### A store marker is worse than a load marker

`IlGenerator.cs:1041` pops the value and writes the marker: **the write does not happen**. A load that
cannot be placed answers nought and says so; a store that cannot be placed leaves whatever was in the
place before, and everything downstream reads that. So an occurrence in a `store` bucket costs more than
one in a `load` bucket, and the ranking below weighs them accordingly
(`il2cpp-the-store-that-never-happened`).

### The other four refusals worth naming

| `file:line` | pass | refuses when |
|---|---|---|
| `IlGenerator.Fork.cs:379` | `TryLoadThroughByRef` (and its store twin) | the by-ref base's **name does not match a parameter** of the method. A `Type&` held in a `vN` or in a frame slot is refused even though the element type is right there |
| `IlGenerator.Fork.cs:391` | the same | the pointee is not a value type, is a primitive, or has no member at that distance |
| `VirtualCallRecovery.cs:60` | `SlotOwner` | the receiver has **no type** and the slot is ≥ 4 — `receiver.Type ?? (slot < 4 ? object : null)`. Only `System.Object`'s own four virtuals can be named without a receiver type |
| `MetadataResolver.cs:56/67/79` | `ResolveMetadataUsages` | a bare `[ADDR]` that is not a string literal, a type usage or a MethodInfo usage — what is left is the `const address` bucket |

## Cross-game baseline — Fluffy Field 1.0.3 (export `_720`)

377 occurrences, and the same shapes in the same order. (`gamefilter`'s tables are Snacky's, so its tier
column is meaningless on Fluffy Field; only the bucket ranking carries over.)

| occ | bucket |
|---|---|
| 84 | `load local untyped +small` |
| 52 | `load local untyped +large` |
| 37 | `load local runtime +mid` |
| 28 | `load local untyped +mid` |
| 15 | `store this statemachine +mid` |
| 15 | `load const address` |
| 14 | `load local runtime +small` |
| 12 | `store stack untyped +0 [i]` |
| 12 | `load this type +mid` |
| 11 | `load local type +mid` |

**164 of 377 (43%) are `local untyped`** — a value analysis named but never typed. That matches
`il2cpp-what-the-three-markers-are-made-of` (714 of 1019 bottomed out in an untyped local at 1.0.734), so
the family is *stable across two games and two years of passes*: it is a general gap, not a fitted guard.

### What the offsets say about the untyped ones

`il2cpp-an-offset-signature-names-nothing` settled that a *managed* type cannot be recovered from the
offsets read through a base (median 711 candidates at N=2; a base whose type is known matches its own
layout only 42% of the time). The same measurement says what offsets *can* do: identify **runtime
structures by table lookup**. `unmanagedcensus.py` now prints that table for every untyped site, and on
Fluffy Field it names **38 of 182** non-indexed untyped loads:

| occ | what the offset is |
|---|---|
| 144 | no runtime structure has a field there — a managed object, and honestly unnameable from here |
| 19 + 19 | `Il2CppClass.vtable[5].method` and `.MethodInfo` — **always as a pair** |
| 8 | `Il2CppClass.static_fields` (0xB8) |
| 2+2, 1+1, 1+1, 1+1 | vtable slots 8, 47, 7, 0, in pairs |
| 2 + 2 | `Il2CppClass.typeHierarchy` / `typeHierarchyDepth` |

**48 of them are a virtual call, not a field read at all.** `[klass+0x188]` / `[klass+0x190]` is the method
pointer and the MethodInfo of vtable slot 5 (`0x138 + 5*0x10`), i.e. a dispatch `VirtualCallRecovery` did
not recover — and the marker is a symptom of the *call* being lost, so a fix aimed at "naming this address"
would be aimed at the wrong thing.

## Snacky Dash 1.11.0, round 2 — the table

*The export root is rewritten by every run (round 3 started while this was being written), so the tree this
was measured on was copied first. The copy and the per-site JSON are in this session's scratchpad:
`round2/ExportedProject/Assets/Scripts`, `snacky-unmanaged.json`, `snacky-census.txt`, and the probe
readings `probe-sm.txt` / `probe-iface.txt` / `probe-fields.txt`. Re-run against any later export with
`python3 unmanagedcensus.py <export>`; add `--no-members` to skip ast-grep attribution.*

13457 occurrences, the same number round 1 measured. **5935 gameplay, 3265 meta, 4257 sdk, 0 unity, 0 bcl.**

| gameplay | meta | sdk | total | bucket |
|---|---|---|---|---|
| 1073 | 538 | 28 | 1639 | `load local type +large` |
| 683 | 395 | 102 | 1180 | `load local untyped -neg` |
| 670 | 369 | 405 | 1444 | `load local type +small` |
| 647 | 345 | 50 | 1042 | `load local type +mid` |
| **519** | 213 | 3 | 735 | **`store this statemachine +mid`** |
| 347 | 266 | 1138 | 1751 | `load local untyped +large` |
| 331 | 278 | 731 | 1340 | `load local untyped +small` |
| 270 | 189 | 257 | 716 | `load local untyped +mid` |
| 177 | 206 | 804 | 1187 | `load local runtime +mid` |
| 176 | 76 | 73 | 325 | `store stack untyped +0 [i]` |
| 144 | 49 | 0 | 193 | `load this statemachine +mid` |
| 138 | 43 | 83 | 264 | `load const address` |
| 88 / 80 | | | 121 / 144 | `load stack untyped +mid` / `+small` |
| 59 | 28 | 12 | 99 | `store const address` |
| 48 | 51 | 9 | 108 | `load local runtime +small` |
| 31 | 43 | 0 | 74 | `store this statemachine +small` |
| 28 / 22 | | | 28 / 22 | `store param byref +small` / `+mid` |

...55 more buckets, none over 40 in gameplay.

### The buckets are not the roots — the offsets are

Running the runtime-structure lookup over **every** site (a typed base too: the largest gameplay bucket is
`[v565 @ X8_v18 (Cysharp.Threading.Tasks.UniTask)+12E]`, and `0x12E` is `Il2CppClass`'s
`interface_offsets_count` — the type on that base is simply wrong, and no managed struct has a field there):

| occ | gameplay | what the offset is |
|---|---|---|
| 8673 | 3764 | no runtime structure has a field there |
| 2302 | **1364** | `Il2CppClass.interface_offsets_count` (0x12E) |
| 1162 | **691** | `Il2CppClass.interfaceOffsets` (0xB0) |
| 757 | 1 | `Il2CppClass.vtable[0].MethodInfo` — almost all sdk |
| 84 / 60 / 44 / 44 / 41 | | `typeHierarchyDepth`, `static_fields`, `actualSize`, `typeHierarchy`, `rgctx_data` |

and one more measurement folds a whole bucket into the same root: of the 683 gameplay `-neg` loads,
**698 are exactly `-8`**, and **136 of the 140 methods that carry one also carry a 0xB0 or 0x12E read**.
`[p - 8]` is the interface the walk compares each table entry against (`il2cpp-struct-layouts`), so it is
the same walk.

**The interface walk is 2753 of the 5935 gameplay occurrences — 46%.** Nothing else in this game is close.

Add the interface-typed bases that are the same dispatch by another spelling —
`Cysharp.Threading.Tasks.IUniTaskSource` 363, `IStateMachineRunnerPromise` 122,
`JuicedUp…IPopupPayload` 35 — and it is over half of everything the game's own code loses to this marker.


## What the fork already claims, per shape — read before proposing anything

| shape | pass | where it runs | what it refuses on |
|---|---|---|---|
| `[typedLocal + off]` | `MetadataResolver.ResolveGetter` (upstream) + `MetadataResolver.Fork` (nested paths, open generics, base types, address arithmetic) | `AfterTypesAndFieldsResolved` | untyped base (`:111`), index/scale (`:108`), no field at the offset (`:193`) |
| an address computed into a register | `FieldAddressBase`, `FieldAddressRecovery`, `FieldAddressThroughCopies`, `FieldAddressSinking`, `SelectedFieldOffset` | `BeforeUnusedLocalsAreDropped` | **every use of the address must be one it can speak for** — a read at distance 0, a value-type call receiver, or a by-ref argument. Any other use and the addition is left alone |
| a static struct's member | `StaticStructMember`, `StaticFieldAddressBase`, `StaticStorageIsTheFirstField` | same hook | — |
| a struct in a frame slot | `StructSlotFields`, `SlotFieldAddress`, `SlotAddressRead`, `SlotAddressArgument`, `TypeAcrossANamedSlot` | `AfterUnusedLocalsAreDropped` | the callee must name the struct (`SlotFieldAddress`), the slot must be reached at a distance that lands on a field |
| an element address in pieces | `ArrayElementAddress`, `ArrayAccessRecovery`, `UnscaledSubscript`, `RankTwoArrayAccess` | late, twice | the stride must equal the element's own width; `UnscaledSubscript` needs one definition and one use |
| a generic type's fields | `GenericFieldLayout` | on demand | only where a **concrete derived type's** first recorded offset confirms the computed end of the base |
| a compiler-generated state machine's fields | `StateMachineFieldLayout` → then re-runs `FieldAddressRecovery` + `FieldAddressThroughCopies` | end of `BeforeUnusedLocalsAreDropped` | **all-or-nothing**: `wanted.Any(at => !layout.ContainsKey(at))` at `:83` throws the whole layout away if a single distance the body uses is not exactly a field start. Also `:56` (the type already has recorded offsets), `:64` (nothing could be laid out), and `Sized` refusing a generic parameter or a non-value type (`:187`, `:200`) |
| a virtual call | `VirtualCallRecovery` | before the field passes | untyped receiver + slot ≥ 4 (`:60`); an interface call takes the walk instead (`InterfaceCallRecovery`) |
| a by-ref parameter read/written through | `IlGenerator.Fork.TryLoadThroughByRef` / `TryStoreThroughByRef` | IL generation | **the local's name must match a parameter** (`:379`) |

Two facts constrain every proposal below:

1. **`MetadataResolver` runs early** (`AfterTypesAndFieldsResolved`), and almost every fork pass that gives a
   base a type runs *later*. Anything that types a base after that point has to re-run resolution itself —
   which is exactly what `StateMachineFieldLayout` does at `ForkPipeline.cs:772-776` and what a new pass in
   the same position would have to do.
2. **`il2cpp-the-dump-is-not-where-the-pass-runs`.** Every diagnosis below was checked at the pass's own
   position, with `LAYOUT_TRACE` / the refusal itself, not off the finished dump.


## How each diagnosis below was made

1. `unmanagedcensus.py <export>` for the bucket, then `--bucket "<key>"` for its sites and `--methods` for
   the bodies that carry most of it.
2. The ISIL of one gameplay method at the build the export ran
   (`probe.dll <snacky .so> <snacky metadata> 6000.0.78f1 dump <Type> <Method>`; `probeg`'s `apkpath`
   points at Fluffy Field, so the paths are given explicitly).
3. **The refusal itself, at the pass's own position**, using the trace switches the passes already carry —
   `LAYOUT_TRACE` in `StateMachineFieldLayout`, `CHAIN_TRACE` in `ArrayElementAddress`, `IFACE_TRACE` in
   `InterfaceCallRecovery`, `PIPETRACE` for the body at every hook. A shape read off a finished dump is not
   evidence about a pass that runs a hundred passes earlier
   (`il2cpp-the-dump-is-not-where-the-pass-runs`).


## The roots, gameplay only (5935 occurrences)

Rolled up from the buckets by what the address *is*, not by how it is spelt
(`unmanagedcensus.py` + the runtime-offset table; the script that produced this is ten lines over its
`--json`):

| occ | % | of which **lost stores** | root |
|---|---|---|---|
| 2055 | 34.6% | 5 | **R1** the interface walk — `Il2CppClass.interfaceOffsets` (0xB0) and `interface_offsets_count` (0x12E) |
| 698 | 11.8% | 4 | **R1** the same walk — the `[p-8]` comparison of each table entry |
| 694 | 11.7% | **545** | **R2** a distance **inside a state machine's field** |
| 593 | 10.0% | 2 | **R3** the base is typed as an **interface**, which has no fields |
| 483 | 8.1% | 33 | **R4** an untyped managed base — no runtime structure, no signature to match |
| 354 | 6.0% | **177** | **R5** a frame slot, mostly `[stackaddr_-A0 + i*8]` |
| 199 | 3.4% | 24 | R8 a typed base with no field at that offset |
| 197 | 3.3% | 59 | R7 a constant address that is no metadata usage |
| 195 | 3.3% | 0 | R8 a runtime-structure type (`Il2CppRgctx`, `Il2CppClass<T>`) at an offset with no field |
| 143 | 2.4% | 7 | **R2** a generic-instance type, which records no offsets |
| 133 | 2.2% | **88** | **R6** a by-ref whose local does not match a parameter name |
| 67 + 49 | 2.0% | 7 | **R1** another runtime structure / a vtable pair (a virtual call) |
| 50 + 25 | 1.3% | 35 | an array-typed base; an indexed frame slot |

**R1 is 2869 of 5935 — 48% of everything the game's own code loses to this marker.**
**R2 is 837, and 552 of them are writes that never happen.**

## The five diagnoses

### 1. R1 — the interface walk. `BoosterBuyUI+<RvResetCountdownLoopAsync>d__40::MoveNext`

At **`InterfaceCallRecovery`'s own position** (`IFACE_TRACE=MoveNext`, not off the finished dump) the walk
is completely intact:

```
120 Add        v185, [v158 @ X8_v27+B0], 8            ; p = interfaceOffsets + 8
116 CheckEqual v162, [v158 @ X8_v27+12E], 0           ; the count
129 CheckEqual v215, [v205 @ X10_v7-8], v136 @ X1_v18 (Il2CppClass<UniTask`1<Boolean>+Awaiter<Boolean>>)
153 ShiftLeft  v234, [v205 @ X10_v7], 4               ; entry = klass + offset<<4
155 Add        v236, v235, 312                        ; + 0x138, the vtable
162 IndirectCall v238, ...
```

Everything `Dispatch` looks for is there — and the **comparand is not a `typeof` constant**. It is a local
holding a **class read out of the rgctx** (`Move v136, [v134 (Il2CppRgctx<Awaiter<Boolean>>)+28]`), because
the interface is a *generic instantiation* and its class is only known at run time. That is the shape the
whole of UniTask is built out of, which is why this game has 2869 of them and Fluffy Field had 16.

`InterfaceComparedAgainst` **claims that path** — `:767` for a local typed `RuntimeClassTypeAnalysisContext`
and `:777` for one dereference further on — so this is **(b) a pass covers it and refuses**, and the refusal
is somewhere inside `Dispatch` (`InterfaceCallRecovery.cs:594`), which today traces the *body* and not the
*reason*. Per `il2cpp-measure-the-refusal-not-the-marker`, the next step is not a rule: it is ~20 lines
recording which of `Dispatch`'s eight `continue`s fired, over the whole game, grouped.

Cross-check with the two memories that already sized this family: the receiver's type was the blocker in
the corpus and measured a **wash** in Fluffy Field (`il2cpp-the-interface-walk-blocker-is-the-receiver-type`),
and the last Fluffy Field walks failed on **tail-merging** (`il2cpp-the-last-interface-walks-are-tail-merged`).
Neither has been shown to be what stops Snacky Dash. Do not assume either.

### 2. R2 — a distance inside a state machine's field. Same method, `[this+30]`

The trace says the layout pass declines, and correctly:

    LAYOUT <RvResetCountdownLoopAsync>d__40::MoveNext already has offsets

`probe fields` gives the recorded layout — the machine is a **struct**, so the offsets carry no header:

    <>1__state    0x00  System.Int32
    <>t__builder  0x08  AsyncUniTaskVoidMethodBuilder
    <>4__this     0x18  BoosterBuyUI
    cancellationToken 0x20
    <>u__1        0x28  UniTask`1<Boolean>+Awaiter<Boolean>      (instance size 0x48)

and the body does this:

```
73 Move v64, this.<>u__1                 ; the field, named perfectly
76 Move this.<>u__1, 0                   ; the front of it, named
77 Move [this @ X0 (…d__40)+30], 0       ; 8 bytes IN, and nothing can name that
```

So the root is not the state machine at all: **`Awaiter<Boolean>` is a generic instance and records no field
offsets**, so the interior word has no member to be resolved to. `MetadataResolver.Fork.PathIntoStructValue`
is the pass for interiors and it has nothing to walk. `StateMachineFieldLayout` cannot help — it declines at
`:56` precisely because this machine's *own* offsets are recorded.

**545 of the 694 are stores**, i.e. `IlGenerator.cs:1041` pops the value and the write does not happen.

### 3. R3 — the base is an interface. `MagnetBoosterController::MoveNext`, `[v456 (IUniTaskSource)+8]`

`IUniTaskSource` has no fields, so `MetadataResolver.cs:193` refuses, and **there is nothing to name**: the
field belongs to whichever class implements the interface, and the type does not say which. This is the
honest case — a marker here is better than a guess, and it should stay. It is also partly downstream of R1:
these locals are typed by the very dispatch chain R1 fails to recover, so re-measure this root *after* R1
rather than opening it.

### 4. R5 — an indexed frame slot. `BoosterPreUseAnimationCoordinator::MoveNext`

`[v359 @ stackaddr_-A0 + v354 @ stackaddr_-98 * 8]`, 176 of them **stores**. A table built on the frame and
indexed at run time: `MetadataResolver.cs:108` refuses anything with an index, and the array passes need a
base typed as an array, which a frame slot is not. `StructSlotFields` / `TypeAcrossANamedSlot` are the
passes in this territory and neither types a slot that is only ever indexed.

### 5. R6 — a by-ref that is not a parameter. 133 occurrences, 88 of them stores

`IlGenerator.Fork.TryLoadThroughByRef` / `TryStoreThroughByRef` already know how to read and write through a
`Type&` — they resolve the member with `MemberAt` and emit `ldfld`/`stfld` — but they require the local's
**name to match a parameter of the method** (`IlGenerator.Fork.cs:379`). A `Type&` that lives in a `vN` or
in a frame slot is refused although its element type is on the local and the member is findable.
`[v36 @ stackaddr_-D0 (UnityEngine.UIVertex&)+50]` is the shape, and it is the same on both games.

## Ranked, by gameplay occurrences per line of code — nothing applied

| rank | change | gameplay occ | lines | what could regress |
|---|---|---|---|---|
| 1 | **Instrument `Dispatch`'s refusal** in `InterfaceCallRecovery` (a `Refused` queue with the register state, dumped by a probe mode, grouped by cause) — then fix what it names | 2869 (48%) | ~20 + unknown | nothing: it is a measurement. The *fix* that follows can name the wrong callee, which is whole-and-wrong; require the slot to exist on the named interface and measure on the corpus, never on `full` |
| 2 | **Lay out a generic instance held in a field, checked against the enclosing type's recorded extent** — `<>u__1` occupies 0x28..0x48, so a computed layout for `Awaiter<Boolean>` is *confirmed* if it sums to 32 bytes, exactly the way `GenericFieldLayout` confirms itself against a derived type's first offset | 837, **552 lost stores** | ~50 | a layout that passes the extent check but is still wrong writes the *wrong field* — the one failure mode worse than a marker. Only accept an exact sum, never a fit-inside; run the corpus |
| 3 | **Let `TryLoadThroughByRef`/`TryStoreThroughByRef` take a by-ref that is not a parameter** — load the local rather than `ldarg`, keep every other condition | 133, **88 lost stores** | ~10 | emitting `ldobj`/`stfld` through a local that is not really a managed pointer gives unverifiable IL and loses the whole body; the guard is that the local's type already *is* `ByRefTypeAnalysisContext`, which is the same guard the parameter path trusts |
| 4 | **Type a frame slot that is only ever indexed** (R5) | 354, **177 lost stores** | ~40 | naming an element of the wrong stride reads the wrong memory quietly; `ArrayElementAddress`'s stride rule is the precedent and it is strict for this reason |
| — | R3 (interface-typed base), R4 (untyped managed base) | 1076 | — | **no fix proposed, and that is the finding.** An interface names no field, and an untyped base cannot be identified from its offsets — measured three ways in `il2cpp-an-offset-signature-names-nothing`. These markers are correct and should stay; re-count them after R1, because many of the locals are typed by the chain R1 loses |

Two cautions carried from earlier rounds. `full` will *fall* if any of these turns a silent wrong answer
into a marker, and that is a win, not a loss (`il2cpp-what-full-is-actually-worth`). And every one of these
markers is on the **address**, so a fix that raises `full` without the corpus agreeing has not been shown to
compute the right answer.
