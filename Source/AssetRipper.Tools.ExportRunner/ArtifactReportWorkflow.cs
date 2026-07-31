using System.Text.Json;

namespace AssetRipper.Tools.ExportRunner;

internal static class ArtifactReportWorkflow
{
	public static int Run(string artifactPath)
	{
		if (!File.Exists(artifactPath))
		{
			Console.WriteLine($"Artifact not found: {artifactPath}");
			return 1;
		}

		using JsonDocument document = RunnerArtifacts.Read(artifactPath);
		if (!document.RootElement.TryGetProperty("ArtifactType", out JsonElement artifactTypeElement))
		{
			Console.WriteLine($"Unsupported artifact file: {artifactPath}");
			return 1;
		}

		string? artifactType = artifactTypeElement.GetString();
		switch (artifactType)
		{
			case "inventory-summary":
			{
				InventorySummary? summary = document.RootElement.Deserialize(RunnerArtifactsJsonContext.Default.InventorySummary);
				if (summary is null)
				{
					Console.WriteLine($"Failed to read inventory summary: {artifactPath}");
					return 1;
				}

				Program.PrintInventorySummary(summary);
				return 0;
			}
			case "export-plan":
			{
				ExportPlan? plan = document.RootElement.Deserialize(RunnerArtifactsJsonContext.Default.ExportPlan);
				if (plan is null)
				{
					Console.WriteLine($"Failed to read export plan: {artifactPath}");
					return 1;
				}

				Program.PrintExportPlan(plan);
				return 0;
			}
			case "export-manifest":
			{
				ExportManifest? manifest = document.RootElement.Deserialize(RunnerArtifactsJsonContext.Default.ExportManifest);
				if (manifest is null)
				{
					Console.WriteLine($"Failed to read export manifest: {artifactPath}");
					return 1;
				}

				Program.PrintExportManifest(manifest);
				return 0;
			}
			case "recursive-unpack":
			{
				RecursiveUnpackArtifact? artifact = document.RootElement.Deserialize(RunnerArtifactsJsonContext.Default.RecursiveUnpackArtifact);
				if (artifact is null)
				{
					Console.WriteLine($"Failed to read recursive-unpack artifact: {artifactPath}");
					return 1;
				}

				Program.PrintRecursiveUnpackArtifact(artifact);
				return 0;
			}
			case "skipped-assets":
			{
				SkippedAssetsArtifact? artifact = document.RootElement.Deserialize(RunnerArtifactsJsonContext.Default.SkippedAssetsArtifact);
				if (artifact is null)
				{
					Console.WriteLine($"Failed to read skipped-assets artifact: {artifactPath}");
					return 1;
				}

				Program.PrintSkippedAssetsArtifact(artifact);
				return 0;
			}
			case "failed-assets":
			{
				FailedAssetsArtifact? artifact = document.RootElement.Deserialize(RunnerArtifactsJsonContext.Default.FailedAssetsArtifact);
				if (artifact is null)
				{
					Console.WriteLine($"Failed to read failed-assets artifact: {artifactPath}");
					return 1;
				}

				Program.PrintFailedAssetsArtifact(artifact);
				return 0;
			}
			default:
				Console.WriteLine($"Unsupported artifact type '{artifactType}'.");
				return 1;
		}
	}
}
