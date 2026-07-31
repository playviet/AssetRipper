using AssetRipper.Assets;
using AssetRipper.Export.Modules.Shaders.Processor;
using AssetRipper.Export.Modules.Shaders.UltraShaderConverter.Converter;
using AssetRipper.Export.Modules.Shaders.UltraShaderConverter.UShader.Function;
using AssetRipper.Import.Logging;
using AssetRipper.Primitives;
using AssetRipper.SourceGenerated.Classes.ClassID_48;
using AssetRipper.SourceGenerated.Extensions;
using AssetRipper.SourceGenerated.Extensions.Enums.Shader;
using AssetRipper.SourceGenerated.Subclasses.SerializedPass;
using AssetRipper.SourceGenerated.Subclasses.SerializedShader;
using AssetRipper.SourceGenerated.Subclasses.SerializedSubProgram;
using System.Text;

namespace AssetRipper.Export.UnityProjects.Shaders;

/// <summary>
/// Exports shaders as ShaderLab with the compiled programs decompiled back to HLSL.
/// </summary>
/// <remarks>
/// Only DirectX vertex and fragment programs can be decompiled. A shader with no usable program still exports its
/// ShaderLab structure, and a variant that fails to decompile becomes a comment, so a shader is never lost outright.
/// </remarks>
public sealed class ShaderDecompileExporter : ShaderExporterBase
{
	public override bool Export(IExportContainer container, IUnityObjectBase asset, string path, FileSystem fileSystem)
	{
		IShader shader = (IShader)asset;
		UnityVersion version = shader.Collection.Version;

		string text;
		try
		{
			text = Decompile(shader, version);
		}
		catch (Exception ex)
		{
			Logger.Error(LogCategory.Export, $"Failed to decompile shader '{shader.GetBestName()}'. Falling back to a dummy shader.", ex);
			return new DummyShaderTextExporter().Export(container, asset, path, fileSystem);
		}

		fileSystem.File.WriteAllText(path, text);
		return true;
	}

	private static string Decompile(IShader shader, UnityVersion version)
	{
		if (!shader.Has_ParsedForm())
		{
			// Before 5.5 the shader is already stored as ShaderLab text, so there is nothing to reconstruct.
			throw new NotSupportedException("The shader has no parsed form.");
		}

		ISerializedShader parsedForm = shader.ParsedForm;
		DecompilationContext context = new(shader, version);
		ShaderLabWriter writer = new(version);
		return writer.Write(parsedForm, context.Decompile);
	}

	/// <summary>
	/// Holds the state that decompiling one shader needs: which platform's blob is being read, and the reflection data
	/// belonging to the pass currently being written.
	/// </summary>
	private sealed class DecompilationContext(IShader shader, UnityVersion version)
	{
		private readonly Dictionary<int, BlobManager> blobManagers = new();
		private readonly Dictionary<ISerializedPass, IReadOnlyDictionary<int, string>> nameTables = new();

		/// <summary>
		/// The platform whose blob is read for this shader, chosen once. -2 means "not chosen yet".
		/// </summary>
		private int platformIndex = -2;

		public DecompiledProgram? Decompile(ISerializedPass pass, ShaderType programType, uint blobIndex)
		{
			int platform = GetPlatformIndex();
			if (platform < 0)
			{
				return null;
			}

			BlobManager blobManager = GetBlobManager(platform);
			ShaderSubProgram shaderSubProgram = blobManager.GetShaderSubProgram((int)blobIndex);
			ShaderGpuProgramType gpuProgramType = shaderSubProgram.GetProgramType(version);

			// OpenGL builds keep the GLSL that Unity's cross compiler produced, so the source is already there and
			// only needs to be handed back. Direct3D builds hold bytecode that has to be decompiled.
			if (IsOpenGL(gpuProgramType))
			{
				string? source = ReadSourceText(shaderSubProgram.ProgramData);
				return source is null ? null : new DecompiledProgram(source, ShaderProgramLanguage.Glsl);
			}

			if (gpuProgramType == ShaderGpuProgramType.SPIRV)
			{
				string? assembly = SpirvProgram.TryDisassemble(shaderSubProgram.ProgramData);
				return assembly is null ? null : new DecompiledProgram(assembly, ShaderProgramLanguage.SpirvAssembly);
			}

			if (!USCShaderConverter.IsSupported(gpuProgramType))
			{
				return null;
			}

			ShaderParams shaderParams = GetShaderParams(shaderSubProgram, pass, programType, blobManager, blobIndex);

			USCShaderConverter converter = new();
			using (MemoryStream stream = new(shaderSubProgram.ProgramData))
			{
				converter.LoadDirectXCompiledShader(stream, gpuProgramType.ToGPUPlatform(), version);
			}
			converter.ConvertDxShaderToUShaderProgram();
			converter.ApplyMetadataToProgram(shaderSubProgram, shaderParams, version);

			UShaderProgram program = converter.ShaderProgram!;
			StringBuilder builder = new();
			UShaderFunctionToHLSL hlsl = new(program, 3);
			builder.Append(hlsl.WriteStruct());
			builder.Append(hlsl.WriteFunction());
			return new DecompiledProgram(builder.ToString(), ShaderProgramLanguage.Hlsl);
		}

		private static bool IsOpenGL(ShaderGpuProgramType programType) => programType
			is ShaderGpuProgramType.GLLegacy
			or ShaderGpuProgramType.GLES
			or ShaderGpuProgramType.GLES3
			or ShaderGpuProgramType.GLES31
			or ShaderGpuProgramType.GLES31AEP
			or ShaderGpuProgramType.GLCore32
			or ShaderGpuProgramType.GLCore41
			or ShaderGpuProgramType.GLCore43;

		/// <summary>
		/// Returns the program as text, or null when it is not text after all.
		/// </summary>
		private static string? ReadSourceText(byte[] data)
		{
			if (data.Length == 0)
			{
				return null;
			}

			// A stray NUL terminator is common and would otherwise end up in the middle of the emitted shader.
			int length = data.Length;
			while (length > 0 && data[length - 1] == 0)
			{
				length--;
			}
			if (length == 0)
			{
				return null;
			}

			string text = Encoding.UTF8.GetString(data, 0, length);
			foreach (char c in text)
			{
				if (char.IsControl(c) && c is not '\n' and not '\r' and not '\t')
				{
					return null;
				}
			}
			return text;
		}

		/// <summary>
		/// From 2021.2 onwards the reflection data lives in its own blob entry instead of being appended to the
		/// program, so it has to be fetched separately and merged with the pass's common parameters.
		/// </summary>
		private ShaderParams GetShaderParams(
			ShaderSubProgram shaderSubProgram,
			ISerializedPass pass,
			ShaderType programType,
			BlobManager blobManager,
			uint blobIndex)
		{
			SerializedProgramInfo? programInfo = GetProgramInfo(pass, programType);

			ShaderParams? shaderParams = shaderSubProgram.ShaderParams;
			if (shaderParams is null && programInfo is not null)
			{
				int index = programInfo.SubProgramInfos.FindIndex(i => i.BlobIndex == blobIndex);
				if (index >= 0 && index < programInfo.ParameterBlobIndices.Count)
				{
					shaderParams = blobManager.GetShaderParams((int)programInfo.ParameterBlobIndices[index]);
				}
			}

			shaderParams ??= new ShaderParams();
			if (programInfo is not null)
			{
				shaderParams.CombineCommon(programInfo);
			}
			return shaderParams;
		}

		private SerializedProgramInfo? GetProgramInfo(ISerializedPass pass, ShaderType programType)
		{
			foreach ((var program, ShaderType type) in pass.GetProgramsWithType())
			{
				if (type == programType)
				{
					return new SerializedProgramInfo(program, GetNameTable(pass));
				}
			}
			return null;
		}

		/// <summary>
		/// The pass stores names once and refers to them by index, so the map has to be inverted to look names up.
		/// </summary>
		private IReadOnlyDictionary<int, string> GetNameTable(ISerializedPass pass)
		{
			if (nameTables.TryGetValue(pass, out IReadOnlyDictionary<int, string>? existing))
			{
				return existing;
			}

			Dictionary<int, string> table = new();
			foreach ((Utf8String name, int index) in pass.NameIndices)
			{
				table[index] = name.String;
			}
			nameTables.Add(pass, table);
			return table;
		}

		private BlobManager GetBlobManager(int platformIndex)
		{
			if (blobManagers.TryGetValue(platformIndex, out BlobManager? existing))
			{
				return existing;
			}
			BlobManager blobManager = BlobManager.FromShader(shader, platformIndex, version);
			blobManagers.Add(platformIndex, blobManager);
			return blobManager;
		}

		/// <summary>
		/// Picks the platform to read programs from, preferring the one that yields the most readable result.
		/// </summary>
		/// <remarks>
		/// Direct3D comes first because its bytecode decompiles back to HLSL, which is the language the shader was
		/// written in. OpenGL comes next because Unity stores its cross compiled GLSL as plain source, so it needs no
		/// decompilation at all. Anything else has no path yet.
		/// </remarks>
		private int GetPlatformIndex()
		{
			if (platformIndex != -2)
			{
				return platformIndex;
			}

			platformIndex = -1;
			GPUPlatform[] platforms = shader.GetPlatforms()?.ToArray() ?? [];

			for (int i = 0; i < platforms.Length && platformIndex < 0; i++)
			{
				if (platforms[i] is GPUPlatform.D3D11 or GPUPlatform.D3D11_9x or GPUPlatform.D3D9)
				{
					platformIndex = i;
				}
			}

			for (int i = 0; i < platforms.Length && platformIndex < 0; i++)
			{
				if (platforms[i] is GPUPlatform.Gles3x or GPUPlatform.Gles20 or GPUPlatform.GlCore or GPUPlatform.OpenGL)
				{
					platformIndex = i;
				}
			}

			// Vulkan comes last because SPIR-V only disassembles; it documents the program instead of rebuilding it.
			for (int i = 0; i < platforms.Length && platformIndex < 0; i++)
			{
				if (platforms[i] is GPUPlatform.Vulkan)
				{
					platformIndex = i;
				}
			}

			if (platformIndex < 0)
			{
				// Worth saying out loud: without a usable platform the pass bodies come out empty, and the user has no
				// way to tell that apart from a decompiler failure.
				Logger.Warning(LogCategory.Export, $"Shader '{shader.GetBestName()}' has no program that can be recovered. It was built for: {string.Join(", ", platforms)}");
			}
			return platformIndex;
		}
	}
}
