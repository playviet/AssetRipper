# Round log — the non-generic half of the ILSpy `Unknown` note slice

Agent worktree `agent-a1b56a3f561e1056d`. Versions 1.8.0–1.8.29, exports 490–519.

Baseline reference numbers are taken from export **356** (fork 1.4.1), the export the note census in
`il2cpp-ilspy-notes-are-a-scorer` was taken on, plus my own untouched build at **1.8.0 / export 490**.

## The shared-`riprun` hazard, checked

A warning arrived that another session's `riprun` restored `assetripper.cpp2il.core/1.8.0` — my range — out
of a stale `project.assets.json`, and exported clean with numbers identical to baseline. **It did not happen
here**, and three things made that so:

* my scratchpad is *inside this worktree*, with its own `nuget.config` carrying an **absolute** path to this
  worktree's `LocalPackages` and a `<clear/>` above it, so the main tree's config cannot be merged in;
* `bumpz.sh` wipes `riprun/obj` and `riprun/bin` and then **fails the round** unless `riprun.deps.json`
  names the version just packed;
* `grep -o "assetripper.cpp2il.core/[0-9.]*" scratchpad/riprun/obj/project.assets.json` reads **1.8.6**
  after round 496 — my own last bump — and is checked in `roundz.sh` from 497 on.

Corroborating: every round here moved numbers in the direction the ISIL predicted, and `probe2` — built by
**ProjectReference**, with no package in the path at all — showed each change in the ISIL before the export
was paid for. A build that never happened cannot do that.

## Rebased onto the HFA struct return

`dad92ee7e` ("The callee assembles its vector return") merged in at 1.8.6 → 1.8.7. Their export 471 is the
new reference: compare2 full 3247 (2553 of 2815 decompiled, 90.7%), commented 508, unmanaged 403,
cfscore 609, roundtrip 1043, decisions 1326, gate 12. Version conflicts resolved in my favour (1.8.7);
nothing else conflicted.

## 1.8.0 — export 490 — baseline, no code change

Purpose: prove the worktree toolchain measures this tree and not another agent's, and fix the numbers every
later round is judged against.

| | 1.8.0 / 490 |
|---|---|
| genfail | 0 |
| `cfscore` full / partial | 609 / 6 — 92 of 96 files clean; unmanaged 15, commented 3, structvalue 1 |
| `compare2` full / partial / dead (decompiled only, 2815) | 2558 (90.9%) / 151 / 106 |
| `compare2` markers | commented 494, unmanaged 389, notfound 50, indirect 21, unknowncall 6, unknown 2, structvalue 1 |
| `allscore` ALL full / partial / dead / missing (2326) | 2119 (91.1%) / 114 / 19 / 74; commented 394 |
| `decisions` | 1382 asked, 1326 survived (95.9%); 293 methods keeping all (94.8%) |
| `roundtrip` whole / partial / dead | 1043 / 1572 / 142 |
| `notecensus` noted / losing · unnoted / losing | 299 / 75 · 3212 / 27 (29.8x) |
| **`notecensus` UNKNOWN slice** | **29 methods, 28 losing, 286 commented** |
| `floatbits` | 2 distinct, 3 sites (all `ProceduralImage`) |

Identical to export 356 on the note census, which is the check that this worktree measures its own tree.

`notecensus.py` gained an UNKNOWN-slice tail this round — the distinct methods carrying any
`Expected X, but got Unknown` note and their commented counts, which is the win condition of this task and
which the per-shape table cannot show (one method carries several shapes and is counted in each). `score.sh`
was rebuilt: the README lists it but it was never committed. Both backed up to `scratchpad-tools/`.

My 16 (the non-generic half), at 490: PrepareTextForBubble 26, GetReadyProvider 15, Show 15,
IntersectLineSegments2D 15, SpawnParticleEffect 10, ResolveImpressionCount 9, GenerateSlicedFilledSprite 7,
IsRaycastLocationValid 6, Awake 6, DrawFeatherBorder 5, EncodeAllInfoIntoVertices 4, GameId 3, Update 3,
ParseFormattedDateString 2, ActiveHash 2, EncodeFloats_0_1_16_16 0. **128 commented.**

## 1.8.1 — export 491 — a float constant left typed as an integer — **KEPT**

`Analysis/FloatConstantInAnInteger.cs` (new), one line among the seeds in `LocalVariables.cs`, and the wrong
remark in `FloatBitsInAnInteger.cs` corrected. The handed-over diagnosis was right and the machinery to act on
it already existed one step away: the evidence is **where the value lands**, so the seed rewrites the constant
and the existing `SharpenAVectorRegister` corrects every type downstream of it for nothing.

| | 490 | 491 |
|---|---|---|
| `floatbits` | 2 distinct, **3 sites** | **0** |
| `compare2` full / partial / commented | 2558 / 151 / 494 | **2560** / **149** / **489** |
| `allscore` ALL full | 2119 | **2120** |
| `roundtrip` facts / whole | 11186 / 1043 | **11188** / 1043 |
| `notecensus` noted / losing | 299 / 75 | **293** / **73** |
| **UNKNOWN slice** | 29 methods, 286 commented | **27 / 282** |
| cfscore, decisions, genfail | 609/6, 1326/1382, 0 | level |

`EncodeFloats_0_1_16_16` now reads exactly as it was written — `float num = 65535f;` and float arithmetic
throughout — where it had been `long num = 1199570688L` and two integer divisions. Two methods left the
Unknown slice outright (`EncodeFloats`, `ETFXFireProjectile::Update`) and `EncodeAllInfoIntoVertices` went
4 → 3. **My 16 are now 14, and 128 commented statements are 125.**

The brief predicted no scorer would move. Four did, slightly, because the correct types unblocked two more
bodies — but the number that decides the round is `floatbits` going to zero and the body reading as its
source, and it would have been kept on those alone.

## 1.8.2 — export 492 — a walk can start at a variable element — **KEPT**

`StringExtension::PrepareTextForBubble`, the worst of my sixteen at 26 commented. The root is one addition
`ArrayWalkerTyping` did not recognise. The compiler works the first element's address out once —
`array + 0x20` — and then, for a loop that starts part-way along, adds the scaled outer index to it and steps
*that* round the inner loop. `Chains` accepted a walk continued by a **constant** step only, so the walk was
registered for the header addition alone, nothing was ever read through it, and every read came out as
`text2 = (string)(((int*)num6))[0]`.

`ArrayWalkerTyping.Chains`/`Count` now also continue a walk through an addition of a **scaled subscript**,
asked through `ArrayElementAddress.Scaled` (made `internal`) so the two passes cannot drift about what
counts as a subscript. The walk's own counter then does the rest: seeded 0, plus the outer index at the
start, plus one per step, mirroring the pointer's dataflow.

| | 491 | 492 |
|---|---|---|
| `compare2` commented | 489 | **470** |
| `livecount` live / branches | — | **+26 / +5**, and only in `StringExtension.cs` |
| `PrepareTextForBubble` commented | 26 | **7** |
| **UNKNOWN slice** commented | 282 | **263** |
| everything else | | level: cfscore 609/6, compare2 full 2560 partial 149 dead 106, allscore 2120, decisions 1326/1382, roundtrip 11188/1043, notes 293/73, genfail 0 |

`commented` falling 19 while `full` sits still is the shape a wrong retype also has, so `livecount` was the
check that decided it: live statements **up** 26 and branches up 5, in exactly the one file, and no other
file in the game moved by a line. Reading the body confirms it: `text2 = array[num6]` with `num6` seeded
from the outer index and stepped by one, which is the `for (int j = i; ...)` the source had. A `WALK_TRACE`
was added beside the existing `CHAIN_TRACE` — a walk that never starts is invisible from the export, since
the pointer arithmetic simply stays.

## 1.8.3 — export 493 — a constant is a value already — **KEPT**

`GameHubRouter::GameId` chooses between `v.game_id` and `"default"` and then loads, which is exactly the
shape `FieldAddressSinking` exists for — but the chain walks every *source* of every member and refused at
`sourceNotALocal`, because one arm of the choice is a string literal. A constant is a value already: there is
nothing on that arm to rewrite, and nothing about it that makes the chain an address. Allowed for a string
and for a nought (which where a reference belongs is `null`) and for nothing else — any other number moved
into a place that is then read through would be a hard-coded address, which managed code does not have.

| | 492 | 493 |
|---|---|---|
| `compare2` full / partial / commented | 2560 / 149 / 470 | **2561** / **148** / **467** |
| `allscore` ALL full / partial | 2120 / 113 | **2121** / **112** |
| `roundtrip` facts / whole | 11188 / 1043 | **11190** / **1044** |
| `notecensus` noted / losing | 293 / 73 | **292** / **72** |
| `livecount` | | **+3 live, +1 branch**, only `GameHubRouter.cs` |
| cfscore, decisions, genfail, floatbits | | level |

`GameId` now reads as its source and carries no note at all.

## 1.8.4 — export 494 — nothing dereferences a number — **KEPT, on the diff rather than on a number**

`ParticleEffectsLibrary::Awake` lost a write outright: `Add v85 (System.Int32), this, 32` and then a store
at `[v85 + 8]`, which is `this.CurrentParticleEffectNum = 1`, came out as
`Console.WriteLine("Unmanaged memory store: …")`. `MetadataResolver.FoldAddressArithmetic` refused it
because it folds only through a base with **no type at all**, and this one had been given a width.

A local that is the base of a memory operand holds an address, whatever the analysis called it, and
`System.Int32` is the plainest case of the width getting there first — a thirty-two bit value cannot be an
address on this architecture. `IsAnAddress` now admits any primitive integer as well as null, and nothing
else, so a base the analysis genuinely named is still left to the field passes.

| | 493 | 494 |
|---|---|---|
| `compare2` unmanaged | 389 | **388** |
| everything else | | **identical**: full 2561, partial 148, commented 467, allscore 2121, decisions 1326, roundtrip 11190/1044, notes 292/72, cfscore 609/6, genfail 0 |
| `livecount` | | −2 live, one file |

**One site in the whole game**, and `livecount` reads −2 — so this was kept on the diff, which is three
lines of noise becoming two lines of code:

```
- _ = 1L;  Console.WriteLine("Unmanaged memory store: [v85 @ X20_v5 (System.Int32)+8]");
+ CurrentParticleEffectNum = 1;
```

A store that never happened is the class of defect the compilability scorers cannot see at all
(`il2cpp-the-store-that-never-happened`), and nothing regressed anywhere, so a fall in `live` that is
entirely noise leaving is not a reason to revert.

## 1.8.5 — export 495 — the stride was widened before it was multiplied — **KEPT**

`ParticleEffectsLibrary::SpawnParticleEffect` indexes a `Vector3[]`, and `(long)i * 12` widens both sides
first, so the twelve arrives as `Move v100 (Int32), 12` then `Move v101 (Int64), v100` and the multiply
reads a **local**. Every question `ArrayElementAddress` asks about a scaling is answered by
`Constant(times)`, so all three reads of the element were lost: two as unmanaged loads through
`array + i*12`, and the third — the one whose byte offset stayed in the addressing mode — as
`offsets[i * 12]`, which **compiles, scores whole and reads the wrong element**.

Three changes in `ArrayElementAddress`, all one idea:
* `Settled` follows a literal through the moves a widening put in front of it. A conversion is a `Move`
  with the target as a **third** operand, so the pattern has to be `[_, { } from, ..]` — matching exactly
  two operands is why the first build of this measured nothing at all.
* the shift and both multiply arms ask `Settled` instead of `Constant`.
* a new branch beside the walker one: an index that is a subscript **scaled by the element** is turned into
  the subscript, with the scale in the addressing mode. That is the half that fixes a wrong answer.

| | 494 | 495 |
|---|---|---|
| `compare2` unmanaged / commented | 388 / 467 | **383** / **465** |
| everything else | | level: full 2561, partial 148, allscore 2121, decisions 1326, roundtrip 11190/1044, notes 292/72, cfscore 609/6, genfail 0 |

`livecount` −2, and the diff says why:

```
- long num = (long)CurrentParticleEffectIndex * 12L;   _ = "Unmanaged memory load: [v103+28]";
- float z2 = z + 0;
+ long num = CurrentParticleEffectIndex;
+ float z = positionInWorldToSpawn.z + particleEffectSpawnOffsets[num].z;
```

Two reads that returned a literal nought now return the value, and the one that was reading element `i*12`
of a three-element array now reads element `i`. Five ILSpy notes went with them.

## 1.8.6 — export 496 — rest the address on the local that already holds the read — **KEPT**

The other half of `FieldAddressBase`. Copy propagation puts an array element straight into an addition —
`Add v482, [words + 0x20 + i*8], 16` — and both field passes want a local of a known type on the left, so
the chain is refused and the address is written out as `words[i] + 16L`. The compiler has invariably put the
element in a register of its own first (it is about to call methods on it), so `RestOnAHolder` points the
addition at that local. **Nothing is hoisted**: where no local already holds the read, the addition is left
alone.

| | 495 | 496 |
|---|---|---|
| `compare2` full / partial / commented / unmanaged | 2561 / 148 / 465 / 383 | **2562** / **147** / **458** / **369** |
| `allscore` ALL full / partial | 2121 / 112 | **2122** / **111** |
| `roundtrip` facts | 11190 | **11191** |
| `notecensus` losing | 72 | **71** |
| **UNKNOWN slice** | 27 methods / 263 | **25 / 252** |
| `livecount` | | **+9 live, +3 branches** |
| cfscore, decisions, genfail, floatbits | | level |

`PrepareTextForBubble` is **out of the slice entirely** — `words[i] + 16L` became `text2.Length`, which is
what `_stringLength` is named through. The four files that read −1 to −3 live all lost the same two lines,
`_ = 0;` and `_ = "Unmanaged memory load: [Il2CppClass<T>+FC]"`, which is the class's own size finally
resolving: noise leaving, in every case.

**My sixteen are now twelve, and 128 commented statements are 94.**

### The execution oracle at 1.8.6

`scratchpad-tools/oracle.sh` against `corpus/corpus.apk` (copied into the worktree; it is gitignored):

```
79 methods run, 51 behave the same, 28 do not
rated `full` 65   of those right 46   of those whole and WRONG 19
```

**Identical to `corpus/BASELINE.md` in every cell.** So none of the five kept rounds has broken a body that
was computing the right answer — and none of the corpus's 79 shapes exercises what they fixed, which is why
it neither rose nor fell.

## 1.8.7 — export 497 — the merge measured, against their export 471

No code of mine changed; this is the six kept rounds re-measured **on top of** `dad92ee7e`. Package version
restored: `assetripper.cpp2il.core/1.8.7`, checked from `project.assets.json`.

| | 471 (HFA alone) | 497 (HFA + my six) |
|---|---|---|
| `compare2` full, decompiled-only of 2815 | 2553 (90.7%) | **2557** (90.8%) |
| `compare2` commented | 508 | **472** |
| `compare2` unmanaged | 403 | **383** |
| `cfscore` full | 609 | 609 |
| `roundtrip` whole | 1043 | **1044** |
| `decisions` | 1326 | 1326 |

**+4 whole, −36 commented, −20 unmanaged, +1 roundtrip whole, nothing down.** My six rounds stack cleanly on
the struct-return work.

What moved the other way is theirs and expected: `allscore` 2122 → 2117 and `cfscore`'s marker line
(unmanaged 15 → 19, files clean 92 → 91, `notecensus` losers 71 → 76) — the five bodies that now admit
incompleteness instead of returning `default(T)`.

## 1.8.8 — export 498 — arithmetic over two floats does not produce a boolean — **KEPT**

`VectorExtensions::IntersectLineSegments2D` opens with `Subtract v22 @ V4_v1 (System.Boolean), p2start.x,
p1start.x` — a float subtraction typed **Boolean**, because a backward phi edge stamped the type of `V4`'s
later life (a comparison result) onto the value in it now. Twelve statements went with it, multiplying a
float by a `bool`.

`SharpenAVectorRegister` overrules a bare integer and nothing else, so `Boolean` blocked its own correction.
It now overrules `System.Boolean` too. **That is safe exactly here and nowhere else**: this is only reached
from `PropagateArithmetic`, which upstream calls for `Add`/`Subtract`/`Multiply`/`Divide` and the shifts and
never for a comparison, and the logical operations are excluded above it — so the question is always "what
does arithmetic over two known floats produce", and the answer is never a boolean.

| | 497 | 498 |
|---|---|---|
| `compare2` commented | 472 | **458** |
| `livecount` | | **+15 live, +6 branches**, only `VectorExtensions.cs` |
| everything else | | level: full 2557, partial 152, unmanaged 383, allscore 2117/116, decisions 1326, roundtrip 11194/1044, notes 290/76, cfscore 609, genfail 0 |

Twelve statements come back and read as the source: `num12 = num2 * num3`, the two divisions, and the four
comparisons that decide whether the segments meet. **This lands in `VectorExtensions.cs`, which is shared
with the struct-return agent** — the rule itself is global and nowhere near the struct-return path, and the
only file in the game it moves is this one, but it is flagged for the merge.

What is left in that body is a second local stamped the same way (`bool flag = (byte)(int)num3 != 0`) and
`_ = "Invalid instruction: … Jump target not found in method: 0x231F0C0"`, which is a branch out of the
method's own range and a different family.

### The execution oracle at 1.8.8

```
79 methods run, 54 behave the same, 25 do not      (BASELINE.md: 51 / 28)
rated `full` 65   of those right 49   whole and WRONG 16      (BASELINE.md: 65 / 46 / 19)
```

**Those three shapes are NOT mine.** `dad92ee7e` measured exactly 54/79 and 49/65 when it landed on master,
and my own reading at 1.8.6 — every one of my first six rounds, without the merge — was **51/79 and 46/65,
identical to `corpus/BASELINE.md` in every cell**. So the correct statement of my result is:

> **My changes moved the oracle not at all, and cost nothing.**

That is the right outcome and not a disappointing one. The corpus has no shapes for the families I fixed
(a float literal in a general register, a walk starting part-way along, an address chosen between, a widened
stride), so it was never going to show a win; it is here to catch me breaking something, and across seven
rounds it caught nothing.

## Where it ends — 1.8.8 / export 498

Unity gate **12 CS7069**, its known floor, unchanged from 496 and from their 471.

`Unknown` slice: **27 methods, 249 commented** (was 29 / 286 at 490). Two of the 27 — `VectorExtensions::With`
10 and `Cell::GetCatBoundsInContainer` 1 — **entered with the struct-return merge, not with anything of
mine**: the slice at 497, before my last round, already carried both.

**My sixteen, 128 commented at 490 → 80 in twelve methods at 498**, and of what is left:

| left | what it is |
|---|---|
| `GetReadyProvider` 15, `ResolveImpressionCount` 9 (+`NotifyRevenuePaid` 8) | the interface walk, tail-merged — diagnosed and written up, not built |
| `BaseAbilityBuyPopup::Show` 15 | an absolute address `0x50D4000` merged with a field address and read through; 7 such literals game-wide |
| `SpawnParticleEffect` 9, `Awake` 6, `EncodeAllInfoIntoVertices` 3 | a struct on the stack / in registers |
| `GenerateSlicedFilledSprite` 7, `IsRaycastLocationValid` 6 | shared file, not touched |
| `DrawFeatherBorder` 5 | `(Color32)(color & 0xFFFFFF)`, one site |
| `ParseFormattedDateString` 2 | an identity `+ 0L` nothing folds, and the front member where the declaration says the struct |
| `ActiveHash` 2 | the instruction after a call reads the receiver's SSA version, not the result's |
| `IntersectLineSegments2D` 1 | a second local stamped `Boolean`, and a jump target outside the method |

## 1.8.9 — export 499 — the tail-merged interface call, separated — **KEPT**

The last interface-walk family in the game: 16 sites in 3 files, all mine, worth the 32 commented statements
I predicted. **Four earlier attempts at this crossing were rules that picked a definition and all four were
reverted** (`il2cpp-object-is-not-a-declaration`). The reason they cannot work is not that the rule was
wrong — the local genuinely holds two different slots naming two different methods, because the compiler
tail-merged two calls into one dispatch, so **there is no answer to pick**. Only separating the paths can be
right.

`InterfaceCallRecovery.TailMerged` / `Arms` / `Separate`. The separation needs **no renaming**, which is what
keeps it to ~200 lines: each arm already computes its own slot and its own class into locals of its own, and
the only lasting output of the shared tail is the call's result register. So the call block is cut in two at
the indirect call, each arm gets the call it stood for written into it and jumps to the second half, and each
walk's opening test is sent to its own arm. Both arms assign the same result local — which is what the merged
code did.

Arms are paired **by block**: one arm per block that gives *both* the slot and the class a value, which is
how SSA destruction lays an edge's copies out. Reading the two locals' definitions separately would pair a
slot from one walk with a class from the other and name a method on the wrong object — the same mistake the
four reverted rules made, in a different place. Refused unless every way into the call block passes through
one of the walks being replaced, and unless every argument is available where the arm's call is written
(dominance computed over the graph as it stands, the analysis-time dominator tree being many rewrites old).

| | 498 | 499 |
|---|---|---|
| `compare2` full / partial | 2557 / 152 | **2559** / **150** |
| `compare2` commented | 458 | **426** |
| `compare2` unmanaged / indirect | 383 / 21 | **348** / **18** |
| `allscore` ALL full / partial | 2117 / 116 | **2119** / **114** |
| `notecensus` methods / losing | 290 / 76 | **288** / **73** |
| `decisions` | 1326 of 1382 | **1326 of 1382** |
| roundtrip, cfscore, genfail | | level |

`ResolveImpressionCount` is now the source exactly, with no marker, no note and no commented statement:

```csharp
int num;
if (IsInterstitialFormat(adFormat)) num = _trackingSaveData.adiCount;
else { if (!IsRewardedFormat(adFormat)) { … return _trackingSaveData.IncrementCustomCount("imp_" + text); }
       num = _trackingSaveData.advCount; }
return num + 1;
```

`GetReadyProvider` recovers all four `IAdsNetwork.IsInterstitialReady/IsAppOpenReady/IsRewardedReady/
IsNativeReady` calls, its `foreach` over `mediationOrder` and its `switch`, with four benign width notes left.

**`livecount` reads −194 live and −58 branches, and that is the point rather than a regression.** What left is
the walk: a guard, a compare-and-step loop and a runtime-helper fallback, sixteen times over — machinery il2cpp
writes at the call site, which was never source. The check that settles it is `decisions.py`, which asks
whether the *original's* branching survived and is **unmoved at 1326 of 1382**; deleting 58 real branches
could not leave it still. `full` up, `partial` down, `unmanaged` −35 and the bodies reading as their source
say the same thing.

The execution oracle is **unmoved at 54/79 and 49/65** — and it does exercise interface dispatch
(`SharedPick`, `ValuePick`, `SumSteps`), so this is a CFG rewrite measured by the one instrument that would
notice it going wrong. Unity gate **12 CS7069**, its floor.

`Unknown` slice **27 → 24 methods, 249 → 217 commented**; all three interface methods left it outright.
**My sixteen are ten methods and 56 commented statements, from 128.**

## After nine rounds there is no family left, only a tail

`owedcensus2.py` over the whole export at 499: **85 owed bodies whose first commented statement was read**,
and the roots are singletons. The largest are `_ = null;` (4) and `long v = obj - N;` (4); everything else is
one or two. So the way to pick the next round here is by *shape*, not by this slice — and two of my own
leftovers were sized this way and are worth writing down rather than building:

* **`BaseAbilityBuyPopup::Show`, 15** — the metadata-init guard's `ADRP` leaves a page constant in X21;
  `MetadataInitGuardRemover` deletes the guard, but SSA destruction has already materialised that constant
  into edge copies, and it merges with `this + 0x98` (`&_abilitySlotUI`), which `FieldAddressSinking` then
  refuses at `sourceNotALocal`. The asm settles what to do: **both** arms of the isinst do
  `MOV X21, X19; STR X1, [X21 + 0x98]!`, so the constant edge cannot reach the read at all. It is an
  impossible-edge phi, not a real choice — fix the dead edge copy, not the sinking rule.
* **`ActiveHash`, 2 — and it is NOT broad: 1 site game-wide.** `CBZ X0, join; …; BLR; join: SUB W8, W0, W21`
  is `s?.GetHashCode() ?? 0`. The null edge carries X0 still holding the string, so the phi merges a
  reference with a number, `CannotBeTheSameValue` refuses that copy, and the subtraction is left reading the
  string. What would fix it is the general fact that **a register a branch was taken on because it tested
  zero is zero on that edge** — conditional constant propagation, which belongs in `SsaForm` and is a bigger
  change than the two statements justify.


