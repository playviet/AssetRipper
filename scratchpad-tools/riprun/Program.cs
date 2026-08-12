// riprun <apk> <exportRoot> <log> [ScriptContentLevel] [fast]
//
// The export runner the whole measurement loop is built on. Reconstructed after Program.cs went missing;
// the shape below is what every call site in the loop passes and what cfscore/compare2/decisions/roundtrip
// expect to find afterwards.
//
// The input is the **apk itself**, never a previous export directory: those hold a second copy of the game's
// assemblies, structure detection then takes the Mono path, dies on a duplicate assembly name, and exports
// nothing recoverable.
using AssetRipper.Export.Configuration;
using AssetRipper.Export.UnityProjects;
using AssetRipper.Import.Configuration;
using AssetRipper.Import.Logging;
using AssetRipper.IO.Files;
using AssetRipper.Processing;

if (args.Length < 3)
{
    Console.WriteLine("usage: riprun <apk> <exportRoot> <log> [ScriptContentLevel] [fast]");
    return 1;
}

string apk = args[0];
string exportRoot = args[1];

Logger.Add(new ConsoleLogger(false));
Logger.Add(new FileLogger(args[2]));

// Matches the configuration the previously verified export was made with.
FullConfiguration settings = new();
settings.ImportSettings.ScriptContentLevel = args.Length > 3 ? Enum.Parse<ScriptContentLevel>(args[3]) : ScriptContentLevel.Level3;
settings.ImportSettings.StreamingAssetsMode = StreamingAssetsMode.Extract;

// "fast" as a fifth argument drops everything that does not change a single line of recovered C#. It is for
// the rounds that are only scored against the original; the export that goes to the editor is the full one.
// ~123s against ~360s, and it scores identically.
bool fast = args.Length > 4 && args[4] == "fast";

settings.ProcessingSettings.EnablePrefabOutlining = !fast;
settings.ProcessingSettings.EnableStaticMeshSeparation = !fast;
settings.ProcessingSettings.EnableAssetDeduplication = !fast;

// Dummy shaders are most of what makes `fast` fast; the export that goes to the editor decompiles them.
settings.ExportSettings.ShaderExportMode = fast ? ShaderExportMode.Dummy : ShaderExportMode.Decompile;
settings.ExportSettings.ShaderNamingMode = ShaderNamingMode.Suffixed;

// Both modes. **Load-bearing for the Unity gate**: without it the project carries stripped plugin DLLs
// instead of referencing the real packages, and Unity's own ApiUpdater.MovedFromExtractor throws a
// NullReferenceException reading `Assets/Plugins/Unity.RenderPipelines.Universal.Runtime.dll`. The editor
// then reports "Scripts have compiler errors" with **zero** `error CS` and produces no assembly at all -
// which reads like the recovery broke and is nothing of the sort.
settings.ExportSettings.RelinkUnityPackages = true;

// The third-party libraries whose real source is on disk: their decompiled bodies are replaced by the
// originals, so the recovery is judged on the game's own code rather than on Dreamteck's or DOTween's.
// Leaving this out is invisible in the 96 scored files - they are the game's own - and moves `compare2` by
// almost sixty bodies, because that one counts the whole game. It is the setting that was hardest to notice
// missing, so the settings dump below stays.
settings.ExportSettings.ScriptOriginalSourceDirectories =
[
    "/Users/playviet/Documents/_BZ/game-hub-origin/Assets/AAA/CFramework/Assets/3rdParty/Demigiant",
    "/Users/playviet/Documents/_BZ/game-hub-origin/Assets/ThirdParty/unity-package/Assets/Utility/EnhancedScroller v2",
    "/Users/playviet/Documents/_BZ/game-hub-origin/Assets/ThirdParty/unity-package/Assets/Utility/SoftMask",
    "/Users/playviet/Documents/_BZ/game-hub-origin/Assets/Dreamteck",
];

// What the run was configured with, into the log. This is how the missing setting above was found after
// Program.cs was lost: the old logs still had the dump, and diffing it against a new one named the difference
// in one line.
Logger.Info(LogCategory.General, "Configuration Settings:");
settings.ImportSettings.Log();
settings.ProcessingSettings.Log();
settings.ExportSettings.Log();

// The export root is deleted first: AssetRipper writes into it rather than over it, so anything left from a
// previous round would be scored as if this one had produced it.
if (Directory.Exists(exportRoot))
{
    Directory.Delete(exportRoot, true);
}

ExportHandler handler = new(settings);
GameData gameData = handler.LoadAndProcess([apk], LocalFileSystem.Instance);
handler.Export(gameData, exportRoot, LocalFileSystem.Instance);

// Last, and on its own line: the wait-loops and unityverify all key on seeing this, and a directory that
// exists is not an export that has finished.
Console.WriteLine("DONE");
return 0;
