# Snacky Dash 1.11.0 — the recovery rounds

Target `/Users/playviet/Documents/_APK/Snacky Dash/Snacky Dash_1.11.0.xapk`
Export `/Users/playviet/Documents/_APK/Snacky Dash/Snacky Dash_1.11.0_1`
Unity 6000.0.66f2, il2cpp metadata 31,1, arm64, 149 MB `libil2cpp.so`, 167817 methods.
**No original source.** Score with `gamescore.py` + `anyverify.sh` + `gamefilter.py`; never `cfscore`/`allscore`.

`RIPRUN_ORIGIN=none` is mandatory — unset substitutes Fluffy Field's DOTween into this game's export.

## Round 1 — 1.15.0, baseline (2026-08-20)

Full Level3, no `fast`. What the log said, before scoring:

| | |
|---|---|
| decompile failures | **28**, all one root: `GenericInstantiation.Instantiate` index out of range |
| bodies discarded | **7**, all `StackImbalanceException` (Fluffy Field: 907) |
| eh tables | 500,948 FDEs, 16,900 functions with a catch, **0 parse failures** |
| relinking | **disabled** — `No installed Unity 6000.0.66f2 was found` |
| `[SerializeReference]` | unsupported; `MMF_Player` MonoBehaviour data unreadable (asset data, not code) |

### What round 1 measured

| | |
|---|---|
| files / bodies | 2787 `.cs`, 15773 bodies, 3663 types, 1 assembly (`Assembly-CSharp`) |
| **full** | **12860 (81.5%)** |
| partial | 2591 (16.4%) |
| dead | 322 (2.0%) |
| **compile gate** | **1 error** across all 2787 files — `CameraTools.cs(238,2) CS1513: } expected` |
| commented | 1930 bodies / 13613 occ |
| unmanaged | 1454 / 13457 |
| indirect | 656 / 1965 |
| notfound | 617 / 1648 |
| notimpl | 35 / 81 |

`notfound` and `indirect` are **15-40x** their Fluffy Field rates (24/39 and 32/35). That gap is the
strongest evidence in the round that something here is fitted to Fluffy Field rather than general.

`json` at `scratchpad/snacky-round1.json`; gate log at `scratchpad/snacky-gate1.log`.

## A rule learned the hard way, 2026-08-20

**riprun deletes the export root before it writes.** Round 2 was pointed at the same directory as round 1 and
destroyed it while three agents were mid-analysis on that tree. **Every round gets its own directory** —
`..._1`, `..._2`, `..._3` — and the previous one is only removed once its `--json` has been written and the
successor has printed `DONE`.

## What was checked and did NOT hold

An audit ranked `NewArm64Utils.cs:57` (Disarm's `NotImplementedException` for `PRFM` re-thrown, costing the
whole body) as CERTAIN on this binary. **It fired zero times**: `grep -c 'Failed to disassemble'` on
`snacky_1.log` is 0, and all 28 failures were the one `ArgumentOutOfRangeException`. The reasoning is sound
and the site is still worth hardening, but it is not a defect of this build. Check a predicted root cause
against the log before spending a round on it.

## Round 2 — 1.15.1, two changes, both already prepared

1. **`GenericInstantiation.Fork.Bind`** — an index past the end returns the parameter un-substituted
   instead of throwing. Source is written and `FORK.md` has its row; **not yet built**. Should take the 28
   failures to 0 and give 19 distinct methods their bodies back.
2. **`ASSETRIPPER_UNITY_EDITOR=/Applications/Unity/Hub/Editor/6000.0.78f1/Unity.app/Contents`** — no code
   change. `UnityPackageIndex.GetEditorPackageDirectories` (`:244`) honours it, and without it
   `TryCreate` returns null (`:68-70`) and `ExportHandler` (`:187-191`) silently drops
   `RelinkedPackageManifestPostExporter`. The riprun comment says relinking is load-bearing for the Unity
   gate: without it the project carries stripped plugin DLLs and the editor's ApiUpdater throws a
   NullReferenceException, which reads as "the recovery broke" and is nothing of the sort.
   6000.0.78f1 is the same Unity 6 major; `anyverify.sh` already picks it by the same rule.

Run it as:

```sh
scratchpad/bump.sh 1.15.0 1.15.1
export RIPRUN_ORIGIN=none ASSETRIPPER_UNITY_EDITOR=/Applications/Unity/Hub/Editor/6000.0.78f1/Unity.app/Contents
scratchpad-tools/exportloop.sh "<xapk>" "<export>" "<logdir>"
python3 scratchpad-tools/gamescore.py "<export>/ExportedProject" --json round2.json
python3 scratchpad-tools/gamescorediff.py round1.json round2.json
scratchpad-tools/anyverify.sh "<export>/ExportedProject"
```

## The queue after that, in order (from `gamefilter.py --queue`)

93.1% of the binary is not the game. Score the 6.91% that is:

| methods | namespace |
|---|---|
| 2451 | `Assembly-CSharp/JuicedUp.Features.Core` — 31% of all gameplay code |
| 907 | `Assembly-CSharp/<global>` — BoosterManager, PillController, Crate |
| 444 | `JuicedUp.Features.WeeklyMissions.Internal` |
| 381 | `JuicesUp.Features.SeasonPass.Internal.Views` |
| 292 | `JuicesUp.Features.SeasonPass.Internal.Core` |

Read `REFUSALS.md`, `ISA-GAP.md`, `EH-AUDIT.md`, `UNITY6-AUDIT.md`, `GAMEFILTER.md` beside this file
before opening a new front — each one already ruled a family in or out.

### What round 2 measured — KEEP

| | round 1 | round 2 |
|---|---|---|
| decompile failures in the log | 28 | **0** |
| rethrown generator crashes in the export | 1 | **0** |
| `package relinking is unavailable` | 1 | **0** |
| full | 12860 (81.5%) | 12861 (81.5%) |
| partial | 2591 | 2592 |
| dead | 322 | 322 |
| bodies / types | 15773 / 3663 | 15775 / 3664 |
| compile gate | 1 × CS1513 | 1 × CS1513 (unchanged, as expected) |
| plugin references the gate found | 117 | 93 |

Kept. The two changes are correct and cost nothing, but be honest about the size: **`full` moved by one.**
The 19 methods the exception was killing live in `Newtonsoft`, `Sirenix`, `Microsoft.Extensions` and
Voodoo's SDK, and the export writes source for **`Assembly-CSharp` only** — so all but one of them were
never in the scored tree at all. The value is that the class of defect is closed and the log is now silent,
not that the score moved. Relinking dropping the plugin references from 117 to 93 is the second change
working: those 24 are now resolved from the editor's packages instead of shipped as stripped DLLs.

`json` at `scratchpad/snacky-round2.json`; gate log at `scratchpad/snacky-gate2.log`.

## Round 3 — 1.15.2, the brace fix

`InvalidSourceRepair.ApplyEdits` (`:1552`) comments a span out with `//` **line** comments and then resumes
copying the source from `span.End` **onto the same builder line**. A `//` runs to the end of the physical
line, so anything that followed the commented span on that line is swallowed. Harmless while the decompiler
writes one statement per line — and `RewriteStructPropertyMember`
(`InvalidSourceRepair.Conversions.cs:144`) does not: it rewrites `worldBounds.center.x = x3;` into a
one-line block `{ Vector3 repairCopy = ...; repairCopy.x = x3; ...; }`. Commenting the middle statement took
the block's closing `}` with it. Everything after — the method's `}` and the class's `}` being commented on
the next attempt, then the eight emptied/`return default` quartets — is consequence, not a second bug.
The mechanism was replayed in Python and reproduces lines 176-177 of the exported file byte for byte.

Fix: new `Source/AssetRipper.Export.UnityProjects/Scripts/InvalidSourceRepair.Braces.cs` plus four call
sites in `InvalidSourceRepair.cs`. Two invariants now enforced rather than argued: never comment a token
outside the body being repaired; never comment text the repair inserted itself.

**Test state, measured both ways:** `dotnet test --filter InvalidSourceRepair` gives **17 passed / 1 failed**
on master and **19 passed / 1 failed** with the fix. The one failure is **pre-existing and unrelated** —
`AnArrowBodiedMemberThatDoesNotCompileIsReplaced` expects `Name => default;`, but the widening repair
promotes `private const string Hidden` to `internal` first, the file then compiles, and no arrow replacement
is needed. The test's expectation is stale, not the code. Left alone.

**Every round gets its own export directory.** Round 3 goes to `Snacky Dash_1.11.0_2`.

## Round 4 — 1.15.4: anonymous types, fmod, the interface census. KEEP

| | round 3 | round 4 |
|---|---|---|
| full | 12861 (81.5%) | **12883 (81.6%)** |
| commented | 13391 occ | **13345** |
| types | 3665 | **3668** |
| empty `.cs` files | 4 | **1** |

**The anonymous type fix.** `--f__AnonymousType0.cs` and two siblings were **0 bytes**: ILSpy hides an
anonymous type from project output because it expects to have rewritten every use back into `new { X = a }`.
On recovered IL it does not — the pattern is not there — so the uses survive as
`_003C_003Ef__AnonymousType0<,>` and the declaration is an empty file beside them. The decisive evidence for
turning the transform off is that `grep -c "new {"` over the whole export is **zero**: it had never once
succeeded. One line, `settings.AnonymousTypes = false`, in `ScriptDecompiler.CreateSettings`.

### The gate is NOT monotonic, and that is the main thing to know about it

Roslyn stops at each tier, so closing one class of error unmasks the next. This session:

| reading | why it moved |
|---|---|
| 1 | a single **parse** error (CS1513) hid every semantic error behind it |
| **1352** | brace fixed → semantic analysis ran for the first time. **Not a regression** |
| 45 | package reference assemblies supplied via `ANYVERIFY_REFS` |
| 25 | anonymous types declared |
| 20 | Timeline / Mathematics / URP references added |
| **1** | the gate had been dropping **124 real `.cs` files** — an export writes an assembly it cannot ship as a DLL as *source* under `Assets/Plugins/<AssemblyName>/`, and `Assembly-CSharp-firstpass` lives there |
| **29** | that 1 was the harness (two `AssemblyInfo.cs` merged into one assembly); excluded → the first honest reading |

**A falling number is not automatically progress and a rising one is not automatically regression.** Say
which every time. `anyverify.sh` now takes the plugin source roots and excludes their `Properties/AssemblyInfo.cs`.

### The 29 real errors

| n | code | what |
|---|---|---|
| 22 | CS7036 | a derived constructor that lost its `: base(...)` |
| 3 | CS7069 | `IValueTaskSource` in mscorlib — the known harness floor |
| 3 | CS0019 | `Operator '>' cannot be applied to 'int' and 'IntPtr'` — a real typing defect |
| 1 | CS1729 | il2cpp **inlined** the intermediate constructor; C# cannot call a grandparent's |

### The interface census overturned its own diagnosis

`IFACE_CENSUS=1` at 15000 entries: **recovered 11314** (75%), and of 3686 refusals —
`no-single-add-defining-the-entry` **1844 (50%)**, `class-not-loaded-from-an-object` 1027,
`offset-below-vtable` 662, `interface-not-named-by-the-comparison` **152 (4%)**.

The generics explanation that had been reached by reading one method was **wrong by a factor of nineteen**.
The real top refusal is copy propagation, not generics. Twenty lines of counting beat a careful single-case
diagnosis. See [[il2cpp-measure-the-refusal-not-the-marker]].

## Round 5 — 1.15.5: CS7036 routed to the base-initializer repair. REVERTED IN PLACE

29 → **50** (CS7036 22, CS1729 22, CS7069 3, CS0019 3). My change, my mistake, and worth recording exactly:

`FindBaseInitializerEdit` already repairs this, but the routing at `InvalidSourceRepair.cs:415` only sent
**CS1729** to it. I added CS7036 plus a guard so it could not fire for a `new Foo()` inside the constructor's
body — and wrote the guard as `node.Span.End > constructor.ParameterList.Span.End`. **`node` for this
diagnostic IS the `ConstructorDeclarationSyntax`, whose span runs to the end of the body**, so the guard
rejected every real case and the repair never fired once.

It was not a null round, though: routing CS7036 away from the statement-commenting path **stopped that path
commenting out constructor bodies it could never fix**, and `FixedIntervalFrameCounter`'s body came back
whole. The 21 new CS1729 are the same 22 constructors re-reported under a second id once their bodies
compiled.

Fixed at 1.15.6 by moving the guard to the call site, where the diagnostic's own `position` is in scope
(`IsConstructorHeader`). **The lesson: a syntax node's span is not the diagnostic's position.**
