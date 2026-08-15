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


