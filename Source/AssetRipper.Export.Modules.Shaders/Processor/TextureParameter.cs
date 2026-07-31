using AssetRipper.IO.Endian;
using AssetRipper.Primitives;

namespace AssetRipper.Export.Modules.Shaders.Processor;

/// <summary>
/// A texture binding of the compiled shader blob.
/// </summary>
public sealed class TextureParameter
{
	public string Name { get; set; } = "";
	public int Index { get; set; }
	public int SamplerIndex { get; set; }
	public bool MultiSampled { get; set; }
	public byte Dim { get; set; }

	public static TextureParameter Read(ref EndianSpanReader reader, UnityVersion version, string name)
	{
		TextureParameter result = new();

		int index = reader.ReadInt32();
		int extraValue = reader.ReadInt32();

		result.Name = name;
		result.Index = index;

		bool hasNewTextureParams = version.GreaterThanOrEquals(2018, 2);
		bool hasMultiSampled = version.GreaterThanOrEquals(2017, 3);
		if (hasNewTextureParams)
		{
			uint textureExtraValue = reader.ReadUInt32();
			result.MultiSampled = (textureExtraValue & 1) == 1;
			result.Dim = unchecked((byte)(textureExtraValue >> 1));
			result.SamplerIndex = extraValue;
		}
		else if (hasMultiSampled)
		{
			uint textureExtraValue = reader.ReadUInt32();
			result.MultiSampled = textureExtraValue == 1;
			result.Dim = unchecked((byte)extraValue);
			result.SamplerIndex = extraValue >> 8;
			if (result.SamplerIndex == 0xFFFFFF)
			{
				result.SamplerIndex = -1;
			}
		}
		else
		{
			result.MultiSampled = false;
			result.Dim = unchecked((byte)extraValue);
			result.SamplerIndex = extraValue >> 8;
			if (result.SamplerIndex == 0xFFFFFF)
			{
				result.SamplerIndex = -1;
			}
		}

		return result;
	}
}
