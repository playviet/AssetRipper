# MATHIMPORTS — the cheap, general half of the call gap

Three additions to `NewArmV8InstructionSet.Fork.ImportedCall`, the hook that names an imported C function
from the ELF at lift time (`FORK.md`, and `il2cpp-the-import-is-named-in-the-file`). Nothing here is keyed on
a virtual address: every decision is made from the `.plt` stub's `R_AARCH64_JUMP_SLOT` symbol name.

## The evidence — call sites per import, counted on both binaries

Counted directly from the ELFs, not from an export: every `BL`/`B` in the `il2cpp` and `.text` sections whose
target is a decoded `.plt` stub, grouped by the stub's dynamic symbol.
Script `importcnt.py` beside `elfmap.py` in the session scratchpad; binaries
`.../temp/ac55/570aa1f3/lib/arm64-v8a/libil2cpp.so` (Snacky Dash) and `_APK/FluffyField/.../libil2cpp.so`.

| symbol | Snacky Dash | Fluffy Field | in both `.plt`? | decision |
|---|---|---|---|---|
| `fmod` | 292 | 12 | yes | **`%`** |
| `fmodf` | 350 | 28 | yes | **`%`** |
| **fmod family** | **642** | **40** | | |
| `sinh` | 21 | 0 | Snacky only | **`Math.Sinh`** |
| `cosh` | 21 | 0 | Snacky only | **`Math.Cosh`** |
| `tanh` | 21 | 0 | Snacky only | **`Math.Tanh`** |
| `ldexpf` | 3 | 5 | yes | **left as a marker — see below** |
| `scalbn` | 4 | 4 | yes | left — same shape as `ldexp` |
| `memcpy` | 34697 | 22132 | yes | **named in the marker only** |
| `memset` | 9593 | 3169 | yes | **named in the marker only** |
| `memmove` | 3121 | 2221 | yes | **named in the marker only** |

Binary-wide counts. Only the fraction of these sites that lands in `Assembly-CSharp` reaches the export, and
several families (`memcpy` as a struct assignment, `memset` as `ClearedStruct`) are absorbed by passes before
a marker is ever written — so these are the population, not the prediction.

Two further imports the census turned up that are **not** part of this task and are unmapped today:
`log2` (101 sites, Snacky only) and `hypot` (4/4). `Math.Log2` exists in netstandard2.1 and would be a
one-word addition; not done here, not measured.

## 1. `fmod`/`fmodf` -> `%`

`OpCode` had `Divide` and no remainder, which is why the math table skipped `fmod` — and it could not have
been mapped to a method either: `Math.IEEERemainder` is **not** `fmod`. It rounds the quotient to nearest;
`fmod` truncates it. C#'s `%` on `float`/`double` and the CLR's `rem` are defined as exactly the truncated
remainder, so this is an identity, not an approximation.

Files, with the upstream half kept to one line each:

| file | change |
|---|---|
| `Cpp2IL.Core/ISIL/OpCode.cs` | one enum member, `Modulus`, **appended** so no existing value moves |
| `Cpp2IL.Core/ISIL/Instruction.cs` | one `case` beside `Divide` in `GetOrSetDestination`, one `or` in `GetSources` |
| `Cpp2IL.Core/IlGenerator.cs` | one `case` in the arithmetic group, one line lowering it to `CilOpCodes.Rem` |
| `Cpp2IL.Core/Analysis/LocalVariables.cs` | one `case` in `PropagateArithmetic`, so a modulus propagates its operand type as a division does |
| `Cpp2IL.Core/Analysis/ArithmeticProducesANumber.cs` (fork) | `Modulus` in `IsArithmetic` |
| `Cpp2IL.Core/InstructionSets/NewArmV8InstructionSet.Fork.cs` (fork) | the hook in `ImportedCall` |

Precedent for the shape: `Select` was added the same way and touches the same four files (`FORK.md`).
`IlGenerator`'s `default:` is a marker, not a throw, so an opcode that reached an unprepared consumer would
degrade rather than lose the body — but every consumer that matters is listed above.

**Operand order and width.** `fmod(x, y)` is `x % y`; aapcs64 puts `x` in `v0`, `y` in `v1`, result in `v0` —
the same float run `pow` and `atan2` in the table beside it already use, so `[V0, V0, V1]`.
The width is deliberately *not* spelled at the instruction: `RegisterFor` normalises `s0`/`d0`/`v0` to one
name and the value's type reaches the register from elsewhere, exactly as for every other entry in this hook.
That is why `PropagateArithmetic` and `ArithmeticProducesANumber` had to learn the opcode — without them a
double modulus in `v0` could keep a bare-integer type and lower to an *integer* `rem`, which is
`il2cpp-a-width-is-not-a-type` in its most expensive form.

Expect it to move: **642 binary-wide sites on Snacky Dash, 40 on Fluffy Field**, of which only the
`Assembly-CSharp` share reaches the export. Each site is one `Method not found @...` that becomes a `%`.

## 2. `sinh`/`cosh`/`tanh` in the math table — done; `ldexp` — NOT done

`System.Math.Sinh/Cosh/Tanh(double)` exist and are the same shape, so they are three words in the existing
`switch`. All 63 Snacky Dash sites are the double form. Should a binary import `sinhf`, the existing suffix
rule takes it to the same entry and `MathIntrinsics.Resolve` reaches `System.MathF`, which has all three; and
where a runtime has neither, `Resolve` returns null and the marker stays. Safe in every direction.

**`ldexp`/`ldexpf` is left as a marker, and so is `scalbn`.** `ldexp(x, n)` is `x * 2^n` and its second
argument is an **integer in `w0`, not a float in `v1`** — so it is neither a managed method of this hook's
shape nor a call whose operands this hook can name from the float run. The nearest managed equivalent,
`Math.ScaleB`, takes `(double, int)`, which `MathIntrinsics.Resolve` cannot match: it requires every parameter
to be of the instruction's own float width. 3 sites on Snacky Dash and 5 on Fluffy Field — not worth a second
argument convention, and a marker is better than a wrong answer.

## 3. The marker names the import

`Cpp2IL.Core/Analysis/UnresolvedCallMarker.cs` (new) + **one line** in `IlGenerator.cs`. A call target that
resolves to no method now reads `Method not found @83D8280 (memcpy)` instead of `Method not found @83D8280`.
Nothing else changes: same instruction, same operands, same recovered statements — only the string carried by
a statement that recovered nothing.

**The `Method not found @` prefix is kept, and that is not cosmetic.**
`Source/AssetRipper.Export.UnityProjects/Scripts/InvalidSourceRepair.cs:200` holds a `TracePrefixes` list and
matches on that prefix to turn the trace from a live `Console.WriteLine` into a discard `_ = "...";`. A marker
that stopped matching it would stay a **live call** — a recovered loop over a struct copy would then write a
line per iteration, which is the gigabytes-of-log failure that list exists to prevent. So the name is appended
after the address rather than replacing the prefix, and `Source/` is not touched.

The consequence is that `CALLGAP.md` proposal 8's secondary aim — taking the 183 deliberate sites *out* of the
`notfound` census — is **not** delivered here, and the `notfound` count will not move. Doing that needs a new
prefix in `InvalidSourceRepair.TracePrefixes`, which is a `Source/` change and belongs to whoever owns that
file next. What is delivered is the diagnostic half: every unresolved import now says what it is, `memcpy`,
`memmove`, `memset`, `ldexpf`, `log2`, `_ZdlPv` and the rest alike.

## What to expect from a measurement

* `notfound` should **fall** by the `Assembly-CSharp` share of the fmod family and of `sinh`/`cosh`/`tanh`,
  and be **unchanged** by change 3.
* `full` may move either way at a fmod site: a marker statement becoming a real `%` can leave the statement
  around it commented instead, which is the better of the two (`il2cpp-the-import-is-named-in-the-file`
  measured exactly that trade the first time this hook landed).
* The correctness measures are the ones that decide it: `roundtrip`, `decisions`, and the corpus. A `%` that
  computes the right answer where a marker computed none is what this is for.
* Watch for an **integer** `%` appearing where a float one belongs — that would mean the type did not reach
  `v0`, and it is the one way this change can be whole and wrong.

## Not done, and why

* `log2` (101 sites, Snacky only): `Math.Log2` exists in netstandard 2.1 and it is one word. Out of scope for
  this task, and unmeasured — if it is taken, check `Resolve` actually finds it in *this* game's mscorlib.
* `hypot` (4/4 sites): no `Math` counterpart of the same shape.
* The `notfound` census change for `memcpy` — needs `Source/`, see above.
