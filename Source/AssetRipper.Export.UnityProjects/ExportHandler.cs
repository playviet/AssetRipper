using AssetRipper.Assets;
using AssetRipper.Assets.Bundles;
using AssetRipper.Export.Configuration;
using AssetRipper.Export.UnityProjects.UserPackages;
using AssetRipper.Export.UserPackages;
using AssetRipper.Export.UnityProjects.EmbeddedFiles;
using AssetRipper.Export.UnityProjects.PathIdMapping;
using AssetRipper.Export.UnityProjects.Project;
using AssetRipper.Export.UnityProjects.Scripts;
using AssetRipper.Export.UnityProjects.Shaders;
using AssetRipper.Import.Configuration;
using AssetRipper.Import.Logging;
using AssetRipper.Import.Structure;
using AssetRipper.Processing;
using AssetRipper.Processing.AnimatorControllers;
using AssetRipper.Processing.Assemblies;
using AssetRipper.Processing.AudioMixers;
using AssetRipper.Processing.Editor;
using AssetRipper.Processing.Meshes;
using AssetRipper.Processing.Prefabs;
using AssetRipper.Processing.Scenes;
using AssetRipper.Processing.ScriptableObject;
using AssetRipper.Processing.Textures;
using AssetRipper.SourceGenerated.Classes.ClassID_114;

namespace AssetRipper.Export.UnityProjects;

public class ExportHandler
{
	protected FullConfiguration Settings { get; }

	public ExportHandler(FullConfiguration settings)
	{
		Settings = settings;
	}

	public GameData Load(IReadOnlyList<string> paths, FileSystem fileSystem)
	{
		if (paths.Count == 1)
		{
			Logger.Info(LogCategory.Import, $"Attempting to read files from {paths[0]}");
		}
		else
		{
			Logger.Info(LogCategory.Import, $"Attempting to read files from {paths.Count} paths...");
		}

		GameStructure gameStructure = GameStructure.Load(paths, fileSystem, Settings);
		GameData gameData = GameData.FromGameStructure(gameStructure);
		Logger.Info(LogCategory.Import, "Finished reading files");
		return gameData;
	}

	public void Process(GameData gameData)
	{
		Logger.Info(LogCategory.Processing, "Processing loaded assets...");
		foreach (IAssetProcessor processor in GetProcessors())
		{
			processor.Process(gameData);
		}
		Logger.Info(LogCategory.Processing, "Finished processing assets");
	}

	protected virtual IEnumerable<IAssetProcessor> GetProcessors()
	{
		// Assembly processors
		yield return new AttributePolyfillGenerator();
		yield return new MonoExplicitPropertyRepairProcessor();
		yield return new ObfuscationRepairProcessor();
		yield return new ForwardingAssemblyGenerator();
		if (Settings.ImportSettings.ScriptContentLevel == ScriptContentLevel.Level1)
		{
			yield return new MethodStubbingProcessor();
		}
		yield return new NullRefReturnProcessor(Settings.ImportSettings.ScriptContentLevel);
		yield return new UnmanagedConstraintRecoveryProcessor();
		if (Settings.ProcessingSettings.RemoveNullableAttributes)
		{
			yield return new NullableRemovalProcessor();
		}
		if (Settings.ProcessingSettings.PublicizeAssemblies)
		{
			yield return new SafeAssemblyPublicizingProcessor();
		}
		if (Settings.ProcessingSettings.RemoveGeneratedCode)
		{
			yield return new GeneratedCodeRemovalProcessor();
		}
		if (Settings.ImportSettings.ScriptContentLevel == ScriptContentLevel.Level3)
		{
			//Only Level3 reconstructs method bodies, and only a reconstructed one is ever unreadable.
			yield return new UnreadableMethodBodyProcessor();
		}
		yield return new InjectedAttributeUsageProcessor();
		yield return new RemoveAssemblyKeyFileAttributeProcessor();
		yield return new InternalsVisibileToPublicKeyRemover();

		// Asset processors
		yield return new SceneDefinitionProcessor();
		yield return new OriginalPathProcessor(Settings.ProcessingSettings.BundledAssetsExportMode);
		yield return new MainAssetProcessor();
		yield return new AnimatorControllerProcessor();
		yield return new AudioMixerProcessor();
		yield return new EditorFormatProcessor(Settings.ProcessingSettings.BundledAssetsExportMode);
		if (Settings.ProcessingSettings.EnableStaticMeshSeparation)
		{
			yield return new StaticMeshSeparationProcessor();
		}
		yield return new LightingDataProcessor();//Needs to be after static mesh separation
		yield return new PrefabProcessor();
		if (Settings.ProcessingSettings.EnablePrefabOutlining)
		{
			yield return new PrefabOutliningProcessor();//Needs the scene hierarchies from PrefabProcessor
		}
		yield return new SpriteProcessor();
		yield return new ScriptableObjectProcessor();

		yield return new ShaderNamingProcessor(Settings.ExportSettings.ShaderNamingMode);

		//Last, so that it overrides the paths chosen by everything above.
		yield return new AssetPathOverrideProcessor(Settings.AssetPathOverrideData);
	}

	public void Export(GameData gameData, string outputPath, FileSystem fileSystem)
	{
		Logger.Info(LogCategory.Export, "Starting export");
		Logger.Info(LogCategory.Export, $"Attempting to export assets to {outputPath}...");
		Logger.Info(LogCategory.Export, $"Game files have these Unity versions: {GetListOfVersions(gameData.GameBundle)}");
		Logger.Info(LogCategory.Export, $"Exporting to Unity version {gameData.ProjectVersion}");

		Settings.ExportRootPath = outputPath;
		Settings.SetProjectSettings(gameData.ProjectVersion);

		if (Settings.ExportSettings.RelinkUnityPackages)
		{
			//Built here rather than in the exporter because deciding what can be relinked needs the loaded game.
			Settings.UnityPackageRelinker = UnityPackageRelinker.TryCreate(
				gameData.ProjectVersion,
				UnityPackageRelinker.GetReferencedScripts(gameData),
				gameData.AssemblyManager.GetAssemblies().Select(a => a.Name!.ToString()));
		}

		ProjectExporter projectExporter = new(Settings, gameData.AssemblyManager);
		BeforeExport(projectExporter);
		projectExporter.DoFinalOverrides(Settings);
		projectExporter.Export(gameData.GameBundle, Settings, fileSystem);

		Logger.Info(LogCategory.Export, "Finished exporting assets");

		foreach (IPostExporter postExporter in GetPostExporters())
		{
			postExporter.DoPostExport(gameData, Settings, fileSystem);
		}
		Logger.Info(LogCategory.Export, "Finished post-export");

		static string GetListOfVersions(GameBundle gameBundle)
		{
			return string.Join(' ', gameBundle
				.FetchAssetCollections()
				.Select(c => c.Version)
				.Distinct()
				.Select(v => v.ToString()));
		}
	}

	protected virtual void BeforeExport(ProjectExporter projectExporter)
	{
		EmbeddedFileExporter embeddedFileExporter = new(Settings.EmbeddedFileData);
		if (embeddedFileExporter.HasRules)
		{
			//Ahead of the normal MonoBehaviour exporters, so a matched asset becomes its payload rather than yaml.
			projectExporter.OverrideExporter<IMonoBehaviour>(embeddedFileExporter);
			projectExporter.EmbeddedFileExporter = embeddedFileExporter;
		}

		UserPackageExporter userPackageExporter = new(Settings.UserPackageData);
		if (userPackageExporter.HasPackages)
		{
			//Registered here so that it takes precedence over every exporter that writes the asset out.
			projectExporter.OverrideExporter<IUnityObjectBase>(userPackageExporter);
			projectExporter.UserPackageExporter = userPackageExporter;
		}
	}

	protected virtual IEnumerable<IPostExporter> GetPostExporters()
	{
		yield return new ProjectVersionPostExporter();
		yield return Settings.UnityPackageRelinker is { } relinker
			? new RelinkedPackageManifestPostExporter(relinker, Settings.UserPackageData)
			: Settings.UserPackageData.Packages.Count > 0
				? new UserPackageManifestPostExporter(Settings.UserPackageData)
				: new PackageManifestPostExporter();
		yield return new GitIgnorePostExporter();
		yield return new StreamingAssetsPostExporter();
		yield return new DllPostExporter();
		yield return new PathIdMapExporter();
	}

	public GameData LoadAndProcess(IReadOnlyList<string> paths, FileSystem fileSystem)
	{
		GameData gameData = Load(paths, fileSystem);
		if (gameData.GameBundle.HasAnyAssetCollections())
		{
			Process(gameData);
		}
		return gameData;
	}

	public void LoadProcessAndExport(IReadOnlyList<string> inputPaths, string outputPath, FileSystem fileSystem)
	{
		GameData gameData = LoadAndProcess(inputPaths, fileSystem);
		Export(gameData, outputPath, fileSystem);
	}

	public void ThrowIfSettingsDontMatch(FullConfiguration settings)
	{
		if (Settings != settings)
		{
			throw new ArgumentException("Settings don't match");
		}
	}
}
