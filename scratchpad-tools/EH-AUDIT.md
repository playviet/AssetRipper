# Does the exception machinery read Snacky Dash 1.11.0?

Audit of the LSDA/`.eh_frame` readers against the **new** binary (Snacky Dash 1.11.0, Unity 6000.0.66f2,
il2cpp metadata 31.1, arm64, 149,489,656-byte `libil2cpp.so`), against the one they were calibrated on
(Fluffy Field 1.0.3). Analysis only — nothing was built and nothing outside `scratchpad-tools/` was touched.

**Verdict: see the last section.** Written as the work went, so the order below is the order of the checks.

---

## 1. What the readers assume

Two implementations of one algorithm:

* `scratchpad-tools/lsda.py` — the Python original, ~290 lines, the diagnostic.
* `External/Cpp2IL/Cpp2IL.Core/Analysis/ExceptionTable.cs` — the same thing in the fork, what actually runs
  in an export. Consumed by `ExceptionEdges.cs` (attaches a landing pad to the graph) and `CatchClauses.cs`.

Both walk the same chain: **section headers → `.eh_frame_hdr` binary-search table → FDE → CIE (for the
encodings) → LSDA in `.gcc_except_table` → call-site table**.

### 1a. Sections read

| | `lsda.py` | `ExceptionTable.cs` |
|---|---|---|
| `.eh_frame_hdr` | required, `Frames.__init__` `lsda.py:177` | required, `Sections` `ExceptionTable.cs:173` |
| `.gcc_except_table` | required, `call_sites` `lsda.py:225` | required, `Sections` `ExceptionTable.cs:175` |
| `.eh_frame` | **never named** — reached only through the FDE pointers in `.eh_frame_hdr` | same, never named |
| `.ARM.exidx` | not read; not consulted; no fallback | not read |

Neither reader has any fallback path. There is no `.eh_frame` linear walk if the header's search table is
missing, and no `.ARM.exidx`/compact-unwind reader at all. **`.eh_frame_hdr` is a hard dependency.**

Both find the sections by parsing the **ELF section header table** by hand
(`lsda.py:26-39`, `ExceptionTable.cs:143-180`) — not the program headers, and not through LibCpp2IL's model.
A `.so` stripped of section headers therefore answers nothing, even though the tables are still loaded.

### 1b. Pointer encodings accepted

`Cursor.encoded` (`lsda.py:94-134`) and `Reader.Encoded` (`ExceptionTable.cs:394-426`) are the same function.

Low nibble (format) accepted: `0x00` absptr(8), `0x01` uleb128, `0x02` udata2, `0x03` udata4, `0x04` udata8,
`0x09` sleb128, `0x0A` sdata2, `0x0B` sdata4, `0x0C` sdata8.
**Anything else throws** — `ValueError` (`lsda.py:121`) / `InvalidOperationException`
(`ExceptionTable.cs:412`). That covers every value DWARF actually defines, so it is not a realistic gap.

High nibble (relative-to) handled: `0x10` pcrel, `0x30` datarel, `0x40` funcrel; anything else is treated as
absolute (`lsda.py:126-133`, `ExceptionTable.cs:419-425`).

**The one real hole: bit `0x80` (`DW_EH_PE_indirect`) is silently ignored by both.** Neither masks it off nor
dereferences it — `enc & 0x0F` and `enc & 0x70` simply drop it, so an indirect pointer yields the *address of
the slot* rather than its contents, with no error. In practice `0x80` appears only on the personality routine
pointer (`0x9B` = indirect|pcrel|sdata4), which both readers read and **throw away**, so it is currently
harmless. Recorded because it would not announce itself if a toolchain used it elsewhere.

`datarel` (`0x30`) resolves against a base the caller sets: the `.eh_frame_hdr` address while reading the
header/FDE/CIE, the `.gcc_except_table` address while reading an LSDA (`lsda.py:198,225`;
`ExceptionTable.cs:190,271`).

### 1c. Personality routine

**Neither reader checks which personality routine is in use.** The CIE's `'P'` augmentation is parsed only to
step the cursor past it and the value is discarded (`lsda.py:162-166`, `ExceptionTable.cs:259-261`). Both then
parse the LSDA as the **GCC/Itanium `__gxx_personality_v0` format** unconditionally: LPStart encoding byte,
TType encoding byte (+ uleb offset), call-site encoding byte, uleb table length, then rows of
`(start, range, landing pad, uleb action)` (`lsda.py:222-246`, `ExceptionTable.cs:268-298`).

A different personality with a different LSDA layout (SjLj, `__gxx_personality_seh0`, Wasm) would be parsed as
if it were this one and produce **plausible garbage rows**, not an error. So the personality is worth checking
by hand even though the code never does — see §3.

### 1d. What happens on something unrecognised — the crux

This is what decides whether a mismatch is loud or silent, and the answer is **silent, at every level**:

| where | what it does |
|---|---|
| `ExceptionTable.cs:105-106` | not an ELF64 → `return`, `Usable` stays `false` |
| **`ExceptionTable.cs:108-109` + `:179`** | **either section missing → `return false` → `Usable` stays `false` → every later call answers `[]`** |
| `ExceptionTable.cs:116-117` | `.eh_frame_hdr` version != 1 → `return`, `Usable` false |
| `ExceptionTable.cs:125-126` | FDE count null, 0, or **> 4,000,000** → `return`, `Usable` false |
| `ExceptionTable.cs:92-95` | **any exception while building → `catch { Usable = false; }`** |
| `ExceptionTable.cs:64-65` | `!Usable` → `return []` |
| **`ExceptionTable.cs:67-75`** | **any exception while reading one function → `catch { return []; }`, comment: "A malformed or unexpected encoding answers nothing rather than taking the method down with it"** |
| `ExceptionTable.cs:200-201` | CIE where an FDE was expected → `[]` |
| `ExceptionTable.cs:210-211` | the query address is not inside the FDE's range → `[]` |
| `ExceptionTable.cs:215-216` | CIE has no `'L'` (no LSDA encoding) → `[]` |
| `ExceptionTable.cs:220-221` | LSDA pointer null or zero → `[]` |

**There is no log line, no counter and no marker on any of these paths.** A reader that does not understand
this binary is indistinguishable, from every scorer this project has, from a binary that contains no `try`
at all: `ExceptionEdges` attaches nothing, `CatchClauses` recovers nothing, every guarded region recovers as
straight-line code, and it all compiles whole. That is exactly the
`il2cpp-what-full-is-actually-worth` failure mode, and it is why this audit exists.

`lsda.py` is louder in two places and looser in one: it `assert`s the header version (`lsda.py:180`) and
raises `KeyError` if either section is missing (`lsda.py:177,225`), but its `lsda_for` has **no containment
check** — it takes the row before the query with `bisect_right` (`lsda.py:193`) and never asks whether the
query is inside that function, where `ExceptionTable.cs:210` does. So `lsda.py` can attribute a neighbour's
table; the C# will not.

Two shared minor looseness items, neither triggered by a normal LLVM CIE: an augmentation letter other than
`z L R P S` is skipped without advancing the cursor, silently mis-parsing everything after it
(`lsda.py:155-167`, `ExceptionTable.cs:249-263`); and the CIE's return-address register is read as ULEB in all
versions, which is correct for v3+ and coincidentally correct for v1 whenever the register number is < 128
(AArch64's LR is 30).

---

## 2. The binary

The runner's copy was still on disk and is the right one — 149,489,656 bytes, `6000.0.66f2` in its strings:

```
scratchpad/riprun/bin/Release/net10.0/temp/10f3/e11fa7d6/lib/arm64-v8a/libil2cpp.so
```

It was **copied** to a session temp dir and every measurement below was taken on the copy; nothing was written
into the runner's tree. Fluffy Field 1.0.3's `libil2cpp.so` was extracted from
`/Users/playviet/Documents/_APK/FluffyField/Fluffy Field_1.0.3_608101946.apk` to the same place.

## 3. What the two binaries actually contain

Every row below was measured, not assumed. The **encodings are byte-for-byte identical**; only the sizes
differ.

| | **Snacky Dash 1.11.0** | **Fluffy Field 1.0.3** | same? |
|---|---|---|:---:|
| Unity | 6000.0.66f2 | 6000.0.78f1 | – |
| `.so` size | 149,489,656 | 84,710,136 | – |
| linker | `LLD 18.0.3` | `LLD 18.0.3` | **yes** |
| compiler | `Android (12470979, +pgo, +bolt, +lto, +mlgo, based on r522817c) clang 18.0.3` | identical string | **yes** |
| ELF class / endian / machine | ELF64 / LE / AArch64 | ELF64 / LE / AArch64 | **yes** |
| section headers | present, 29, shstrndx 27 | present | **yes** |
| `.eh_frame_hdr` | 0x212af8c, **4,006,068 B** | 0x1259b74, 2,382,196 B | present in both |
| `.eh_frame` | 0x24fd640, **17,397,720 B** | 0x149f6e8, 10,323,504 B | present in both |
| `.gcc_except_table` | 0x168c728, **1,346,456 B** | 0xc6d610, 714,612 B | present in both |
| `.ARM.exidx` | **absent** | absent | yes (arm64 uses DWARF) |
| personality routine | `__gxx_personality_v0`, defined in-binary @ 0x3c03b68 | same symbol | **yes** |

Fluffy Field's three section sizes reproduce
[[il2cpp-the-catch-body-is-deleted-at-the-start]] exactly (714 KB / 2.4 MB / 10.3 MB), so the comparison
binary is the one the readers were calibrated on.

### 3a. `.eh_frame_hdr` header — identical

```
Snacky Dash   0212af8c:  01 1b 03 3b  b0 26 3d 00  d4 a4 07 00  94 de 46 01 ...
Fluffy Field  01259b74:  01 1b 03 3b  70 5b 24 00  6c 8b 04 00  ac 1f c2 00 ...
                         ^^ ^^ ^^ ^^
```

`version 1`, `eh_frame_ptr = 0x1B` (pcrel|sdata4), `fde_count = 0x03` (udata4),
`table = 0x3B` (datarel|sdata4) — **the same four bytes in both**, and every one of them on a path both
readers handle.

FDE count: **500,948** (Snacky Dash) vs 297,836 (Fluffy Field). The second reproduces the "297,836 functions
have an FDE" already on record. 500,948 is **12.5% of the 4,000,000 cap** at `ExceptionTable.cs:125`.

### 3b. CIEs — identical

There are **exactly two CIEs** in each binary's whole `.eh_frame`, and they are the same two:

| | Snacky Dash | Fluffy Field |
|---|---|---|
| CIE #1 | v1 `"zR"`, aug data `1B` | v1 `"zR"`, aug data `1B` |
| CIE #2 | v1 `"zPLR"`, aug data `9C D9AA580500000000 1C 1B` | v1 `"zPLR"`, aug data `9C 5D1C060300000000 1C 1B` |

Decoded, CIE #2 — the only one that carries an LSDA:

* **`P` = `0x9C`** = `indirect | pcrel | sdata8` → an 8-byte personality pointer. Both readers consume its
  8 bytes correctly and **discard the value**, so the ignored `0x80` indirect bit costs nothing here. The
  only difference between the binaries is the pointer itself, which must differ.
* **`L` = `0x1C`** = `pcrel | sdata8` → an **8-byte** LSDA pointer. Handled: `0x0C → 8 bytes signed`
  (`lsda.py:118`, `ExceptionTable.cs:411`), then `0x10 → pcrel` (`lsda.py:127`, `ExceptionTable.cs:421`).
* **`R` = `0x1B`** = `pcrel | sdata4`.

### 3c. LSDA headers — identical

Over **every** LSDA in each binary, only two header shapes occur, and they are the same two in both:

| LPStart enc | TType enc | call-site enc | Snacky Dash | Fluffy Field |
|---|---|---|---|---|
| `FF` (omit) | `9C` | `01` (uleb128) | 16,900 | 8,632 |
| `FF` (omit) | `FF` (omit) | `01` (uleb128) | 1,033 | 1,002 |

`LPStart = FF` → the landing-pad base is the function start (`lsda.py:228`,
`ExceptionTable.cs:274`) — correct. `TType = 9C` is only ever *skipped over* (a byte plus a uleb offset),
never decoded as a pointer, so the unhandled indirect bit again costs nothing.
Call-site rows are uleb128 function-relative offsets, which is what both readers add `function` to.

### 3d. Census — the tables parse, with zero failures

Run through `lsda.py`'s own classes so the audit tests the shipped code, not a re-implementation:

```
                                          Snacky Dash    Fluffy Field
functions with an FDE                         500948          297836
  of those, with an LSDA                       17933            9634
    with at least one CATCH call site          16900            8632
    catch call sites in total                 166623           81436
    cleanup/finally pads only                   1033            1002
parse failures                                     0               0
max call-site rows in one LSDA                   711             711   (C# cap is 4096)
```

**Zero exceptions raised across 17,933 LSDAs.** Fluffy Field's numbers reproduce the recorded
"9,634 an LSDA, 8,632 at least one catch call site, 81,436 catch call sites" exactly, which validates the
harness as well as the binary.

**Snacky Dash has roughly twice as much exception structure as Fluffy Field** — 16,900 catching functions
against 8,632, and 166,623 catch call sites against 81,436.

### 3e. The FDEs cover the code that matters

The game's method bodies are in the `il2cpp` section, not `.text`, so table coverage of `.text` alone would
be useless:

| | Snacky Dash | Fluffy Field |
|---|---|---|
| FDE starts in `.text` (runtime) | 132,885 | 81,249 |
| FDE starts in **`il2cpp`** (method bodies) | **368,063** | 216,587 |
| ...with an LSDA, in `.text` | 1,389 | 1,354 |
| ...with an LSDA, in **`il2cpp`** | **16,544** | 8,280 |

FDE starts are sorted ascending in both (the binary search in `Frames.lsda_for` / `UpperBound` requires it).

### 3f. The address mapping the C# reader depends on

`ExceptionTable.Reader.Take` (`ExceptionTable.cs:328`) goes through LibCpp2IL's
`ElfFile.MapVirtualAddressToRaw`, which searches the **program headers**, while `Sections()` reads the
**section headers**. That split is a place the two could disagree. They do not:

```
LOAD  off 0x000000  vaddr 0x0000000  filesz 0x3594e18  R    <- .gcc_except_table, .eh_frame_hdr, .eh_frame
LOAD  off 0x3594e20 vaddr 0x3598e20  filesz 0x4e419e0  R E  <- .text, il2cpp, .plt
```

All three EH sections sit in the first LOAD, where **vaddr == file offset**, so the mapping is the identity
there and cannot go wrong. (The code segment carries a 0x4000 skew, which the mapper handles correctly.)

### 3g. Relocations do not rewrite the tables

LibCpp2IL applies `.rela.dyn` into its in-memory copy of the file, so a relocation landing in an EH section
would silently corrupt what `ExceptionTable` later reads. Checked directly — **976,722 relocations scanned in
Snacky Dash, 534,546 in Fluffy Field, and not one lands in `.eh_frame`, `.eh_frame_hdr` or
`.gcc_except_table` in either.** As expected: those sections are self-relative by construction.

### 3h. The one C#-only branch, tested

`ExceptionTable.cs:210` rejects a query that is not inside the FDE's own `[pc_begin, pc_begin + range)`;
`lsda.py:193` has no such check, so the C# could reject what the Python accepts. Tested against the exact
predicate over every FDE:

```
                                              Snacky Dash    Fluffy Field
FDEs checked                                       500948          297836
  hdr-table start != FDE pc_begin                       0               0
  FDEs whose CIE has an 'L'                         17933            9634
  ...of those REJECTED by ExceptionTable.cs:210          0               0
```

**Not one rejection**, and the `.eh_frame_hdr` table's function start equals the FDE's own `pc_begin`
everywhere. The C# and the Python agree on this binary.

## 4. The parse is right, not merely successful

A silently-wrong reader produces plausible rows, so one was checked against the disassembly. Function
`0x3C215B8`, in the `il2cpp` section:

```
try [3C215FC, 3C21600) -> pad 3C21628  action 3     <- catch
try [3C21600, 3C21644) -> pad 0        action 0
try [3C21644, 3C21648) -> pad 3C2164C  action 0     <- cleanup
try [3C21650, 3C21658) -> pad 3C21660  action 5
```

```asm
3c215fc: bl   0x3c21ab0                      <- the protected call, [3C215FC, 3C21600) exactly
...
3c21628: cmp  w1, #0x1                       <- pad 3C21628: the unwinder's selector test
3c2162c: mov  x19, x0
3c21630: b.ne 0x3c21650
3c21638: bl   0x83d8230 <__cxa_begin_catch@plt>
3c21644: bl   0x83d8240 <__cxa_end_catch@plt> <- protected [3C21644, 3C21648) exactly
3c21648: b    0x3c21604
3c2164c: mov  x19, x0                        <- pad 3C2164C: the cleanup path
3c21660: bl   0x3598e80                      <- pad 3C21660: terminate
3c21664: sub  sp, sp, #0x50                  <- next function's prologue; one past the end
```

Every row lands on an instruction boundary and on exactly the right instruction, and the catch pad is the
`cmp w1, #1` / `bl __cxa_begin_catch` shape [[il2cpp-the-catch-is-in-the-graph-already]] describes. This is
the real table, not a coincidence.

## 5. The consumers are not binary-specific either

* `ExceptionEdges.cs:61` calls `ExceptionTable.For(method.AppContext, method.UnderlyingPointer)` — the hook
  `ForkPipeline.AfterTheGraphIsBuilt` is still wired from `MethodAnalysisContext.cs:372`.
* **No per-binary constant exists in any of the three files.** The only hex literals in
  `CatchClauses.cs`, `ExceptionEdges.cs` and `ExceptionTable.cs` are two `0xFFFFFFFF` DWARF64 length escapes;
  the `D5E510` / `6959B0` addresses appear only in doc comments.
* `CatchClauses.Dispatch` recognises the managed dispatch purely by **shape** — a `ConditionalJump` on a
  `CheckEqual`/`CheckNotEqual` against zero, over an optional `And ..., 1`, over an unresolved call, with the
  caught type taken from an `Il2CppClass<T>`-typed operand. No addresses and no symbol names, so it carries
  across binaries by construction.

---

## 6. Verdict

**Yes — the reader handles this binary as written. It is not silently finding nothing.**

The two binaries were produced by the *same* toolchain — `LLD 18.0.3` and Android clang 18.0.3 `r522817c`,
character-for-character the same `.comment` string — and they emit character-for-character the same unwind
encodings. Every byte that decides the parse is identical:

* both sections present, section headers present, `.ARM.exidx` absent in both (arm64 is DWARF-only);
* `.eh_frame_hdr` header `01 1B 03 3B` in both;
* two CIEs in both, `"zR"`/`1B` and `"zPLR"`/`9C…1C 1B` — same augmentation string, same `L`, `P` and `R`
  encoding bytes;
* the same two LSDA header shapes, `FF/9C/01` and `FF/FF/01`;
* `__gxx_personality_v0` in both, which is the LSDA layout both readers assume.

And it is confirmed by execution rather than by inspection: **17,933 LSDAs parsed with zero failures,
16,900 of them carrying a catch call site, 166,623 catch call sites in all, 16,544 of the LSDA-bearing
functions in the `il2cpp` section where the game's own bodies are** — and a spot check against the
disassembly puts every call-site row on exactly the right instruction.

**The branch that would have decided it otherwise is `ExceptionTable.cs:179`**

```csharp
return header != 0 && exceptTable != 0;
```

— reached from `ExceptionTable.cs:108-109`, whose `return` leaves `Tables.Usable == false`, after which
`ExceptionTable.cs:64-65` answers `[]` for **every method in the game** with no log line, no counter and no
marker. On this binary it returns `true`: `.eh_frame_hdr` is at 0x212af8c and `.gcc_except_table` at
0x168c728, both non-zero. The second such branch, `ExceptionTable.cs:67-75`, catches every parse exception
and answers `[]` per method; on this binary it never fires, because nothing throws.

### What this does and does not establish

**Established:** the table layer is sound on Snacky Dash. `try` ranges and landing pads are being read, and
read correctly.

**Not established:** how many `catch` clauses actually reach the exported C#. That is the structural half —
`ExceptionEdges`' three deliberate under-reaches and `CatchClauses`' dispatch recognition — and only an
export measures it. The recogniser is shape-keyed, so there is no *a priori* reason it should fail here, but
"the table parses" and "a clause is recovered" are two different claims and only the first is proven above.
**The measurement to run after the current export: count `catch` in the exported tree.** Zero, against
16,900 functions that have a catch call site, would mean the failure moved downstream of this audit.

### Two latent hazards, neither firing on this binary

1. **`DW_EH_PE_indirect` (`0x80`) is silently ignored by both readers** — masked away rather than
   dereferenced, yielding the address of a slot instead of its contents. It occurs here only on the
   personality pointer (`0x9C`) and on the TType encoding, both of which are consumed for their byte count
   and discarded, so it is inert. A toolchain that used it on the `L` or call-site encoding would produce
   wrong pads with no error.
2. **The 4,000,000-FDE cap** (`ExceptionTable.cs:125`) is the one limit that scales with binary size.
   Snacky Dash is at 500,948 — 12.5% — so there is room, but it is worth remembering that exceeding it
   disables the whole feature silently.

**The general finding for the next binary:** none of these checks needs a build. `lsda.py <so> --census`
plus the four header bytes of `.eh_frame_hdr` and the augmentation data of each CIE answer the question in
under a minute, and a census of zero is the signal that the reader has met an encoding it does not know.
