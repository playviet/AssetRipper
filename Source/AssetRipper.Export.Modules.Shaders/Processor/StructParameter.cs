using AssetRipper.IO.Endian;

namespace AssetRipper.Export.Modules.Shaders.Processor;

/// <summary>
/// A struct declared inside a constant buffer of the compiled shader blob, along with its members.
/// </summary>
public sealed class StructParameter
{
	public string Name { get; set; } = "";
	public int Index { get; set; }
	public int ArraySize { get; set; }
	public int Size { get; set; }
	public List<ConstantBufferParameter> CBParams { get; set; } = [];

	public static StructParameter Read(ref EndianSpanReader reader)
	{
		StructParameter result = new();

		result.Name = reader.ReadAlignedCountString();
		result.Index = reader.ReadInt32();
		result.ArraySize = reader.ReadInt32();
		result.Size = reader.ReadInt32();

		int paramCount = reader.ReadInt32();
		result.CBParams = new List<ConstantBufferParameter>(paramCount);
		for (int i = 0; i < paramCount; i++)
		{
			result.CBParams.Add(ConstantBufferParameter.Read(ref reader, result.Name));
		}

		return result;
	}
}
