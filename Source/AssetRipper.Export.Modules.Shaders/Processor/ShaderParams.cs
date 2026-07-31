using AssetRipper.IO.Endian;
using AssetRipper.Primitives;

namespace AssetRipper.Export.Modules.Shaders.Processor;

/// <summary>
/// The reflection data of a compiled shader blob: its constant buffers and its resource bindings.
/// </summary>
public sealed class ShaderParams
{
	public ConstantBuffer? BaseConstantBuffer { get; set; }
	public List<ConstantBuffer> ConstantBuffers { get; set; } = [];
	public List<TextureParameter> TextureParameters { get; set; } = [];
	public List<BufferBinding> ConstBindings { get; set; } = [];
	public List<BufferBinding> Buffers { get; set; } = [];
	public List<UAVParameter> UAVs { get; set; } = [];
	public List<SamplerParameter> Samplers { get; set; } = [];

	public static ShaderParams Read(ref EndianSpanReader reader, UnityVersion version, bool readBlobVersion)
	{
		ShaderParams result = new();

		if (readBlobVersion)
		{
			_ = reader.ReadInt32();
		}

		int firstParamsCount = reader.ReadInt32();
		if (firstParamsCount > 0)
		{
			result.BaseConstantBuffer = ConstantBuffer.Read(ref reader, version);
			result.ConstantBuffers = new List<ConstantBuffer>(firstParamsCount - 1);
			for (int i = 1; i < firstParamsCount; i++)
			{
				result.ConstantBuffers.Add(ConstantBuffer.Read(ref reader, version));
			}
		}
		else
		{
			result.ConstantBuffers = [];
		}

		int secondParamsCount = reader.ReadInt32();
		for (int i = 0; i < secondParamsCount; i++)
		{
			string name = reader.ReadAlignedCountString();
			int type = reader.ReadInt32();

			switch (type)
			{
				case 0:
					result.TextureParameters.Add(TextureParameter.Read(ref reader, version, name));
					break;
				case 1:
					result.ConstBindings.Add(BufferBinding.Read(ref reader, name));
					break;
				case 2:
					result.Buffers.Add(BufferBinding.Read(ref reader, name));
					break;
				case 3:
					result.UAVs.Add(UAVParameter.Read(ref reader, name));
					break;
				case 4:
					result.Samplers.Add(SamplerParameter.Read(ref reader));
					break;
			}
		}

		return result;
	}

	public void CombineCommon(SerializedProgramInfo progInfo)
	{
		List<ConstantBuffer> commonCBuffers = progInfo.CommonCBuffers;
		List<BufferBinding> commonConstBindings = progInfo.CommonCBBindings;

		foreach (ConstantBuffer commonCBuf in commonCBuffers)
		{
			if (commonCBuf.Partial)
			{
				ConstantBuffer? insertInto = ConstantBuffers.FirstOrDefault(c => c.Name == commonCBuf.Name);
				insertInto?.CBParams.AddRange(commonCBuf.CBParams);
			}
		}

		ConstBindings.AddRange(commonConstBindings);

		TextureParameters.AddRange(progInfo.CommonTextureParameters);
	}
}
