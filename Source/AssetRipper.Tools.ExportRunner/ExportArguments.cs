using Ookii.CommandLine;
using System.ComponentModel;

namespace AssetRipper.Tools.ExportRunner;

[GeneratedParser]
[ParseOptions(IsPosix = true)]
internal sealed partial class ExportArguments
{
	[CommandLineArgument("mode", DefaultValue = null)]
	[Description("The export backend mode. Supported modes: primary, dump.")]
	public string? Mode { get; set; }

	[CommandLineArgument(IsPositional = true)]
	[Description("The input paths to export.")]
	public string[]? InputPaths { get; set; }

	[CommandLineArgument("profile", DefaultValue = null)]
	[Description("The export profile. Supported profiles: player-art, characters, ui, audio, narrative, cg, backgrounds, sprites, full-project, full-raw.")]
	public string? Profile { get; set; }

	[CommandLineArgument("output", DefaultValue = null, ShortName = 'o')]
	[Description("The output path for export commands.")]
	public string? OutputPath { get; set; }

	[CommandLineArgument("keep-output", DefaultValue = false)]
	[Description("Keep existing output instead of cleaning the output directory before export.")]
	public bool KeepOutput { get; set; }

	[CommandLineArgument("recursive-unpack", DefaultValue = RecursiveUnpackMode.On)]
	[Description("Recursive unpack policy for Unity-readable nested bundle payloads. Supported values: on, off.")]
	public RecursiveUnpackMode RecursiveUnpack { get; set; } = RecursiveUnpackMode.On;

	[CommandLineArgument("shard-direct-children", DefaultValue = false)]
	[Description("Compatibility shorthand for --shard-strategy direct-children.")]
	public bool ShardDirectChildren { get; set; }

	[CommandLineArgument("shard-strategy", DefaultValue = ShardStrategyMode.Off)]
	[Description("Shard planning policy. Supported values: off, direct-children, auto.")]
	public ShardStrategyMode ShardStrategy { get; set; } = ShardStrategyMode.Off;
}

internal enum RecursiveUnpackMode
{
	On,
	Off,
}

internal enum ShardStrategyMode
{
	Off,
	DirectChildren,
	Auto,
}
