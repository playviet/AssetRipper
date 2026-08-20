# The native integer in recovered C# — what it is, how much of it there is, and what is actually broken

Written against **Snacky Dash 1.11.0, export `_4`** (round 5, finished) and
**Fluffy Field 1.0.3, export `_720`**, with `scratchpad-tools/nintcensus.py` (this file's instrument).
`probe` was **not built** in this session (`scratchpad/probe/bin` is empty and the lane was told not to
build), so everything below is read off the exported C# and off the generator's own source. **No ISIL was
read.** Where that matters it is said so.

## Why look

Three `CS0019` errors survived to the compiler in round 5, and all three are a native integer meeting
something that is not one:

| site | error |
|---|---|
| `JuicedUp/Features/CloudContent/CloudContentDebugger.cs:1389` | `'<=' cannot be applied to 'UIntPtr' and 'UIntPtr'` |
| `JuicedUp/Features/Core/CrateProgressTracker.cs:739` | `'>' cannot be applied to 'int' and 'IntPtr'` |
| `JuicedUp/Features/Core/SnakeOccupancyManager.cs:682` | `'>' cannot be applied to 'int' and 'IntPtr'` |

Three errors is what *survived*. The question the census answers is how much of the same thing is sitting
in bodies that compile.

## The census — Snacky Dash `_4`

`python3 scratchpad-tools/nintcensus.py "<export>"`. Occurrences, not lines; tiers from `gamefilter.py`.
(This build has no `unity`/`bcl` .cs at all — the export writes `Assembly-CSharp` only.)

```
shape            gameplay       meta        sdk      total
[live]
guard-zero           1267        645        243       2155
decl-default          472        346        386       1204
decl-copy             245        138        204        587
decl-zero             239        147        184        570
compare                62         18        140        220
decl-bare              88         50         60        198
cast                   16          4         25         45
addr-arith             24          0          8         32
other                  10          6          3         19
unsafe-as               2          0          0          2
intptr-zero             2          0          0          2
TOTAL                2427       1354       1253       5034

[commented] TOTAL    364        208        977       1549
```

**5034 live occurrences, 3781 of them in the game's own code.** But they are not one defect, and the split
is the whole point:

### 1. `guard-zero` — 2155 live, 1912 in game. Collateral, not a typing defect, but the worst thing here.

    if ((nint)0 == 0)

**2141 of the 2151 live occurrences are immediately preceded by an `Unmanaged memory load:` marker**
(measured, 3-line lookback). This is `IlGenerator.LoadOperand`'s fallback for a memory operand that did not
resolve: `Ldstr` + `Console.WriteLine` + `Ldc_I4_0` + `Conv_I`. The condition that should have tested the
loaded value tests a placebo instead, so **1912 branches in game code are decided at compile time and always
taken**. They compile. They are silently wrong. They are pure downstream of the `unmanaged` marker family
and the way to shrink them is to resolve the loads, not to touch the native int.

### 2. `decl-*` — 2559 live (1725 in game). The absence of a type, not a wrong type.

    IntPtr intPtr = default(IntPtr);   nint num = 0;   nint num2;   IntPtr a = b;

A value the fixpoint never typed lowers to `IL2CPP_TYPE_I` and is declared native-int. That is the
convention, deliberately: `LowersToNativeInt(null) == true`. These are the visible surface of
[[il2cpp-untyped-bases-are-downstream]] and of every unresolved call's result; they are not a mistyping to
correct in the generator.

### 3. `compare` — 220 live (80 in game). Almost all of it compiles and is fine.

By shape, over the whole export: `0 == (nint)obj` and `0 != (nint)obj` (~68) are null tests against an
untyped value and are correct; `(nuint)a < (nuint)8u` (~30) are the unsigned bound checks
([[il2cpp-a-carry-condition-is-an-unsigned-comparison]]) and are correct.

### 4. `addr-arith` — 32 live (24 in game). Silently wrong, small.

    num2 = (int)((nint)0 + (nint)num2);

Placeholder zero folded into address arithmetic. Same root as `guard-zero`.

### 5. `unsafe-as` + `intptr-zero` — 4 live, and they are exactly the three compile errors.

## Fluffy Field `_720`, the same census

```
[live] decl-default 74 · decl-copy 46 · decl-zero 21 · guard-zero 20 · cast 14 · compare 14 ·
       decl-bare 10 · addr-arith 2      TOTAL 201 (all gameplay)
[commented] TOTAL 24
```

**Zero ordered comparisons involving a native int, and 20 `guard-zero` against Snacky's 2155.** The
100-fold gap in `guard-zero` is the same gap `SNACKY-ROUNDS.md` records for `notfound`/`indirect`: the
unmanaged-load family is where this fork is fitted to Fluffy Field.

## The three errors, one at a time

### `CloudContentDebugger.cs:1389` — an ordered comparison between two **reference**-typed operands

```csharp
//screen4 = (Voodoo.Sauce.Debugger.Screen)num;    <- commented out, could not be kept as code
//screen5 = (Voodoo.Sauce.Debugger.Screen)num2;
if (Unsafe.As<Screen, UIntPtr>(ref screen5) <= Unsafe.As<Screen, UIntPtr>(ref screen4))
```

`num` and `num2` are the two counters the loop just kept (`num` = files displayed, capped at 40; `num2` =
files matched). The registers holding them were typed `Voodoo.Sauce.Debugger.Screen`, a **class**, so
`num2 <= num` was emitted as `clt`/`cgt` on two object references. `Unsafe.As<T, UIntPtr>` is *ILSpy's*
spelling of an ordered comparison on references — there is no native integer in the IL at all.

**Root: a register carrying both a `Screen` and a counter, typed from the wrong end.** The same shape
[[il2cpp-the-nint-came-from-a-phi-edge]] and [[il2cpp-a-declaration-beats-a-width-for-references-too]]
describe. It is *not* the same defect as the other two, and it is not fixable in the generator: widening a
reference to `int64` is not possible.

### `CrateProgressTracker.cs:739` and `SnakeOccupancyManager.cs:682` — the same one defect

Both are, in the original, `dict.TryGetValue(key, out int v) && v > 0`:

```csharp
TailColorCount.TryGetValue(key, out var value) && value > unchecked(global::System.IntPtr.Zero)
_tailCellCountDict.TryGetValue(cell, out var value); bool flag2 = value > unchecked(...IntPtr.Zero);
```

`value` is `int`. The right-hand zero reached the IL stack as a **native integer**, i.e. as
`ldc.i4.0; conv.i`. There are exactly five places that emit that pair, and only four are reachable for a
comparison operand:

| emitter | `IlGenerator` | leaves a marker? |
|---|---|---|
| a memory operand that did not resolve | `.cs:861-864` | **yes**, `Unmanaged memory load:` |
| a `RuntimeMethodInfoAnalysisContext` used as a value | `.cs:875-876` | no |
| a `TypeAnalysisContext` used as a value (a class handle) | `.cs:885-886` | no |
| `PushDefaultOf` / `AddDefaultValue` of a native-int type | `.cs:924`, `.Fork.cs:2143` | `PushDefaultOf` yes |

Neither site carries a marker within three lines, so the native-int side is **a class handle or a
MethodInfo used as a value** — a runtime stand-in where a number belongs. *Which* of the two cannot be
settled without an ISIL dump, and `probe` is not built.

**Why it does not compile, and why nothing else like it does.** `widenToInt64` in `IlGenerator.cs:606`
already exists for exactly this: an ordered comparison with a native int on either side converts both sides
to `int64` first, because C# has no `>` on `System.IntPtr`. But it asks
`LowersToNativeInt(instruction.Operands[k])`, and that overload (`IlGenerator.Fork.cs:1750`) is

```csharp
private static bool LowersToNativeInt(object operand) =>
    operand is LocalVariable local && LowersToNativeInt(local.Type);
```

— it answers **only for a local**. A native int that arrives from any of the four rows above is invisible to
it, the widening does not happen, and `cgt` between an `int32` and a native int is what the decompiler
writes as `int > IntPtr`.

**This family has no silent tail, and that is the finding.** An `int` against a native int in an *ordered*
comparison always fails to compile (`System.IntPtr` has no relational operators), so every member of the
family is already a compile error; there is none sitting quietly computing a wrong answer. The same operand
kind in an *equality* is the 1694 `(nint)0 == 0` lines — those compile, and their wrongness is the
`guard-zero` story above, not this one.

## Is there a general rule?

**Two of the three, yes; the third, no — and the third is a different defect entirely.**

The rule for the two: *a native integer on the IL stack does not have to have come from a local.* It is a
statement about the generator's own emission, not about this binary, and it lives entirely in the fork's own
file. Applied at `LowersToNativeInt(object)` it can only fire in an ordered comparison (`mixedEquality`
independently requires both operands to be `LocalVariable`, and `widenToInt64` is reached from nowhere
else), so its blast radius is exactly the sites that already fail to compile.

Written at `External/Cpp2IL/Cpp2IL.Core/IlGenerator.Fork.cs`, `LowersToNativeInt(object)`. **UNMEASURED** —
this lane could not build. It is inert on Fluffy Field by construction (that export has zero ordered
comparisons involving a native int), which makes it safe there but not *validated* there.

The rule for `CloudContentDebugger` would be "an ordered comparison between two references is
inexpressible", and it is the wrong place to act: the operands should not have been references. Left alone.

**The change is recorded as a row in `External/Cpp2IL/FORK.md`.** It is one predicate in the fork's own
file; nothing upstream moved. Measure it with `gamescore.py` + `anyverify.sh` - the only thing it can move
is the compile gate, from 3 CS0019 to 1 (the `CloudContentDebugger` one is a different defect and stays).
