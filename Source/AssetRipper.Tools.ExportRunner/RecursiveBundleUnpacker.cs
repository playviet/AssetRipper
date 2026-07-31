using System.Collections.Concurrent;
using AssetRipper.Export.Configuration;
using AssetRipper.Export.PrimaryContent;
using AssetRipper.Export.UnityProjects;
using AssetRipper.Import.Logging;
using AssetRipper.IO.Files;

namespace AssetRipper.Tools.ExportRunner;

internal static class RecursiveBundleUnpacker
{
	private static readonly HashSet<string> CandidateExtensions = new(StringComparer.OrdinalIgnoreCase)
	{
		"",
		".ab",
		".assetbundle",
		".bin",
		".bundle",
		".dat",
	};

	private static readonly HashSet<string> IgnoredExtensions = new(StringComparer.OrdinalIgnoreCase)
	{
		".atlas",
		".bmp",
		".cs",
		".dds",
		".dll",
		".flac",
		".glb",
		".jpeg",
		".jpg",
		".json",
		".meta",
		".mp3",
		".ogg",
		".otf",
		".png",
		".shader",
		".skel",
		".tga",
		".ttf",
		".txt",
		".wav",
		".xml",
	};

	public static RecursiveUnpackExecutionResult UnpackRecursively(string rootOutputPath, FileSystem fileSystem)
	{
		int workerCount = GetWorkerCount("ASSETRIPPER_UNPACK_WORKERS", fallback: 2);
		ConcurrentDictionary<string, byte> seenPaths = new(StringComparer.OrdinalIgnoreCase);
		ConcurrentBag<RecursiveUnpackResultRecord> results = [];
		using BlockingCollection<string> workQueue = [];
		int pendingCount = 0;
		int candidateCount = 0;
		int attemptedCount = 0;
		int unpackedCount = 0;
		int retainedCount = 0;
		int failedCount = 0;

		void EnqueueIfNew(string path)
		{
			string fullPath = Path.GetFullPath(path);
			if (seenPaths.TryAdd(fullPath, 0))
			{
				Interlocked.Increment(ref candidateCount);
				Interlocked.Increment(ref pendingCount);
				workQueue.Add(fullPath);
			}
		}

		foreach (string path in EnumerateCandidateFiles(rootOutputPath, fileSystem))
		{
			EnqueueIfNew(path);
		}

		if (Volatile.Read(ref pendingCount) == 0)
		{
			return new RecursiveUnpackExecutionResult(
				new RecursiveUnpackSummary(workerCount, 0, 0, 0, 0, 0),
				[]);
		}

		Logger.Info(LogCategory.Export, $"Recursively unpacking nested Unity bundles with up to {workerCount} worker(s).");

		Task[] workers = Enumerable.Range(0, workerCount)
			.Select(_ => Task.Run(() =>
			{
				foreach (string filePath in workQueue.GetConsumingEnumerable())
				{
					try
					{
						Interlocked.Increment(ref attemptedCount);
						RecursiveUnpackOutcome outcome = TryUnpack(filePath, fileSystem);
						results.Add(new RecursiveUnpackResultRecord(
							Path: filePath,
							Status: outcome.Status,
							OutputDirectory: outcome.OutputDirectory,
							Reason: outcome.Reason));

						switch (outcome.Status)
						{
							case "unpacked":
								Interlocked.Increment(ref unpackedCount);
								if (!string.IsNullOrWhiteSpace(outcome.OutputDirectory))
								{
									foreach (string nestedPath in EnumerateCandidateFiles(outcome.OutputDirectory, fileSystem))
									{
										EnqueueIfNew(nestedPath);
									}
								}
								break;
							case "failed":
								Interlocked.Increment(ref failedCount);
								break;
							default:
								Interlocked.Increment(ref retainedCount);
								break;
						}
					}
					finally
					{
						if (Interlocked.Decrement(ref pendingCount) == 0)
						{
							workQueue.CompleteAdding();
						}
					}
				}
			}))
			.ToArray();

		Task.WaitAll(workers);

		if (unpackedCount > 0)
		{
			Logger.Info(LogCategory.Export, $"Recursively unpacked {unpackedCount} nested Unity bundle(s).");
		}

		return new RecursiveUnpackExecutionResult(
			new RecursiveUnpackSummary(
				WorkerCount: workerCount,
				CandidateFileCount: candidateCount,
				AttemptedFileCount: attemptedCount,
				UnpackedFileCount: unpackedCount,
				RetainedFileCount: retainedCount,
				FailedFileCount: failedCount),
			results
				.OrderBy(result => result.Path, StringComparer.OrdinalIgnoreCase)
				.ToArray());
	}

	private static IEnumerable<string> EnumerateCandidateFiles(string rootDirectory, FileSystem fileSystem)
	{
		if (!fileSystem.Directory.Exists(rootDirectory))
		{
			yield break;
		}

		foreach (string path in Directory.EnumerateFiles(rootDirectory, "*", SearchOption.AllDirectories))
		{
			if (IsCandidateFile(path))
			{
				yield return path;
			}
		}
	}

	private static bool IsCandidateFile(string filePath)
	{
		string extension = Path.GetExtension(filePath);
		if (IgnoredExtensions.Contains(extension))
		{
			return false;
		}

		if (CandidateExtensions.Contains(extension))
		{
			return true;
		}

		string stemExtension = Path.GetExtension(Path.GetFileNameWithoutExtension(filePath));
		return stemExtension is ".ab" or ".assetbundle" or ".bundle";
	}

	private static RecursiveUnpackOutcome TryUnpack(string filePath, FileSystem fileSystem)
	{
		if (!fileSystem.File.Exists(filePath))
		{
			return new RecursiveUnpackOutcome("retained", null, "missing-file");
		}

		if (!SchemeReader.IsReadableFile(filePath, fileSystem))
		{
			return new RecursiveUnpackOutcome("retained", null, "unreadable-by-scheme-reader");
		}

		FullConfiguration nestedSettings = new();
		nestedSettings.LoadFromDefaultPath();
		ExportHandler handler = new(nestedSettings);
		string? unpackDirectory = null;

		try
		{
			var gameData = handler.LoadAndProcess([filePath], fileSystem);
			if (!gameData.GameBundle.HasAnyAssetCollections())
			{
				return new RecursiveUnpackOutcome("retained", null, "no-asset-collections");
			}

			unpackDirectory = GetUnpackDirectory(filePath, fileSystem);
			ResetOutputDirectory(unpackDirectory);

			nestedSettings.ExportRootPath = unpackDirectory;
			PrimaryContentExporter.CreateDefault(gameData, nestedSettings).Export(gameData.GameBundle, nestedSettings, fileSystem);

			if (!Directory.EnumerateFileSystemEntries(unpackDirectory).Any())
			{
				Directory.Delete(unpackDirectory, true);
				return new RecursiveUnpackOutcome("retained", null, "empty-unpack-output");
			}

			fileSystem.File.Delete(filePath);
			return new RecursiveUnpackOutcome("unpacked", unpackDirectory, null);
		}
		catch (Exception ex)
		{
			Logger.Warning(LogCategory.Export, $"Failed to recursively unpack '{filePath}': {ex.Message}");
			if (unpackDirectory is not null && Directory.Exists(unpackDirectory))
			{
				try
				{
					Directory.Delete(unpackDirectory, true);
				}
				catch
				{
				}
			}

			return new RecursiveUnpackOutcome("failed", null, ex.Message);
		}
	}

	private static string GetUnpackDirectory(string filePath, FileSystem fileSystem)
	{
		string parentDirectory = fileSystem.Path.GetDirectoryName(filePath) ?? string.Empty;
		string fileName = fileSystem.Path.GetFileName(filePath);
		string directoryName;

		if (fileName.EndsWith(".bytes", StringComparison.OrdinalIgnoreCase))
		{
			directoryName = fileName[..^".bytes".Length];
		}
		else
		{
			directoryName = fileSystem.Path.GetFileNameWithoutExtension(fileName);
		}

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

	private static int GetWorkerCount(string envKey, int fallback)
	{
		if (int.TryParse(Environment.GetEnvironmentVariable(envKey), out int explicitValue) && explicitValue > 0)
		{
			return explicitValue;
		}
		if (int.TryParse(Environment.GetEnvironmentVariable("ASSETRIPPER_WORKERS"), out int sharedValue) && sharedValue > 0)
		{
			return sharedValue;
		}

		return Math.Max(1, Math.Min(fallback, Environment.ProcessorCount));
	}

	private readonly record struct RecursiveUnpackOutcome(string Status, string? OutputDirectory, string? Reason);
}

internal readonly record struct RecursiveUnpackExecutionResult(
	RecursiveUnpackSummary Summary,
	RecursiveUnpackResultRecord[] Results);
