using AssetRipper.Assets;
using AssetRipper.Import.Logging;

namespace AssetRipper.Export.UnityProjects.Deduplication;

/// <summary>
/// Reverses the asset duplication that Unity performs when the same asset is needed by more than one bundle.
/// </summary>
/// <remarks>
/// The first copy of an asset that is encountered is exported normally. Every later copy with the same content hash is
/// redirected to that first copy instead of being written out again, so references from any bundle resolve to a single
/// asset in the exported project.
/// <para>
/// MonoScripts are already deduplicated by <see cref="Scripts.ScriptExporter"/>, which derives their GUID from the
/// script's identity rather than from the bundle it was found in.
/// </para>
/// </remarks>
public sealed class DeduplicationExporter : IAssetExporter
{
	private readonly Dictionary<AssetContentHash, IUnityObjectBase> originals = new();
	private int duplicateCount;

	public bool TryCreateCollection(IUnityObjectBase asset, [NotNullWhen(true)] out IExportCollection? exportCollection)
	{
		exportCollection = null;

		// An asset that belongs to another asset is exported as part of it, so redirecting it here would strand it.
		if (asset.MainAsset is not null && !ReferenceEquals(asset.MainAsset, asset))
		{
			return false;
		}

		if (!AssetContentHasher.TryComputeHash(asset, out AssetContentHash hash))
		{
			return false;
		}

		if (originals.TryGetValue(hash, out IUnityObjectBase? original))
		{
			duplicateCount++;
			exportCollection = new DeduplicatedExportCollection(asset, original);
			return true;
		}

		originals.Add(hash, asset);
		// Fall through to the exporter that actually knows how to write this asset.
		return false;
	}

	/// <summary>
	/// Logs how much duplication was removed. Meaningful only once collection creation has finished.
	/// </summary>
	public void LogSummary()
	{
		Logger.Info(LogCategory.Export, $"Deduplicated {duplicateCount} {(duplicateCount == 1 ? "asset" : "assets")}");
	}

	AssetType IAssetExporter.ToExportType(IUnityObjectBase asset) => throw new NotSupportedException();

	bool IAssetExporter.ToUnknownExportType(Type type, out AssetType assetType)
	{
		// This exporter never claims a type on its own, so the next exporter in the stack decides.
		assetType = default;
		return false;
	}
}
