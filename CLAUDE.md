# Working in this repo

This fork of AssetRipper exists to recover IL2CPP method bodies from an arm64 Android build. Everything below
is about getting that done in as few tokens and as little wall-clock time as possible.

`RECOVERY.md` beside this file is the long form: how to bring up a **new** game, what each scorer is blind
to, what decides keep-or-revert, the families to check in order, and how to work several games at once so a
fix is general rather than fitted. Read it when starting on a different build; this file is the short version
and wins where the two disagree.

## Read memory before searching the codebase

`~/.claude/projects/-Users-playviet-Documents--BZ-AssetRipper/memory/` holds what earlier sessions worked out
and the code does not say: the goal, the verification loop, the toolchain, the runtime struct offsets, the
upstream-mergeability rule. `MEMORY.md` is loaded at session start — it is the index, one line per memory.

**Read the memory the index points at before you grep for the same thing.** A memory is one file and costs a
few hundred tokens; rediscovering an il2cpp field offset costs a build, an export and a disassembly session.

**Write memory as soon as something is worth keeping**, not at the end of the session — a fact learned and not
written is a fact the next session pays for again. Worth keeping: a runtime layout, a measurement that decided
something, a tool invocation that is hard to reconstruct, a preference stated once. Not worth keeping:
anything the code, git history or this file already says.

**Keep the index honest.** When a memory turns out to be wrong, edit or delete it in the same turn — a stale
memory is worse than a missing one, because it is trusted. When a memory names a file, function or flag,
check it still exists before acting on it.

## Search codebase memory before reading the code

The repository is indexed into a knowledge graph reachable through the `codebase-memory` tools. It is the
second thing to consult, after the notes above and before any grep:

- **`search_code`** finds a pattern and returns the *containing functions*, deduplicated and ranked by
  structural importance, rather than a list of lines. The project is
  **`Users-playviet-Documents-_BZ-AssetRipper`** — one hyphen before `_BZ`, and the tools will not find it
  under any other spelling.
- **`search_graph`, `trace_path` and `get_architecture`** answer the questions grep cannot: what calls this,
  what would break, which pass runs before which.

Why it goes first: this fork is a hundred small passes whose *order* is the whole design, and the question is
almost always "who else touches this shape" rather than "where is this string". A whole session went on
`SubCellVisual::UpdateFaceTracking` losing its `Mathf.Sin`, and the answer was that two passes in the same
hook wrote and then un-wrote one register name. `search_code("StackSlots.ValuePrefix")` names all five passes
that touch it in one call, `OutPointerSlotResult.Run` and `OutParameterWriteback.Run` among them.

**Reindex when a task is finished** — after the commit, not before, so the graph matches what is staged:

```
index_repository(repo_path="/Users/playviet/Documents/_BZ/AssetRipper", mode="fast")
```

`fast` is the mode for this repo; `full` adds similarity edges and takes long enough to be its own task. An
index that lags the tree is the same hazard as a stale memory: it is trusted, and it is wrong.

## The loop

Never report a recovery change without measuring it. The loop, in order:

```sh
scratchpad/bump.sh <old> <new>              # version must change; NuGet caches by version
scratchpad/riprun ... <out> <log> 3 fast    # `fast` export: ~123s, vs ~360s full
scratchpad/cfscore.py <out>                 # 96 originals, scored method by method
scratchpad/compare2.py <out>                # every body in the game, not just the 96
scratchpad/roundtrip.py <out>               # what the binary says the method does, no source needed
scratchpad/decisions.py <out>               # did the branching survive
scratchpad/difftest.py <out>                # run it against the original and compare the answers
```

Five scorers, because each is blind somewhere the next one is not. The last is the only one that can tell a
method that is right from one that only looks right, and it is the one to believe when they disagree. There is
also a corpus built on purpose - `scratchpad/corpus/`, a Unity project compiled to arm64 il2cpp - where every
method has known source; `scratchpad/autodiff.py` runs the whole of it without being told what to test.

Keep a change only if it makes the recovery **better**, which is not the same as making `full` go up. `full`
counts methods that compile whole, and a method can compile whole and be wrong: an execution oracle found nine
of ten methods rated `full` and two of ten that actually work. So weigh the correctness measures - does it
still branch, does it still compute the right answer - above the compilability ones, and where they disagree,
say which you followed and why. A read that becomes a marker is better than a read that quietly returns
element zero, even though the marker costs `full` and the wrong answer does not.

`commented` is a noisy signal — one uncompilable declaration cascades
into every later statement that used the local, so most commented statements are collateral, not causes. Chase
the markers (`unmanaged`, `notfound`, `notimpl`, `mangled`) instead; the cascade clears itself.

Run the **full** export and Unity verification once, before staging — not every round.

## Speed and tokens

- **Start the export in the background and edit the next change while it runs.** An export is minutes; do not
  sit in front of it.
- **Batch independent changes into one round** where they touch different passes, and split them again only if
  the round measures worse.
- **Measure with `cfscore.py`, not by reading exported files.** Reading a recovered `.cs` costs thousands of
  tokens and tells you less than the score does. Read one file only to find a root cause, and read the part
  you need — `grep -n` for the shape first, then `sed -n` the range.
- **Read ISIL, not exported C#, when diagnosing a body** — `scratchpad/probe asm <addr>`. It is the level the
  passes actually operate on.
- **Make independent tool calls in one message.** Two greps that do not depend on each other should not be two
  round trips.
- Do not re-read a file just edited; do not re-derive a fact already established in the session.

## Stay mergeable with both upstreams

This project is a fork of two repos at once, and **both are updated from regularly**:

| | upstream | lives in | update with |
|---|---|---|---|
| AssetRipper | `github.com/AssetRipper/AssetRipper` (remote `upstream`, `master`) | this repo | `/update-assetripper` |
| Cpp2IL | `github.com/SamboyCoding/Cpp2IL` (`development`) | `External/Cpp2IL` (vendored) | `/update-cpp2il` |

Every change has to be written so that a later release of either can be taken without a fight. That is a
constraint on *how* code is written here, not a step at the end:

- **Never change the signature of an upstream method** in either tree. Clone it beside the original and
  change the clone, so a merge never has to reconcile two versions of one declaration.
- **Put new work in files upstream does not have.** In Cpp2IL: `Cpp2IL.Core/Analysis/*.cs` for a new pass, or
  a `*.Fork.cs` sibling that turns upstream's class `partial` and adds nothing else to the upstream file. In
  AssetRipper: a new file in the owning project, not a rewrite of an existing one.
- **What must go in an upstream file should be one line.** A case in a switch, one call at the one point in a
  pipeline where it belongs, one condition — with the bulk in the fork's own file. `ForkPipeline.cs` exists
  so that the Cpp2IL pipeline is one call per hook rather than a pass list inlined into upstream's method.
- **`FORK.md` and `rebase-onto-upstream.sh` are this repo's, not the fork's.** Any `rsync --delete` from a
  Cpp2IL working copy into `External/Cpp2IL` must exclude them, or it silently deletes both.
- **Record it.** `External/Cpp2IL/FORK.md` lists the base commit and every upstream file the fork touches,
  with what it changes and why. If a change adds a file to that set, add the row in the same edit — that
  table is what makes a rebase a review rather than an archaeology exercise.
- **A merge or rebase is not done until it is measured.** Run the export and `cfscore.py` afterwards: a
  conflict resolved the wrong way shows up as `full` going down, and nowhere else.

## Rules that do not bend

- **Build with `~/.dotnet`.** The system SDK fails with a misleading missing-member error.
- Passes are ordered in `Cpp2IL.Core/Analysis/ForkPipeline.cs`, and where each runs is as load-bearing as what
  it does. Write down the reason beside it.
