# Recovering IL2CPP method bodies — where this stands

This fork exists to recover the method bodies of an arm64 Android IL2CPP build as C# a Unity project will
compile and run. This file is the handover: what the state is, how it is measured, what has been tried, and
what is left. `CLAUDE.md` is the working rules; `External/Cpp2IL/FORK.md` is how the vendored fork stays
mergeable; `LocalPackages/README.md` is the fix-by-fix changelog.

## The state, at 1.0.517

Measured against the original Unity project the build came from (96 files, `Assets/AAA/CF`), and against the
binary itself.

> **The scorers were lost and rebuilt** between 1.0.453 and 1.0.454, and `cfscore`/`compare2`/`decisions` are
> on a **new scale** from then on: they count every member with a body rather than the 423 the old filter
> chose, and their marker vocabulary is taken from `IlGenerator`'s own `Ldstr` operands. Numbers below are
> not comparable with the 1.0.327 table this replaces. `roundtrip` and the corpora carry across unchanged.
> Durable copies of all of them are in `scratchpad-tools/`.

| | |
|---|---|
| method bodies discarded as unreadable | **0** (was 907) |
| `error CS` in the exported project | **0** |
| Unity 6000.0.78f1 batch import | **compiles**, `Assembly-CSharp.dll` produced |
| **bodies the generator failed to build** | **25-26** — jitters by two between identical builds |
| whole methods, against the original | **326** of 443, and **62 of 96 files** have nothing left in them |
| whole bodies, game-wide | **2723** of 3609 (75.5%), 162 dead |
| decisions surviving (`if`, loops, `switch`, `?:`, `&&`) | **94.3%**, 1259 of 1335 |
| operations surviving, judged from the binary alone | **1296** methods whole of 1993 measured |
| unresolved memory reads, game-wide | **1256** (1612 before the class-pointer work at 1.0.466) |
| commented statements, game-wide | **2328** (2350 at 1.0.478) |
| calls that still resolve to nothing but an address | **288** (344 before 1.0.463) |
| pure functions that execute identically to the original | **38 of 43** in the corpus |

Of the five corpus methods that still behave differently, `Distance` and `Divide` are corpus artefacts rather
than defects, `Bits` needs a 32-bit truncation the fork has priced and declined, and `Tally` and `Guarded`
are open. `SumSteps` closed at 1.0.493.

## How anything here is judged

Five scorers, in `scratchpad/`. **None subsumes another**, and the session that built them found a defect that
four of the five could not see.

| | asks | needs the original? |
|---|---|---|
| `cfscore.py` | did this method come back whole, against the original | yes |
| `compare2.py` | the same, over every body in the game | yes |
| `roundtrip.py` | did the calls, fields and literals the **binary** names survive | **no** |
| `decisions.py` | did the branching survive | yes |
| `difftest.py` / `autodiff.py` | run it and compare the answers | yes |

`roundtrip.py` is the one that transfers to a game nobody has the source of. `difftest.py` is the only one
that can tell a method that is right from one that merely looks right — it found nine methods rated *full* by
every other scorer of which only two actually worked.

There are also **two ground-truth corpora**: `scratchpad/corpus/` and `scratchpad/corpus6/`, the same Unity
project written to be recovered and built to arm64 IL2CPP on purpose, so every method has known source on
shapes chosen rather than found. `autodiff.py` runs the whole of either without being told what to test.

Two of the seven the corpus still fails are **not defects at all**: `Divide` and `Guarded` both divide, and
arm64's `UDIV`/`SDIV` do not trap - il2cpp emitted no zero check, so clang deleted the handler and the shipped
binary returns a number where the source would have caught. Writing a `catch` there would be fabrication.

The corpus was grown from 29 methods to 43 once it stopped failing, across eight families it had no shape for
- lambdas, StringBuilder and string splitting, dictionary and list mutation, switch on a string, shifts and
masks, boxing, checked arithmetic, try/finally, and virtual dispatch through a base class. Seven of the fifteen
passed first time; the other seven were defects, of which two are fixed and one half fixed. **Growing the
corpus is the cheapest way to find a defect** - one Unity build, and it does not depend on guessing.

`Areas` and `Boxed` were fixed since - an inlined constructor built its objects with zeroed fields, and a cast
whose answer is read through is an unboxing rather than a cast. `Filtered` and `Tally` remain, both blocked on
a struct wider than sixteen bytes coming back through the pointer in `x8` rather than in a register.

Of the three original failures, **none is a known defect** - `Distance` is a corpus artifact (the build
strips `Mathf.Sqrt`, so `FSQRT` has nothing to name it back to), `Divide` is a faithful transcription of a
handler clang deleted, and `SumSteps` sits behind a deliberately-chosen rule. A new failure there is a
regression by definition.

They are built on **different Unity versions** — 2022.3.62f2 and 6000.0.78f1 — and that is the point. The game
itself is Unity 6, so for a long time the only corpus was on the *older* editor and nothing said so. Building
the second one found two wrong-value bugs that had nothing to do with the version
(`subs` recording its comparison after overwriting the register it compared; a phi slot left unfilled where
both arms of a branch land on the same block) and **nothing** to do with the `Il2CppClass` offsets, which were
the predicted risk — all three binaries report metadata version 31. Both corpora now score **22 of 25 and fail
on exactly the same three methods**, which is the strongest evidence the fork has that it is recovering
il2cpp rather than recovering this build.

```sh
scratchpad/bump.sh <old> <new>          # version must change; NuGet caches by version
scratchpad/riprun ... <out> <log> 3 fast
scratchpad/cfscore.py <out>             # and compare2 / roundtrip / decisions / difftest
scratchpad/unityverify.sh <out> <log>   # before staging, not every round
```

## Four rules that were learned the hard way

**A marker is a signpost; a wrong value is a lie that scores well.** A read that becomes a placeholder is
better than one that quietly returns element zero, even though the placeholder costs `full` and the wrong
answer does not. `CLAUDE.md`'s keep-rule was amended for this.

**Naming an inlined instruction costs branches.** Turning an arm64 instruction into the library method it was
compiled from removes the marker and recovers the call — and three times has cost more decisions than it
bought, because the instruction is also what the *next* instruction reads. Measure `decisions.py`, not the
marker count.

**A baseline is a number measured now, from this tree.** The export is not textually deterministic — two runs
of one source differ in 424 of 533 files — and `compare2`'s game-wide count moves with that while `cfscore`
and `decisions` do not. A real win was nearly discarded against a number from several builds earlier.

**The disassembler is not an oracle either.** `Disarm` reports `fabd`'s second source as its first, so
lifting its operands as given made the difference between a value and itself - zero, compiling, wrong
everywhere. It reports an instruction it cannot place at all with **no address**. It reports a vector load's
offset unscaled. Every one of those was found by disassembling the same word with the toolchain's own
disassembler and comparing; `scratchpad/neon.py` does that over every distinct word in the game and is the
reason the fork's own decoders can be trusted.

**Methods are lifted in parallel.** Per-method state on the instruction set is shared between them. One
dictionary written from several methods at once cost 570 bodies and looked like an unrelated crash - and every
measurement taken while it was live was noise. `[ThreadStatic]`.

**A revert is a measurement, not a diagnosis.** Three of the reverts recorded here as "naming an instruction
costs branches" were nothing of the kind. `FRINT` cost 33 decisions and *all 33 were in one method*, which
`Math.Floor(double)` made uncompilable; `CINC` was reverted twice for an operand-count bug in how it was
lifted. Both are now in, and both moved every scorer up. Before reverting on a number, `diff` the per-method
output of the scorer that moved and find out which method it came from.

**A pass that finds nothing is usually in the wrong place, not wrong.** Where a pass runs in
`Analysis/ForkPipeline.cs` is as load-bearing as what it does; the reason for each position is written beside
it.

## What is left, ranked, and what it will cost

| family | size | what it needs |
|---|---|---|
| **reads through a base with no type** | 2508 game-wide | **Measured and closed for now.** 739 are the stack pointer, 377 are calls to runtime helpers no symbol names, and 1158 are cascades of those. Four fixes were tried and every one measured worse or did nothing - see `il2cpp-untyped-bases-are-downstream` in memory before trying a fifth. Thunk following is dead too: of the 377 targets only 13 are a single branch and none reaches a managed method. |
| **generic sharing** | ~470 placeholders left | Two ways in are taken — a type parameter standing for itself (−155 reads) and the invoker entry point (−14 calls). What blocks the rest is now **proved**: a shared body whose result is a type parameter takes a hidden return buffer in x0, so every argument is one register out and nothing its context holds resolves. The mechanism is not in doubt; what is missing is a way to tell, from metadata alone, whether the body at an address is the shared one or a specialisation. Assuming every generic-returning method has the buffer makes it far worse. |
| **vector lanes the model refuses** | most of the 32 markers | `Analysis`-free: `InstructionSets/VectorLanes.cs` decodes the Advanced SIMD encodings the disassembler package refuses and lifts each lane as a register of its own. What is left is mostly **refusal rather than ignorance** - the model will not emit half a vectorised expression, so one lane it cannot follow costs the whole instruction. Closing them means following the dataflow further, not decoding more. `frintm`/`frintp` in that set are deliberate. |
| the depth pre-check an `is` leaves behind | 11 reads | `Analysis/InlinedTypeTestRecovery.cs` claims every one of these tests; what is left is the `typeHierarchyDepth >= depth` guard in front of it, which the `isinst` subsumes but which still feeds a branch. Removing it means removing control flow, so it is a deliberate decision rather than an oversight. |
| width, I4 against I8 | 15 sites | `SXTW`/`SMULL` lift to a plain move because ISIL carries no width. Needs a new opcode; small payoff. |
| rank-2 arrays, non-float structs, `String._stringLength` | one corpus method each | each described in memory |
| calls into the C library | 224 game-wide, **11 in the 96** | 580 calls resolve to no managed method; 224 go through the procedure linkage table, of which 79 are `Mathf`/`Math` compiled to a real call. Naming one means resolving its linkage-table slot through a relocation to a symbol, which LibCpp2IL does not expose. Measured and left: six cleanly nameable calls inside the measured files does not pay for the ELF work. |


## Things that are **not** defects — do not "fix" these

* **A missing `catch`.** `sdiv` does not trap and il2cpp emitted no zero check, so clang proved the try body
  non-throwing and deleted the handler — confirmed from `.eh_frame`, whose FDE for that function has no LSDA
  while the same binary has plenty that do. The recovery is a faithful transcription; producing a `catch`
  would be fabrication.
* **`CINC`.** Measured and reverted three times.
* **Most of the "mangled" marker.** Of 72, about 53 are redundant with markers already counted, cosmetic, or
  charged to a phantom method called `if` that `cfscore`'s pattern invents out of an `else if` line.
* **`List<T>._items.Length`.** It is the capacity, not the count. 64 of 81 `_items` reads are that.

## The layouts are no longer guesswork

`Cpp2IL.Core/Il2CppClassLayout.cs` holds every field of `Il2CppClass` and `MethodInfo` for this build,
computed from the struct Unity itself ships in
`Unity.app/Contents/il2cpp/libil2cpp/il2cpp-class-internals.h`. Five offsets found empirically over earlier
sessions — `element_class` 0x40, `interfaceOffsets` 0xB0, `static_fields` 0xB8, `rgctx_data` 0xC0,
`interface_offsets_count` 0x12E — all agree with it exactly, which is what makes the rest trustworthy. The
upstream `Il2CppClassUsefulOffsets` describes an older metadata and disagrees on three of them.

**Read the header before guessing at an offset.** One command settled what several rounds of inference had
only narrowed:

* `0xFC` is `stack_slot_size` — real data an inlined allocation computes with, so answering it with a constant
  would compile and be wrong. That is why the quick way through these reads was refused.
* `0x135` is the byte holding `initialized_and_no_error` — bookkeeping the program never sees, so answering it
  is right, exactly as for the flag already recognised at 0xE4.
* `MethodInfo + 0x10` is `invoker_method`, one of **three** pointers a body can reach a method's own code
  through and the one a fully shared generic is entered by. Only the plain pointer at zero was accepted.

## Where the knowledge lives

Thirty memory files under `~/.claude/projects/-Users-playviet-Documents--BZ-AssetRipper/memory/`, indexed by
`MEMORY.md`. **Read the index before searching the code** — a runtime struct offset costs a few hundred tokens
there and a build, an export and a disassembly session to rediscover. The ones that change how you work:

* `il2cpp-recovery-verification` — the measure-first loop
* `il2cpp-execution-oracle`, `il2cpp-decisions-are-the-unmeasured-half` — why five scorers
* `il2cpp-ground-truth-corpus` — the corpus and how to rebuild it
* `il2cpp-what-the-agents-found` — the ranked map of everything still broken
* `il2cpp-naming-an-instruction-costs-branches`, `il2cpp-remeasure-the-baseline` — the two traps
* `il2cpp-struct-layouts`, `il2cpp-read-the-header-unity-ships`, `aapcs64-argument-registers` — the runtime facts worth not rediscovering
