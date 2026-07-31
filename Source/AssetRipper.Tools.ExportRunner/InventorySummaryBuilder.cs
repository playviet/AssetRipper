using AssetRipper.Assets;

namespace AssetRipper.Tools.ExportRunner;

internal static class InventorySummaryBuilder
{
	public static InventorySummary Build(Processing.GameData gameData, string[] inputPaths)
	{
		List<IUnityObjectBase> assets = gameData.GameBundle.FetchAssets().ToList();
		Dictionary<string, int> classCounts = new(StringComparer.Ordinal);
		Dictionary<string, int> directoryCounts = new(StringComparer.Ordinal);
		int assetCount = 0;
		int assetsWithBestDirectoryCount = 0;

		foreach (IUnityObjectBase asset in assets)
		{
			assetCount++;
			string className = asset.ClassName;
			classCounts[className] = classCounts.TryGetValue(className, out int classCount) ? classCount + 1 : 1;

			string directory = asset.GetBestDirectory();
			if (!string.IsNullOrWhiteSpace(directory))
			{
				assetsWithBestDirectoryCount++;
				directoryCounts[directory] = directoryCounts.TryGetValue(directory, out int directoryCount) ? directoryCount + 1 : 1;
			}
		}

		ProfileEvidence[] profileEvidence = ProfileSelection.BuildInventoryEvidence(assets);

		return new InventorySummary(
			ArtifactType: "inventory-summary",
			CreatedAt: DateTimeOffset.Now,
			InputPaths: inputPaths,
			ProjectVersion: gameData.ProjectVersion.ToString(),
			AssetCollectionCount: gameData.GameBundle.FetchAssetCollections().Count(),
			AssetCount: assetCount,
			ResourceFileCount: gameData.GameBundle.FetchResourceFiles().Count(),
			DistinctOutputBucketCount: directoryCounts.Count,
			AssetsWithBestDirectoryCount: assetsWithBestDirectoryCount,
			PathSemantics: ClassifyPathSemantics(assetCount, assetsWithBestDirectoryCount, directoryCounts.Count),
			SuggestedProfiles: ProfileSelection.SuggestProfiles(classCounts, directoryCounts, profileEvidence),
			ProfileEvidence: profileEvidence,
			TopAssetClasses: classCounts
				.OrderByDescending(pair => pair.Value)
				.ThenBy(pair => pair.Key, StringComparer.Ordinal)
				.Take(20)
				.Select(pair => new KeyCount(pair.Key, pair.Value))
				.ToArray(),
			TopOutputBuckets: directoryCounts
				.OrderByDescending(pair => pair.Value)
				.ThenBy(pair => pair.Key, StringComparer.Ordinal)
				.Take(20)
				.Select(pair => new KeyCount(pair.Key, pair.Value))
				.ToArray());
	}

	private static string ClassifyPathSemantics(int assetCount, int assetsWithBestDirectoryCount, int distinctOutputBucketCount)
	{
		if (assetCount == 0)
		{
			return "empty";
		}

		double bestDirectoryRatio = (double)assetsWithBestDirectoryCount / assetCount;
		if (bestDirectoryRatio >= 0.75 && distinctOutputBucketCount >= 50)
		{
			return "path-rich";
		}

		if (bestDirectoryRatio >= 0.35)
		{
			return "mixed";
		}

		return "type-bucket-heavy";
	}
}
