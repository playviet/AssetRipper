using AssetRipper.IO.Files;

namespace AssetRipper.Tools.ExportRunner;

internal static class ShardPlanner
{
	public static ShardPlan Resolve(ExportCommandOptions options, FileSystem fileSystem)
	{
		ArgumentNullException.ThrowIfNull(options);
		ArgumentNullException.ThrowIfNull(fileSystem);

		return options.ShardStrategy switch
		{
			ShardStrategyMode.Off => CreateSingleJobPlan(options, "disabled-by-option"),
			ShardStrategyMode.DirectChildren => CreateDirectChildPlan(options, fileSystem, "forced-direct-children"),
			ShardStrategyMode.Auto => ResolveAutoPlan(options, fileSystem),
			_ => CreateSingleJobPlan(options, "unknown-strategy"),
		};
	}

	private static ShardPlan ResolveAutoPlan(ExportCommandOptions options, FileSystem fileSystem)
	{
		if (options.InputPaths.Length != 1 || !fileSystem.Directory.Exists(options.InputPaths[0]))
		{
			return CreateSingleJobPlan(options, "auto-requires-single-directory-input");
		}

		string rootInput = options.InputPaths[0];
		string[] directEntries = Directory.EnumerateFileSystemEntries(rootInput).ToArray();
		if (directEntries.Length == 0)
		{
			return CreateSingleJobPlan(options, "auto-no-direct-children");
		}

		int directDirectoryCount = directEntries.Count(Directory.Exists);
		int directFileCount = directEntries.Length - directDirectoryCount;
		int bundleSignalCount = directEntries.Count(HasBundleSignal);
		int shardThreshold = GetAutoShardThreshold();
		int bundleThreshold = GetAutoBundleSignalThreshold();
		bool rootHasBundleSignal = HasBundleSignal(rootInput);

		if (directEntries.Length >= shardThreshold && (bundleSignalCount >= bundleThreshold || rootHasBundleSignal || directFileCount >= shardThreshold))
		{
			return CreateDirectChildPlan(
				options,
				fileSystem,
				$"auto-bundle-depot direct_entries={directEntries.Length} direct_directories={directDirectoryCount} direct_files={directFileCount} bundle_signals={bundleSignalCount}");
		}

		return CreateSingleJobPlan(
			options,
			$"auto-single-job direct_entries={directEntries.Length} direct_directories={directDirectoryCount} direct_files={directFileCount} bundle_signals={bundleSignalCount}");
	}

	private static ShardPlan CreateSingleJobPlan(ExportCommandOptions options, string reason)
	{
		return new ShardPlan(
			StrategyToken: GetShardStrategyToken(options.ShardStrategy),
			IsSharded: false,
			DecisionReason: reason,
			Jobs:
			[
				new PlannedExportJob(
					Name: Path.GetFileName(options.OutputPath),
					InputPaths: options.InputPaths,
					OutputPath: options.OutputPath,
					CleanOutput: false,
					SkipIfDoneMarkerExists: false,
					DoneMarkerPath: null,
					RunLogPath: null)
			]);
	}

	private static ShardPlan CreateDirectChildPlan(ExportCommandOptions options, FileSystem fileSystem, string reason)
	{
		if (options.InputPaths.Length != 1 || !fileSystem.Directory.Exists(options.InputPaths[0]))
		{
			throw new InvalidOperationException("shard strategy requires exactly one directory input path.");
		}

		string rootInput = options.InputPaths[0];
		string runLogPath = Path.Combine(options.OutputPath, "run.log");
		List<PlannedExportJob> jobs = [];
		List<ShardInputGroup> looseFileGroups = [];

		foreach (ShardInputGroup group in GroupDirectChildren(rootInput))
		{
			if (group.Kind == ShardInputGroupKind.Directory)
			{
				string shardOutputPath = Path.Combine(options.OutputPath, group.Name);
				jobs.Add(new PlannedExportJob(
					Name: group.Name,
					InputPaths: group.InputPaths,
					OutputPath: shardOutputPath,
					CleanOutput: options.CleanOutput,
					SkipIfDoneMarkerExists: true,
					DoneMarkerPath: Path.Combine(shardOutputPath, ".done"),
					RunLogPath: runLogPath));
			}
			else
			{
				looseFileGroups.Add(group);
			}
		}

		if (looseFileGroups.Count > 0)
		{
			foreach (ShardInputGroup group in GroupLooseFiles(looseFileGroups))
			{
				string shardOutputPath = Path.Combine(options.OutputPath, group.RelativeOutputPath);
				jobs.Add(new PlannedExportJob(
					Name: group.Name,
					InputPaths: group.InputPaths,
					OutputPath: shardOutputPath,
					CleanOutput: options.CleanOutput,
					SkipIfDoneMarkerExists: true,
					DoneMarkerPath: Path.Combine(shardOutputPath, ".done"),
					RunLogPath: runLogPath));
			}
		}

		return new ShardPlan(
			StrategyToken: GetShardStrategyToken(options.ShardStrategy),
			IsSharded: true,
			DecisionReason: reason,
			Jobs: jobs.ToArray());
	}

	private static IEnumerable<ShardInputGroup> GroupDirectChildren(string rootInput)
	{
		foreach (var item in Directory.EnumerateFileSystemEntries(rootInput)
			.OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
			.Select(entry => new
			{
				Entry = entry,
				Name = Path.GetFileName(entry),
			})
			.Where(item => !string.IsNullOrWhiteSpace(item.Name)))
		{
			yield return Directory.Exists(item.Entry)
				? new ShardInputGroup(item.Name, ShardInputGroupKind.Directory, [item.Entry], item.Name)
				: new ShardInputGroup(item.Name, ShardInputGroupKind.File, [item.Entry], item.Name);
		}
	}

	private static IEnumerable<ShardInputGroup> GroupLooseFiles(List<ShardInputGroup> fileGroups)
	{
		int batchSize = GetShardFileBatchSize();
		List<string> filePaths = fileGroups.SelectMany(group => group.InputPaths).OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();
		List<IGrouping<string, string>> groupedByPrefix = filePaths
			.GroupBy(GetStablePrefixKey, StringComparer.OrdinalIgnoreCase)
			.OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
			.ToList();

		if (ShouldUsePrefixGrouping(filePaths.Count, groupedByPrefix))
		{
			int prefixBatchSize = GetShardPrefixBatchSize();
			foreach (IGrouping<string, string> group in groupedByPrefix)
			{
				List<string> orderedPaths = group.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();
				if (orderedPaths.Count <= prefixBatchSize)
				{
					yield return new ShardInputGroup(
						Name: $"files-{group.Key}",
						Kind: ShardInputGroupKind.File,
						InputPaths: orderedPaths.ToArray(),
						RelativeOutputPath: Path.Combine("__files", group.Key));
				}
				else
				{
					foreach ((string[] batch, int batchIndex) in Batch(orderedPaths, prefixBatchSize))
					{
						yield return new ShardInputGroup(
							Name: $"files-{group.Key}-{batchIndex:0000}",
							Kind: ShardInputGroupKind.File,
							InputPaths: batch,
							RelativeOutputPath: Path.Combine("__files", group.Key, $"batch-{batchIndex:0000}"));
					}
				}
			}

			yield break;
		}

		foreach ((string[] batch, int batchIndex) in Batch(filePaths, batchSize))
		{
			yield return new ShardInputGroup(
				Name: $"files-batch-{batchIndex:0000}",
				Kind: ShardInputGroupKind.File,
				InputPaths: batch,
				RelativeOutputPath: Path.Combine("__files", $"batch-{batchIndex:0000}"));
		}
	}

	private static IEnumerable<(string[] Batch, int BatchIndex)> Batch(List<string> entries, int batchSize)
	{
		for (int index = 0, batchIndex = 1; index < entries.Count; index += batchSize, batchIndex++)
		{
			yield return (entries.Skip(index).Take(batchSize).ToArray(), batchIndex);
		}
	}

	private static int GetShardFileBatchSize()
	{
		if (int.TryParse(Environment.GetEnvironmentVariable("ASSETRIPPER_SHARD_FILE_BATCH_SIZE"), out int configuredValue) && configuredValue > 0)
		{
			return configuredValue;
		}

		return 64;
	}

	private static int GetShardPrefixBatchSize()
	{
		if (int.TryParse(Environment.GetEnvironmentVariable("ASSETRIPPER_SHARD_PREFIX_BATCH_SIZE"), out int configuredValue) && configuredValue > 0)
		{
			return configuredValue;
		}

		return Math.Max(8, GetShardFileBatchSize());
	}

	private static int GetAutoShardThreshold()
	{
		if (int.TryParse(Environment.GetEnvironmentVariable("ASSETRIPPER_AUTO_SHARD_MIN_DIRECT_CHILDREN"), out int configuredValue) && configuredValue > 0)
		{
			return configuredValue;
		}

		return 24;
	}

	private static int GetAutoBundleSignalThreshold()
	{
		if (int.TryParse(Environment.GetEnvironmentVariable("ASSETRIPPER_AUTO_SHARD_MIN_BUNDLE_SIGNALS"), out int configuredValue) && configuredValue > 0)
		{
			return configuredValue;
		}

		return 8;
	}

	private static bool HasBundleSignal(string path)
	{
		string name = Path.GetFileName(path);
		if (string.IsNullOrWhiteSpace(name))
		{
			return false;
		}

		string normalized = name.Trim().ToLowerInvariant();
		string extension = Path.GetExtension(normalized);
		if (extension is ".ab" or ".bundle" or ".assetbundle" or ".unity3d")
		{
			return true;
		}

		return normalized.Contains("assetbundle", StringComparison.Ordinal)
			|| normalized.Equals("ab", StringComparison.Ordinal)
			|| normalized.Contains("bundles", StringComparison.Ordinal)
			|| normalized.Contains("streamingassets", StringComparison.Ordinal);
	}

	private static bool ShouldUsePrefixGrouping(int fileCount, List<IGrouping<string, string>> groupedByPrefix)
	{
		if (fileCount < GetAutoBundleSignalThreshold())
		{
			return false;
		}

		if (groupedByPrefix.Count < 2)
		{
			return false;
		}

		IGrouping<string, string> dominant = groupedByPrefix
			.OrderByDescending(group => group.Count())
			.ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
			.First();

		int nonTrivialGroups = groupedByPrefix.Count(group => group.Count() >= 3);
		return dominant.Count() >= Math.Max(4, fileCount / 6)
			&& nonTrivialGroups >= 2;
	}

	private static string GetStablePrefixKey(string path)
	{
		string name = Path.GetFileNameWithoutExtension(path);
		if (string.IsNullOrWhiteSpace(name))
		{
			return "misc";
		}

		ReadOnlySpan<char> span = name.AsSpan();
		List<string> tokens = [];
		int tokenStart = 0;

		for (int index = 0; index < span.Length; index++)
		{
			char c = span[index];
			bool boundary = c is '_' or '-' or '.' || char.IsDigit(c);
			if (boundary)
			{
				if (index > tokenStart)
				{
					tokens.Add(span[tokenStart..index].ToString());
				}

				tokenStart = index + 1;
			}
		}

		if (tokenStart < span.Length)
		{
			tokens.Add(span[tokenStart..].ToString());
		}

		string[] usefulTokens = tokens
			.Select(token => token.Trim().ToLowerInvariant())
			.Where(token => token.Length >= 2)
			.Take(2)
			.ToArray();

		return usefulTokens.Length > 0
			? string.Join("-", usefulTokens)
			: "misc";
	}

	private static string GetShardStrategyToken(ShardStrategyMode strategy)
	{
		return strategy switch
		{
			ShardStrategyMode.Off => "off",
			ShardStrategyMode.DirectChildren => "direct-children",
			ShardStrategyMode.Auto => "auto",
			_ => "unknown",
		};
	}
}

internal sealed record ShardPlan(
	string StrategyToken,
	bool IsSharded,
	string DecisionReason,
	PlannedExportJob[] Jobs);

internal enum ShardInputGroupKind
{
	Directory,
	File,
}

internal sealed record ShardInputGroup(
	string Name,
	ShardInputGroupKind Kind,
	string[] InputPaths,
	string RelativeOutputPath);
