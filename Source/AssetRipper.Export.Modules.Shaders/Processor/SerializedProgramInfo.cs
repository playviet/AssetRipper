using AssetRipper.Assets.Generics;
using AssetRipper.SourceGenerated.Subclasses.BufferBindingParameter;
using AssetRipper.SourceGenerated.Subclasses.ConstantBuffer;
using AssetRipper.SourceGenerated.Subclasses.MatrixParameter;
using AssetRipper.SourceGenerated.Subclasses.SerializedPlayerSubProgram;
using AssetRipper.SourceGenerated.Subclasses.SerializedProgram;
using AssetRipper.SourceGenerated.Subclasses.SerializedProgramParameters;
using AssetRipper.SourceGenerated.Subclasses.SerializedSubProgram;
using AssetRipper.SourceGenerated.Subclasses.TextureParameter;
using AssetRipper.SourceGenerated.Subclasses.VectorParameter;

namespace AssetRipper.Export.Modules.Shaders.Processor;

/// <summary>
/// One shader stage (vertex, fragment, ...) of a pass: its compiled variants plus the reflection data
/// that Unity factored out of the individual blob entries and stored on the asset instead.
/// </summary>
public sealed class SerializedProgramInfo
{
	public List<uint> ParameterBlobIndices { get; set; } = [];
	public List<SerializedSubProgramInfo> SubProgramInfos { get; set; } = [];
	public List<TextureParameter> CommonTextureParameters { get; set; } = [];
	public List<ConstantBuffer> CommonCBuffers { get; set; } = [];
	public List<BufferBinding> CommonCBBindings { get; set; } = [];

	/// <param name="program">The <c>progVertex</c>/<c>progFragment</c>/... field of a serialized pass.</param>
	/// <param name="nameTable">
	/// The pass' <c>m_NameIndices</c> map, inverted to index to name. Everything reflection related on the
	/// asset side refers to names by index into this table rather than storing the strings inline.
	/// </param>
	public SerializedProgramInfo(ISerializedProgram program, IReadOnlyDictionary<int, string> nameTable)
	{
		if (program.Has_PlayerSubPrograms())
		{
			// 2021.2 and later. Both lists are nested one level deeper: the outer list is indexed by hardware
			// tier, and the last tier is the most capable one, so that is the one worth decompiling.
			ParameterBlobIndices = GetLast(program.ParameterBlobIndices) is { } blobIndices
				? [.. blobIndices]
				: [];

			SubProgramInfos = GetLast(program.PlayerSubPrograms) is { } playerSubPrograms
				? [.. playerSubPrograms.Select(static p => new SerializedSubProgramInfo(p))]
				: [];
		}
		else
		{
			ParameterBlobIndices = [];
			SubProgramInfos = [.. program.SubPrograms.Select(static p => new SerializedSubProgramInfo(p))];
		}

		if (program.Has_CommonParameters())
		{
			ISerializedProgramParameters commonParameters = program.CommonParameters;
			CommonTextureParameters = GetCommonTextureParams(commonParameters.TextureParams, nameTable);
			CommonCBuffers = GetCommonCBuffers(commonParameters.ConstantBuffers, nameTable);
			CommonCBBindings = GetCommonCBBindings(commonParameters.ConstantBufferBindings, nameTable);
		}
		else
		{
			CommonTextureParameters = [];
			CommonCBuffers = [];
			CommonCBBindings = [];
		}
	}

	public List<SerializedSubProgramInfo> GetForPlatform(int gpuProgramType)
	{
		return [.. SubProgramInfos.Where(spi => spi.GpuProgramType == gpuProgramType)];
	}

	private static AssetList<T>? GetLast<T>(AssetList<AssetList<T>> list) where T : notnull, new()
	{
		return list.Count > 0 ? list[list.Count - 1] : null;
	}

	private static List<TextureParameter> GetCommonTextureParams(AccessListBase<ITextureParameter> parameters, IReadOnlyDictionary<int, string> nameTable)
	{
		List<TextureParameter> result = new(parameters.Count);
		foreach (ITextureParameter parameter in parameters)
		{
			result.Add(new TextureParameter
			{
				Name = GetName(nameTable, parameter.NameIndex),
				Index = parameter.Has_Index() ? parameter.Index : -1,
				SamplerIndex = parameter.Has_SamplerIndex() ? parameter.SamplerIndex : -1,
				MultiSampled = parameter.Has_MultiSampled() && parameter.MultiSampled,
				Dim = unchecked((byte)parameter.Dim),
			});
		}
		return result;
	}

	private static List<ConstantBuffer> GetCommonCBuffers(AccessListBase<IConstantBuffer> buffers, IReadOnlyDictionary<int, string> nameTable)
	{
		List<ConstantBuffer> result = new(buffers.Count);
		foreach (IConstantBuffer buffer in buffers)
		{
			ConstantBuffer constantBuffer = new()
			{
				Name = GetName(nameTable, buffer.NameIndex),
				UsedSize = buffer.Size,
				// A partial buffer is merged into the blob's own buffer of the same name by ShaderParams.CombineCommon.
				Partial = buffer.Has_IsPartialCB() && buffer.IsPartialCB,
			};

			List<ConstantBufferParameter> parameters = new(buffer.MatrixParams.Count + buffer.VectorParams.Count);
			foreach (IMatrixParameter matrixParameter in buffer.MatrixParams)
			{
				parameters.Add(MakeParameter(matrixParameter, nameTable));
			}
			foreach (IVectorParameter vectorParameter in buffer.VectorParams)
			{
				parameters.Add(MakeParameter(vectorParameter, nameTable));
			}
			constantBuffer.CBParams = parameters;

			// Struct parameters are never emitted into the common buffers, only into the blob's own buffers.
			constantBuffer.StructParams = [];

			result.Add(constantBuffer);
		}
		return result;
	}

	private static List<BufferBinding> GetCommonCBBindings(AccessListBase<IBufferBindingParameter> bindings, IReadOnlyDictionary<int, string> nameTable)
	{
		List<BufferBinding> result = new(bindings.Count);
		foreach (IBufferBindingParameter binding in bindings)
		{
			result.Add(new BufferBinding
			{
				Name = GetName(nameTable, binding.NameIndex),
				Index = binding.Has_Index() ? binding.Index : -1,
				ArraySize = binding.Has_ArraySize() ? binding.ArraySize : 0,
			});
		}
		return result;
	}

	private static ConstantBufferParameter MakeParameter(IMatrixParameter parameter, IReadOnlyDictionary<int, string> nameTable)
	{
		return new ConstantBufferParameter
		{
			ParamName = GetName(nameTable, parameter.NameIndex),
			ParamType = (ShaderParamType)parameter.Type,
			Rows = parameter.RowCount,
			Columns = parameter.RowCount,
			IsMatrix = true,
			ArraySize = parameter.ArraySize,
			Index = parameter.OffsetInConstantBuffer,
		};
	}

	private static ConstantBufferParameter MakeParameter(IVectorParameter parameter, IReadOnlyDictionary<int, string> nameTable)
	{
		return new ConstantBufferParameter
		{
			ParamName = GetName(nameTable, parameter.NameIndex),
			ParamType = (ShaderParamType)parameter.Type,
			Rows = parameter.Dim,
			Columns = 1,
			IsMatrix = false,
			ArraySize = parameter.ArraySize,
			Index = parameter.OffsetInConstantBuffer,
		};
	}

	private static string GetName(IReadOnlyDictionary<int, string> nameTable, int nameIndex)
	{
		return nameTable.TryGetValue(nameIndex, out string? name) ? name : "";
	}
}
