using AssetRipper.Assets;
using AssetRipper.Assets.Bundles;
using AssetRipper.Export.Configuration;
using AssetRipper.Export.PrimaryContent;
using AssetRipper.Export.UnityProjects;
using AssetRipper.Import.AssetCreation;
using AssetRipper.Import.Logging;
using AssetRipper.Import.Structure.Assembly.Managers;
using AssetRipper.IO.Files;
using AssetRipper.IO.Files.CompressedFiles;
using AssetRipper.IO.Files.ResourceFiles;
using AssetRipper.IO.Files.SerializedFiles;
using AssetRipper.SourceGenerated.Classes.ClassID_49;
using System.Diagnostics.CodeAnalysis;

namespace AssetRipper.Tools.ExportRunner;

internal sealed class CliTextAssetContentExtractor : IContentExtractor
{
	private readonly FullConfiguration templateSettings;

	public CliTextAssetContentExtractor(FullConfiguration templateSettings)
	{
		this.templateSettings = templateSettings;
	}

	public bool TryCreateCollection(IUnityObjectBase asset, [NotNullWhen(true)] out ExportCollectionBase? exportCollection)
	{
		if (asset is ITextAsset textAsset && textAsset.Script_C49.Data.Length > 0)
		{
			exportCollection = new TextAssetExportCollection(this, textAsset);
			return true;
		}

		exportCollection = null;
		return false;
	}

	private bool TryExportNested(ITextAsset asset, string filePath, FileSystem fileSystem)
	{
		byte[] data = asset.Script_C49.Data.ToArray();
		using FileBase parsedFile = SchemeReader.ReadFile(data, filePath, Path.GetFileName(filePath));
		FileBase effectiveFile = UnwrapParsedFile(parsedFile);

		(GameBundle Bundle, BaseManager AssemblyManager)? nestedContent = TryBuildBundle(effectiveFile);
		if (nestedContent is null || !nestedContent.Value.Bundle.HasAnyAssetCollections())
		{
			return false;
		}

		string nestedOutputPath = GetNestedOutputPath(filePath, fileSystem);
		ResetOutputDirectory(nestedOutputPath);

		try
		{
			FullConfiguration nestedSettings = CreateNestedSettings(nestedOutputPath);
			Processing.GameData nestedGameData = new(
				nestedContent.Value.Bundle,
				nestedContent.Value.Bundle.GetMaxUnityVersion(),
				nestedContent.Value.AssemblyManager,
				null);
			ExportHandler handler = new(nestedSettings);
			handler.Process(nestedGameData);
			CliPrimaryExporterFactory.Create(nestedGameData, nestedSettings, recursiveUnpack: true)
				.Export(nestedGameData.GameBundle, nestedSettings, fileSystem);

			if (!Directory.EnumerateFileSystemEntries(nestedOutputPath).Any())
			{
				Directory.Delete(nestedOutputPath, true);
				return false;
			}

			Logger.Info(LogCategory.Export, $"Unpacked nested Unity payload '{Path.GetFileName(filePath)}' into '{nestedOutputPath}'.");
			return true;
		}
		catch (Exception ex)
		{
			Logger.Warning(LogCategory.Export, $"Failed to export nested Unity payload '{filePath}': {ex.Message}");
			if (Directory.Exists(nestedOutputPath))
			{
				try
				{
					Directory.Delete(nestedOutputPath, true);
				}
				catch
				{
				}
			}
			return false;
		}
	}

	private static FileBase UnwrapParsedFile(FileBase file)
	{
		while (file is CompressedFile compressedFile)
		{
			compressedFile.ReadContentsRecursively();
			if (compressedFile.UncompressedFile is null)
			{
				break;
			}
			file = compressedFile.UncompressedFile;
		}

		if (file is FileContainer container)
		{
			container.ReadContentsRecursively();
		}

		return file;
	}

	private static (GameBundle Bundle, BaseManager AssemblyManager)? TryBuildBundle(FileBase file)
	{
		BaseManager assemblyManager = new(_ => { });
		GameAssetFactory factory = new(assemblyManager);
		GameBundle bundle = new();

		switch (file)
		{
			case ResourceFile or FailedFile:
				return null;
			case SerializedFile serializedFile:
				bundle.AddCollectionFromSerializedFile(serializedFile, factory);
				bundle.InitializeAllDependencyLists();
				return (bundle, assemblyManager);
			case FileContainer container:
				bundle.AddBundle(SerializedBundle.FromFileContainer(container, factory));
				bundle.InitializeAllDependencyLists();
				return (bundle, assemblyManager);
			default:
				return null;
		}
	}

	private FullConfiguration CreateNestedSettings(string exportRootPath)
	{
		FullConfiguration settings = new()
		{
			ImportSettings = templateSettings.ImportSettings,
			ProcessingSettings = templateSettings.ProcessingSettings,
			ExportSettings = templateSettings.ExportSettings,
			ExportRootPath = exportRootPath,
		};
		return settings;
	}

	private static string GetNestedOutputPath(string filePath, FileSystem fileSystem)
	{
		string parentDirectory = fileSystem.Path.GetDirectoryName(filePath) ?? string.Empty;
		string fileName = fileSystem.Path.GetFileName(filePath);
		string directoryName = fileName.EndsWith(".bytes", StringComparison.OrdinalIgnoreCase)
			? fileName[..^".bytes".Length]
			: fileSystem.Path.GetFileNameWithoutExtension(fileName);

		if (string.IsNullOrWhiteSpace(directoryName))
		{
			directoryName = $"{fileName}_unpacked";
		}

		return GetAvailableDirectoryPath(parentDirectory, directoryName, fileSystem);
	}

	private static string GetAvailableDirectoryPath(string parentDirectory, string directoryName, FileSystem fileSystem)
	{
		string candidatePath = fileSystem.Path.Join(parentDirectory, directoryName);
		if (!fileSystem.File.Exists(candidatePath) && !fileSystem.Directory.Exists(candidatePath))
		{
			return candidatePath;
		}

		string suffixedDirectoryName = $"{directoryName}_unpacked";
		string suffixedCandidatePath = fileSystem.Path.Join(parentDirectory, suffixedDirectoryName);
		if (!fileSystem.File.Exists(suffixedCandidatePath) && !fileSystem.Directory.Exists(suffixedCandidatePath))
		{
			return suffixedCandidatePath;
		}

		for (int index = 2; ; index++)
		{
			string numberedCandidatePath = fileSystem.Path.Join(parentDirectory, $"{suffixedDirectoryName}_{index:000}");
			if (!fileSystem.File.Exists(numberedCandidatePath) && !fileSystem.Directory.Exists(numberedCandidatePath))
			{
				return numberedCandidatePath;
			}
		}
	}

	private static void ResetOutputDirectory(string outputPath)
	{
		if (Directory.Exists(outputPath))
		{
			try
			{
				Directory.Delete(outputPath, true);
			}
			catch (DirectoryNotFoundException)
			{
			}
		}
		Directory.CreateDirectory(outputPath);
	}

	private sealed class TextAssetExportCollection : SingleExportCollection<ITextAsset>
	{
		public TextAssetExportCollection(CliTextAssetContentExtractor contentExtractor, ITextAsset asset) : base(contentExtractor, asset)
		{
		}

		protected override string ExportExtension => "bytes";

		protected override bool ExportInner(string filePath, string dirPath, FileSystem fileSystem)
		{
			CliTextAssetContentExtractor contentExtractor = (CliTextAssetContentExtractor)ContentExtractor;
			if (contentExtractor.TryExportNested(Asset, filePath, fileSystem))
			{
				return true;
			}

			fileSystem.File.WriteAllBytes(filePath, Asset.Script_C49.Data);
			return true;
		}
	}
}
