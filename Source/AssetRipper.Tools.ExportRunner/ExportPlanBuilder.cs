using AssetRipper.IO.Files;

namespace AssetRipper.Tools.ExportRunner;

internal static class ExportPlanBuilder
{
	public static ExportPlan Build(ExportCommandOptions options, FileSystem fileSystem, int jobWorkers)
	{
		ArgumentNullException.ThrowIfNull(options);

		ShardPlan shardPlan = ShardPlanner.Resolve(options, fileSystem);

		return new ExportPlan(
			ArtifactType: "export-plan",
			CreatedAt: DateTimeOffset.Now,
			Mode: options.Mode,
			Profile: options.Profile,
			InputPaths: options.InputPaths,
			OutputPath: options.OutputPath,
			RecursiveUnpack: options.RecursiveUnpack,
			ShardStrategy: shardPlan.StrategyToken,
			ShardDirectChildren: shardPlan.IsSharded,
			ShardDecisionReason: shardPlan.DecisionReason,
			CleanOutput: options.CleanOutput,
			JobWorkers: jobWorkers,
			Jobs: shardPlan.Jobs);
	}
}
