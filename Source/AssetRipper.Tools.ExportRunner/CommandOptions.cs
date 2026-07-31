namespace AssetRipper.Tools.ExportRunner;

internal sealed record InspectCommandOptions(string[] InputPaths);
internal sealed record AnalyzeCommandOptions(string[] InputPaths, string? ReportPath);
internal sealed record ReportCommandOptions(string ArtifactPath);

internal sealed record ExportCommandOptions(
	string Mode,
	string? Profile,
	string[] InputPaths,
	string OutputPath,
	bool CleanOutput,
	bool RecursiveUnpack,
	ShardStrategyMode ShardStrategy);
