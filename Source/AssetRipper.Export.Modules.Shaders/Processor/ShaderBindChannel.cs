using AssetRipper.IO.Endian;

namespace AssetRipper.Export.Modules.Shaders.Processor;

/// <summary>
/// A mapping from a vertex input source slot to a vertex component in the compiled shader blob.
/// </summary>
public sealed class ShaderBindChannel
{
	public int Source { get; set; }
	public VertexComponent Target { get; set; }

	public static ShaderBindChannel Read(ref EndianSpanReader reader)
	{
		return new ShaderBindChannel
		{
			Source = reader.ReadInt32(),
			Target = (VertexComponent)reader.ReadInt32(),
		};
	}
}
