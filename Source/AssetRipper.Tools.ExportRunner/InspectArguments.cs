using Ookii.CommandLine;
using System.ComponentModel;

namespace AssetRipper.Tools.ExportRunner;

[GeneratedParser]
[ParseOptions(IsPosix = true)]
internal sealed partial class InspectArguments
{
	[CommandLineArgument(IsPositional = true)]
	[Description("The input paths to inspect.")]
	public string[]? InputPaths { get; set; }
}
