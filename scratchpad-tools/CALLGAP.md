# CALLGAP — where Snacky Dash's 1648 `notfound` and 1965 `indirect` sites come from

Round 1 of Snacky Dash (Unity 6000.0.66f2, metadata 31,1) against Fluffy Field 1.0.3:

| | Snacky Dash | Fluffy Field |
|---|---|---|
| `notfound` | 617 bodies / 1648 sites (3.91%) | 24 bodies / 39 sites |
| `indirect` | 656 bodies / 4.16% / 1965 sites | 32 / 35 |

`callcensus.py <export>/ExportedProject` beside this file is the census; it keys `notfound` on the
address literal in the marker and `indirect` on the **first operand of the `IndirectCall`**, which is the
branch target and so the only part that says what could not be resolved.

**Status: complete.** Round 1's tree was deleted when round 2 started, so §5 is round 2's census —
same game, same settings, 0 `GenericInstantiation` failures instead of 28.

**The answer in one line: 72% of both families are one idiom — a generic virtual method resolved
through a runtime helper nothing names (`3B02AC0`, 853 sites), followed by an indirect call through the
`MethodInfo` it returns (~1,743 sites).** Everything else is a long tail. See §5 and proposal #1.

---

## 1. The binary, and the one structural difference from Fluffy Field

`libil2cpp.so`, 149 MB, arm64, **not** stripped of dynamic symbols (2251 defined, 241 `il2cpp_*`).

| section | VA | size | what is in it |
|---|---|---|---|
| `.text` | `0x3598e20` | 6.7 MB | the il2cpp **runtime** (C++), plus the thunk table |
| `il2cpp` | `0x3c09744` | 75 MB | the **generated** code — every managed method body |
| `.plt` | `0x83d81e0` | 0x2620 | 610 entries, 608 decode to a `R_AARCH64_JUMP_SLOT` symbol |

Fluffy Field has the **same two-section layout** (`.text` 4 MB at `0x1e7bb20`, `il2cpp` 42 MB at
`0x2276c04`), so the split is not the difference between the two games. It matters for a different
reason, in §4.

### The call census of the binary itself

Every `BL` in the `il2cpp` section — i.e. every direct call generated code makes — decoded and
histogrammed (`blhist.py`, in the session scratchpad):

```
BL sites in the generated section : 2,366,124   over 92,442 distinct targets
   -> .text (runtime)             : 1,614,975
   -> il2cpp (another managed body):  634,019
   -> .plt (an imported C function):  117,130
```

After following one-instruction `b` thunks the way `NewArmV8InstructionSet.Fork.FollowThunks` does, and
counting how many of those land on an address Cpp2IL knows as a key function:

```
reaching a key-function value     :   210,115   of 1,630,895 that reach .text
```

So **~87% of the calls generated code makes into the runtime reach an address nothing in the tables
names.** That is the population `notfound` is drawn from; what keeps the exported number down to 1648 is
that most of those bodies are not in `Assembly-CSharp`, and that several families (throw helpers,
`memcpy`) are absorbed by other passes before the marker is written.

### The top targets, named from the binary

| target | sites (binary-wide) | shape at the address | what it is |
|---|---|---|---|
| `0x3B1D444` | 313,051 | real body, `stp x30,x19` … | **metadata init** — the function il2cpp calls at the top of nearly every method (6,574 `.text` callers as well). This is what `AttemptInstructionAnalysisToFillGaps` votes for. |
| `0x3AE41B4` | 294,611 | `str x30; bl 0x3B4A4FC; dmb ish; ldr x30; ret` | **write-barrier wrapper** — a 5-instruction wrapper, *not* a one-instruction thunk, so `FollowThunks` cannot reach through it. Only `GetWriteBarrier()` can name it. |
| `0x3AE4330` | 156,422 | `b 0x3B41CC0` | thunk to `il2cpp_runtime_class_init_actual` (`il2cpp_runtime_class_init` export `0x3AEE1C0` is a second thunk to the same place) |
| `0x3AE445C` | 143,149 | `str x30; bl 0x3B2CFE4` | throw-helper wrapper (the raise family lives at `0x3B2CF4C`±) |
| `0x3AE41C8` | 107,604 | `b 0x3B4A4FC` | the *thunk* form of the same target `0x3AE41B4` wraps |
| `0x3AE4160` | 100,946 | `b 0x3B8A848` | |
| `0x3AE444C` | 93,511 | `b 0x3B20450` | `il2cpp_codegen_object_new` |
| `0x3AE4320` | 44,509 | `str x30; stp xzr,xzr,[x0,#0x38]; bl 0x3B2CF4C` | raise-exception wrapper |
| `0x3AE4338` | 39,828 | `b 0x3B20398` | `il2cpp_vm_object_box` |
| `0x83D8240` | 30,468 | `.plt` | an imported C function — `ImportedFunctionAt` names these |
| `0x3AE433C` | 13,865 | `b 0x3B208A8` | `il2cpp_vm_object_unbox` |

**`0x3AE4xxx` is this binary's thunk table**, exactly as `0x2183xxx` is Fluffy Field's. Nothing in the
fork keys on those literals — `FollowThunks` and `IsKeyFunctionAddress` are both shape/table based — so
the table's move is not itself the defect.

---

## 2. Which of Cpp2IL's key functions this binary has, and at what addresses

Resolved from the ELF the way `BaseKeyFunctionAddresses.Find` does — export by name, then
`FindFunctionThisIsAThunkOf` (first `B`, else first `BL`) — and cross-checked by disassembly:

| key function | export | `::actual` (thunked from it) | is the actual itself a thunk? |
|---|---|---|---|
| `il2cpp_object_new` | `0x3AEE174` | `0x3B20450` | **yes — `b 0x3B20760`** |
| `il2cpp_value_box` | `0x3AEE198` | `0x3B20398` | no |
| `il2cpp_object_unbox` | `0x3AEE194` | `0x3B208A8` | no |
| `il2cpp_runtime_class_init` | `0x3AEE1C0` | `0x3B41CC0` | no |
| `il2cpp_raise_exception` | `0x3AEDDC8` | `0x3B2CF4C` | no |
| `il2cpp_string_new` / `_wrapper` | `0x3AEE1D8` / `0x3AEE1DC` | `0x3B1E184` (both) | no |
| `il2cpp_type_get_object` | `0x3AEE248` | `0x3B21E88` | no |
| `il2cpp_resolve_icall` | `0x3AED870` | `0x3B2E5A0` | no |
| `il2cpp_array_new_specific` | `0x3AED88C` | `0x3B1FEB4` | no |
| `il2cpp_codegen_object_new` | — | `0x3AE444C` (thunk **to** `0x3B20450`) | — |
| `il2cpp_codegen_write_barrier` | — | `0x3AE41B4` **if `GetWriteBarrier` finds it** | — |
| metadata init | — | `0x3B1D444` **if the vote in `AttemptInstructionAnalysisToFillGaps` succeeds** | — |

### How a proposal avoids the 1.0.436 regression

`FORK.md` records that following a thunk unguarded is the worst regression this fork has measured,
because **Cpp2IL's key functions are themselves thunks** — following them moves each one off its own
name. The existing guard is `!IsKeyFunctionAddress(target)` on the *entry* address, which is a shape
rule, not an address rule, and it stays. Every proposal below is stated as a rule over the
`resolvedAddressSet` or over the instruction shape, and **none of them names an address**.

One thing the entry guard does not cover, and this binary is the first to show it:
`il2cpp_object_new::actual` = `0x3B20450` is a key-function *value* and is itself `b 0x3B20760`.
`FollowThunks` walks **through** it, because the guard is only tested on the address the call names, not
on each address the walk passes. `KeyFunctionReaching` then cannot recover the name, because
`SingleCallIn` counts only `BL` (`0x94000000`) and this hop is a `B`. In *this* binary that particular
call is saved by a different route — `il2cpp_codegen_object_new` = `0x3AE444C` is itself a key function,
so the entry guard fires first and the walk never starts — but the hazard is real and general, and it is
one condition to close.

---

## 3. `indirect` — dispatch, not unpropagated constants

Every `BLR Xn` in the generated section, classified by walking back for the instruction that defines
`Xn` (`blrshape.py`):

```
BLR sites in the generated section: 137,934      (window 24 instructions, stop at the previous call)

  39,689  28.8%  virtual dispatch    - ldr Xm,[recv,#0] (class ptr) then ldr Xn,[Xm,#slot]
  36,432  26.4%  delegate / field    - ldr Xm,[obj,#imm] then ldr Xn,[Xm]  (Delegate.method_ptr at +0x18
                                        alone is 6,221)
  29,850  21.6%  definition further back than the window, or clobbered by an intervening call
  21,064  15.3%  a load whose base the window did not settle
   7,670   5.6%  interface dispatch  - ldr Xm,[recv,#0x140] then ldr Xn,[Xm,#slot]
   1,617   1.2%  address constant    - adrp, or adrp+ldr out of the GOT
   1,612   1.2%  mov from another register
```

The virtual slice is spread over ~700 distinct vtable offsets (+0x2A8 3509, +0x3C8 2111, +0x288 1744,
+0x218 1426, …), which is what a vtable looks like and not what a missed constant looks like.

**At most 1.2% of the `BLR` sites in this binary are a register holding a constant** the analysis could
have propagated. The rest is dispatch.

That is the whole binary, though — 167,817 methods, of which `Assembly-CSharp` is a small slice — and
**§5 shows the export disagrees with this section's emphasis**: over the binary the family is ordinary
vtable dispatch, in the game's own code it is the generic seam. Both are true; the second is the one
that decides the round. The two agree on the part that matters, which is that a constant the analysis
missed is not the story.

---

## 3b. The two binaries side by side — the call *mix* is nearly identical

Both `.so` files decoded the same way, normalised per million words of generated code, so the size
difference (18.8M words vs 10.6M) cannot flatter either:

| import (via `.plt`) | Snacky sites | Fluffy sites | Snacky per 1M words | Fluffy per 1M words |
|---|---|---|---|---|
| `__cxa_*` (begin_catch, end_catch, throw, allocate) | 67,940 | 33,432 | 3,609 | 3,147 |
| `memcpy` / `memset` / `memmove` | 38,924 | 22,087 | 2,068 | 2,079 |
| `__stack_chk_fail` | 8,200 | 3,815 | 436 | 359 |
| **`fmod` + `fmodf`** | **637** | **37** | **34** | **3.5** |
| `sinh`/`cosh`/`tanh` | 57 | 0 | 3.0 | 0 |
| all other unmapped, non-`__cxa`, non-`mem*` imports | 38 | 27 | 2.0 | 2.5 |

**The important negative result: the `__cxa_*` family is the same density in both binaries and does not
appear in Fluffy Field's `notfound` census at all.** Every one of Fluffy Field's 18 `notfound` addresses
is named below — none is a `__cxa_*` stub — so exception intrinsics are already absorbed before the
marker is written, and they are *not* the Snacky Dash gap despite being 68,000 call sites.

Fluffy Field's 18 `notfound` addresses, named from its own ELF:

```
 4AFBB20  10  .plt   memcpy         (deliberately unmapped - a struct assignment, not a call)
 1E7BEA8   4  .text
 2184734   2  .text  (its thunk table)
 21BD1E8   2  .text
 2184480   2  .text
 4AFBB70   2  .plt   fmodf          <- no entry in the math table
 4AFBBF0   2  .plt   memmove
 21C0518   2  .text
 4AFBB80   2  .plt   ldexpf         <- no entry in the math table
 21844F4   2  .text
 1E7F0E8   2  .text
 ... 7 more .text addresses, 1 site each
```

Two thirds of Fluffy Field's whole `notfound` family is `.plt` imports the math table does not
cover. **`fmod`/`fmodf` is 34 per 1M words in Snacky Dash against 3.5 in Fluffy Field — ten times the
density, and it is the single largest unmapped import in both games.** `fmodf(a,b)` is what C# `a % b`
on a float compiles to; there is no `Modulus` in `OpCode`, which is why it was never in the table.

## 4. What is fitted to Fluffy Field, and what is not

Checked and found **general** (no work needed):

* `ElfFile.Fork.ImportedFunctionAt` — this binary's `.plt` stubs are `adrp x16 / ldr x17 / add x16 / br
  x17`, so the `adrp`+`ldr` first-two-instruction requirement holds and 608 of 610 entries decode to a
  `R_AARCH64_JUMP_SLOT` symbol. 117,130 generated call sites go to `.plt`; none of them should be
  `notfound`.
* `FollowThunks` / `IsKeyFunctionAddress` — shape and table, no literals. The three addresses `FORK.md`
  names (`0x2183F68`, `0x2183F70`, `0x21BC67C`) appear only in **comments**, as worked examples; the code
  does not test them. This binary's equivalents (`0x3AE4330`, `0x3AE4338`, `0x3AE433C`) are reached by
  the same rule.
* The `0x3AE4xxx` thunk table is found the same way `0x2183xxx` was.

Checked and found **suspect on this binary**:

* **`GetCallerCount` and `FindAllThunkFunctions` see only `.text`.**
  `ElfFile.GetEntirePrimaryExecutableSection()` returns `.text` and nothing else, and the generated code
  is in a separate `il2cpp` section. So when `FindThunks` ranks candidate thunks by caller count it is
  counting **6.7 MB of runtime and ignoring 75 MB of callers**. On this binary it still picks right —
  `0x3AE444C` has 105 `.text` callers against 0 for every rival, and `0x3AE4330` has 1086 — but the
  ranking is being decided by a rounding error rather than by the 93,511 and 156,422 calls that actually
  exist. A binary where two thunks both have a handful of `.text` callers gets an arbitrary answer.
* **`FollowThunks` does not re-test the guard at each hop** (§2), and this is the first binary where a
  key-function value is itself a `b` thunk.
* **`0x3AE41B4`, 294,611 sites, is a five-instruction wrapper, not a thunk.** Nothing follows it. Its
  whole hope is `GetWriteBarrier()`, whose anchors are five corlib methods. **Settled by §5: it is not
  in the export's census at all, so `GetWriteBarrier` found it** and the anchors survive in this Unity 6
  corlib.
* Same shape, different family: `0x3AE445C` (143,149), `0x3AE4320` (44,509), `0x3AE4464` (27,513),
  `0x3AE4454` (12,819) are all `str x30; …; bl <raise-family>` wrappers around the throw helpers at
  `0x3B2CF4C`±. **Settled by §5: none of them is in the census either**, so `ThrowHelperRecovery` and
  `MetadataResolver.ThrownExceptionAt` reach them by their own route. Leave them alone.

Found by the export and **not** visible from the binary at all:

* **Cpp2IL has no key function for the generic-virtual-method resolver**, and the game's own code is
  full of it. That is the gap, it is 72% of the round, and no amount of thunk or import work touches it
  — see §5 and proposal #1. The binary-side census could not have found it, because binary-wide the
  idiom is a rounding error against 313,051 metadata-init calls; it only stands out once the census is
  restricted to the assembly the game wrote.

---

## 5. The export histogram — round 2, and it is one family

```
notfound    1648 sites    107 distinct addresses    298 files
indirect    1965 sites      7 distinct target registers   359 files
```

Round 2 landed with **0 `GenericInstantiation` failures** (round 1: 28) and 7 `StackImbalance`
discards, so the marker counts are round 1's to within those 19 methods.

| address | sites | files | cum% | what it is |
|---|---|---|---|---|
| **`3B02AC0`** | **853** | **126** | **51.8%** | `.text` — **the generic-virtual-method resolver** (see below) |
| `3DD433C` | 207 | 15 | 64.3% | `il2cpp` — a **managed** body `MethodsByAddress` has no entry for |
| `83D8280` | 144 | 37 | 73.1% | `.plt` `memcpy` — deliberately unmapped |
| `359A0FC` | 40 | 15 | 75.5% | `.text` |
| `83D8320` | 35 | 15 | 77.6% | `.plt` `memset` |
| `3B42218` | 31 | 1 | 79.5% | `.text` |
| `3AE41CC` | 27 | 1 | 81.1% | `.text` thunk table |
| `359ABCC` | 24 | 13 | 82.6% | `.text` |
| `3B1D778` | 18 | 8 | 83.7% | `.text` — a second generic-method resolver, same idiom |
| `3E10050` | 17 | 12 | 84.7% | `il2cpp` |
| `3B20664` | 15 | 12 | 85.6% | `.text` |
| `83D8240` / `83D8260` / `83D8270` / `83D8230` | 7 / 6 / 6 / 5 | | | `.plt` `__cxa_end_catch` / `allocate_exception` / `throw` / `begin_catch` |
| … 95 more addresses | 1-10 each | | | |

By section: **1134 sites over 60 `.text` addresses, 307 over 40 `il2cpp` addresses, 207 over 7 `.plt`
imports** (144 `memcpy`, 35 `memset`, 24 `__cxa_*`, 4 `memmove`).

### The prediction in the previous section was wrong, and that is a result

`__cxa_begin_catch` and `__cxa_end_catch` are **24 sites in all, 1.5% of the family**, against 68,000
call sites in the binary. `CatchClauses.DropThePlumbing` is absorbing essentially all of them.
**The `notfound` gap is not an exception-handling gap.** `EH-AUDIT.md`'s open question — do the
recognised clauses reach the exported C# — is answered in the affirmative by this census, from the other
side.

### The one family: generic virtual dispatch

Every site of `3B02AC0` looks the same, and 786 of its 853 (**92.1%**) are followed within four lines by
an `Indirect call` — which is 786 of the whole `indirect` family (**40.0%**):

```csharp
_ = "Unmanaged memory load: [v79 @ X1_v3 (Il2CppMethodInfo)+50]";   // the generic method's slot
_ = "Unmanaged memory load: [v84 @ X8_v10+140]";                    // off the receiver
_ = "Method not found @3B02AC0";                                    // resolve -> a MethodInfo in x0
_ = "Unmanaged memory load: [v89 @ X0_v15+8]";                      // its method pointer
_ = "Indirect call: 95 IndirectCall v92 @ X3_v1, returnVal1 @ X0_v17 (T), this @ X0 (ES3Reader), …";
T result = default(T);                                              // and the answer is thrown away
```

`ES3Reader::Read<T>`, `ES3Reader::ReadInto<T>`, `ES3Reader::ReadProperty<T>` are three of the 126 files.
Disassembled, `3B02AC0` reads a bit out of `[x0+0x53]`, takes `[x0+0x40]` when it is set, pairs it with
`[[x1+0x40]+0x10]`, builds a three-word structure on the stack and tail-calls `0x3B02B20` — the runtime
resolving a generic virtual method against a receiver. `3B1D778` (18 sites) is a second entry point into
the same idiom, and `3DD433C` (207 sites) is the same seam from the other end: a **generic method body
inside the `il2cpp` section that the method table does not name**, called from
`BattlePassInstaller::RegisterRoot` and friends where the recovered C# still shows the
`RegistrationBuilder.As<_0021_00210>()` shape.

### `indirect`, classified from the export

Each `Indirect call` site by what stands in the six lines before it:

| | sites | share | |
|---|---|---|---|
| a single unmanaged load, and **919 of the 954 are `[X+8]`** | 954 | 48.5% | the resolved `MethodInfo`'s method pointer — the same idiom with the resolver call further up or commented out |
| `@3B02AC0` then the call | 789 | 40.2% | generic virtual dispatch |
| two adjacent fields loaded, then the second called | 152 | 7.7% | delegate invocation (`method_ptr` beside `target`) |
| nothing in the six lines before | 68 | 3.5% | |
| after some other unresolved call | 2 | 0.1% | |

By target register: **X8 1606 (81.7%)**, X9 278, X2 44, X3 20, X4 14, X10 2, X5 1. Fluffy Field's
`indirect` is X9-dominated (28 of 35) — the ordinary virtual call — and Snacky Dash's is X8-dominated,
which is this idiom and not that one.

**So the honest split the brief asked for: ~1,743 of 1,965 `indirect` sites (88.7%) are the generic
method call that follows an unresolved resolver, 152 (7.7%) are delegate invocation, and the residue is
ordinary dispatch. Essentially none is a register holding a constant** — which agrees with the binary
census in §3 (1.2% `adrp`-defined) and disagrees with its *emphasis*: over the whole binary the family
is ordinary vtable dispatch, but in `Assembly-CSharp` specifically it is the generic seam, because that
is the code the game itself writes.

### What that means for the size of the whole round

| | sites | share of the two families combined (3,613) |
|---|---|---|
| generic virtual / generic method resolution + the call after it | **~2,590** | **72%** |
| delegate invocation | 152 | 4% |
| `memcpy` / `memset` / `memmove` at `.plt` | 183 | 5% |
| `__cxa_*` | 24 | 0.7% |
| everything else — 98 addresses, ordinary dispatch | ~664 | 18% |

---

## 6. Proposals, ranked by sites per line of code

The census moved the ranking. **One family is 72% of both markers**, and none of the thunk/import work
that looked biggest from the binary is near it.

| # | change | files | lines | sites | keyed on |
|---|---|---|---|---|---|
| **1** | **Find the generic-virtual-method resolver and name it, then recover the dispatch it feeds.** It is not in `BaseKeyFunctionAddresses` at all, so nothing looks for it. Find it the way `AttemptInstructionAnalysisToFillGaps` finds metadata init — **by a vote over sampled method bodies, on the idiom rather than on a name**: the `.text` target of a `BL` whose result register `x0` is, within a few instructions, loaded from at `+8` and then `BLR`-ed. That shape occurs nowhere else. Then one hook that rewrites the whole run — resolve, load, indirect call — into a single call whose callee is the generic method the `MethodInfo` operand already names (the operands carry `Il2CppMethodInfo` types at the call site, see the ISIL above). | `NewArm64KeyFunctionAddresses.Fork.cs` + a new `Analysis/GenericVirtualDispatch.cs` + one line in `ForkPipeline.cs` | ~120 | **853 `notfound` + ~1,743 `indirect` = ~2,590** (72% of the round) | the idiom, voted for across sampled bodies |
| **2** | **`3DD433C` and the other 40 `il2cpp`-section addresses: a managed body the method table does not name.** 307 sites over 40 addresses, `3DD433C` alone 207. Same seam from the other side — generic instantiations whose live copy is registered under a different address (`il2cpp-the-live-copy-is-in-no-table`, `ClonedMethodRecovery`). Worth a census of its own before code: are the 40 all one generic definition's specialisations? | `Analysis/ClonedMethodRecovery.cs` | ? | 307 | the address being inside the generated section |
| 3 | **`fmod`/`fmodf` -> the `%` operator.** The largest unmapped import in **both** games (637 binary-wide sites here, 37 on Fluffy Field). `OpCode` has `Divide` but no `Modulus`, which is why the math table skips it: add `Modulus` to the enum (one line in an upstream file), one case in the generator beside `Divide`, and `"fmod" => …` in the fork's own table. Exact, not an approximation. | `ISIL/OpCode.cs` (1 line) + `IlGenerator.Fork.cs` + `NewArmV8InstructionSet.Fork.cs` | ~8 | 637 binary-wide | the ELF symbol |
| 4 | **Re-test `IsKeyFunctionAddress` at every hop of `FollowThunks`, not only at the entry**, and **let `SingleCallIn` accept an unconditional `B` as the single call**. Two halves of one hazard, two and three lines. `il2cpp_object_new::actual` (`0x3B20450`) is a key-function value that is *itself* `b 0x3B20760`; the walk goes through it and `KeyFunctionReaching` cannot get back, because it only counts `BL`. Nothing in this export is currently lost to it — the entry guard catches `il2cpp_codegen_object_new` first — so this is insurance, not a fix. | `NewArmV8InstructionSet.Fork.cs` | 5 | 0 today | the resolved-address set |
| 5 | **Follow a `str x30 / bl X / … / ret` wrapper the way a `b` thunk is followed** — body ≤6 instructions ending in `RET`, exactly one `BL`, everything else a frame save/restore, `MOV`, `MOVZ` or memory barrier; no second `BL`, no `ADRP`, no conditional branch. Guarded by `IsKeyFunctionAddress` on the entry exactly as `FollowThunks` is. **Measured before proposing: that predicate matches exactly 2 addresses in the whole 6.7 MB `.text` that generated code ever calls** — `0x3AE41B4` (294,611 binary-wide, the write barrier) and `0x3B1D4A0` (28,169). Neither appears in this export's census, so it buys nothing *here*; it is the general form of a family that will. | `NewArmV8InstructionSet.Fork.cs` | ~20 | 0 in this export | instruction shape only |
| 6 | **`sinh`/`cosh`/`tanh` and `ldexp` in the math table.** One word each in the existing `switch`; `ldexp(x,n)` is `x * 2^n` and wants the `exp2` treatment. | `NewArmV8InstructionSet.Fork.cs` | ~5 | 60 binary-wide | the ELF symbol |
| 7 | **Count callers over the whole executable image, not `.text`.** `ElfFile.GetEntirePrimaryExecutableSection()` returns `.text` alone and the generated code is in a separate `il2cpp` section, so `FindThunks` ranks candidates by 6.7 MB of callers while ignoring 75 MB. On this binary it still picks right (`0x3AE444C` has 105 `.text` callers against 0 for every rival) but by a margin that is an accident. A fork-only "all executable sections" helper on `ElfFile`; never change the upstream signature. | `NewArm64KeyFunctionAddresses.Fork.cs` + `ElfFile.Fork.cs` | ~15 | correctness insurance | `SHF_EXECINSTR`, not a name |
| 8 | **Give `memcpy`/`memset`/`memmove` a marker that says what they are.** 183 sites, 11% of `notfound`, and the same on Fluffy Field. Not a call fix — a naming fix, so the census stops charging a deliberate decision to this family. | `NewArmV8InstructionSet.Fork.cs` | ~5 | 183 | the ELF symbol |

**Not proposed, deliberately:**

* the `str x30 / bl <raise>` throw wrappers — `0x3AE445C` (143,149 binary-wide), `0x3AE4320`, `0x3AE4464`,
  `0x3AE4454` — never `RET` and so never match #5's predicate. They belong to
  `MetadataResolver.ThrownExceptionAt` / `ThrowHelperRecovery`, and
  `il2cpp-a-branch-inside-is-not-a-callee` is the record of what loosening the follow rule there costs.
  None of them is in this export's census, so they are already handled.
* anything about `__cxa_*` (24 sites) or exception recognition — measured, and it is not the problem.

**#1 is the whole round.** It is also the one that has to be built carefully: it invents a key function
Cpp2IL does not have, and `il2cpp-a-thrown-body-scores-as-a-whole-one` says to count generation failures
beside the scorers whenever a call becomes a call that was not one before. The vote gives it the same
safety property `AttemptInstructionAnalysisToFillGaps` has — if the idiom is not overwhelmingly the most
common, it declines and nothing changes.
