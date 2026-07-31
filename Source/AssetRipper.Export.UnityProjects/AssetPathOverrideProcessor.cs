using AssetRipper.Assets;
using AssetRipper.Export.Configuration;
using AssetRipper.Import.Logging;
using AssetRipper.Processing;

namespace AssetRipper.Export.UnityProjects;

/// <summary>
/// Applies the user's rules for where exported assets are written.
/// </summary>
/// <remarks>
/// The rules are matched in order and the first match wins, so a specific rule should be listed before a general one.
/// </remarks>
public sealed class AssetPathOverrideProcessor(AssetPathOverrideData data) : IAssetProcessor
{
	public void Process(GameData gameData)
	{
		if (data.Overrides.Count == 0)
		{
			return;
		}

		int applied = 0;
		foreach (IUnityObjectBase asset in gameData.GameBundle.FetchAssets())
		{
			foreach (AssetPathOverrideRule rule in data.Overrides)
			{
				if (!Matches(rule, asset))
				{
					continue;
				}

				if (rule.Directory is not null)
				{
					asset.OverrideDirectory = rule.Directory;
				}
				if (rule.FileName is not null)
				{
					asset.OverrideName = rule.FileName;
				}
				if (rule.Extension is not null)
				{
					asset.OverrideExtension = rule.Extension;
				}
				applied++;
				break;
			}
		}

		Logger.Info(LogCategory.Processing, $"Applied path overrides to {applied} {(applied == 1 ? "asset" : "assets")}");
	}

	private static bool Matches(AssetPathOverrideRule rule, IUnityObjectBase asset)
	{
		if (rule.ClassName is not null && !string.Equals(rule.ClassName, asset.ClassName, StringComparison.Ordinal))
		{
			return false;
		}
		if (rule.Name is not null && !string.Equals(rule.Name, asset.GetBestName(), StringComparison.Ordinal))
		{
			return false;
		}
		if (rule.OriginalPathPrefix is not null
			&& (asset.OriginalPath is null || !asset.OriginalPath.StartsWith(rule.OriginalPathPrefix, StringComparison.Ordinal)))
		{
			return false;
		}
		// A rule that matches everything and changes nothing would be pointless, but it is the user's to write.
		return true;
	}
}
