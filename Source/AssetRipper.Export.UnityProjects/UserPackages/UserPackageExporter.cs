using AssetRipper.Assets;
using AssetRipper.Export.Configuration;
using AssetRipper.Import.Logging;
using AssetRipper.SourceGenerated;

namespace AssetRipper.Export.UnityProjects.UserPackages;

/// <summary>
/// Redirects assets that the user has declared as belonging to a third party package.
/// </summary>
/// <remarks>
/// Without this, an asset that also ships inside a package gets written out as its own copy. Adding the package to the
/// exported project then leaves two copies of the same asset with different GUIDs, and every reference points at the
/// wrong one. Redirecting the asset to its identity inside the package keeps references working once the package is
/// installed.
/// </remarks>
public sealed class UserPackageExporter : IAssetExporter
{
	private readonly Dictionary<PackageAssetKey, MetaPtr> pointers;
	private int redirectedCount;

	public UserPackageExporter(UserPackageData data)
	{
		pointers = new Dictionary<PackageAssetKey, MetaPtr>();
		foreach (UserPackage package in data.Packages)
		{
			foreach (UserPackageAsset asset in package.Assets)
			{
				if (!TryParseGuid(asset.Guid, out UnityGuid guid))
				{
					Logger.Warning(LogCategory.Export, $"Package '{package.Name}' declares asset '{asset.Name}' with an unusable GUID '{asset.Guid}'. Skipping it.");
					continue;
				}

				PackageAssetKey key = new(asset.Name, asset.ClassName);
				long fileID = asset.FileID ?? 0;
				if (!pointers.TryAdd(key, new MetaPtr(fileID, guid, AssetType.Meta)))
				{
					Logger.Warning(LogCategory.Export, $"Package '{package.Name}' declares '{asset.Name}' more than once. Keeping the first declaration.");
				}
			}
		}
	}

	public bool HasPackages => pointers.Count > 0;

	public bool TryCreateCollection(IUnityObjectBase asset, [NotNullWhen(true)] out IExportCollection? exportCollection)
	{
		exportCollection = null;
		if (pointers.Count == 0)
		{
			return false;
		}

		string name = asset.GetBestName();
		// A rule naming a class is more specific than one that does not, so it is tried first.
		if (!pointers.TryGetValue(new PackageAssetKey(name, asset.ClassName), out MetaPtr pointer)
			&& !pointers.TryGetValue(new PackageAssetKey(name, null), out pointer))
		{
			return false;
		}

		if (pointer.FileID == 0)
		{
			pointer = pointer with { FileID = ExportIdHandler.GetMainExportID(asset) };
		}

		redirectedCount++;
		exportCollection = new SingleRedirectExportCollection(asset, pointer);
		return true;
	}

	public void LogSummary()
	{
		Logger.Info(LogCategory.Export, $"Redirected {redirectedCount} {(redirectedCount == 1 ? "asset" : "assets")} into user defined packages");
	}

	AssetType IAssetExporter.ToExportType(IUnityObjectBase asset) => AssetType.Meta;

	bool IAssetExporter.ToUnknownExportType(Type type, out AssetType assetType)
	{
		// This exporter only claims specific named assets, so it cannot answer for a whole type.
		assetType = default;
		return false;
	}

	/// <summary>
	/// <see cref="UnityGuid"/> has no TryParse, and a GUID typed by hand is exactly the kind of value that is worth
	/// rejecting with a message rather than an exception.
	/// </summary>
	private static bool TryParseGuid(string? text, out UnityGuid guid)
	{
		guid = default;
		if (text is not { Length: 32 })
		{
			return false;
		}
		foreach (char c in text)
		{
			if (!char.IsAsciiHexDigit(c))
			{
				return false;
			}
		}
		guid = UnityGuid.Parse(text);
		return guid != UnityGuid.Zero;
	}

	private readonly record struct PackageAssetKey(string Name, string? ClassName);
}
