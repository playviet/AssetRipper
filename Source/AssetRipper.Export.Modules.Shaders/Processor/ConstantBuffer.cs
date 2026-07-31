using AssetRipper.IO.Endian;
using AssetRipper.Primitives;

namespace AssetRipper.Export.Modules.Shaders.Processor;

/// <summary>
/// A constant buffer declaration of the compiled shader blob.
/// </summary>
public sealed class ConstantBuffer
{
	public string Name { get; set; } = "";
	public int UsedSize { get; set; }
	public bool Partial { get; set; }
	public List<ConstantBufferParameter> CBParams { get; set; } = [];
	public List<StructParameter> StructParams { get; set; } = [];

	public static ConstantBuffer Read(ref EndianSpanReader reader, UnityVersion version)
	{
		ConstantBuffer result = new();

		result.Name = reader.ReadAlignedCountString();
		result.UsedSize = reader.ReadInt32();
		result.Partial = false;

		int paramCount = reader.ReadInt32();
		result.CBParams = new List<ConstantBufferParameter>(paramCount);
		for (int i = 0; i < paramCount; i++)
		{
			result.CBParams.Add(ConstantBufferParameter.Read(ref reader));
		}

		bool hasStructParams = version.GreaterThanOrEquals(2017, 3);
		if (hasStructParams)
		{
			int structCount = reader.ReadInt32();
			result.StructParams = new List<StructParameter>(structCount);
			for (int i = 0; i < structCount; i++)
			{
				result.StructParams.Add(StructParameter.Read(ref reader));
			}
		}
		else
		{
			result.StructParams = [];
		}

		return result;
	}
}
