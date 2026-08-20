# GATEFLOOR.md — every error the Snacky Dash gate still reports, classified

Export `/Users/playviet/Documents/_APK/Snacky Dash/Snacky Dash_1.11.0_4/ExportedProject` (round 5's `_5`
did not exist yet — an export was mid-write, `pgrep -f net10.0/riprun` returned pid 14447).
Gate: `anyverify.sh`, `ANYVERIFY_REFS=scratchpad/pkgrefs`, Unity 6000.0.78f1 (project says 6000.0.66f2),
2787 `.cs`, 228 reference assemblies (121 engine, 93 plugins, 14 extra).

    before this document's harness fix   error CS : 50
    after  it                            error CS : 47      compiled : no

**Read the count with the tier rule.** Roslyn stops at each tier, so a class of error fixed unmasks the next
and a class of error *unmasked* is not a regression. This session the number went
1 → 1352 → 45 → 25 → 20 → 1 → 29 → 50 → **47**. The 50 is not comparable to the 29 — that run used a
different reference set. Compare a number only against a run with the same export **and** the same refs, and
the 47 only against `anyverify.sh` at or after the `mscorlib` change below.

## The whole census

| code | n | class | root |
|---|---|---|---|
| CS7036 | 22 | **(a) recovery defect** | the base constructor il2cpp inlined — no `: base(...)` emitted |
| CS1729 | 22 | **(a) recovery defect** | same family; 21 "no ctor takes 0 arguments", 1 wrong-arity `: base(...)` |
| CS0019 | 3 | **(a) recovery defect** | `int > IntPtr` / `UIntPtr <= UIntPtr` — the untyped-base family |
| ~~CS7069~~ | ~~3~~ | ~~(b) harness~~ | **CLOSED** — the reference set had no usable `mscorlib`; the gate now builds one |

**(a) real recovery defects: 47. (b) harness/reference artifacts: 0 (was 3, closed). (c) unfixable: 0.**

Nothing is unclassified and nothing is parked in (c). Every one of the 47 is the export's own.

---

## (a) 44 errors, one family: **the base constructor il2cpp inlined**

This is 88% of the gate and it is a single root, not two codes. il2cpp inlines a constructor whose body is
small — and a base constructor that only stores its arguments always is. What the IL then contains is one of
two shapes, and the export writes a constructor with no legal base initializer either way.

Confirmed against the binary (`probe … dump <Type>` on Snacky Dash's own `libil2cpp.so`; note
`scratchpad/probeg` points at **Fluffy Field's** binary via `scratchpad/apkpath` and silently prints nothing
for a Snacky Dash type — that is not "the method is missing").

### Shape A — the call survives, uncompilable, and is commented out (27 of 44)

The `.ctor` call on `this` is still in the IL. The decompiler writes it as a method call, which is not C#,
and `InvalidSourceRepair` comments it out. The constructor is then left with no base initializer at all:

```csharp
public ES3Type_intArray()          // CS1729: 'ES3ArrayType' does not contain a constructor that takes 0 arguments
{
    Type typeFromHandle = typeof(int[]);
    ES3Type instance = ES3Type_int.Instance;
    //AssetRipper: commented out, this could not be kept as code.
    //((ES3Type)this)._002Ector(typeFromHandle);
    elementType = instance;
    …
}
```

and the ISIL says exactly that — `58 CallVoid ES3Type..ctor, this @ X0, v51 @ X0_v13 (System.Type)`.

**The arguments are right there.** Every one of the 27 carries the call verbatim:

| site | commented call | the base initializer it should be |
|---|---|---|
| `ES3Types/ES3Type_intArray.cs:14` | `((ES3Type)this)._002Ector(typeFromHandle)` | `: base(typeFromHandle)` |
| `ES3Types/ES3Type_ES3RefDictionary.cs:15` | `base._002Ector(typeFromHandle, instance, instance)` | `: base(typeFromHandle, instance, instance)` |
| `ES3Internal/ES3JSONWriter.cs:25` | `base._002Ector(settings, flag, flag2)` | `: base(settings, flag, flag2)` |
| `ES3Internal/ES3FileStream.cs:18` | `base._002Ector(path: path2, mode: …, access: …, share: …, bufferSize: …, useAsync: useAsync)` | the same, as `: base(...)` |
| `Voodoo/…/ProgressEvent.cs:59` | `base._002Ector(obj.ToString(), null)` | `: base(obj.ToString())` — see the ancestor rule below |

The 27, by file: 22 × `ES3Types/*`, `ES3Internal/ES3FileStream.cs`, `ES3Internal/ES3JSONWriter.cs`,
`Voodoo/…/Analytics/ProgressEvent.cs`, `Voodoo/…/PerformanceTracking/CustomPerformanceTrackingManager.cs`,
`Voodoo/…/Analytics/VoodooAnalyticsLoggerEvent.cs` (2 constructors).

**This half is mechanical and has no ambiguity in it**: a `_002Ector` call on `this`/`base` as the first
effectful statement of a constructor is the base initializer, and its argument list is already recovered.
Two obstacles are real: C# requires the arguments be evaluable *before* `this` exists (`typeFromHandle` and
`instance` are locals assigned above the call in `ES3Type_intArray`, so they must be inlined into the
initializer or the whole thing must move), and where the call names a **strict ancestor** rather than the
direct base it needs the ancestor rule below.

### Shape B — the call is gone; the base constructor's body was inlined (17 of 44)

Nothing marks these. The base constructor's statements are simply in the derived constructor, and the
recovery is *semantically correct* — it just is not legal C#, because C# has no way to say "my base
constructor already ran".

```csharp
public sealed class BlockerLdfTutorialCondition : BaseLdfTutorialCondition
{
    public BlockerLdfTutorialCondition(ILdfTutorialRepository repository)   // CS7036
    {
        base._repository = repository;      // this IS BaseLdfTutorialCondition(ILdfTutorialRepository)'s body
    }
}
```

ISIL: `10 Nop / 12 Move this._repository, repository @ X1 / 22 Return`. The call is not merely un-hoisted —
it is **not in the binary**. This is `il2cpp-an-inlined-constructor-builds-the-wrong-object` one level up:
that memory is about a constructor inlined at an *allocation site*; this is one inlined into its own
*derived constructor*.

The 17: 11 × `JuicedUp/Features/Core/LdfTutorial/Conditions/*LdfTutorialCondition.cs`,
2 × `Voodoo/Live/Debugger/{Wheel,FeatureGroup}OfferDebugUI.cs`,
3 × `Voodoo/…/PerformanceTracking/{FixedIntervalFrameCounter,IntervalBased…,SceneBased…}.cs`,
and `Voodoo/…/Analytics/DesignEvent.cs`.

**The recovery for shape B is the InlinedConstructorRecovery rule, applied to the base initializer**: match
each write in the run against a parameter of the direct base's constructor (field name without `_`/`m_`, plus
type), and when *every* parameter is matched exactly once, delete the run and emit `: base(args)`. The
existing pass already has this matcher, and its own hard-won rule applies unchanged — fold only on a total
match, or it will build the wrong object (`il2cpp-the-error-census-is-not-a-defect-census`, the `MakeIdent`
case). For the 11 `LdfTutorialCondition`s the match is exact and total: one parameter, one write.

### `DesignEvent` — the wrong-arity `: base(...)`, and it is exactly one site

The parent's diagnosis is **confirmed from the binary**, not just plausible:

```
===== Voodoo.Sauce.Internal.Analytics.DesignEvent::.ctor  Void (String eventName, Dictionary`2 customFields)
  b2 [Call]
      12 CallVoid BaseAnalyticsEvent..ctor, this @ X0 (…DesignEvent), eventName @ X1 (System.String), 0
      14 Move this._customFields, customFields @ X2
```

`DesignEvent : GameAnalyticsEvent : BaseAnalyticsEvent`. `GameAnalyticsEvent(string eventName)` does nothing
but `: base(eventName, null)`, so il2cpp inlined it away and the IL calls **`BaseAnalyticsEvent..ctor`
directly, with the grandparent's two arguments**. The decompiler hoisted that call into a base initializer
correctly — but "base" of `DesignEvent` is `GameAnalyticsEvent`, which takes one argument:

```csharp
public DesignEvent(string eventName, Dictionary<string, object> customFields)
    : base(eventName, null)      // CS1729: 'GameAnalyticsEvent' does not contain a constructor that takes 2
```

C# has no syntax for skipping a level, so the emitted text is unfixable *as written*. **Refuted only in one
detail**: the situation is not unfixable, because the level that was skipped is a pure forwarder.

**The correct recovery, and it generalises.** When a hoisted `.ctor` call targets a strict *ancestor* A of the
declaring type rather than the direct base B, walk B's constructors and take the one whose own base
initializer forwards to A with an argument list matching the call's. Here `GameAnalyticsEvent` has exactly one
constructor and it forwards `(eventName, null)` — the call's `(eventName, 0)` — so the exact answer is

```csharp
    : base(eventName)
```

Where no forwarding constructor of B matches, the body genuinely cannot be expressed and the site should be
marked rather than emitted wrong. That case does not arise in this export.

**How many constructors have this shape? Exactly one.** A whole-export census
(`ctorcensus.py`, below) over 2773 types / 966 constructor-bearing types finds 189 `: base(...)` initializers
that resolve, and 9 whose argument count is not an arity of the direct base — but 8 of the 9 are false
positives of arity-only matching, because the base constructor has an **optional** parameter
(`BaseAnalyticsEvent(string, Dictionary<string,object> = null)` accepts `base(1)`;
`ApiServiceBase`, `PurchaseFailedException` likewise). The compiler agrees: only `DesignEvent` errors.

**So the ancestor-skipping shape alone does not justify a pass.** The family it belongs to — the inlined base
constructor, 44 sites, 88% of the gate — emphatically does, and the ancestor rule is one clause of it.
Shape A (27) is the cheap half and is worth doing first.

## (a) 3 errors: `CS0019`, arithmetic on `IntPtr`/`UIntPtr`

    JuicedUp/Features/CloudContent/CloudContentDebugger.cs(1389,16)  Operator '<=' … 'UIntPtr' and 'UIntPtr'
    JuicedUp/Features/Core/CrateProgressTracker.cs(739,65)           Operator '>'  … 'int' and 'IntPtr'
    JuicedUp/Features/Core/SnakeOccupancyManager.cs(682,17)          Operator '>'  … 'int' and 'IntPtr'

Real defects, the untyped-base family seen from the compiler's end
(`il2cpp-untyped-bases-are-downstream`, and `CS0019` is the same code that family produces on Fluffy Field).
**Owned by another agent** (`scratchpad-tools/NINT.md`, the `IntPtr` family) — recorded here for the count,
not worked.

## (b) → 0 errors: `CS7069` was the harness, and it is now **closed**

    BoosterManager.cs(183,20)                              Reference to type 'IValueTaskSource' claims it
    JuicedUp/…/BoosterForcedTutorialController.cs(314,9)   is defined in 'mscorlib', but it could not be
    JuicedUp/Features/Settings/SettingsPopup.cs(403,21)    found

**The root is not `IValueTaskSource` and not the recovered C#.** All three lines touch UniTask —
`uniTaskSource?.GetResult(token)` on a `Cysharp.Threading.Tasks.IUniTaskSource`. Read straight out of the
metadata rather than guessed:

    UniTask.dll        -> mscorlib v4.0.0.0 b77a5c561934e089        (assembly reference)
    TYPEREF UniTask.dll        System.Threading.Tasks.Sources.IValueTaskSource   -> [mscorlib]
    TYPEREF UniTask.dll        System.Threading.Tasks.Sources.IValueTaskSource`1 -> [mscorlib]
    TYPEREF UniTask.DOTween.dll System.Threading.Tasks.Sources.IValueTaskSource  -> [mscorlib]

`UniTask.dll` ships in the export's `Assets/Plugins`; it was compiled in the player build against Unity's
`mscorlib`, and `IUniTaskSource` implements `[mscorlib]IValueTaskSource`. To bind *any* member call on
`IUniTaskSource` the compiler must first resolve that base interface. The Unity editor never reports this,
because it compiles the project against Unity's own `mscorlib`.

### Where the "stripped mscorlib" theory dies

The netstandard2.1 reference pack **does** ship an `mscorlib.dll`
(`~/.nuget/packages/netstandard.library.ref/2.1.0/ref/netstandard2.1/mscorlib.dll`). It is a *facade*: 1055
`TypeForwardedTo` rows covering the netstandard2.0-era mscorlib surface, and `IValueTaskSource` is not one of
them. And the stripping story is wrong in the other direction too — **both** Unity's editor mscorlib
(`MonoBleedingEdge/lib/mono/unityaot-linux/mscorlib.dll`, v4.0.0.0) **and** the game-hub
`Il2CppBackup/Managed/mscorlib.dll` declare `IValueTaskSource`, `IValueTaskSource\`1`, `IBufferWriter\`1` and
`ReadOnlySequence\`1` outright. The types were always on disk. Nothing was ever stripped.

### Three routes measured, on export `_4`

| route | errors | CS7069 | why |
|---|---|---|---|
| baseline | 50 | 3 | |
| `mscorlib` added as an **extra reference** — Unity's, or a facade of ours | **50** | **3** | no effect at all. RAR resolves `mscorlib` to the ref pack's facade and **silently drops the duplicate**; the reference never reaches csc |
| Unity's real mscorlib **force-swapped** into `@(ReferencePath)` after RAR | — | — | **Roslyn crashes**: `NullReferenceException` in `PEModule.HasAttributeUsageAttribute`, 72 stack-frame "errors", no output assembly. A second `System.Object` in a netstandard2.1 compilation is not survivable |
| a minimal facade of ours force-swapped in | 153 | 84 | it wins the slot but takes the ref pack facade's 1055 forwarders down with it — 84 CS7069 + 57 CS0453 |
| **a UNION facade force-swapped in** | **47** | **0** | the ref pack's forwarders *plus* the missing types |

**The fix, and it is in the gate now.** `scratchpad-tools/mscorlib-shim/` builds an assembly named
`mscorlib`, `AssemblyVersion 4.0.0.0`, that is the union: `forwards.cs` is 986 generated
`[assembly: TypeForwardedTo]` rows — every type the ref pack's own facade forwards, minus 69 that
netstandard2.1 does not expose by name — and `shim.cs` declares the handful it does not forward
(`IValueTaskSource`, `IValueTaskSource<T>`, `ValueTaskSourceStatus`, `ValueTaskSourceOnCompletedFlags`,
`IBufferWriter<T>`). `anyverify.sh` builds it into `$ANYVERIFY_WORK/mscorlib` on every run (~1 s) and emits
an `AnyverifySwapMscorlib` target that replaces the facade in `@(ReferencePath)` **after**
`ResolveAssemblyReferences` — adding it as a `<Reference>` is provably not enough. `ANYVERIFY_NO_MSCORLIB=1`
turns it off. The gate now prints an `mscorlib :` line saying which assembly it used.

Verified end to end through `anyverify.sh` itself: **50 → 47, all three CS7069 gone, no new error of any
code.**

**Add a type to `shim.cs` when a new CS7069 names one.** Fluffy Field's recorded "known floor of 6"
(`ReadOnlySequence<>`, `IBufferWriter<>`, `ReadOnlySpan<>` in `SaveIO`/`SaveBaseSingleton`) is the same
defect and should close the same way — `IBufferWriter<T>` is already in. `ReadOnlySequence<T>` is a struct
with real members and has to be copied exactly; it has not been done because Fluffy Field was not
re-measured here. **`ANYVERIFY.md`'s "known floor" paragraph is wrong and this supersedes it.**

## (c) genuinely unfixable: 0

Nothing in this export is in this class. The one thing that looked like it — `DesignEvent`'s
ancestor-skipping base initializer, which really has no C# syntax — is fixable by routing through the
forwarding constructor of the level that was inlined away, as above.

## Where this leaves the floor

    error CS : 47      compiled : no
    47 real recovery defects · 0 harness artifacts · 0 unfixable

**There is no floor.** Every remaining error is the export's, and 44 of the 47 are one family with a known
mechanism, evidence from the binary, and a recovery for each of its two shapes. The other 3 are the `IntPtr`
family and belong to the agent holding `NINT.md`.

The next round's number is comparable to **47** only if it is `anyverify.sh` at or after this change, on an
export of Snacky Dash, with `ANYVERIFY_REFS=scratchpad/pkgrefs`. A run with `ANYVERIFY_NO_MSCORLIB=1`
reproduces 50.

## Reproducing

```sh
export ANYVERIFY_REFS=/Users/playviet/Documents/_BZ/AssetRipper/scratchpad/pkgrefs
zsh scratchpad-tools/anyverify.sh "<export>/ExportedProject" /tmp/gate.log

# the constructor census (arity only - read its header before trusting a count)
python3 scratchpad-tools/ctorcensus.py "<export>/ExportedProject/Assets/Scripts"
```

`scratchpad/probeg` **cannot** dump a Snacky Dash method: `scratchpad/apkpath` names Fluffy Field's apk and
`probeg` extracts from that, so a Snacky Dash type prints nothing and exits 0. To dump one, extract this
game's binary yourself and call `probe.dll` directly:

```sh
unzip -o -j "<xapk>" 'com.b2p.gobbledash2.apk' 'config.arm64_v8a.apk' -d $D
unzip -o -j $D/config.arm64_v8a.apk 'lib/arm64-v8a/libil2cpp.so' -d $D
unzip -o -j $D/com.b2p.gobbledash2.apk 'assets/bin/Data/Managed/Metadata/global-metadata.dat' -d $D
dotnet scratchpad/probe/bin/Release/net10.0/probe.dll \
  $D/libil2cpp.so $D/global-metadata.dat 6000.0.66f2 dump <TypeSubstring> [<method>]
```
