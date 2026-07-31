using Ookii.CommandLine;
using System.ComponentModel;

namespace AssetRipper.Tools.ExportRunner;

[GeneratedParser]
[ParseOptions(IsPosix = true)]
internal sealed partial class ReportArguments
{
	[CommandLineArgument(IsPositional = true)]
	[Description("Path to a JSON artifact produced by analyze or export.")]
	public string? ArtifactPath { get; set; }
}
