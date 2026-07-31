using AssetRipper.IO.Endian;

namespace AssetRipper.Export.Modules.Shaders.Processor;

/// <summary>
/// The set of vertex input channel bindings of a sub program in the compiled shader blob.
/// </summary>
public sealed class ParserBindChannels
{
	public int SourceMap { get; set; }
	public ShaderBindChannel[] Channels { get; set; } = [];

	public static ParserBindChannels Read(ref EndianSpanReader reader)
	{
		ParserBindChannels result = new();

		result.SourceMap = reader.ReadInt32();

		int bindCount = reader.ReadInt32();
		result.Channels = new ShaderBindChannel[bindCount];
		for (int i = 0; i < bindCount; i++)
		{
			ShaderBindChannel channel = ShaderBindChannel.Read(ref reader);
			result.Channels[i] = channel;
			result.SourceMap |= 1 << channel.Source;
		}

		return result;
	}
}
