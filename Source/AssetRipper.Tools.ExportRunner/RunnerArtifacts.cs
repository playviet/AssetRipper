using AssetRipper.Export.PrimaryContent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AssetRipper.Tools.ExportRunner;

internal static class RunnerArtifacts
{
	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		WriteIndented = true,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
	};

	public static void Write<T>(string path, T artifact)
	{
		string? directory = Path.GetDirectoryName(path);
		if (!string.IsNullOrWhiteSpace(directory))
		{
			Directory.CreateDirectory(directory);
		}

		File.WriteAllText(path, SerializeArtifact(artifact));
	}

	public static JsonDocument Read(string path)
	{
		return JsonDocument.Parse(File.ReadAllText(path));
	}

	private static string SerializeArtifact<T>(T artifact)
	{
		return artifact switch
		{
			InventorySummary value => JsonSerializer.Serialize(value, RunnerArtifactsJsonContext.Default.InventorySummary),
			ExportPlan value => JsonSerializer.Serialize(value, RunnerArtifactsJsonContext.Default.ExportPlan),
			ExportManifest value => JsonSerializer.Serialize(value, RunnerArtifactsJsonContext.Default.ExportManifest),
			RecursiveUnpackArtifact value => JsonSerializer.Serialize(value, RunnerArtifactsJsonContext.Default.RecursiveUnpackArtifact),
			SkippedAssetsArtifact value => JsonSerializer.Serialize(value, RunnerArtifactsJsonContext.Default.SkippedAssetsArtifact),
			FailedAssetsArtifact value => JsonSerializer.Serialize(value, RunnerArtifactsJsonContext.Default.FailedAssetsArtifact),
			_ => throw new NotSupportedException($"Unsupported artifact type '{typeof(T).FullName}'."),
		};
	}
}

internal sealed record KeyCount(string Key, int Count);

internal sealed record InventorySummary(
	string ArtifactType,
	DateTimeOffset CreatedAt,
	string[] InputPaths,
	string ProjectVersion,
	int AssetCollectionCount,
	int AssetCount,
	int ResourceFileCount,
	int DistinctOutputBucketCount,
	int AssetsWithBestDirectoryCount,
	string PathSemantics,
	string[] SuggestedProfiles,
	ProfileEvidence[] ProfileEvidence,
	KeyCount[] TopAssetClasses,
	KeyCount[] TopOutputBuckets);

internal sealed record ProfileEvidence(
	string Profile,
	int MatchedAssets,
	int StrongMatches,
	KeyCount[] TopBuckets,
	KeyCount[] TopSignals);

internal sealed record ExportPlan(
	string ArtifactType,
	DateTimeOffset CreatedAt,
	string Mode,
	string? Profile,
	string[] InputPaths,
	string OutputPath,
	bool RecursiveUnpack,
	string ShardStrategy,
	bool ShardDirectChildren,
	string? ShardDecisionReason,
	bool CleanOutput,
	int JobWorkers,
	PlannedExportJob[] Jobs);

internal sealed record PlannedExportJob(
	string Name,
	string[] InputPaths,
	string OutputPath,
	bool CleanOutput,
	bool SkipIfDoneMarkerExists,
	string? DoneMarkerPath,
	string? RunLogPath);

internal sealed record ExportManifest(
	string ArtifactType,
	DateTimeOffset StartedAt,
	DateTimeOffset FinishedAt,
	string Mode,
	string? Profile,
	string[] InputPaths,
	string OutputPath,
	bool RecursiveUnpack,
	string ShardStrategy,
	bool ShardDirectChildren,
	string? ShardDecisionReason,
	bool CleanOutput,
	int LoadWorkers,
	int ExportWorkers,
	int UnpackWorkers,
	ExportRunOutcomeSummary OutcomeSummary,
	ExportJobManifest[] Jobs);

internal sealed record ExportJobManifest(
	string Name,
	string Exporter,
	string[] InputPaths,
	string OutputPath,
	string Status,
	DateTimeOffset StartedAt,
	DateTimeOffset FinishedAt,
	int FileCount,
	long TotalBytes,
	ExportSelectionSummary? Selection,
	ExportJobOutcomeSummary OutcomeSummary,
	RecursiveUnpackSummary? RecursiveUnpack,
	RecursiveUnpackResultRecord[] RecursiveUnpackResults,
	PrimarySkippedCollection[] SkippedCollections,
	PrimaryFailedCollection[] FailedCollections,
	InventorySummary? Inventory,
	string? ErrorMessage);

internal sealed record RecursiveUnpackSummary(
	int WorkerCount,
	int CandidateFileCount,
	int AttemptedFileCount,
	int UnpackedFileCount,
	int RetainedFileCount,
	int FailedFileCount);

internal sealed record RecursiveUnpackArtifact(
	string ArtifactType,
	DateTimeOffset CreatedAt,
	string Mode,
	string? Profile,
	string OutputPath,
	RecursiveUnpackJobRecord[] Jobs);

internal sealed record RecursiveUnpackJobRecord(
	string Name,
	string OutputPath,
	RecursiveUnpackSummary Summary,
	RecursiveUnpackResultRecord[] Results);

internal sealed record RecursiveUnpackResultRecord(
	string Path,
	string Status,
	string? OutputDirectory,
	string? Reason);

internal sealed record ExportRunOutcomeSummary(
	int JobCount,
	int SuccessCount,
	int SkippedCount,
	int FailedCount,
	int SelectedCollectionCount,
	int SelectionSkippedCollectionCount,
	int ExportFailedCollectionCount,
	int RecursiveUnpackCandidateCount,
	int RecursiveUnpackAttemptedCount,
	int RecursiveUnpackUnpackedCount,
	int RecursiveUnpackRetainedCount,
	int RecursiveUnpackFailedCount);

internal sealed record ExportJobOutcomeSummary(
	OutcomeReasonCount[] SelectionSkipReasons,
	OutcomeReasonCount[] ExportFailureReasons,
	OutcomeReasonCount[] RecursiveUnpackStatuses,
	OutcomeReasonCount[] RecursiveUnpackReasons);

internal sealed record OutcomeReasonCount(
	string Key,
	int Count);

internal sealed record ExportSelectionSummary(
	int TotalCollections,
	int SelectedCollections,
	int SkippedCollections,
	int FailedCollections,
	string? SkipReason);

internal sealed record SkippedAssetsArtifact(
	string ArtifactType,
	DateTimeOffset CreatedAt,
	string Mode,
	string? Profile,
	string OutputPath,
	SkippedAssetJobRecord[] Jobs);

internal sealed record SkippedAssetJobRecord(
	string Name,
	string OutputPath,
	PrimarySkippedCollection[] SkippedCollections);

internal sealed record FailedAssetsArtifact(
	string ArtifactType,
	DateTimeOffset CreatedAt,
	string Mode,
	string? Profile,
	string OutputPath,
	FailedAssetJobRecord[] Jobs);

internal sealed record FailedAssetJobRecord(
	string Name,
	string OutputPath,
	PrimaryFailedCollection[] FailedCollections);

[JsonSourceGenerationOptions(
	WriteIndented = true,
	DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
	PropertyNamingPolicy = JsonKnownNamingPolicy.Unspecified)]
[JsonSerializable(typeof(InventorySummary))]
[JsonSerializable(typeof(ProfileEvidence))]
[JsonSerializable(typeof(ExportPlan))]
[JsonSerializable(typeof(PlannedExportJob))]
[JsonSerializable(typeof(ExportManifest))]
[JsonSerializable(typeof(ExportRunOutcomeSummary))]
[JsonSerializable(typeof(RecursiveUnpackArtifact))]
[JsonSerializable(typeof(RecursiveUnpackJobRecord))]
[JsonSerializable(typeof(RecursiveUnpackResultRecord))]
[JsonSerializable(typeof(RecursiveUnpackSummary))]
[JsonSerializable(typeof(SkippedAssetsArtifact))]
[JsonSerializable(typeof(FailedAssetsArtifact))]
[JsonSerializable(typeof(KeyCount))]
[JsonSerializable(typeof(ExportJobManifest))]
[JsonSerializable(typeof(ExportJobOutcomeSummary))]
[JsonSerializable(typeof(OutcomeReasonCount))]
[JsonSerializable(typeof(ExportSelectionSummary))]
[JsonSerializable(typeof(SkippedAssetJobRecord))]
[JsonSerializable(typeof(FailedAssetJobRecord))]
[JsonSerializable(typeof(PrimarySkippedCollection))]
[JsonSerializable(typeof(PrimaryFailedCollection))]
internal sealed partial class RunnerArtifactsJsonContext : JsonSerializerContext
{
}
