using AssetRipper.IO.Endian;

namespace AssetRipper.Export.Modules.Shaders.Processor;

/// <summary>
/// An unordered access view binding of the compiled shader blob.
/// </summary>
public sealed class UAVParameter
{
	public string Name { get; set; } = "";
	public int Index { get; set; }
	public int OriginalIndex { get; set; }

	public static UAVParameter Read(ref EndianSpanReader reader, string name)
	{
		return new UAVParameter
		{
			Name = name,
			Index = reader.ReadInt32(),
			OriginalIndex = reader.ReadInt32(),
		};
	}
}
