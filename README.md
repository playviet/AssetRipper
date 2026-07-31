# AssetRipper

A tool for extracting and analyzing Unity game files. It reads a build — a game directory, an APK, an asset bundle, a
serialized file — and writes back either a Unity project you can open in the editor, or the plain assets themselves.

Two front ends ship in this repository:

* a **GUI** that runs as a local web application, for browsing a build and exporting interactively, and
* a **CLI** for scripted and repeatable exports, with profiles that select only the kind of content you want.

Unity `3.5.0` through `6000.5.X` are supported, though quality varies by version.

## Requirements

* **.NET SDK 10.0.302 or newer.** Older 10.0 SDKs will fail to build this repository. The source generators target
  Roslyn 5.6, and an SDK that ships Roslyn 5.3 (such as 10.0.203) silently refuses to load them, which surfaces as
  confusing "type or namespace not found" errors for generated members rather than as a version complaint.

Check what you have with `dotnet --list-sdks`.

## Building

```sh
dotnet build AssetRipper.slnx -c Release
```

Binaries land under `Source/0Bins/<project>/<configuration>/`.

---

## GUI

```sh
dotnet run --project Source/AssetRipper.GUI.Free -c Release
```

The application starts a local web server and opens your browser. Load a game folder or file from the landing page,
browse the assets, adjust settings, and export.

| Option | Meaning |
| --- | --- |
| `--port <n>` | Host on this port instead of a random free one. |
| `--headless` | Do not open a browser window. |
| `--log` / `--no-log` | Write a log file. On by default. |
| `--log-path <path>` | Where to write that log. |
| `--local-web-file <path>` | Serve this file instead of fetching its online counterpart. |

### Settings

The settings page controls import, processing and export. The ones worth knowing about:

* **Script content level** — how much of the game's code to recover. Level 2 gives full method bodies for Mono games
  and empty ones for IL2CPP. Level 3 additionally attempts to recover IL2CPP method bodies; it is experimental. On an
  arm64 Android build it keeps a body for around 70% of methods — the rest are dropped because they do not decompile
  or do not compile — and the project still builds. The result is closer to a readable trace of the native code than
  to the original source, so Level 2 remains the faithful option and Level 3 is for reading the logic.
* **Shader export mode** — `Dummy` writes stubs that compile, `Yaml` writes the raw asset, `Decompile` recovers the
  real programs. See [Shaders](#shaders) below for what that yields per platform.
* **Shader naming** — `Suffixed` appends ` (Ripped)` to shader names. A build contains the shaders of whatever render
  pipeline it used, and the exported project usually references that same pipeline as a package, so Unity ends up with
  two shaders of the same name and silently picks one. Off by default, since it trades away the names the game used.
* **Static mesh separation** — undoes the mesh merging Unity applies to static renderers when a scene is built.
* **Asset deduplication** — collapses assets that Unity copied into several bundles down to one.
* **Prefab outlining** — finds repeated GameObject hierarchies in scenes and turns them back into prefab instances.
* **Relink Unity packages** — references the real packages instead of the stripped copies the build shipped. See
  [Package relinking](#package-relinking) below.

Files loaded on the configuration files page (asset path overrides, user defined packages) are plain JSON and are
described under [Configuration files](#configuration-files).

---

## CLI

```sh
dotnet run --project Source/AssetRipper.Tools.ExportRunner -c Release -- <command> [...]
```

Or run the built binary directly:

```sh
Source/0Bins/AssetRipper.Tools.ExportRunner/Release/AssetRipper.Tools.ExportRunner <command> [...]
```

The examples below use `ExportRunner` as shorthand for whichever of those you prefer.

### inspect

Prints what a build contains — asset counts by class and by output directory — without writing anything.

```sh
ExportRunner inspect "Fluffy Field.apk"
```

Use it to decide which profile is worth running before committing to a full export.

### analyze

Same inventory, optionally written to a JSON report.

```sh
ExportRunner analyze "Fluffy Field.apk" --report inventory.json
```

### export

Extracts assets.

```sh
ExportRunner export "Fluffy Field.apk" --output ./out --profile audio
```

| Option | Meaning |
| --- | --- |
| `--output <path>`, `-o` | Where to write. Required. |
| `--profile <name>` | Export only the content this profile selects. See below. |
| `--mode <primary\|dump>` | Backend. `primary` writes plain assets, `dump` writes a Unity project. Defaults to the profile's mode. |
| `--keep-output` | Do not clean the output directory first. |
| `--recursive-unpack on\|off` | Unpack nested bundles found inside the build. On by default. |
| `--shard-strategy off\|direct-children\|auto` | Split a large input into shards and export them separately. |

`--shard-direct-children` is kept as shorthand for `--shard-strategy direct-children`.

#### Profiles

A profile scores each export collection by its name and path and keeps the ones that look like the content you asked
for. This is heuristic: it is a way to get the interesting 5% of a large game quickly, not a guarantee.

| Profile | Selects |
| --- | --- |
| `player-art` | Character and player facing artwork |
| `characters` | Portraits, standing art, Live2D, Spine |
| `ui` | Icons, atlases, HUD, menus |
| `audio` | BGM, SFX, voice |
| `narrative` | Dialogue, script and text data |
| `cg` | Event CG, illustrations, gallery art |
| `backgrounds` | Scene and location backdrops |
| `sprites` | Sprites, atlases, sheets, textures |
| `full-project` | Everything, as a Unity project |
| `full-raw` | Everything, as plain assets |

Every export writes machine readable artifacts next to the assets:

| File | Contents |
| --- | --- |
| `export-manifest.json` | Every asset written, with its source |
| `skipped-assets.json` | What the profile excluded, and why |
| `failed-assets.json` | What failed to export, and why |
| `export-plan.json` | The plan the run followed |
| `recursive-unpack.json` | Nested bundles that were unpacked |
| `summary.txt` | Human readable summary |

The `--profile` run reports its selection up front, for example
`Selected 32 of 648 primary collections for export`, so you can tell a narrow profile from a broken one.

### report

Re-prints a summary from a previous run's artifacts.

```sh
ExportRunner report ./out
```

### Legacy form

Kept for compatibility with older scripts:

```sh
ExportRunner primary <input-path> <output-path> [more-input-paths...]
ExportRunner dump    <input-path> <output-path> [more-input-paths...]
```

### Environment variables

| Variable | Meaning |
| --- | --- |
| `ASSETRIPPER_EXPORT_WORKERS` | Parallel export workers. Defaults to 4, capped at the processor count. CLI only. |
| `ASSETRIPPER_UNITY_EDITOR` | Where to find the Unity editor, when it is not where the Hub puts it. Used by [package relinking](#package-relinking); applies to the GUI as well. |

---

## Shaders

What `Decompile` can recover depends on which graphics API the build shipped, because Unity stores something different
for each.

| Build target | Stored as | Result |
| --- | --- | --- |
| OpenGL / OpenGL ES | GLSL source | The GLSL itself, emitted in a `GLSLPROGRAM` block. Nothing is lost. |
| Direct3D | DXBC bytecode | Decompiled back to HLSL in a `CGPROGRAM` block. |
| Vulkan | SMOL-V compressed SPIR-V | Disassembled to SPIR-V assembly, with real variable names where the module kept them. Emitted commented out, because disassembly is not source and cannot be recompiled. |
| Other | — | ShaderLab structure only. The log says which platform the shader was built for. |

The ShaderLab around the programs — properties, subshaders, passes, tags, render state — is reconstructed in every
case, including render state overrides written as `[_PropertyName]`.

## Package relinking

A build ships the compiled assemblies of every package it used. Exported as plugins, those leave the project holding a
stripped copy of a package it could simply reference. That is not harmless: Unity's own API updater crashes on the
stripped URP assembly, and a stripped copy of a shared dependency such as Newtonsoft.Json shadows the real one and
breaks the source of every package compiled against it.

With **Relink Unity packages** on, such an assembly is dropped and the package it came from is added to
`Packages/manifest.json`, with every component that pointed at a script inside it repointed at the same script in the
package.

**This needs the matching editor version installed.** The package version and the script GUIDs are read from it rather
than guessed, because a version that does not exist stops the project opening and a wrong GUID unbinds components
silently. Without that editor, relinking is skipped and a warning says so. Set `ASSETRIPPER_UNITY_EDITOR` if your
install is not where Unity Hub puts it.

An assembly is only relinked when every script the game references inside it is found in the package; one miss leaves
the whole assembly alone. Class names that more than one file in a package declares are treated as not found rather
than guessed at.

Measured on an Android build exported for Unity 6000.0.78f1: 36 assemblies relinked, 35 fewer plugin DLLs, the API
updater crashes gone, no compiler errors, and the same 2443 of 2446 script bindings resolving as before — 1860 of them
now into packages.

Off by default, because it changes what the project references.

## Configuration files

Both files are optional and are loaded from the GUI's configuration files page.

**Asset path overrides** move exported assets. Rules are tried in order; the first match wins.

```json
{
  "overrides": [
    { "className": "Texture2D", "originalPathPrefix": "Assets/UI", "directory": "Assets/Art/UI" },
    { "name": "MainTheme", "directory": "Assets/Audio/Music", "fileName": "theme" }
  ]
}
```

**Embedded files** pull a payload out of a serialized field and write it as a real file. Games often keep their own
data — maps, level definitions, dialogue, custom mesh formats — in a byte array or string field on a ScriptableObject
rather than as an asset Unity understands, and exported normally that ends up inline in a YAML file that nothing can
open. A MonoBehaviour must match every condition a rule specifies, and a rule whose field is not present is ignored so
the asset still exports as usual.

```json
{
  "rules": [
    { "scriptNamespace": "Timberborn.BlueprintSystem", "nameSuffix": "blueprint", "field": "_content", "extension": "blueprint", "text": true },
    { "scriptNamespace": "Timberborn.AssetSystem", "directoryPrefix": "Assets/Resources/maps", "field": "_bytes", "extension": "timber" }
  ]
}
```

**User defined packages** stop assets that also ship inside a third party package from being written out as duplicate
copies. Declared assets are redirected to their identity inside the package, and the package is added to
`Packages/manifest.json`.

```json
{
  "packages": [
    {
      "name": "com.unity.textmeshpro",
      "version": "3.0.6",
      "assets": [
        { "name": "LiberationSans SDF", "className": "Material", "guid": "e73a58f6e2c4f6b4d9c02f0dc1b3d2e5" }
      ]
    }
  ]
}
```

## Credits and license

AssetRipper is licensed under the [GNU General Public License v3.0](LICENSE.md), and is the work of
[ds5678](https://github.com/sponsors/ds5678) and its [contributors](https://assetripper.github.io/AssetRipper/articles/Credits.html).

The command line front end originates from [AssetRipper-CLI](https://github.com/MeikoMei16/AssetRipper-CLI) by
MeikoMei16, also under GPL-3.0.

Shader decompilation builds on [USCSandbox](https://github.com/nesrak1/USCSandbox) by nesrak1, and the SMOL-V decoder is
ported from [smol-v](https://github.com/aras-p/smol-v) by Aras Pranckevičius.

AssetRipper is not sponsored by or affiliated with Unity Technologies or its affiliates. "Unity" is a registered
trademark of Unity Technologies or its affiliates in the U.S. and elsewhere.
