# Recovering IL2CPP method bodies — where this stands

This fork exists to recover the method bodies of an arm64 Android IL2CPP build as C# a Unity project will
compile and run. This file is the handover: what the state is, how it is measured, what has been tried, and
what is left. `CLAUDE.md` is the working rules; `External/Cpp2IL/FORK.md` is how the vendored fork stays
mergeable; `LocalPackages/README.md` is the fix-by-fix changelog.

## The state, at 1.0.203

Measured against the original Unity project the build came from (96 files, `Assets/AAA/CF`), and against the
binary itself.

| | |
|---|---|
| method bodies discarded as unreadable | **0** (was 907) |
| `error CS` in the exported project | **0** |
| Unity 6000.0.78f1 batch import | **0 errors, 0 shader errors**, 533 files |
| decisions surviving (`if`, loops, `switch`, `?:`, `&&`) | **92.2%**, and 123 of 141 methods keep every one |
| whole methods, against the original | **274** of 415 measured |
| whole bodies, game-wide | ~2290 of 2946 |
| operations surviving, judged from the binary alone | calls **78.6%**, fields 74.4%, literals 82.8% |
| unresolved memory reads, game-wide | **2405** (2582 before the generic work) |
| calls still made through an unnamed pointer | **121** (155 before) |
| pure functions that execute identically to the original | 14 of 24 in the corpus, 6 of 10 in the game |

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

There is also a **ground-truth corpus**: `scratchpad/corpus/`, a Unity project written to be recovered and
built to arm64 IL2CPP on purpose, so every method has known source on shapes chosen rather than found.
`autodiff.py` runs the whole of it without being told what to test.

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

**A pass that finds nothing is usually in the wrong place, not wrong.** Where a pass runs in
`Analysis/ForkPipeline.cs` is as load-bearing as what it does; the reason for each position is written beside
it.

## What is left, ranked, and what it will cost

| family | size | what it needs |
|---|---|---|
| **generic sharing** | ~470 placeholders left: 133 rgctx reads, 121 indirect calls, 274 interface-dispatch | An open shared body has no instantiation. Two of the three ways in are now taken — a type parameter standing for itself (−155 reads) and the invoker entry point (−14 calls) — and what remains is the interface walk, where the interface being searched for is itself an rgctx entry. |
| **Disarm decode coverage** | 158 occurrences, 80 methods | `INVALID`/`UNIMPLEMENTED` sentinels from the disassembler package. Needs decoding in the fork, as was done for the logical-immediate bug, or an upstream fix. |
| **the inlined cast** | 22 reads of `typeHierarchyDepth`, plus the `typeHierarchy` chain behind them | `klass->typeHierarchy[depth - 1] == target` is how `isinst` compiles inline. Both fields are named now, so this is a defined pass rather than a guess. |
| width, I4 against I8 | 15 sites | `SXTW`/`SMULL` lift to a plain move because ISIL carries no width. Needs a new opcode; small payoff. |
| rank-2 arrays, non-float structs, `String._stringLength` | one corpus method each | each described in memory |

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
* `il2cpp-struct-layouts`, `aapcs64-argument-registers` — the runtime facts worth not rediscovering
