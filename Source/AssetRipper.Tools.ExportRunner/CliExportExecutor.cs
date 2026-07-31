using AssetRipper.Export.Configuration;
using AssetRipper.Export.PrimaryContent;
using AssetRipper.Export.UnityProjects;
using AssetRipper.Import.Logging;
using AssetRipper.IO.Files;
using System.Text.Json;

namespace AssetRipper.Tools.ExportRunner;

internal sealed class CliExportExecutor
{
	private readonly FileSystem fileSystem;
	private readonly ExportScheduler scheduler = new();

	public CliExportExecutor(FileSystem fileSystem)
	{
		this.fileSystem = fileSystem;
	}

	public int Execute(ExportCommandOptions options)
	{
		if (options.InputPaths.Length == 0)
		{
			Console.WriteLine("No input paths were provided.");
			return 1;
		}

		LogExecutionSettings(options);
		PrepareRootOutput(options);

		int jobWorkers = GetJobWorkerCount();
		ExportPlan plan = ExportPlanBuilder.Build(options, fileSystem, jobWorkers);
		string planPath = Path.Combine(options.OutputPath, "export-plan.json");
		RunnerArtifacts.Write(planPath, plan);
		Logger.Info(LogCategory.Export, $"Plan written to {planPath}");

		ExportJobManifest[] jobs = scheduler.ExecuteAsync(
			plan,
			job => ExecutePlannedJob(options, job),
			(job, result) => OnJobCompleted(job, result),
			workerCount: jobWorkers).GetAwaiter().GetResult();
		ExportManifest manifest = CreateManifest(options, plan, plan.CreatedAt, jobs);

		string manifestPath = Path.Combine(options.OutputPath, "export-manifest.json");
		RunnerArtifacts.Write(manifestPath, manifest);
		Logger.Info(LogCategory.Export, $"Manifest written to {manifestPath}");

		WriteSkippedAssetsArtifact(options, manifest);
		WriteFailedAssetsArtifact(options, manifest);
		WriteRecursiveUnpackArtifact(options, manifest);
		WriteSummary(options, manifest);
		return manifest.Jobs.Any(job => string.Equals(job.Status, "failed", StringComparison.Ordinal)) ? 1 : 0;
	}

	private void PrepareRootOutput(ExportCommandOptions options)
	{
		if (options.CleanOutput)
		{
			ResetOutputDirectory(options.OutputPath);
		}
		else
		{
			Directory.CreateDirectory(options.OutputPath);
		}
	}

	private ExportJobManifest ExecutePlannedJob(ExportCommandOptions options, PlannedExportJob job)
	{
		if (job.SkipIfDoneMarkerExists && !string.IsNullOrWhiteSpace(job.DoneMarkerPath) && File.Exists(job.DoneMarkerPath))
		{
			AppendLog(job.RunLogPath, $"SKIP {job.Name} {DateTimeOffset.Now:O}");
			return new ExportJobManifest(
				Name: job.Name,
				Exporter: options.Mode,
				InputPaths: job.InputPaths,
				OutputPath: job.OutputPath,
				Status: "skipped",
				StartedAt: DateTimeOffset.Now,
				FinishedAt: DateTimeOffset.Now,
				FileCount: CountFilesSafe(job.OutputPath),
				TotalBytes: SumFileSizesSafe(job.OutputPath),
				Selection: null,
				OutcomeSummary: new ExportJobOutcomeSummary([], [], [], []),
				RecursiveUnpack: null,
				RecursiveUnpackResults: [],
				SkippedCollections: [],
				FailedCollections: [],
				Inventory: null,
				ErrorMessage: null);
		}

		AppendLog(job.RunLogPath, $"START {job.Name} {DateTimeOffset.Now:O}");
		return ExportInto(options.Mode, options.Profile, job.InputPaths, job.OutputPath, options.RecursiveUnpack, job.CleanOutput, job.Name);
	}

	private void OnJobCompleted(PlannedExportJob plannedJob, ExportJobManifest result)
	{
		if (string.IsNullOrWhiteSpace(plannedJob.RunLogPath))
		{
			return;
		}

		if (string.Equals(result.Status, "success", StringComparison.Ordinal))
		{
			if (!string.IsNullOrWhiteSpace(plannedJob.DoneMarkerPath))
			{
				Directory.CreateDirectory(plannedJob.OutputPath);
				File.WriteAllText(plannedJob.DoneMarkerPath, string.Empty);
			}

			AppendLog(plannedJob.RunLogPath, $"DONE {plannedJob.Name} {DateTimeOffset.Now:O}");
		}
		else if (string.Equals(result.Status, "skipped", StringComparison.Ordinal))
		{
			AppendLog(plannedJob.RunLogPath, $"SKIP {plannedJob.Name} {DateTimeOffset.Now:O}");
		}
		else
		{
			AppendLog(plannedJob.RunLogPath, $"FAIL {plannedJob.Name} {DateTimeOffset.Now:O}");
		}
	}

	private ExportJobManifest ExportInto(string mode, string? profile, string[] inputPaths, string outputPath, bool recursiveUnpack, bool cleanOutput, string name)
	{
		DateTimeOffset startedAt = DateTimeOffset.Now;

		try
		{
			if (cleanOutput)
			{
				ResetOutputDirectory(outputPath);
			}
			else
			{
				Directory.CreateDirectory(outputPath);
			}

			FullConfiguration settings = LoadSettings();
			ExportHandler handler = new(settings);
			var gameData = handler.LoadAndProcess(inputPaths, fileSystem);
			InventorySummary inventory = InventorySummaryBuilder.Build(gameData, inputPaths);
			ExportSelectionSummary? selectionSummary = null;
			RecursiveUnpackExecutionResult? recursiveUnpackResult = null;
			PrimarySkippedCollection[] skippedCollections = [];
			PrimaryFailedCollection[] failedCollections = [];

			switch (mode)
			{
				case "primary":
					settings.ExportRootPath = outputPath;
					PrimaryExportStats primaryStats = CliPrimaryExporterFactory.Create(gameData, settings, recursiveUnpack)
						.ExportSelected(gameData.GameBundle, settings, fileSystem, ProfileSelection.CreatePredicate(profile));
					skippedCollections = primaryStats.SkippedCollections.ToArray();
					failedCollections = primaryStats.FailedCollectionDetails.ToArray();
					selectionSummary = new ExportSelectionSummary(
						TotalCollections: primaryStats.TotalCollections,
						SelectedCollections: primaryStats.SelectedCollections,
						SkippedCollections: primaryStats.SkippedBySelection,
						FailedCollections: primaryStats.FailedCollections,
						SkipReason: profile is null or "full-raw" ? null : "excluded-by-profile");
					if (recursiveUnpack)
					{
						recursiveUnpackResult = RecursiveBundleUnpacker.UnpackRecursively(outputPath, fileSystem);
					}
					break;
				case "dump":
					handler.Export(gameData, outputPath, fileSystem);
					if (recursiveUnpack)
					{
						recursiveUnpackResult = RecursiveBundleUnpacker.UnpackRecursively(outputPath, fileSystem);
					}
					break;
				default:
					RecursiveUnpackResultRecord[] defaultUnpackResults = recursiveUnpackResult?.Results ?? [];
					return new ExportJobManifest(
						Name: name,
						Exporter: mode,
						InputPaths: inputPaths,
						OutputPath: outputPath,
						Status: "failed",
						StartedAt: startedAt,
						FinishedAt: DateTimeOffset.Now,
						FileCount: CountFilesSafe(outputPath),
						TotalBytes: SumFileSizesSafe(outputPath),
						Selection: selectionSummary,
						OutcomeSummary: ExportOutcomeSummaryBuilder.BuildJobSummary(
							skippedCollections,
							failedCollections,
							defaultUnpackResults),
						RecursiveUnpack: recursiveUnpackResult?.Summary,
						RecursiveUnpackResults: defaultUnpackResults,
						SkippedCollections: skippedCollections,
						FailedCollections: failedCollections,
						Inventory: inventory,
						ErrorMessage: $"Unknown export mode '{mode}'. Expected 'primary' or 'dump'.");
			}

			RecursiveUnpackResultRecord[] recursiveUnpackResults = recursiveUnpackResult?.Results ?? [];
			ExportJobOutcomeSummary outcomeSummary = ExportOutcomeSummaryBuilder.BuildJobSummary(
				skippedCollections,
				failedCollections,
				recursiveUnpackResults);

			return new ExportJobManifest(
				Name: name,
				Exporter: mode,
				InputPaths: inputPaths,
				OutputPath: outputPath,
				Status: "success",
				StartedAt: startedAt,
				FinishedAt: DateTimeOffset.Now,
				FileCount: CountFilesSafe(outputPath),
				TotalBytes: SumFileSizesSafe(outputPath),
				Selection: selectionSummary,
				OutcomeSummary: outcomeSummary,
				RecursiveUnpack: recursiveUnpackResult?.Summary,
				RecursiveUnpackResults: recursiveUnpackResults,
				SkippedCollections: skippedCollections,
				FailedCollections: failedCollections,
				Inventory: inventory,
				ErrorMessage: null);
		}
		catch (Exception ex)
		{
			Logger.Error(LogCategory.Export, $"Failed job '{name}': {ex}");
			return new ExportJobManifest(
				Name: name,
				Exporter: mode,
				InputPaths: inputPaths,
				OutputPath: outputPath,
				Status: "failed",
				StartedAt: startedAt,
				FinishedAt: DateTimeOffset.Now,
				FileCount: CountFilesSafe(outputPath),
				TotalBytes: SumFileSizesSafe(outputPath),
				Selection: null,
				OutcomeSummary: new ExportJobOutcomeSummary([], [], [], []),
				RecursiveUnpack: null,
				RecursiveUnpackResults: [],
				SkippedCollections: [],
				FailedCollections: [],
				Inventory: null,
				ErrorMessage: ex.Message);
		}
	}

	private static FullConfiguration LoadSettings()
	{
		FullConfiguration settings = new();
		settings.LoadFromDefaultPath();
		return settings;
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

	private static void AppendLog(string? logPath, string message)
	{
		if (string.IsNullOrWhiteSpace(logPath))
		{
			return;
		}

		File.AppendAllText(logPath, message + Environment.NewLine);
		Logger.Info(LogCategory.Export, message);
	}

	private static ExportManifest CreateManifest(ExportCommandOptions options, ExportPlan plan, DateTimeOffset startedAt, ExportJobManifest[] jobs)
	{
		return new ExportManifest(
			ArtifactType: "export-manifest",
			StartedAt: startedAt,
			FinishedAt: DateTimeOffset.Now,
			Mode: options.Mode,
			Profile: options.Profile,
			InputPaths: options.InputPaths,
			OutputPath: options.OutputPath,
			RecursiveUnpack: options.RecursiveUnpack,
			ShardStrategy: plan.ShardStrategy,
			ShardDirectChildren: plan.ShardDirectChildren,
			ShardDecisionReason: plan.ShardDecisionReason,
			CleanOutput: options.CleanOutput,
			LoadWorkers: GetWorkerCount("ASSETRIPPER_LOAD_WORKERS"),
			ExportWorkers: GetWorkerCount("ASSETRIPPER_EXPORT_WORKERS"),
			UnpackWorkers: GetWorkerCount("ASSETRIPPER_UNPACK_WORKERS"),
			OutcomeSummary: ExportOutcomeSummaryBuilder.BuildRunSummary(jobs),
			Jobs: jobs);
	}

	private static void LogExecutionSettings(ExportCommandOptions options)
	{
		Logger.Info(
			LogCategory.Export,
			$"Execution mode={options.Mode} profile={options.Profile ?? "none"} shardStrategy={options.ShardStrategy} recursiveUnpack={options.RecursiveUnpack} cleanOutput={options.CleanOutput} workers(load={GetWorkerCount("ASSETRIPPER_LOAD_WORKERS")}, export={GetWorkerCount("ASSETRIPPER_EXPORT_WORKERS")}, unpack={GetWorkerCount("ASSETRIPPER_UNPACK_WORKERS")}, jobs={GetJobWorkerCount()})");
	}

	private static int GetJobWorkerCount()
	{
		return ParseWorker(Environment.GetEnvironmentVariable("ASSETRIPPER_JOB_WORKERS"))
			?? 1;
	}

	private static int GetWorkerCount(string variableName)
	{
		return ParseWorker(Environment.GetEnvironmentVariable(variableName))
			?? ParseWorker(Environment.GetEnvironmentVariable("ASSETRIPPER_WORKERS"))
			?? GetDefaultWorkerCount(variableName);
	}

	private static int? ParseWorker(string? value)
	{
		return int.TryParse(value, out int parsed) && parsed > 0 ? parsed : null;
	}

	private static int GetDefaultWorkerCount(string variableName)
	{
		int fallback = variableName switch
		{
			"ASSETRIPPER_LOAD_WORKERS" => 2,
			"ASSETRIPPER_EXPORT_WORKERS" => 4,
			"ASSETRIPPER_UNPACK_WORKERS" => 2,
			_ => 4,
		};

		return Math.Max(1, Math.Min(fallback, Environment.ProcessorCount));
	}

	private static int CountFilesSafe(string path)
	{
		try
		{
			return Directory.Exists(path) ? Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Count() : 0;
		}
		catch
		{
			return 0;
		}
	}

	private static long SumFileSizesSafe(string path)
	{
		try
			{
				return Directory.Exists(path)
					? Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
						.Select(file => new FileInfo(file).Length)
						.Sum()
					: 0L;
			}
		catch
		{
			return 0L;
		}
	}

	private static void WriteSkippedAssetsArtifact(ExportCommandOptions options, ExportManifest manifest)
	{
		List<SkippedAssetJobRecord> jobs = manifest.Jobs
			.Where(job => job.SkippedCollections.Length > 0)
			.Select(job => new SkippedAssetJobRecord(job.Name, job.OutputPath, job.SkippedCollections))
			.ToList();

		if (jobs.Count == 0)
		{
			return;
		}

		string artifactPath = Path.Combine(options.OutputPath, "skipped-assets.json");
		RunnerArtifacts.Write(
			artifactPath,
			new SkippedAssetsArtifact(
				ArtifactType: "skipped-assets",
				CreatedAt: DateTimeOffset.Now,
				Mode: options.Mode,
				Profile: options.Profile,
				OutputPath: options.OutputPath,
				Jobs: jobs.ToArray()));
		Logger.Info(LogCategory.Export, $"Skipped assets written to {artifactPath}");
	}

	private static void WriteFailedAssetsArtifact(ExportCommandOptions options, ExportManifest manifest)
	{
		List<FailedAssetJobRecord> jobs = manifest.Jobs
			.Where(job => job.FailedCollections.Length > 0)
			.Select(job => new FailedAssetJobRecord(job.Name, job.OutputPath, job.FailedCollections))
			.ToList();

		if (jobs.Count == 0)
		{
			return;
		}

		string artifactPath = Path.Combine(options.OutputPath, "failed-assets.json");
		RunnerArtifacts.Write(
			artifactPath,
			new FailedAssetsArtifact(
				ArtifactType: "failed-assets",
				CreatedAt: DateTimeOffset.Now,
				Mode: options.Mode,
				Profile: options.Profile,
				OutputPath: options.OutputPath,
				Jobs: jobs.ToArray()));
		Logger.Info(LogCategory.Export, $"Failed assets written to {artifactPath}");
	}

	private static void WriteRecursiveUnpackArtifact(ExportCommandOptions options, ExportManifest manifest)
	{
		List<RecursiveUnpackJobRecord> jobs = manifest.Jobs
			.Where(job => job.RecursiveUnpack is not null)
			.Select(job => new RecursiveUnpackJobRecord(
				job.Name,
				job.OutputPath,
				job.RecursiveUnpack!,
				job.RecursiveUnpackResults))
			.ToList();

		if (jobs.Count == 0)
		{
			return;
		}

		string artifactPath = Path.Combine(options.OutputPath, "recursive-unpack.json");
		RunnerArtifacts.Write(
			artifactPath,
			new RecursiveUnpackArtifact(
				ArtifactType: "recursive-unpack",
				CreatedAt: DateTimeOffset.Now,
				Mode: options.Mode,
				Profile: options.Profile,
				OutputPath: options.OutputPath,
				Jobs: jobs.ToArray()));
		Logger.Info(LogCategory.Export, $"Recursive unpack artifact written to {artifactPath}");
	}

	private static void WriteSummary(ExportCommandOptions options, ExportManifest manifest)
	{
		int successCount = manifest.Jobs.Count(job => string.Equals(job.Status, "success", StringComparison.Ordinal));
		int skippedCount = manifest.Jobs.Count(job => string.Equals(job.Status, "skipped", StringComparison.Ordinal));
		int failedCount = manifest.Jobs.Count(job => string.Equals(job.Status, "failed", StringComparison.Ordinal));
		int fileCount = manifest.Jobs.Sum(job => job.FileCount);
		long totalBytes = manifest.Jobs.Sum(job => job.TotalBytes);

		List<string> lines =
		[
			$"mode={manifest.Mode}",
			$"profile={manifest.Profile ?? "none"}",
			$"output={manifest.OutputPath}",
			$"started={manifest.StartedAt:O}",
			$"finished={manifest.FinishedAt:O}",
			$"recursive_unpack={manifest.RecursiveUnpack}",
			$"shard_strategy={manifest.ShardStrategy}",
			$"shard_direct_children={manifest.ShardDirectChildren}",
			$"shard_decision_reason={manifest.ShardDecisionReason ?? "none"}",
			$"workers.load={manifest.LoadWorkers}",
			$"workers.export={manifest.ExportWorkers}",
			$"workers.unpack={manifest.UnpackWorkers}",
			$"jobs.total={manifest.Jobs.Length}",
			$"jobs.success={successCount}",
			$"jobs.skipped={skippedCount}",
			$"jobs.failed={failedCount}",
			$"selection.selected={manifest.OutcomeSummary.SelectedCollectionCount}",
			$"selection.skipped={manifest.OutcomeSummary.SelectionSkippedCollectionCount}",
			$"selection.failed={manifest.OutcomeSummary.ExportFailedCollectionCount}",
			$"unpack.candidates={manifest.OutcomeSummary.RecursiveUnpackCandidateCount}",
			$"unpack.attempted={manifest.OutcomeSummary.RecursiveUnpackAttemptedCount}",
			$"unpack.unpacked={manifest.OutcomeSummary.RecursiveUnpackUnpackedCount}",
			$"unpack.retained={manifest.OutcomeSummary.RecursiveUnpackRetainedCount}",
			$"unpack.failed={manifest.OutcomeSummary.RecursiveUnpackFailedCount}",
			$"files.total={fileCount}",
			$"bytes.total={totalBytes}",
		];

		string summaryPath = Path.Combine(options.OutputPath, "summary.txt");
		File.WriteAllLines(summaryPath, lines);
		Logger.Info(LogCategory.Export, $"Summary written to {summaryPath}");
	}
}
