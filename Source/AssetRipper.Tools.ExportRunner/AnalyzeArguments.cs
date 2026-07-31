using Ookii.CommandLine;
using System.ComponentModel;

namespace AssetRipper.Tools.ExportRunner;

[GeneratedParser]
[ParseOptions(IsPosix = true)]
internal sealed partial class AnalyzeArguments
{
	[CommandLineArgument(IsPositional = true)]
	[Description("The input paths to analyze.")]
	public string[]? InputPaths { get; set; }

	[CommandLineArgument("report", ShortName = 'r', DefaultValue = null)]
	[Description("Optional path to write the structured analysis report as JSON.")]
	public string? ReportPath { get; set; }
}
