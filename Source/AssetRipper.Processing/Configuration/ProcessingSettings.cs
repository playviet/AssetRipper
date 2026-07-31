using AssetRipper.Import.Logging;

namespace AssetRipper.Processing.Configuration;

public sealed record class ProcessingSettings
{
	public bool EnablePrefabOutlining { get; set; } = false;
	public bool EnableStaticMeshSeparation { get; set; } = true;
	public bool EnableAssetDeduplication { get; set; } = false;
	public bool RemoveNullableAttributes { get; set; } = false;
	public bool PublicizeAssemblies { get; set; } = false;

	/// <summary>
	/// Remove the members that Unity's own source generators produced, so the editor can generate them again.
	/// </summary>
	/// <remarks>
	/// Off by default. It has only been exercised against synthetic input, since no game using Netcode for GameObjects
	/// was available to try it on.
	/// </remarks>
	public bool RemoveGeneratedCode { get; set; } = false;
	public BundledAssetsExportMode BundledAssetsExportMode { get; set; } = BundledAssetsExportMode.DirectExport;

	public void Log()
	{
		Logger.Info(LogCategory.General, $"{nameof(EnablePrefabOutlining)}: {EnablePrefabOutlining}");
		Logger.Info(LogCategory.General, $"{nameof(EnableStaticMeshSeparation)}: {EnableStaticMeshSeparation}");
		Logger.Info(LogCategory.General, $"{nameof(EnableAssetDeduplication)}: {EnableAssetDeduplication}");
		Logger.Info(LogCategory.General, $"{nameof(RemoveGeneratedCode)}: {RemoveGeneratedCode}");
		Logger.Info(LogCategory.General, $"{nameof(BundledAssetsExportMode)}: {BundledAssetsExportMode}");
	}
}
