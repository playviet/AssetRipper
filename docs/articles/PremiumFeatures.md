# Premium Features

AssetRipper has several premium features to make its export more similar to the original project, in order to reduce the difficulty for game developers to compare them.

## Static Mesh Separation

Objects marked as static in a scene get merged when the game is compiled. This is an optimization Unity uses to reduce draw calls. This feature reverses that process to the best of its ability.

* If a mesh is used statically multiple times in the game, even across different scenes, AssetRipper intelligently identifies these duplicates and generates a single mesh for all the instances.
* If the original mesh exists in the game files, it's used instead of generating a new mesh.
* Mesh names are lost during static batching, so the GameObject name is used instead. Some sensible name cleaning is applied.

This feature has a setting for enabling it, which defaults to true.

## Shader Decompilation

This is an experimental shader decompiler that strives to support all variants and preserve perfect semantics. However, it's not yet polished, so expect it to throw errors while ripping some shaders and for there to be compilation errors in Unity Editor. Please report any issues on [GitHub](https://github.com/AssetRipper/AssetRipper/issues).

**Platform Support:**

* Vulkan shaders can be decompiled on any platform.
* DirectX shaders can only be decompiled on Windows computers.

## Prefab Outlining

When a game is compiled, all prefabs in a scene are inlined (instantiated), so any information about the original prefab is lost. This feature attempts to reverse that process by analyzing all GameObject hierarchies in the game and identifying repetitions that can be replaced with new (or existing) prefabs.

This feature has a setting for enabling it, which defaults to false.

## Traditional Il2Cpp Analysis

This is an experimental feature for analyzing code compiled with Il2Cpp. It takes a traditional decompilation approach. It can be enabled by selecting Script Content Level 3 in the settings.

For performance, mscorlib and any assemblies whose names start with System or Unity have been excluded from analysis.

Arm64 is analyzed with `NewArmV8InstructionSet`. The older `Arm64InstructionSet` produces no ISIL at all, so nothing downstream of it can reconstruct anything, and an arm64 game would come out with empty methods however high the content level was set.

What a reconstructed body contains has to survive two checks, because what does not survive costs far more than itself:

* `UnreadableMethodBodyProcessor` drops the bodies no reader accepts, such as a branch that points outside the method. The decompiler throws on one of those, and it decompiles a whole file at a time, so the type that contained the method would be written out as an empty file.
* `InvalidSourceRepair` compiles the decompiled source against the assemblies it was recovered alongside, and comments out the statements the errors point at. The editor compiles an assembly as a whole, so a single statement that does not compile would cost the project every script in that assembly. Commenting rather than deleting keeps it readable, and a method usually has one or two statements the analysis could not type and dozens it could.

It also comments out the messages recovery writes where it could not translate something. They are calls to `Console.WriteLine`, which compile but also run: a recovered loop the editor happens to call will otherwise fill the log with them.

Measured on an arm64 Android game, against the original Unity project it was built from:

| | |
|---|---|
| types in the original with a counterpart recovered | 590 of 617 (96%) |
| methods in those types with a counterpart recovered | 2973 of 3040 (98%) |
| recovered methods that are whole compiling code | 63% |
| recovered methods with some statements commented out | 31% |
| recovered methods with nothing in them | 6% |

163 methods could not be repaired statement by statement and were emptied, and 11387 statements were commented out.
The project imports into the editor with no compile errors and no shader errors, at content level 2 and 3 alike.
Improvements are ongoing.

Iterator methods partly recover. 13 come back as `yield return` and 12 as `yield break`; 26 do not, and say which
requirement of the decompiler's iterator transform they failed rather than failing silently. The two that were fixed
to get there were a merged return, which gives back no literal for the transform to find, and a field read the
simplifier was carrying past the writes that invalidated it.

What comes out is a readable trace of what the native code does, not the original source. Expect calls into helpers the analysis could not name and values it could not type. Level 2 remains the setting for a faithful project; Level 3 is for reading the logic.

## Asset Deduplication

When building multiple asset bundles, it's very common for Unity to duplicate assets shared between bundles. This allows each bundle to stay self-contained. Asset deduplication is an experimental feature for reversing that process.

Some assets are easier to deduplicate than others. Currently, this is limited to:

* Mono Scripts
* Shaders
* Compute Shaders
* Audio Clips
* Text Assets
* Meshes
* Textures without sprites

This feature has a setting for enabling it, which defaults to false.

## User Defined Package Export

Without this feature, exported projects reference only the default set of Unity core modules. Whenever a user adds references to packages that their game used, the new packages will conflict with assemblies and scripts within the exported project. Deleting the conflicting exported files will break any asset references to those files.

There are third-party tools available to help fix broken script references, but they mostly rely on guessing the scripts from the MonoBehaviour fields. In addition, they do nothing to fix broken references for other asset types. That is the purpose of this feature: export asset and package references, so that the user doesn't have to fix broken references later.

Unfortunately, it is not feasible to datamine all possible packages, not even restricted to the official offerings from Unity. As such, users are responsible for mining the packages specific to their game. However, there are some resources to help them do the required datamining. https://github.com/AssetRipper/MarrowMiningDemo

This experimental feature can be enabled by going to the Configuration Files page, which can be accessed with "View/Configuration Files" in the file menu. To enable the feature, the user must upload package data json files appropriate for their game. Example json files are available in the Marrow Mining Demo I linked above.

## Asset Path Overrides

This feature allows users to change the export destination of an asset. It can be enabled by going to the Configuration Files page, which can be accessed with "View/Configuration Files" in the file menu. To enable the feature, the user must upload a json file appropriate for their game.

### Path Override File Structure

Path overrides can be supplied as a json file.

```json
{
	"Files": {
		"cab-bcaf22789432bda1e5d0eea9d2521ddd": {
			"4476349470337976665": "Assets/AssetRenamed.txt"
		},
		"level1.assets":
		{
			"1": "Assets/Prefabs/Prefab1.prefab",
			"2": "Assets/Prefabs/Prefab2.prefab",
			"3": "Assets/Prefabs/Subfolder/SpecialPrefab.prefab",
			"12": "Assets/Images/MyTexture.png"
		}
	}
}
```

`Files` is a dictionary with string keys representing asset collection names. The values are also dictionaries. They use the asset path id as the key and the new output path as the value. Paths are relative to the project root.
