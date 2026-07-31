using AssetRipper.IO.Endian;

namespace AssetRipper.Export.Modules.Shaders.Processor;

/// <summary>
/// A single scalar, vector or matrix entry inside a constant buffer of the compiled shader blob.
/// </summary>
public sealed class ConstantBufferParameter
{
	public string ParamName { get; set; } = "";
	public ShaderParamType ParamType { get; set; }
	public int Rows { get; set; }
	public int Columns { get; set; }
	public bool IsMatrix { get; set; }
	public int ArraySize { get; set; }
	public int Index { get; set; }

	public static ConstantBufferParameter Read(ref EndianSpanReader reader, string structName = "")
	{
		ConstantBufferParameter result = new();

		string name = reader.ReadAlignedCountString();
		result.ParamName = structName.Length > 0 ? $"{structName}.{name}" : name;

		result.ParamType = (ShaderParamType)reader.ReadInt32();
		result.Rows = reader.ReadInt32();
		result.Columns = reader.ReadInt32();
		result.IsMatrix = reader.ReadInt32() > 0;
		result.ArraySize = reader.ReadInt32();
		result.Index = reader.ReadInt32();

		return result;
	}
}
