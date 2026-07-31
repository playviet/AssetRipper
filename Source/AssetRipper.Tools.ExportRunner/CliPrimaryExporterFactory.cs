using AssetRipper.Export.Configuration;
using AssetRipper.Export.PrimaryContent;
using AssetRipper.SourceGenerated.Classes.ClassID_49;

namespace AssetRipper.Tools.ExportRunner;

internal static class CliPrimaryExporterFactory
{
	public static PrimaryContentExporter Create(Processing.GameData gameData, FullConfiguration settings, bool recursiveUnpack)
	{
		PrimaryContentExporter exporter = PrimaryContentExporter.CreateDefault(gameData, settings);
		if (recursiveUnpack)
		{
			exporter.RegisterHandler<ITextAsset>(new CliTextAssetContentExtractor(settings));
		}
		return exporter;
	}
}
