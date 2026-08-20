# The instruction-set gap against Snacky Dash 1.11.0

**Target** — Snacky Dash 1.11.0, Unity **6000.0.66f2**, il2cpp metadata **31.1**, arm64 only.
`lib/arm64-v8a/libil2cpp.so`, 149,489,656 bytes, stripped, `elf64-littleaarch64`,
BuildID `0b20385f1d6ac4890c87d9b06561b7f785b1f4ed`.

**Method** — complete disassembly, **not sampled**. `llvm-objdump` 20.1.7 (Homebrew) over the whole
`il2cpp` section, mnemonic tallied in `awk`. ~70 s for 18.8 M instructions.

```sh
llvm-objdump -d --no-show-raw-insn --section=il2cpp libil2cpp.so \
  | awk '/^ *[0-9a-f]+:/ {c[$2]++} END {for (m in c) printf "%d\t%s\n", c[m], m}' | sort -rn
```

## Which section is the denominator

| section | VMA | size | what it holds |
|---|---|---|---|
| `.text` | `0x3598e20` | 6.7 MB | the il2cpp **runtime** — hand-written C++, not lifted |
| **`il2cpp`** | `0x3c09744` | **75 MB** | the **transpiled managed code** — this is what Cpp2IL lifts |

Everything below counts the `il2cpp` section only: **18,823,846 instructions, 196 distinct mnemonics.**
Fluffy Field 1.0.3's same section is 10,621,841 instructions and 184 mnemonics.

---

## 1. What the fork actually translates

The lifter is one `switch (instruction.Mnemonic)` in
`External/Cpp2IL/Cpp2IL.Core/InstructionSets/NewArmV8InstructionSet.cs:247`, and its `default:` arm at
line 1161 is the whole of the failure mode:

```csharp
default:
    Add(address, OpCode.NotImplemented, $"Instruction {instruction.Mnemonic} not yet implemented.");
```

**109 mnemonics have a case.** In alphabetical order:

```
ADD ADDS ADR ADRP AND ANDS ASRV B BIC BL BLR BR CBNZ CBZ CCMN CCMP CINC CINV CMP CNEG CSEL CSET
CSETM CSINC CSINV CSNEG DMB DSB EON EOR FABD FABS FADD FCCMP FCMP FCSEL FCVT FCVTMS FCVTPS FCVTZS
FCVTZU FDIV FMAXNM FMINNM FMOV FMUL FNEG FNMUL FRINTM FRINTP FSQRT FSUB ISB LDP LDR LDRB LDRH LDRSB
LDRSH LDRSW LDUR LDURB LDURH LDURSW LSLV LSRV MADD MOV MOVI MOVK MOVN MOVZ MRS MSR MSUB MUL NEG NOP
ORN ORR RET SBFM SCVTF SDIV SMADDL SMSUBL SMULL STP STR STRB STRH STUR STURB STURH SUB SUBS SXTB SXTH
SXTW TBNZ TBZ UBFM UCVTF UDIV UMADDL UMSUBL UMULL UXTB UXTH
```

### Three things run *before* the switch and can pre-empt it

1. **`vectorLanes.TryConvert`** (`NewArmV8InstructionSet.cs:239`) — `VectorLanes.cs`, 2112 lines. This is a
   **second, independent Advanced-SIMD decoder that works on the raw 32-bit word**, not on Disarm's
   mnemonic. It exists because Disarm refuses nearly all NEON. Its sub-decoders, by encoding class:
   `Copy` (SIMD copy: `dup`/`umov`/`ins`/`mov` element), `Immediate` (`movi` 8-bit+shift), `Arithmetic`
   (`0 Q U 01110 …` — per-lane arithmetic and compares), `Integer`, `Scaled` (`fmul` by-element),
   `Permute` (`zip`/`uzp`/`trn`/`ext`), `Reduce` (across-lane `addv`/`umaxv`/`uminv`), `Pairwise`
   (`faddp` only), `Bitwise`, `Shifted` (`shl`/`cmlt`/`ushr` by constant), plus `Store`/`Load`/`LoadPair`/
   `Replicate` for whole-register moves.
2. **`StackFrameWriteback`** (`NewArmV8InstructionSet.cs:244`) — pre/post-index `stp`/`ldp` through `sp`.
3. **`NewArm64Utils.Refused.cs`** — runs earlier still, over the decoded body, and *renames* words Disarm
   returned as `INVALID`. It covers exactly three shapes: `FCVT Sd, Dn`, `LDRSB` unsigned-immediate, and
   `UMULH`/`SMULH`. Its own doc-comment is explicit that multiply-high gets "an honest marker" — i.e.
   **naming it does not give it a case**, it only gives it an address so branches to it survive.

### Explicitly refused, on purpose

`NOP`, `MRS`, `MSR`, `DMB`, `DSB`, `ISB` are translated to `OpCode.Nop` deliberately — a system-register
access or a barrier has no managed meaning, and a placeholder would take down the statement it landed in.

Two more are **deliberately absent** and have been reverted twice (per
`il2cpp-naming-an-instruction-costs-branches`): naming an instruction can cost branches, so `FRINTM`/
`FRINTP`/`CINC` history is not a simple "add a case" story. They now *do* have cases; the caution stands.

---

## 2. The aliasing problem, and how it was settled

`llvm-objdump` prints ARM's preferred **aliases**; Disarm reports something else, and the `switch` keys on
Disarm's name. Joining the two naively is wrong in both directions. The question was settled empirically
rather than guessed:

* **Disarm's enum was read out of the assembly** (`strings` over
  `~/.nuget/packages/disarm/2022.1.0-master.99/lib/net6.0/Disarm.dll`). `LSR`, `ASR`, `ROR`, `BFI`, `BFXIL`,
  `UBFX`, `SBFX`, `SMULH`, `UMULH`, `BICS`, `ADC`, `MVNI`, `REV*`, `RBIT`, `CLZ`, `LDPSW`, `LDURSB`,
  `FCVTPU`, `DUP`, `UMOV`, `INS` are all present as standalone strings.
  **Caveat: absence proves nothing** — .NET metadata shares string *suffixes*, so `MOV` is stored inside
  `FMOV` and never appears on its own. Presence is evidence; absence is not.
* **A live marker census settled the direction.** Grepping every Fluffy Field export tree under
  `/Users/playviet/Documents/_APK/FluffyField/*/ExportedProject/Assets/Scripts` for the real marker text
  `Not implemented instruction: Instruction <NAME>` yields exactly two names: `INVALID` ×13 and **`SBFM` ×8**.

`SBFM` appearing as a *live marker* is decisive: for the bitfield family Disarm reports the **base**
mnemonic, not the alias. So `lsl`, `lsr`, `asr`, `ubfx`, `ubfiz`, `sbfx`, `sbfiz` all arrive as `UBFM`/`SBFM`
and **are handled** — they are not a gap. By the same rule `bfi`/`bfxil` arrive as **`BFM`, which has no
case at all**, so those two *are* a gap, and certainly.

The one alias question left open is **`cmn` → `ADDS` or `CMN`?** `ADDS` has a case, `CMN` does not, and
`cmn` is 9,620 sites here. The base-mnemonic rule says `ADDS` and it is fine; but `CMP` has its *own* case
in the switch beside `SUBS`, which shows Disarm does sometimes prefer the compare spelling. **This is the
single cheapest thing to confirm** — one `probe`/export check, and it is either nothing or the largest
scalar gap in the binary.

---

## 3. The gap: mnemonics in the binary with no case in the lifter

**Headline: the instruction set is very nearly covered. 13,967 of 18,823,846 instructions — 0.074% —
have no case, across 75 distinct mnemonics. Of those, VectorLanes' raw-word decoder claims another
5,332, leaving 8,635 (0.046%) genuinely unrecovered.**

The `VectorLanes` column says which raw-word sub-decoder claims that encoding space (read from the bit
masks in `VectorLanes.cs`, **not** executed — see the caveat at the end). `FF rate ratio` normalises for
the two binaries' different sizes: `(count/18.8M) / (ffcount/10.6M)`.

| # | mnemonic | count | % of all | form | arrives as | VectorLanes | FF count | FF rate ratio |
|---|---|---|---|---|---|---|---|---|
| 1 | `dup` | 2,445 | 0.0130% | SIMD | `DUP` | Copy | 862 | 1.6x |
| 2 | `xtn` | 1,182 | 0.0063% | SIMD | `XTN` | — | 394 | 1.7x |
| 3 | `ror` | 952 | 0.0051% | scalar | `EXTR/RORV` | — | 815 | 0.7x |
| 4 | `bfxil` | 809 | 0.0043% | scalar | `BFM` | — | 175 | 2.6x |
| 5 | `ushll` | 794 | 0.0042% | SIMD | `USHLL` | — | 112 | 4.0x |
| 6 | `bfi` | 639 | 0.0034% | scalar | `BFM` | — | 191 | 1.9x |
| 7 | `ld1` | 512 | 0.0027% | SIMD | `LD1` | — | 93 | 3.1x |
| 8 | `shrn` | 463 | 0.0025% | SIMD | `SHRN` | — | 5 | 52.3x |
| 9 | `shll` | 453 | 0.0024% | SIMD | `SHLL` | — | 2 | 127.8x |
| 10 | `ushl` | 408 | 0.0022% | SIMD | `USHL` | — | 145 | 1.6x |
| 11 | `fcmgt` | 399 | 0.0021% | SIMD | `FCMGT` | Arithmetic | 104 | 2.2x |
| 12 | `umov` | 305 | 0.0016% | SIMD | `UMOV` | Copy | 149 | 1.2x |
| 13 | `bit` | 260 | 0.0014% | SIMD | `BIT` | Integer | 69 | 2.1x |
| 14 | `faddp` | 258 | 0.0014% | SIMD | `FADDP` | Pairwise | 187 | 0.8x |
| 15 | `zip1` | 255 | 0.0014% | SIMD | `ZIP1` | Permute | 65 | 2.2x |
| 16 | `ext` | 249 | 0.0013% | SIMD | `EXT` | Permute | 128 | 1.1x |
| 17 | `xtn2` | 248 | 0.0013% | SIMD | `XTN2` | — | 120 | 1.2x |
| 18 | `bsl` | 226 | 0.0012% | SIMD | `BSL` | Integer | 46 | 2.8x |
| 19 | `cmhi` | 219 | 0.0012% | SIMD | `CMHI` | — | 67 | 1.8x |
| 20 | `fcmeq` | 206 | 0.0011% | SIMD | `FCMEQ` | Arithmetic | 47 | 2.5x |
| 21 | `rev64` | 196 | 0.0010% | SIMD | `REV64` | — | 74 | 1.5x |
| 22 | `fcvtl` | 195 | 0.0010% | SIMD | `FCVTL` | — | 2 | 55.0x |
| 23 | `cmeq` | 182 | 0.0010% | SIMD | `CMEQ` | — | 45 | 2.3x |
| 24 | `umulh` | 155 | 0.0008% | scalar | `UMULH` | — | 118 | 0.7x |
| 25 | `bics` | 155 | 0.0008% | scalar | `BICS` | — | 102 | 0.9x |
| 26 | `bif` | 142 | 0.0008% | SIMD | `BIF` | Integer | 31 | 2.6x |
| 27 | `cmhs` | 140 | 0.0007% | SIMD | `CMHS` | — | 52 | 1.5x |
| 28 | `shl` | 109 | 0.0006% | SIMD | `SHL` | Shifted | 50 | 1.2x |
| 29 | `fcvtn` | 101 | 0.0005% | SIMD | `FCVTN` | — | 5 | 11.4x |
| 30 | `sshll` | 96 | 0.0005% | SIMD | `SSHLL` | — | 7 | 7.7x |
| 31 | `fcmlt` | 87 | 0.0005% | SIMD | `FCMLT` | — | 29 | 1.7x |
| 32 | `extr` | 79 | 0.0004% | scalar | `EXTR` | — | 49 | 0.9x |
| 33 | `fcmge` | 78 | 0.0004% | SIMD | `FCMGE` | Arithmetic | 7 | 6.3x |
| 34 | `ushr` | 72 | 0.0004% | SIMD | `USHR` | Shifted | 2 | 20.3x |
| 35 | `rev` | 70 | 0.0004% | scalar | `REV` | — | 51 | 0.8x |
| 36 | `uzp1` | 65 | 0.0003% | SIMD | `UZP1` | Permute | 40 | 0.9x |
| 37 | `uzp2` | 62 | 0.0003% | SIMD | `UZP2` | Permute | 17 | 2.1x |
| 38 | `fcvtn2` | 58 | 0.0003% | SIMD | `FCVTN2` | — | 0 | absent |
| 39 | `zip2` | 52 | 0.0003% | SIMD | `ZIP2` | Permute | 20 | 1.5x |
| 40 | `smulh` | 51 | 0.0003% | scalar | `SMULH` | — | 42 | 0.7x |
| 41 | `cmgt` | 51 | 0.0003% | SIMD | `CMGT` | — | 3 | 9.6x |
| 42 | `ldpsw` | 48 | 0.0003% | scalar | `LDPSW` | — | 10 | 2.7x |
| 43 | `ldursb` | 38 | 0.0002% | scalar | `LDURSB` | — | 4 | 5.4x |
| 44 | `cmlt` | 35 | 0.0002% | SIMD | `CMLT` | — | 25 | 0.8x |
| 45 | `fcvtl2` | 32 | 0.0002% | SIMD | `FCVTL2` | — | 0 | absent |
| 46 | `mvni` | 29 | 0.0002% | SIMD | `MVNI` | Immediate | 16 | 1.0x |
| 47 | `uaddlv` | 26 | 0.0001% | SIMD | `UADDLV` | Reduce | 14 | 1.0x |
| 48 | `cnt` | 26 | 0.0001% | SIMD | `CNT` | — | 14 | 1.0x |
| 49 | `sshl` | 24 | 0.0001% | SIMD | `SSHL` | — | 0 | absent |
| 50 | `smax` | 23 | 0.0001% | SIMD | `SMAX` | — | 7 | 1.9x |
| 51 | `addv` | 23 | 0.0001% | SIMD | `ADDV` | Reduce | 13 | 1.0x |
| 52 | `umaxv` | 21 | 0.0001% | SIMD | `UMAXV` | Reduce | 23 | 0.5x |
| 53 | `ld1r` | 21 | 0.0001% | SIMD | `LD1R` | — | 6 | 2.0x |
| 54 | `smin` | 18 | 0.0001% | SIMD | `SMIN` | — | 1 | 10.2x |
| 55 | `sshr` | 17 | 0.0001% | SIMD | `SSHR` | Shifted | 17 | 0.6x |
| 56 | `cmge` | 17 | 0.0001% | SIMD | `CMGE` | — | 0 | absent |
| 57 | `uminv` | 16 | 0.0001% | SIMD | `UMINV` | Reduce | 16 | 0.6x |
| 58 | `trn1` | 16 | 0.0001% | SIMD | `TRN1` | Permute | 4 | 2.3x |
| 59 | `st1` | 14 | 0.0001% | SIMD | `ST1` | — | 8 | 1.0x |
| 60 | `rbit` | 12 | 0.0001% | scalar | `RBIT` | — | 2 | 3.4x |
| 61 | `usra` | 5 | 0.0000% | SIMD | `USRA` | — | 7 | 0.4x |
| 62 | `adc` | 5 | 0.0000% | scalar | `ADC` | — | 1 | 2.8x |
| 63 | `rev16` | 4 | 0.0000% | scalar | `REV16` | — | 4 | 0.6x |
| 64 | `mla` | 4 | 0.0000% | SIMD | `MLA` | — | 2 | 1.1x |
| 65 | `fcmle` | 4 | 0.0000% | SIMD | `FCMLE` | — | 0 | absent |
| 66 | `rev32` | 2 | 0.0000% | SIMD | `REV32` | — | 0 | absent |
| 67 | `ushll2` | 1 | 0.0000% | SIMD | `USHLL2` | — | 1 | 0.6x |
| 68 | `uaddl2` | 1 | 0.0000% | SIMD | `UADDL2` | — | 1 | 0.6x |
| 69 | `uaddl` | 1 | 0.0000% | SIMD | `UADDL` | — | 1 | 0.6x |
| 70 | `trn2` | 1 | 0.0000% | SIMD | `TRN2` | Permute | 2 | 0.3x |
| 71 | `subhn` | 1 | 0.0000% | SIMD | `SUBHN` | — | 0 | absent |
| 72 | `fcvtpu` | 1 | 0.0000% | scalar | `FCVTPU` | — | 0 | absent |
| 73 | `clz` | 1 | 0.0000% | scalar | `CLZ` | — | 0 | absent |
| 74 | `addp` | 1 | 0.0000% | SIMD | `ADDP` | — | 1 | 0.6x |
| 75 | `abs` | 1 | 0.0000% | SIMD | `ABS` | — | 0 | absent |

---

## 4. The top 15, what each does, and what recovering it would cost

Operand shapes below are the real ones, counted over the whole `il2cpp` section.

| # | mnemonic | n | what it does | cost to recover | class |
|---|---|---|---|---|---|
| 1 | `dup` | 2,445 | one lane (or a general register) copied into every lane | **already claimed** by `VectorLanes.Copy` | SIMD |
| 2 | `xtn` | 1,182 | narrow each lane to half its width (`.2s←.2d`, `.4h←.4s`, `.8b←.8h`) | **new fork helper** in `VectorLanes` — two-reg-misc space, opcode `10010`, currently unclaimed | SIMD |
| 3 | `ror` | 952 | rotate right — **100% register form**, so it arrives as `RORV` | **one ISIL emit**, but ISIL has no rotate: must expand to `(x >>> n) \| (x << (w-n))` | **pure arithmetic — cheap** |
| 4 | `bfxil` | 809 | bitfield extract and insert low — copy a field of `Rn` over the low bits of `Rd` | **one case** on `BFM` + the existing `BitfieldMove.cs` machinery | **pure register/arith — cheap** |
| 5 | `ushll` | 794 | widen each lane, zero-extended (`.2d←.2s` ×668) | **new fork helper** — shift-by-immediate space, opcode `10100`, `Shifted` claims only `01010`/`00000` | SIMD |
| 6 | `bfi` | 639 | bitfield insert — place a field of `Rn` at bit `#lsb` of `Rd` | **one case** on `BFM`, same as `bfxil` | **pure register/arith — cheap** |
| 7 | `ld1` | 512 | load **one lane** from memory (`{ v.s }[1]`, `{ v.d }[1]`) | **new fork helper** in `VectorLanes` — the ASIMD load space is outside both `Copy` and `Load` masks | SIMD |
| 8 | `shrn` | 463 | shift right and narrow (`.2s←.2d`) | **new fork helper**, shift-by-immediate opcode `10000` | SIMD |
| 9 | `shll` | 453 | shift left long — **every one is `v.2d, v.2s, #32`** | **new fork helper**, two-reg-misc opcode `10011` | SIMD |
| 10 | `ushl` | 408 | per-lane **variable** shift (`.2d,.2d,.2d`) | **new fork helper**, three-same opcode `01000`; `Integer` claims only `00011` | SIMD |
| 11 | `fcmgt` | 399 | per-lane float compare, result is an all-ones mask | **already claimed** by `VectorLanes.Arithmetic` | SIMD |
| 12 | `umov` | 305 | move one lane into a general register | **already claimed** by `VectorLanes.Copy` | SIMD |
| 13 | `bit` | 260 | bitwise insert if true — a per-lane select | **already claimed** by `VectorLanes.Integer` | SIMD |
| 14 | `faddp` | 258 | add the two lanes of a pair together | **already claimed** by `VectorLanes.Pairwise` | SIMD |
| 15 | `zip1` | 255 | interleave the low lanes of two registers | **already claimed** by `VectorLanes.Permute` | SIMD |

### The cheap, high-value ones — pure register moves and arithmetic

Only **three** of the top 15 are scalar integer work, and all three are cheap:

* **`bfi` + `bfxil` — 1,448 sites, one shared case on `BFM`.** This is the biggest genuine win in the list.
  `BitfieldMove.cs` already implements `UBFM`/`SBFM`; `BFM` is the third member of the same encoding family
  and differs only in that it *merges* with the destination instead of zeroing it, i.e.
  `Rd = (Rd & ~mask) | ((Rn >> ror) & mask)`. The dominant shapes say what they are for:
  `bfi x, x, #32, #32` (224) packs two 32-bit values into one 64-bit register — **the same
  index-in-the-high-half idiom** already documented in
  `il2cpp-a-decoded-instruction-with-an-unrenderable-operand`; `bfxil x, x, #0, #1` (319) and the other
  1-bit forms are **a `bool` field packed into a struct**. Left as `notimpl` each one takes its statement
  and usually the body.
* **`ror` — 952 sites, all register-form (`RORV`).** Rotate is what every hash function is made of
  (`string.GetHashCode`, dictionary bucketing). ISIL has no rotate opcode, so the emit is the two-shift-or
  expansion, not a one-liner — but it is still pure integer arithmetic with no lane model involved.
* `umulh`/`smulh` (206 combined), `bics` (155), `extr` (79), `rev`/`rev16`/`rev32` (76), `ldpsw` (48),
  `ldursb` (38), `rbit` (12), `adc` (5), `clz` (1) are the scalar tail. `umulh`/`smulh` are the high half
  of a multiply — **how every division by a constant is done** — and already have names via
  `NewArm64Utils.Refused.cs`, which its own comment says leaves them "an honest marker". `ldpsw` and
  `ldursb` are plain loads and are the cheapest cases in the whole list.

### The ones that are not cheap

Everything else in the table is Advanced SIMD, and per `il2cpp-a-lane-is-a-register` these cannot be lifted
as if the register held one value — the whole point of the code is that two or four independent scalars live
in one register. Each needs **lane-aware lifting**, i.e. a new sub-decoder inside `VectorLanes`, not a case
in the switch. There is **no crypto and no atomics** in this binary at all (no `aes*`, `sha*`, `ldadd`,
`cas*`, `ldax*`/`stlx*` appear anywhere in the histogram), so that whole category of risk is absent.

---

## 5. Snacky Dash vs Fluffy Field — the new-toolchain risk

This is the part that matters: an instruction the newer compiler emits and the older one did not is exactly
what no earlier calibration could have seen.

### 5a. Present here, absent from Fluffy Field entirely

Ten mnemonics, **220 instructions total** — a very short list, and none of it is scalar-important:

| mnemonic | n | form | note |
|---|---|---|---|
| `fcvtn2` | 58 | SIMD | narrow double→single into the *upper* lanes |
| `fcvtl2` | 32 | SIMD | widen the upper lanes single→double |
| `sshl` | 24 | SIMD | signed per-lane variable shift |
| `cmge` | 17 | SIMD | per-lane signed compare ≥ |
| `fcmle` | 4 | SIMD | per-lane float compare ≤ |
| `rev32` | 2 | SIMD | reverse bytes within 32-bit elements |
| `subhn` | 1 | SIMD | subtract and narrow, high half |
| `fcvtpu` | 1 | scalar | float→unsigned, round toward +∞ |
| `clz` | 1 | scalar | count leading zeros |
| `abs` | 1 | SIMD | per-lane absolute value |

Two *handled* mnemonics are also new here — `mneg` (5) and `smsubl` (1) — and cost nothing.

### 5b. The real signal: present in both, but far more common here

Absence is a weak test on a 220-instruction tail. The rate ratio is the stronger one, and it shows a single
coherent cluster the older toolchain barely used:

| mnemonic | here | there | ratio | |
|---|---|---|---|---|
| `shll` | 453 | 2 | **127.8x** | all `v.2d, v.2s, #32` |
| `fcvtl` | 195 | 2 | **55.0x** | all `v.2d, v.2s` |
| `shrn` | 463 | 5 | **52.3x** | all `v.2s, v.2d, #n` |
| `ushr` | 72 | 2 | 20.3x | |
| `fcvtn` | 101 | 5 | 11.4x | all `v.2s, v.2d` |
| `smin` | 18 | 1 | 10.2x | |
| `cmgt` | 51 | 3 | 9.6x | |
| `sshll` | 96 | 7 | 7.7x | |
| `fcmge` | 78 | 7 | 6.3x | |
| `ldursb` | 38 | 4 | 5.4x | scalar |
| `ushll` | 794 | 112 | 4.0x | 668 are `.2d ← .2s` |
| `rbit` | 12 | 2 | 3.4x | scalar |
| `ld1` | 512 | 93 | 3.1x | single-lane loads |

**`shll`/`ushll`/`xtn`/`shrn`/`fcvtl`/`fcvtn`/`ushl` are one idiom, and it is new.** Read together:

```
shll  v0.2d, v0.2s, #32      ; two 32-bit lanes into the HIGH half of two 64-bit lanes
ushl  v0.2d, v0.2d, v1.2d    ; per-lane variable shift, done in 64 bits
shrn  v0.2s, v0.2d, #n       ; take the top back down to two 32-bit lanes
```

That is **two-lane 64-bit integer arithmetic used to implement 32-bit operations** — the vectorised form of
a variable shift and of magic-number division, done a pair at a time. Alongside it,
`fcvtl v.2d, v.2s` / `fcvtn v.2s, v.2d` is **a pair of floats promoted to double, computed, and narrowed
back**. Unity 6000.0's il2cpp emits this shape freely; the Fluffy Field toolchain essentially did not
(2 and 5 sites respectively, against 453 and 195 here).

The second, smaller new shape is **narrow-lane packing**: `xtn v.4h, v.4s` (331) with `ushll v.4s, v.4h` (99),
and `xtn v.8b, v.8h` (218) with `ushll v.8h, v.8b` (27) — 16-bit and 8-bit lane work, which is `Color32`,
texture and UTF-16 string handling.

Third: **`ld1 { v.s }[1], [x]` (247) and `ld1 { v.d }[1], [x]` (96)** — loading a *single lane* from memory.
This is precisely the `Vector2`/`Vector3` field-by-field load that `VectorLanes` exists for, but the ASIMD
single-structure load space is outside both its `Copy` mask (`(word>>21 & 0x1FF)` must be `001110000`/
`101110000`) and its `Load` mask (`(word>>27 & 0x7)` must be `111`; `ld1` has `001`). **It is the closest
of the SIMD gaps to machinery the fork already has.**

---

## 6. Caveats, stated plainly

* **The histogram is exact, not sampled** — the whole `il2cpp` section, 18,823,846 instructions.
* **The join is at mnemonic level.** A mnemonic with a case can still fail on an *operand form* the case
  does not handle (`LDRSB` unsigned-immediate and `FCVT Sd,Dn` were exactly that, and needed
  `NewArm64Utils.Refused.cs`). This measurement cannot see that class, and it is known to be non-empty.
* **The `VectorLanes` column was read from bit masks, not executed.** No build was run (an export was in
  flight). Treat "claimed" as "the space is claimed", not "this instruction is recovered correctly".
* **Disarm may refuse a word outright**, returning `INVALID` **with address 0**, which is worse than
  `notimpl`: the instruction is *absent* and every branch to it dies with its block
  (`il2cpp-the-word-the-disassembler-refuses`). This report counts what the *toolchain* decodes; how much
  of it Disarm refuses on this binary needs `probe2 … invalid` against this `.so`, which needs a build.
* **`cmn` (9,620) is unresolved** and is the one number here that could move materially — see §2.

## 7. Bottom line

The instruction-set gap is **not** the thing standing between this fork and Snacky Dash. 99.93% of the
binary's instructions have a lifter case, the new-toolchain delta is 220 instructions across ten mnemonics,
and there is no crypto or atomics anywhere. The work that is worth doing, in order:

1. **`cmn` — confirm it arrives as `ADDS`.** Cheapest check, largest possible number.
2. **`BFM` (`bfi` + `bfxil`, 1,448 sites)** — one case, scalar, reuses `BitfieldMove.cs`.
3. **`RORV` (`ror`, 952 sites)** — scalar, needs the two-shift-or expansion.
4. **`ld1` single-lane loads (512)** — nearest SIMD gap to existing machinery.
5. **The `.2d` widen/narrow cluster** (`shll`/`ushll`/`xtn`/`shrn`/`ushl`/`fcvtl`/`fcvtn`, ~3,600 sites) —
   genuinely new in this Unity line, and a lane-model project rather than a set of cases.
