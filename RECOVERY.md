# Recovering an il2cpp game, and learning from more than one

This is the operating manual for pointing this fork at a build and getting method bodies back. It is written
so that a session starting cold can bring up a **new** game, score it honestly, and improve the fork without
repeating work — and so that several games can be worked at once, each holding the others honest.

`CLAUDE.md` is the short version and takes precedence where the two disagree. The long-form record of every
family, fix and dead end is in `~/.claude/projects/.../memory/`, indexed by `MEMORY.md`. **Read the memory
before searching the code**; a memory costs a few hundred tokens and rediscovering a runtime offset costs a
build, an export and a disassembly session.

---

## 1. What a new game needs

| | |
|---|---|
| The build | an `.apk`/`.aab`, or `libil2cpp.so` + `global-metadata.dat` |
| Architecture | **arm64 only.** Every lifter change in this fork is arm64. An x86-64 build gets none of it |
| Unity version | read from the import log: `found Unity version: X`. Install that exact editor if you can |
| Original source | **optional**, and only for scoring — recovery never reads it |

The editor matters twice: it compiles the export for the gate, and `UnityEditorReferences` uses its
unstripped assemblies so the repair pass stops discarding code the editor would accept.

## 2. Bring-up, in order

```sh
scratchpad/bump.sh <old> <new>            # ALWAYS. NuGet caches by version; without a bump you measure the old build
scratchpad/riprun ... <out> <log> 3 fast  # `fast`: ~2 min, scores identically to the full export
```

`riprun` is `scratchpad/riprun/` (a project, not a script) — run
`dotnet scratchpad/riprun/bin/Release/net10.0/riprun.dll <apk> <exportRoot> <log> 3 fast`. If it is missing,
`memory/tools/` holds the whole toolchain; every old log ends with a settings dump that recovers a forgotten
option.

Build with `~/.dotnet`. The system SDK fails with a misleading missing-member error.

## 3. Scoring — and what each scorer is blind to

Run **all** of them. Each is blind exactly where the next one sees.

| tool | needs source? | what it answers | blind to |
|---|---|---|---|
| `compare2.py <export>/ExportedProject` | no | every body in the game: whole / partial / dead | whether a whole body is *right* |
| `cfscore.py <export>` | yes | method by method against the original | anything outside those files |
| `decisions.py <export>` | yes | did the branching survive | values |
| `roundtrip.py <export>/ExportedProject <rt.jsonl>` | **no** | do the calls, fields and literals the binary names still appear | ordering, values |
| `autodiff.py` (corpus) | yes | **runs** the recovered code against the original and compares answers | only covers the corpus |

`grep -c 'Decompiling.*failed' <log>` **every round.** A body the generator threw on has one statement and
no marker, so it scores as *whole* on every other measure — a change that broke 691 methods once read as an
improvement. The count jitters by ±2; treat anything larger as real.

### Without original source

`compare2`, `roundtrip` and the generation-failure count work on any binary. That is enough to run the loop:
`roundtrip` is the one that can tell a method that is right from one that only looks right, because it asks
the binary rather than the export.

### First run on a new game: calibrate before believing

Score the **committed** build first and write the numbers down. A rebuilt or re-pointed scorer that reports
differently looks exactly like a code regression. Two faults found this way, both making recovery look worse
than it was: members named after their `[Attribute(...)]` instead of themselves, and the scorer demanding
methods that sit behind `#if UNITY_EDITOR` and are not in the binary at all.

## 4. The loop

```
read the memory index  →  measure  →  find a root, not a symptom  →  read the ISIL  →
one change  →  measure all scorers + gen-fail  →  keep or revert with the numbers  →  record
```

* **Start the export in the background and edit the next change while it runs.**
* **Batch independent changes** that touch different passes; split them again only if the round measures worse.
* **Measure with the scorers, not by reading exported files.** Read one file only to find a root cause, and
  read the part you need: `grep -n` for the shape, then `sed -n` the range.
* **Read ISIL, not exported C#, when diagnosing** — `scratchpad/probeg isil <Type> [Method]`. It is the level
  the passes operate on. `probeg asm <Type> [Method]` when the ISIL itself looks wrong.
* Run the **full** export and the Unity gate once before staging, not every round. Measured at 1.0.673: the
full export scores **identically** to `fast` on every scorer - `commented` 1907, 72 of 96 files clean, same
markers - and compiles in Unity to a 947 KB `Assembly-CSharp.dll` with 0 `error CS`. `fast` differs only in
prefab outlining, mesh separation, deduplication and dummy shaders, none of which touch the recovered C#.

## 5. What decides keep or revert

**Keep a change only if it makes the recovery better, which is not the same as making `full` go up.** A method
can compile whole and be wrong: an execution oracle found nine of ten methods rated `full` and two of ten that
actually work.

Order of authority, highest first:

1. **`autodiff` / the execution oracle** — it ran and gave the right answer.
2. **`roundtrip`** — the binary says this method calls X and touches Y, and the export still does.
3. **`decisions`** — the branching survived.
4. **`compare2` / `cfscore` `full`** — it compiles whole.
5. `commented` — noisy. One uncompilable declaration cascades into every later statement that used the local,
   so most commented statements are collateral. Chase the markers (`unmanaged`, `notfound`, `notimpl`,
   `mangled`) instead; the cascade clears itself.

Where they disagree, say which you followed and why. **A read that becomes a marker is better than a read
that quietly returns element zero**, even though the marker costs `full` and the wrong answer does not.

**Revert what is inert.** Byte-identical means the premise was wrong; find out why before rebuilding, and
record it — a documented negative is worth as much as a fix, because it stops the next session repeating it.

## 6. Where the errors come from, and what to try in order

For a new game, work this list top-down. Each entry names the measurement that sizes it.

1. **Is the disassembler right?** Differential-test it against llvm over the whole binary, by the numbers.
   Six wrong-value bugs were found this way and none was findable by reading code. Everything downstream
   inherits these, so do it first.
2. **What does the compiler say?** `REPAIR_WHY=<file>` writes the C# diagnostic id **and message** behind every
   statement the repair comments out. The id alone is useless (`CS0030` covers 1800 statements); the message
   splits it into families with separate answers. This seam paid more than any other.
3. **What is the first commented statement of each body that nothing else blocks?** `roots.py` — counting all
   commented statements gives a map of cascades; counting the first one of each body gives the causes.
4. **What do the markers decompose into?** Group `unmanaged` reads by the shape of their base, `notfound` by
   address, `indirect` by call shape. Then chase the largest, and **instrument the refusal** — put a counter at
   every `continue` in the pass that should have handled it and print on a modulus smaller than the population.
5. **Which bodies kept nothing?** `deadbodies.py`. Mostly genuinely empty methods; check before chasing.

### Families with known answers — check each on a new game

Runtime and ABI, all arm64/il2cpp and expected to transfer:

* a struct of floats is one register per field, both as parameter and as argument
* a big struct returns through `x8`; a `Vector2` returns in `v0`
* the register holding a stack slot's address **is** the slot — do not dereference it
* a class pointer is named from the operand type, not read from memory
* `il2cpp_defaults` is a table of built-in classes; locate it by counting
* an rgctx entry, a shared generic's `MethodInfo`, and the instantiation written down in it
* every event accessor is an `Interlocked.CompareExchange`
* a `.plt` stub is an import — follow it to its JUMP_SLOT relocation

Compiler idioms:

* the subscript lives in the addressing mode; a pointer-stepping loop must become an index
* a bitfield move is a shift either way; a conditional compare is an `&&`
* one wide store can be two adjacent fields
* an inlined constructor is an allocation plus field writes
* the bounds check outlives the access it belongs to

Language-level, where recovery is right and C# cannot say it:

* `goto` into a block, `memcpy`/`memset`, a private field of a type this project does not declare

## 7. Rules that do not bend

* **Build with `~/.dotnet`.**
* **Bump the version before every export.** Twice in one session a measurement was of the previous build
  because a bump silently failed — check its output, not just the export's.
* **Where a pass runs is as load-bearing as what it does.** Write the reason beside it in `ForkPipeline.cs`.
* **Stay mergeable with both upstreams.** Never change the signature of an upstream method — clone it beside
  the original. Put new work in files upstream does not have. What must go in an upstream file should be one
  line. Record it in `External/Cpp2IL/FORK.md` in the same edit.
* **Score only a finished export.** A half-written one reported `full=344, commented=0` where the truth was
  295 and 520.
* **Keep `probe` and `riprun` on the same build.** `probebump.sh` proves it; probe once drifted 75 versions
  and every ISIL reading in between was of code that had since changed.

## 8. Working several games at once

Ground truth from more than one build is what turns a fix from *calibrated* into *general*. It has already
paid: a second corpus on a different Unity version exposed two ordinary correctness bugs that every scorer but
the execution oracle was blind to, and both were version-independent.

**Onboarding a game into the set**

1. Export and score it as-is. Record the baseline in memory, dated, with the fork version.
2. Point `cfscore` at its originals (`ORIGIN`), and exclude what the build cannot contain
   (`#if UNITY_EDITOR` and friends) before treating anything as missing.
3. Regenerate `rt.jsonl` for it so `roundtrip` has that binary's ground truth.
4. Build a small **corpus** for it if the game has shapes the existing corpus lacks — a Unity project compiled
   to arm64 il2cpp on purpose, where every method has known source and can be *run*.

**The rule that makes it training rather than fitting**

> A change is only kept when it does not make **any** game in the set worse.

Measure every game before staging. A predicate narrowed to make one binary's numbers move is fitting; a
predicate that holds on two binaries built by different Unity versions is a rule. Several guards in this fork
are narrow for exactly this reason and are flagged as one-game-deep in
`memory/il2cpp-how-general-is-this-fork.md` — those are the first things a second game should re-test.

**What a disagreement between games means**

* fails on the new game only → a pinned constant or a predicate calibrated on the old one. Re-derive it from
  the header Unity ships rather than by inference.
* fails on both → a real defect, and the more valuable find.
* passes both but the oracle disagrees → a wrong value no marker shows. These are the expensive ones and only
  execution finds them.

**Record per game, not once**: baseline numbers, its Unity version and metadata version, which families it
exercises that others do not, and every negative result with its numbers.
