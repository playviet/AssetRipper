# `anyverify.sh` — the compile gate for a game with no project on disk

`unityverify.sh` is Fluffy Field's gate and only Fluffy Field's: three of its four reference sets are paths
under `/Users/playviet/Documents/_BZ/game-hub` (`Library/ScriptAssemblies`, `Library/PackageCache`,
`Library/Bee/.../Il2CppBackup/Managed`), and none of them exist for a game we only have an apk of. On any
other game it either dies or silently reports the export's errors plus a few hundred missing-reference ones.

`anyverify.sh` is the same gate with those three legs cut off.

    scratchpad-tools/anyverify.sh <export>/ExportedProject [log]

## What it references

1. **the export's own `Assets/Plugins/**/*.dll`** — whatever the export itself shipped. For most games that
   is everything: AssetRipper writes every assembly it did not decompile into `Plugins`, package assemblies
   included. Goods Puzzle's export ships `UnityEngine.UI`, `Unity.TextMeshPro`, `Unity.Addressables` and
   `Newtonsoft.Json` there and needs nothing else. Fluffy Field's export ships none of the four, which is
   why `unityverify.sh` had to reach into the original project at all.
2. **the installed editor's `Managed/UnityEngine`** — the engine the editor has, not the game's own copies,
   which the managed linker stripped (`il2cpp-the-engine-the-editor-has`). The version is read from the
   export's `ProjectSettings/ProjectVersion.txt` and matched against `/Applications/Unity/Hub/Editor`:
   exact if installed, else the newest install sharing the major version (`2022.` → `2022.3.62f2`,
   `6000.` → `6000.0.78f1`), else `$ANYVERIFY_DEFAULT_UNITY` (default `6000.0.78f1`).
   `$ASSETRIPPER_UNITY_MANAGED` overrides all of it with a path to a `Contents/Managed`.
3. **`$ANYVERIFY_REFS`** — colon-separated extra directories, searched recursively for `*.dll`.

The engine is added **first**, so it wins any name a plugin also declares. Anything named `*Editor` is
dropped, and so is any dll whose name matches a directory under `Assets/Scripts` — that assembly is in the
compilation as source, and a second copy as a dll makes every call `CS0121 ambiguous` (516 of them, the
mistake `unityverify.sh` records).

Other env: `$ANYVERIFY_WORK` (build dir, default `$TMPDIR/anyverify-gate`, wiped each run — two runs at once
must not share it), `$ANYVERIFY_UNITY_HUB`.

## `mscorlib`: a fourth reference the gate builds itself

There is a fourth assembly in the compilation that comes from none of the three sets. Every plugin dll in a
Unity player build was compiled against Unity's `mscorlib`, and any of them that declares a type from it in
its own metadata — `UniTask.dll` declares `IUniTaskSource : [mscorlib]IValueTaskSource` — makes the compiler
resolve that typeref before it can bind a member call on the plugin type. The netstandard2.1 reference pack
ships an `mscorlib`, but only as a facade over the netstandard2.0-era surface, and the resolve fails:
`CS7069: Reference to type 'X' claims it is defined in 'mscorlib', but it could not be found`, once per
*source* line that touches the plugin type.

`anyverify.sh` builds `mscorlib-shim/` — a union facade, the ref pack's 986 usable forwarders plus the types
it does not forward — and swaps it into `@(ReferencePath)` **after** `ResolveAssemblyReferences`. Adding it
as an ordinary `<Reference>` does nothing, because RAR resolves `mscorlib` to the ref pack's facade and drops
the duplicate; and swapping in Unity's *real* mscorlib crashes Roslyn outright. `GATEFLOOR.md` has all three
measurements. `ANYVERIFY_NO_MSCORLIB=1` disables it. Add a type to `shim.cs` when a new CS7069 names one.

## Set 3 is what makes it a superset

`anyverify.sh` **is** `unityverify.sh` when you hand it the same references:

```sh
HUB=/Users/playviet/Documents/_BZ/game-hub
ANYVERIFY_REFS=$HUB/Library/ScriptAssemblies:$HUB/Library/Bee/Android/Prj/IL2CPP/Il2CppBackup/Managed \
  scratchpad-tools/anyverify.sh <export>/ExportedProject
```

measured on export `_720`: **6 errors, all CS7069**, in `SaveIO` and `SaveBaseSingleton`, naming
`ReadOnlySequence<>`, `IBufferWriter<>`, `ReadOnlySpan<>`. That is `unityverify.sh`'s known floor of 12,
exactly (see below), reached without `PackageCache` — the Newtonsoft leg turned out to be unnecessary
because `ScriptAssemblies` covers it.

> **Corrected, 2026-08-20: those 6 are not a floor.** CS7069 is the reference set missing an `mscorlib`
> the plugin dlls' typerefs can bind to, and it is now fixed in the gate — see the `mscorlib` section of
> `anyverify.sh` and the measurements in `GATEFLOOR.md`. Snacky Dash's three went to zero and the export's
> error count 50 → 47 with nothing new. Fluffy Field's six should close the same way once
> `ReadOnlySequence<T>` and `ReadOnlySpan<T>` are added to `mscorlib-shim/shim.cs` (`IBufferWriter<T>`
> already is); that has not been re-measured on `_720`.

## The count is halved, and that is the fix

msbuild prints every diagnostic **twice** — once at `CoreCompile`, once in the `Build FAILED` summary — and
the two lines are byte-identical. `unityverify.sh` counts with `grep -cE 'error CS'`, so **every number it
has ever reported is exactly double the real one**. Measured on three logs: 926/463, 12/6, 8/4. `anyverify.sh`
dedupes (`sort -u`) and reports the real count, so halve `unityverify`'s number before comparing. Its
documented "floor of 16, now 12" is a floor of 8, now 6; its "924 errors that are all CS0246 Image" is 462.

## The fix queue

Before the two lines it prints the top 25 error codes with counts, and three example messages for each of
the top 5. That ranking is the point: a gate that says "no, 3714" is not actionable, and one that says
`CS0246: 399 / CS0234: 55 / CS0538: 9` names the next defect family. Output ends, always, with

    compiled : yes|no
    error CS : <n>

## Measured, 2026-08-20

| export | unity | sources | refs | errors | top codes |
|---|---|---|---|---|---|
| Goods Puzzle 4.72A | 2022.3.62f2 (picked, exact) | 4538 | 175 (83 engine, 92 plugins) | **4** | CS0246: 4 |
| Fluffy Field `_720`, no extra refs | 6000.0.78f1 | 533 | 170 (121 engine, 49 plugins) | **463** | CS0246 399, CS0234 55, CS0538 9 |
| Fluffy Field `_720`, `ANYVERIFY_REFS` = game-hub | 6000.0.78f1 | 533 | 299 (+129 extra) | **6** | CS7069 6 |

Neither compiled — `compiled : yes` needs zero errors.

**Goods Puzzle's 4 are a real export defect, not a harness one.** `UIThemeIngame.cs` opens with
`using System.Drawing;` where it wanted `using UnityEngine.UI;`, and `Image` resolves to nothing.
`System.Drawing` is a real namespace in netstandard2.1 (`Point`, `Size`, `Color`) so the `using` itself
compiles; only `Image` is missing. The editor would report the same four. One file, one wrong using
directive, and Goods Puzzle's 4538 recovered files are otherwise a compiling project.

**Fluffy Field's 463 without extra refs are all missing package references** — `Image`, `GridLayoutGroup`,
`UnityEngine.EventSystems`, `UnityEngine.AddressableAssets`, and `CS0538 ILayoutElement ... is not an
interface` downstream of them. The FF export ships no UI/TMP/Addressables assembly at all, so on that game
`$ANYVERIFY_REFS` is not optional. Worth knowing for the export itself: a game whose apk-only sibling ships
those in `Plugins` gates cleanly, so FF's export is writing fewer assemblies to `Plugins` than Goods
Puzzle's is.

## Build

`~/.dotnet` (SDK 10.0.302), `dotnet build` on a generated `netstandard2.1` csproj: `LangVersion 9.0` (at
`latest`, `field` is a contextual keyword and ordinary variables named `field` become errors the editor
never reports), `AllowUnsafeBlocks`, `Nullable disable`, `WarningLevel 0`, `EnableDefaultCompileItems false`
with one explicit `<Compile Include="$SCRIPTS/**/*.cs" />`. ~40 s for 533 files, ~3 min for 4538.

## Log

- Wrote `anyverify.sh`, first run printed nothing: `add $dll && (( N++ ))` under `set -e` — post-increment
  from 0 returns 0, the `&&` list fails, and the script exits silently. Also `[ -n "$x" ] && return`
  returns **0**, so a skipped dll counted as added. Both now explicit (`if add ...; then (( N += 1 )); fi`,
  `return 1`).
- Verified on the two exports above and on the `ANYVERIFY_REFS` path.
