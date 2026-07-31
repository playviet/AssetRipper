using AssetRipper.Export.Modules.Shaders.Processor;
using AssetRipper.Export.Modules.Shaders.UltraShaderConverter.DirectXDisassembler;
using AssetRipper.Export.Modules.Shaders.UltraShaderConverter.USIL;
using AssetRipper.Export.Modules.Shaders.UltraShaderConverter.UShader.DirectX;
using AssetRipper.Export.Modules.Shaders.UltraShaderConverter.UShader.Function;
using AssetRipper.Primitives;
using AssetRipper.SourceGenerated.Extensions.Enums.Shader;

namespace AssetRipper.Export.Modules.Shaders.UltraShaderConverter.Converter;

/// <summary>
/// Turns a compiled DirectX shader program into readable HLSL.
/// </summary>
/// <remarks>
/// The compiled bytecode is disassembled into DXBC, lifted into USIL, annotated with the reflection data that Unity
/// stores alongside it, and finally written back out as HLSL.
/// <para>
/// Only vertex and fragment programs for DirectX are handled. Console (NVN) programs need a Nintendo Switch shader
/// translator, which this build does not carry.
/// </para>
/// </remarks>
public sealed class USCShaderConverter
{
	public DirectXCompiledShader? DxShader { get; private set; }
	public UShaderProgram? ShaderProgram { get; private set; }

	public void LoadDirectXCompiledShader(Stream data, GPUPlatform graphicApi, UnityVersion version)
	{
		int offset = GetDirectXDataOffset(version, graphicApi, data.ReadByte());

		// The disassembler reads from position zero, so the Unity header is dropped rather than skipped over.
		data.Position = offset;
		MemoryStream trimmed = new();
		data.CopyTo(trimmed);
		trimmed.Position = 0;

		DxShader = new DirectXCompiledShader(trimmed);
	}

	/// <summary>
	/// Unity prefixes the DirectX bytecode with a header whose size depends on the engine version and on the header's
	/// own version byte.
	/// </summary>
	private static int GetDirectXDataOffset(UnityVersion version, GPUPlatform graphicApi, int headerVersion)
	{
		if (graphicApi == GPUPlatform.D3D9)
		{
			return 0;
		}

		bool hasGSInputPrimitive = version.GreaterThanOrEquals(5, 4);
		int offset = hasGSInputPrimitive ? 6 : 5;
		if (headerVersion >= 2)
		{
			offset += 0x20;
		}
		return offset;
	}

	public void ConvertDxShaderToUShaderProgram()
	{
		if (DxShader is null)
		{
			throw new InvalidOperationException($"{nameof(LoadDirectXCompiledShader)} must be called first.");
		}

		DirectXProgramToUSIL converter = new(DxShader);
		converter.Convert();
		ShaderProgram = converter.shader;
	}

	public void ApplyMetadataToProgram(ShaderSubProgram subProgram, ShaderParams shaderParams, UnityVersion version)
	{
		if (ShaderProgram is null)
		{
			throw new InvalidOperationException($"{nameof(ConvertDxShaderToUShaderProgram)} must be called first.");
		}

		ShaderGpuProgramType programType = subProgram.GetProgramType(version);
		UShaderFunctionType functionType = GetFunctionType(programType);
		if (functionType == UShaderFunctionType.Unknown)
		{
			throw new NotSupportedException($"Only vertex and fragment programs are supported, not {programType}.");
		}

		ShaderProgram.shaderFunctionType = functionType;
		USILOptimizerApplier.Apply(ShaderProgram, shaderParams);
	}

	public static UShaderFunctionType GetFunctionType(ShaderGpuProgramType programType) => programType switch
	{
		ShaderGpuProgramType.DX11VertexSM40 or ShaderGpuProgramType.DX11VertexSM50 => UShaderFunctionType.Vertex,
		ShaderGpuProgramType.DX11PixelSM40 or ShaderGpuProgramType.DX11PixelSM50 => UShaderFunctionType.Fragment,
		_ => UShaderFunctionType.Unknown,
	};

	/// <summary>
	/// Whether this converter can handle the given program at all, so that a caller can skip unsupported variants
	/// instead of catching an exception for each one.
	/// </summary>
	public static bool IsSupported(ShaderGpuProgramType programType)
	{
		return GetFunctionType(programType) != UShaderFunctionType.Unknown;
	}
}
