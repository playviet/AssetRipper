using AssetRipper.Assets;
using AssetRipper.Import.Logging;
using AssetRipper.IO.Files;

namespace AssetRipper.Tools.ExportRunner;

internal static class Program
{
	private static int Main(string[] args)
	{
		Logger.Add(new CliConsoleLogger());

		try
		{
			if (args.Length == 0)
			{
				PrintUsage();
				return 1;
			}

			string command = args[0].Trim().ToLowerInvariant();
			if (command is "primary" or "dump")
			{
				return RunLegacyExport(args);
			}

			return command switch
			{
				"inspect" => RunInspect(args[1..]),
				"analyze" => RunAnalyze(args[1..]),
				"export" => RunExport(args[1..]),
				"report" => RunReport(args[1..]),
				_ => UnknownCommand(args[0]),
			};
		}
		catch (Exception ex)
		{
			Console.WriteLine(ex);
			return 1;
		}
	}

	private static int RunInspect(string[] args)
	{
		InspectArguments? arguments = InspectArguments.Parse(args);
		if (arguments is null)
		{
			return 1;
		}

		if (arguments.InputPaths is null or { Length: 0 })
		{
			Console.WriteLine("Usage: AssetRipper.Tools.ExportRunner inspect <input-path> [more-input-paths...]");
			return 1;
		}

		PrintInventorySummary(InventoryWorkflow.LoadSummary(arguments.InputPaths.Select(Path.GetFullPath).ToArray(), LocalFileSystem.Instance));
		return 0;
	}

	private static int RunAnalyze(string[] args)
	{
		AnalyzeArguments? arguments = AnalyzeArguments.Parse(args);
		if (arguments is null)
		{
			return 1;
		}

		if (arguments.InputPaths is null or { Length: 0 })
		{
			Console.WriteLine("Usage: AssetRipper.Tools.ExportRunner analyze <input-path> [more-input-paths...] [--report <report-path>]");
			return 1;
		}

		string[] inputPaths = arguments.InputPaths.Select(Path.GetFullPath).ToArray();
		InventorySummary summary = InventoryWorkflow.LoadSummary(inputPaths, LocalFileSystem.Instance);

		PrintInventorySummary(summary);

		if (!string.IsNullOrWhiteSpace(arguments.ReportPath))
		{
			string reportPath = Path.GetFullPath(arguments.ReportPath);
			RunnerArtifacts.Write(reportPath, summary);
			Console.WriteLine();
			Console.WriteLine($"Analysis report written to {reportPath}");
		}

		return 0;
	}

	private static int RunExport(string[] args)
	{
		string[] normalizedArgs = NormalizeExportArgs(args);
		ExportArguments? arguments = ExportArguments.Parse(normalizedArgs);
		if (arguments is null)
		{
			return 1;
		}

		if ((string.IsNullOrWhiteSpace(arguments.Mode) && string.IsNullOrWhiteSpace(arguments.Profile))
			|| arguments.InputPaths is null || arguments.InputPaths.Length < 1 || string.IsNullOrWhiteSpace(arguments.OutputPath))
		{
			Console.WriteLine("Usage: AssetRipper.Tools.ExportRunner export <input-path> [more-input-paths...] --output <output-path> [--profile <profile> | --mode <primary|dump>] [--keep-output] [--recursive-unpack on|off] [--shard-strategy off|direct-children|auto]");
			Console.WriteLine("Compatibility: AssetRipper.Tools.ExportRunner export <primary|dump> <input-path> ...");
			return 1;
		}

		ShardStrategyMode shardStrategy = arguments.ShardDirectChildren
			? ShardStrategyMode.DirectChildren
			: arguments.ShardStrategy;

		ResolvedExportSettings resolved = ExportProfileResolver.Resolve(arguments.Mode, arguments.Profile);
		if (!string.IsNullOrWhiteSpace(resolved.Note))
		{
			Console.WriteLine(resolved.Note);
		}

		ExportCommandOptions options = new(
			resolved.Mode,
			resolved.Profile,
			arguments.InputPaths.Select(Path.GetFullPath).ToArray(),
			Path.GetFullPath(arguments.OutputPath),
			CleanOutput: !arguments.KeepOutput,
			RecursiveUnpack: arguments.RecursiveUnpack == RecursiveUnpackMode.On,
			ShardStrategy: shardStrategy);
		return new CliExportExecutor(LocalFileSystem.Instance).Execute(options);
	}

	private static int RunReport(string[] args)
	{
		ReportArguments? arguments = ReportArguments.Parse(args);
		if (arguments is null)
		{
			return 1;
		}

		if (string.IsNullOrWhiteSpace(arguments.ArtifactPath))
		{
			Console.WriteLine("Usage: AssetRipper.Tools.ExportRunner report <artifact-path>");
			return 1;
		}

		return ArtifactReportWorkflow.Run(Path.GetFullPath(arguments.ArtifactPath));
	}

	internal static void PrintExportPlan(ExportPlan plan)
	{
		Console.WriteLine($"Mode: {plan.Mode}");
		if (!string.IsNullOrWhiteSpace(plan.Profile))
		{
			Console.WriteLine($"Profile: {plan.Profile}");
		}
		Console.WriteLine($"Output: {plan.OutputPath}");
		Console.WriteLine($"Created: {plan.CreatedAt:O}");
		Console.WriteLine($"Recursive unpack: {plan.RecursiveUnpack}");
		Console.WriteLine($"Shard strategy: {plan.ShardStrategy}");
		Console.WriteLine($"Shard direct children: {plan.ShardDirectChildren}");
		if (!string.IsNullOrWhiteSpace(plan.ShardDecisionReason))
		{
			Console.WriteLine($"Shard decision: {plan.ShardDecisionReason}");
		}
		Console.WriteLine($"Clean output: {plan.CleanOutput}");
		Console.WriteLine($"Job workers: {plan.JobWorkers}");
		Console.WriteLine($"Jobs: {plan.Jobs.Length}");
		Console.WriteLine();
		Console.WriteLine("Planned jobs:");
		foreach (PlannedExportJob job in plan.Jobs)
		{
			Console.WriteLine($"  {job.Name} output={job.OutputPath}");
			Console.WriteLine($"           inputs={string.Join(", ", job.InputPaths)}");
			Console.WriteLine($"           clean_output={job.CleanOutput} skip_if_done={job.SkipIfDoneMarkerExists}");
		}
	}

	private static int RunLegacyExport(string[] args)
	{
		if (args.Length < 3)
		{
			Console.WriteLine("Usage: AssetRipper.Tools.ExportRunner <primary|dump> <input-path> <output-path> [more-input-paths...]");
			return 1;
		}

		ExportCommandOptions options = new(
			args[0].Trim().ToLowerInvariant(),
			Profile: null,
			args.Skip(1).Where((_, index) => index != 1).Select(Path.GetFullPath).ToArray(),
			Path.GetFullPath(args[2]),
			CleanOutput: true,
			RecursiveUnpack: args[0].Trim().Equals("primary", StringComparison.OrdinalIgnoreCase),
			ShardStrategy: ShardStrategyMode.Off);
		return new CliExportExecutor(LocalFileSystem.Instance).Execute(options);
	}

	internal static void PrintInventorySummary(InventorySummary summary)
	{
		Console.WriteLine($"Project version: {summary.ProjectVersion}");
		Console.WriteLine($"Asset collections: {summary.AssetCollectionCount}");
		Console.WriteLine($"Assets: {summary.AssetCount}");
		Console.WriteLine($"Resource files: {summary.ResourceFileCount}");
		Console.WriteLine($"Distinct output buckets: {summary.DistinctOutputBucketCount}");
		Console.WriteLine($"Assets with best-directory signal: {summary.AssetsWithBestDirectoryCount}");
		Console.WriteLine($"Path semantics: {summary.PathSemantics}");
		Console.WriteLine($"Suggested profiles: {string.Join(", ", summary.SuggestedProfiles)}");
		Console.WriteLine();

		Console.WriteLine("Profile evidence:");
		foreach (ProfileEvidence evidence in summary.ProfileEvidence
			.Where(item => item.MatchedAssets > 0)
			.Take(8))
		{
			string buckets = evidence.TopBuckets.Length == 0
				? "none"
				: string.Join(", ", evidence.TopBuckets.Take(3).Select(bucket => $"{bucket.Key} ({bucket.Count})"));
			string signals = evidence.TopSignals.Length == 0
				? "none"
				: string.Join(", ", evidence.TopSignals.Take(5).Select(signal => $"{signal.Key} ({signal.Count})"));
			Console.WriteLine($"  {evidence.Profile} matched={evidence.MatchedAssets} strong={evidence.StrongMatches}");
			Console.WriteLine($"           buckets={buckets}");
			Console.WriteLine($"           signals={signals}");
		}
		Console.WriteLine();

		Console.WriteLine("Top asset classes:");
		foreach (KeyCount entry in summary.TopAssetClasses.Take(15))
		{
			Console.WriteLine($"  {entry.Count,8}  {entry.Key}");
		}

		Console.WriteLine();
		Console.WriteLine("Top output buckets:");
		foreach (KeyCount entry in summary.TopOutputBuckets.Take(15))
		{
			Console.WriteLine($"  {entry.Count,8}  {entry.Key}");
		}
	}

	internal static void PrintExportManifest(ExportManifest manifest)
	{
		Console.WriteLine($"Mode: {manifest.Mode}");
		if (!string.IsNullOrWhiteSpace(manifest.Profile))
		{
			Console.WriteLine($"Profile: {manifest.Profile}");
		}
		Console.WriteLine($"Output: {manifest.OutputPath}");
		Console.WriteLine($"Started: {manifest.StartedAt:O}");
		Console.WriteLine($"Finished: {manifest.FinishedAt:O}");
		Console.WriteLine($"Recursive unpack: {manifest.RecursiveUnpack}");
		Console.WriteLine($"Shard strategy: {manifest.ShardStrategy}");
		Console.WriteLine($"Shard direct children: {manifest.ShardDirectChildren}");
		if (!string.IsNullOrWhiteSpace(manifest.ShardDecisionReason))
		{
			Console.WriteLine($"Shard decision: {manifest.ShardDecisionReason}");
		}
		Console.WriteLine($"Workers: load={manifest.LoadWorkers}, export={manifest.ExportWorkers}, unpack={manifest.UnpackWorkers}");
		Console.WriteLine($"Jobs: {manifest.Jobs.Length}");
		Console.WriteLine($"Outcome summary: success={manifest.OutcomeSummary.SuccessCount} skipped={manifest.OutcomeSummary.SkippedCount} failed={manifest.OutcomeSummary.FailedCount}");
		if (manifest.OutcomeSummary.SelectedCollectionCount > 0 || manifest.OutcomeSummary.SelectionSkippedCollectionCount > 0 || manifest.OutcomeSummary.ExportFailedCollectionCount > 0)
		{
			Console.WriteLine($"Selections: selected={manifest.OutcomeSummary.SelectedCollectionCount} skipped={manifest.OutcomeSummary.SelectionSkippedCollectionCount} failed={manifest.OutcomeSummary.ExportFailedCollectionCount}");
		}
		if (manifest.OutcomeSummary.RecursiveUnpackAttemptedCount > 0 || manifest.OutcomeSummary.RecursiveUnpackCandidateCount > 0)
		{
			Console.WriteLine($"Recursive unpack summary: candidates={manifest.OutcomeSummary.RecursiveUnpackCandidateCount} attempted={manifest.OutcomeSummary.RecursiveUnpackAttemptedCount} unpacked={manifest.OutcomeSummary.RecursiveUnpackUnpackedCount} retained={manifest.OutcomeSummary.RecursiveUnpackRetainedCount} failed={manifest.OutcomeSummary.RecursiveUnpackFailedCount}");
		}
		Console.WriteLine();
		Console.WriteLine("Job results:");
		foreach (ExportJobManifest job in manifest.Jobs)
		{
			Console.WriteLine($"  {job.Status,-7} {job.Name} exporter={job.Exporter} files={job.FileCount} bytes={job.TotalBytes}");
			if (job.Selection is not null)
			{
				Console.WriteLine($"           selected={job.Selection.SelectedCollections}/{job.Selection.TotalCollections} skipped={job.Selection.SkippedCollections} failed={job.Selection.FailedCollections}{(string.IsNullOrWhiteSpace(job.Selection.SkipReason) ? "" : $" reason={job.Selection.SkipReason}")}");
			}
			if (job.RecursiveUnpack is not null)
			{
				Console.WriteLine($"           unpack candidates={job.RecursiveUnpack.CandidateFileCount} attempted={job.RecursiveUnpack.AttemptedFileCount} unpacked={job.RecursiveUnpack.UnpackedFileCount} retained={job.RecursiveUnpack.RetainedFileCount} failed={job.RecursiveUnpack.FailedFileCount}");
			}
			if (job.FailedCollections.Length > 0)
			{
				Console.WriteLine($"           failure-details={job.FailedCollections.Length}");
			}
			foreach (OutcomeReasonCount item in job.OutcomeSummary.SelectionSkipReasons.Take(3))
			{
				Console.WriteLine($"           skip-reason {item.Key} count={item.Count}");
			}
			foreach (OutcomeReasonCount item in job.OutcomeSummary.ExportFailureReasons.Take(3))
			{
				Console.WriteLine($"           failure-reason {item.Key} count={item.Count}");
			}
			foreach (OutcomeReasonCount item in job.OutcomeSummary.RecursiveUnpackStatuses.Take(3))
			{
				Console.WriteLine($"           unpack-status {item.Key} count={item.Count}");
			}
			foreach (OutcomeReasonCount item in job.OutcomeSummary.RecursiveUnpackReasons.Take(3))
			{
				Console.WriteLine($"           unpack-reason {item.Key} count={item.Count}");
			}
			if (job.Inventory is not null)
			{
				Console.WriteLine($"           assets={job.Inventory.AssetCount} buckets={job.Inventory.DistinctOutputBucketCount} semantics={job.Inventory.PathSemantics}");
			}
			if (!string.IsNullOrWhiteSpace(job.ErrorMessage))
			{
				Console.WriteLine($"           error={job.ErrorMessage}");
			}
		}
	}

	internal static void PrintRecursiveUnpackArtifact(RecursiveUnpackArtifact artifact)
	{
		Console.WriteLine($"Mode: {artifact.Mode}");
		if (!string.IsNullOrWhiteSpace(artifact.Profile))
		{
			Console.WriteLine($"Profile: {artifact.Profile}");
		}
		Console.WriteLine($"Output: {artifact.OutputPath}");
		Console.WriteLine($"Jobs: {artifact.Jobs.Length}");
		Console.WriteLine();
		Console.WriteLine("Recursive unpack job summaries:");
		foreach (RecursiveUnpackJobRecord job in artifact.Jobs)
		{
			Console.WriteLine($"  {job.Name} candidates={job.Summary.CandidateFileCount} attempted={job.Summary.AttemptedFileCount} unpacked={job.Summary.UnpackedFileCount} retained={job.Summary.RetainedFileCount} failed={job.Summary.FailedFileCount}");
			foreach (var group in job.Results
				.GroupBy(item => item.Status)
				.OrderByDescending(group => group.Count()))
			{
				Console.WriteLine($"           {group.Key} count={group.Count()}");
			}
		}
	}

	internal static void PrintSkippedAssetsArtifact(SkippedAssetsArtifact artifact)
	{
		Console.WriteLine($"Mode: {artifact.Mode}");
		if (!string.IsNullOrWhiteSpace(artifact.Profile))
		{
			Console.WriteLine($"Profile: {artifact.Profile}");
		}
		Console.WriteLine($"Output: {artifact.OutputPath}");
		Console.WriteLine($"Jobs: {artifact.Jobs.Length}");
		Console.WriteLine();
		Console.WriteLine("Skipped job summaries:");
		foreach (SkippedAssetJobRecord job in artifact.Jobs)
		{
			Console.WriteLine($"  {job.Name} skipped={job.SkippedCollections.Length}");
			foreach (var group in job.SkippedCollections
				.GroupBy(item => item.Reason)
				.OrderByDescending(group => group.Count())
				.Take(5))
			{
				Console.WriteLine($"           {group.Key} count={group.Count()}");
			}
		}
	}

	internal static void PrintFailedAssetsArtifact(FailedAssetsArtifact artifact)
	{
		Console.WriteLine($"Mode: {artifact.Mode}");
		if (!string.IsNullOrWhiteSpace(artifact.Profile))
		{
			Console.WriteLine($"Profile: {artifact.Profile}");
		}
		Console.WriteLine($"Output: {artifact.OutputPath}");
		Console.WriteLine($"Jobs: {artifact.Jobs.Length}");
		Console.WriteLine();
		Console.WriteLine("Failed job summaries:");
		foreach (FailedAssetJobRecord job in artifact.Jobs)
		{
			Console.WriteLine($"  {job.Name} failed={job.FailedCollections.Length}");
			foreach (var group in job.FailedCollections
				.GroupBy(item => item.Reason)
				.OrderByDescending(group => group.Count())
				.Take(5))
			{
				Console.WriteLine($"           {group.Key} count={group.Count()}");
			}
		}
	}

	private static int UnknownCommand(string command)
	{
		Console.WriteLine($"Unknown command '{command}'.");
		PrintUsage();
		return 1;
	}

	private static void PrintUsage()
	{
		Console.WriteLine("Usage:");
		Console.WriteLine("  AssetRipper.Tools.ExportRunner inspect <input-path> [more-input-paths...]");
		Console.WriteLine("  AssetRipper.Tools.ExportRunner analyze <input-path> [more-input-paths...] [--report <report-path>]");
		Console.WriteLine("  AssetRipper.Tools.ExportRunner export <input-path> [more-input-paths...] --output <output-path> [--profile <profile> | --mode <primary|dump>] [--keep-output] [--recursive-unpack on|off] [--shard-strategy off|direct-children|auto]");
		Console.WriteLine("  Compatibility: --shard-direct-children is kept as shorthand for --shard-strategy direct-children");
		Console.WriteLine("  Profiles: player-art, characters, ui, audio, narrative, cg, backgrounds, sprites, full-project, full-raw");
		Console.WriteLine("  AssetRipper.Tools.ExportRunner report <artifact-path>");
		Console.WriteLine("  AssetRipper.Tools.ExportRunner <primary|dump> <input-path> <output-path> [more-input-paths...]");
	}

	private static string[] NormalizeExportArgs(string[] args)
	{
		if (args.Length > 0 && !args[0].StartsWith("-", StringComparison.Ordinal) && ExportProfileResolver.IsRecognizedModeToken(args[0]))
		{
			return ["--mode", args[0], .. args[1..]];
		}

		return args;
	}

	private sealed class CliConsoleLogger : ILogger
	{
		private readonly ConsoleLogger inner = new(false);

		public void BlankLine(int numLines) => inner.BlankLine(numLines);

		public void Log(LogType type, LogCategory category, string message)
		{
			if (category == LogCategory.ExportProgress && type == LogType.Info)
			{
				return;
			}

			if (category == LogCategory.Import && type == LogType.Info)
			{
				if ((message.StartsWith("Asset bundle '", StringComparison.Ordinal) || message.StartsWith("Game file '", StringComparison.Ordinal))
					&& message.EndsWith("has been found", StringComparison.Ordinal))
				{
					return;
				}
			}

			if (category == LogCategory.Import && type == LogType.Warning)
			{
				if (message.StartsWith("Dependency 'archive:/", StringComparison.Ordinal) && message.EndsWith("' wasn't found", StringComparison.Ordinal))
				{
					return;
				}
			}

			inner.Log(type, category, message);
		}
	}
}
