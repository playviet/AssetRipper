using AssetRipper.Assets;
using AssetRipper.Export.Configuration;
using AssetRipper.Import.Logging;
using AssetRipper.SourceGenerated.Extensions;
using System.Collections.Concurrent;

namespace AssetRipper.Export.PrimaryContent;

public sealed partial class PrimaryContentExporter
{
	/// <summary>
	/// Exports primary content, optionally exporting only the collections a caller selects, and reports what happened
	/// to each one.
	/// </summary>
	/// <remarks>
	/// This exists alongside <see cref="Export(Assets.Bundles.GameBundle, FullConfiguration, FileSystem)"/> rather than
	/// replacing it, so that command line callers can filter and report without changing the signature the rest of the
	/// application uses.
	/// <para>
	/// Collections are exported in parallel. Two collections can resolve to the same output path, so each path gets a
	/// lock and writers to the same path are serialised; without that they would race and produce a truncated file.
	/// </para>
	/// </remarks>
	/// <param name="selectionPredicate">Decides per collection whether to export it. Null exports everything.</param>
	public PrimaryExportStats ExportSelected(
		Assets.Bundles.GameBundle fileCollection,
		FullConfiguration settings,
		FileSystem fileSystem,
		Func<ExportCollectionBase, ExportCollectionSelectionDecision>? selectionPredicate = null)
	{
		List<ExportCollectionBase> allCollections = CreateCollections(fileCollection)
			.Where(c => c.Exportable)
			.ToList();

		List<ExportCollectionBase> collections = [];
		List<PrimarySkippedCollection> skippedCollections = [];

		if (selectionPredicate is null)
		{
			collections.AddRange(allCollections);
		}
		else
		{
			foreach (ExportCollectionBase collection in allCollections)
			{
				ExportCollectionSelectionDecision decision = selectionPredicate(collection);
				if (decision.Include)
				{
					collections.Add(collection);
				}
				else
				{
					skippedCollections.Add(Describe<PrimarySkippedCollection>(collection, decision.Reason ?? "excluded-by-selection"));
				}
			}

			Logger.Info(LogCategory.Export, $"Selected {collections.Count} of {allCollections.Count} primary collections for export.");
		}

		int totalCount = collections.Count;
		int workerCount = GetWorkerCount();
		Logger.Info(LogCategory.Export, $"Exporting {totalCount} primary collections with up to {workerCount} worker(s).");

		if (totalCount == 0)
		{
			return new PrimaryExportStats(allCollections.Count, 0, skippedCollections.Count, 0, skippedCollections, []);
		}

		ConcurrentDictionary<string, object> exportLocks = new(StringComparer.OrdinalIgnoreCase);
		ConcurrentBag<PrimaryFailedCollection> failedCollections = [];
		int completedCount = 0;
		int failedCount = 0;

		Parallel.ForEach(collections, new ParallelOptions { MaxDegreeOfParallelism = workerCount }, collection =>
		{
			string exportKey = collection.GetExportKey(settings.ExportRootPath, fileSystem);
			object exportLock = exportLocks.GetOrAdd(exportKey, static _ => new object());

			bool exportedSuccessfully;
			lock (exportLock)
			{
				exportedSuccessfully = collection.Export(settings.ExportRootPath, fileSystem);
			}

			if (!exportedSuccessfully)
			{
				Interlocked.Increment(ref failedCount);
				failedCollections.Add(Describe<PrimaryFailedCollection>(collection, "exporter-returned-false"));
				Logger.Warning(LogCategory.ExportProgress, $"Failed to export '{collection.Name}'");
			}

			int currentCount = Interlocked.Increment(ref completedCount);
			if (ShouldLogProgress(currentCount, totalCount))
			{
				Logger.Info(LogCategory.ExportProgress, $"({currentCount}/{totalCount}) Exported '{collection.Name}'");
			}
		});

		if (failedCount > 0)
		{
			Logger.Warning(LogCategory.Export, $"{failedCount} primary collection(s) failed to export.");
		}

		return new PrimaryExportStats(
			allCollections.Count,
			totalCount,
			skippedCollections.Count,
			failedCount,
			skippedCollections,
			failedCollections.ToArray());
	}

	private static T Describe<T>(ExportCollectionBase collection, string reason) where T : ICollectionDescription<T>
	{
		IUnityObjectBase? asset = collection.ExportableAssets.FirstOrDefault();
		return T.Create(collection.Name, asset?.ClassName ?? "Unknown", asset?.GetBestDirectory() ?? "", reason);
	}

	private static int GetWorkerCount()
	{
		const int Fallback = 4;
		string? value = Environment.GetEnvironmentVariable("ASSETRIPPER_EXPORT_WORKERS");
		if (int.TryParse(value, out int parsed) && parsed > 0)
		{
			return parsed;
		}
		return Math.Max(1, Math.Min(Fallback, Environment.ProcessorCount));
	}

	/// <summary>
	/// Keeps the log readable on large exports by thinning progress lines out as the total grows.
	/// </summary>
	private static bool ShouldLogProgress(int currentCount, int totalCount)
	{
		if (currentCount <= 10 || currentCount == totalCount)
		{
			return true;
		}

		int interval = totalCount switch
		{
			>= 10000 => 500,
			>= 5000 => 250,
			>= 1000 => 100,
			_ => 25,
		};
		return currentCount % interval == 0;
	}
}

/// <summary>
/// What <see cref="PrimaryContentExporter.ExportSelected"/> did.
/// </summary>
public readonly record struct PrimaryExportStats(
	int TotalCollections,
	int SelectedCollections,
	int SkippedBySelection,
	int FailedCollections,
	IReadOnlyList<PrimarySkippedCollection> SkippedCollections,
	IReadOnlyList<PrimaryFailedCollection> FailedCollectionDetails);

/// <summary>
/// Whether to export a collection, and why not when it is excluded.
/// </summary>
public readonly record struct ExportCollectionSelectionDecision(bool Include, string? Reason)
{
	public static ExportCollectionSelectionDecision Included() => new(true, null);
	public static ExportCollectionSelectionDecision Excluded(string reason) => new(false, reason);
}

/// <summary>
/// Lets the skipped and failed records be built by one shared helper.
/// </summary>
public interface ICollectionDescription<out T>
{
	static abstract T Create(string name, string className, string directory, string reason);
}

public readonly record struct PrimarySkippedCollection(string Name, string ClassName, string Directory, string Reason)
	: ICollectionDescription<PrimarySkippedCollection>
{
	public static PrimarySkippedCollection Create(string name, string className, string directory, string reason)
		=> new(name, className, directory, reason);
}

public readonly record struct PrimaryFailedCollection(string Name, string ClassName, string Directory, string Reason)
	: ICollectionDescription<PrimaryFailedCollection>
{
	public static PrimaryFailedCollection Create(string name, string className, string directory, string reason)
		=> new(name, className, directory, reason);
}
