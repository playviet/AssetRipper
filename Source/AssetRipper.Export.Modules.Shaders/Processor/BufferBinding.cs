using AssetRipper.IO.Endian;

namespace AssetRipper.Export.Modules.Shaders.Processor;

/// <summary>
/// A constant buffer or shader buffer binding slot of the compiled shader blob.
/// </summary>
public sealed class BufferBinding
{
	public string Name { get; set; } = "";
	public int Index { get; set; }
	public int ArraySize { get; set; }

	public static BufferBinding Read(ref EndianSpanReader reader, string name)
	{
		return new BufferBinding
		{
			Name = name,
			Index = reader.ReadInt32(),
			ArraySize = reader.ReadInt32(),
		};
	}
}
