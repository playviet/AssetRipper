using AssetRipper.IO.Endian;

namespace AssetRipper.Export.Modules.Shaders.Processor;

/// <summary>
/// A sampler state binding of the compiled shader blob.
/// </summary>
public sealed class SamplerParameter
{
	public uint Sampler { get; set; }
	public int BindPoint { get; set; }

	public static SamplerParameter Read(ref EndianSpanReader reader)
	{
		return new SamplerParameter
		{
			BindPoint = reader.ReadInt32(),
			Sampler = reader.ReadUInt32(),
		};
	}
}
