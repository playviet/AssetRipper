using AssetRipper.Export.Configuration;
using AssetRipper.Export.UnityProjects;
using AssetRipper.IO.Files;

namespace AssetRipper.Tools.ExportRunner;

internal static class InventoryWorkflow
{
	public static InventorySummary LoadSummary(string[] inputPaths, FileSystem fileSystem)
	{
		ArgumentNullException.ThrowIfNull(inputPaths);
		ArgumentNullException.ThrowIfNull(fileSystem);

		FullConfiguration settings = new();
		settings.LoadFromDefaultPath();

		ExportHandler handler = new(settings);
		var gameData = handler.LoadAndProcess(inputPaths, fileSystem);
		return InventorySummaryBuilder.Build(gameData, inputPaths);
	}
}
