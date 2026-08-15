# Round log — the non-generic half of the ILSpy `Unknown` note slice

Agent worktree `agent-a1b56a3f561e1056d`. Versions 1.8.0–1.8.29, exports 490–519.

Baseline reference numbers are taken from export **356** (fork 1.4.1), the export the note census in
`il2cpp-ilspy-notes-are-a-scorer` was taken on, plus my own untouched build at **1.8.0 / export 490**.

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


