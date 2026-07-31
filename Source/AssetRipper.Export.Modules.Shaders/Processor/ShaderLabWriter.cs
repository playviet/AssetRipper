using AssetRipper.Assets.Generics;
using AssetRipper.Export.Modules.Shaders.Extras;
using AssetRipper.Primitives;
using AssetRipper.SourceGenerated.Extensions;
using AssetRipper.SourceGenerated.Subclasses.SerializedCustomEditorForRenderPipeline;
using AssetRipper.SourceGenerated.Subclasses.SerializedPass;
using AssetRipper.SourceGenerated.Subclasses.SerializedPlayerSubProgram;
using AssetRipper.SourceGenerated.Subclasses.SerializedProgram;
using AssetRipper.SourceGenerated.Subclasses.SerializedProperties;
using AssetRipper.SourceGenerated.Subclasses.SerializedProperty;
using AssetRipper.SourceGenerated.Subclasses.SerializedShader;
using AssetRipper.SourceGenerated.Subclasses.SerializedShaderDependency;
using AssetRipper.SourceGenerated.Subclasses.SerializedShaderFloatValue;
using AssetRipper.SourceGenerated.Subclasses.SerializedShaderRTBlendState;
using AssetRipper.SourceGenerated.Subclasses.SerializedShaderState;
using AssetRipper.SourceGenerated.Subclasses.SerializedStencilOp;
using AssetRipper.SourceGenerated.Subclasses.SerializedSubProgram;
using AssetRipper.SourceGenerated.Subclasses.SerializedSubShader;
using AssetRipper.SourceGenerated.Subclasses.SerializedTagMap;
using SerializedPassType = AssetRipper.SourceGenerated.Extensions.Enums.Shader.SerializedShader.SerializedPassType;
using ShaderType = AssetRipper.SourceGenerated.Extensions.Enums.Shader.ShaderType;

namespace AssetRipper.Export.Modules.Shaders.Processor;

/// <summary>
/// Produces the HLSL body of a single compiled sub program.
/// </summary>
/// <returns>The decompiled program text, or <see langword="null"/> if the sub program could not be decompiled.</returns>
/// <summary>
/// The language a recovered shader program is written in. It decides which block encloses it in ShaderLab.
/// </summary>
public enum ShaderProgramLanguage
{
	/// <summary>
	/// Goes in a CGPROGRAM block. This is what decompiled Direct3D bytecode produces.
	/// </summary>
	Hlsl,

	/// <summary>
	/// Goes in a GLSLPROGRAM block. OpenGL builds store this as source, so it is recovered rather than decompiled.
	/// </summary>
	Glsl,

	/// <summary>
	/// SPIR-V disassembly, which is what a Vulkan build yields. It is readable but it is not shader source, so it is
	/// emitted commented out and the pass compiles as if it had no program.
	/// </summary>
	SpirvAssembly,
}

/// <summary>
/// A recovered shader program.
/// </summary>
public readonly record struct DecompiledProgram(string Body, ShaderProgramLanguage Language);

/// <summary>
/// Recovers the program at <paramref name="blobIndex"/>, or returns null when it cannot be recovered.
/// </summary>
public delegate DecompiledProgram? SubProgramDecompiler(ISerializedPass pass, ShaderType programType, uint blobIndex);

/// <summary>
/// Reconstructs ShaderLab source text from the parsed form of a Unity shader.
/// </summary>
/// <remarks>
/// Not thread safe: a single instance writes into one buffer and must not be shared between concurrent exports.
/// </remarks>
public sealed class ShaderLabWriter(UnityVersion version)
{
	private readonly StringBuilderIndented sb = new();

	public string Write(ISerializedShader shader, Func<uint, DecompiledProgram?> decompileSubProgram)
	{
		ArgumentNullException.ThrowIfNull(decompileSubProgram);
		return Write(shader, (_, _, blobIndex) => decompileSubProgram(blobIndex));
	}

	public string Write(ISerializedShader shader, SubProgramDecompiler? decompileSubProgram = null)
	{
		sb.Clear();

		sb.AppendLine($"Shader \"{shader.Name}\" {{");
		sb.Indent();
		{
			WriteProperties(shader.PropInfo);

			bool anySubShaderWritten = false;
			foreach (ISerializedSubShader subShader in shader.SubShaders)
			{
				// ShaderLab requires a sub shader to declare at least one pass. Unity's build time stripping can
				// leave one with none, and writing that out verbatim makes the whole file fail to parse, taking
				// the passes of the other sub shaders down with it.
				if (subShader.Passes.Count > 0)
				{
					WriteSubShader(shader, subShader, decompileSubProgram);
					anySubShaderWritten = true;
				}
			}

			if (!anySubShaderWritten)
			{
				WritePlaceholderSubShader(shader);
			}

			WriteTrailer(shader);
		}
		sb.Unindent();
		sb.AppendLine("}");

		return sb.ToString();
	}

	private void WriteTrailer(ISerializedShader shader)
	{
		foreach (SerializedShaderDependency dependency in shader.Dependencies)
		{
			sb.AppendLine($"Dependency \"{dependency.From}\" = \"{dependency.To}\"");
		}

		if (shader.FallbackName.String.Length > 0)
		{
			sb.AppendLine($"Fallback \"{shader.FallbackName}\"");
		}

		// The custom editor classes live in the original project's editor assembly, which is not exported,
		// so referencing them would make the shader fail to import. Emit them as comments instead.
		if (shader.CustomEditorName.String.Length > 0)
		{
			sb.AppendLine($"//CustomEditor \"{shader.CustomEditorName}\"");
		}
		if (shader.Has_CustomEditorForRenderPipelines())
		{
			foreach (SerializedCustomEditorForRenderPipeline customEditor in shader.CustomEditorForRenderPipelines)
			{
				sb.AppendLine($"//CustomEditorForRenderPipeline \"{customEditor.CustomEditorName}\" \"{customEditor.RenderPipelineType}\"");
			}
		}
	}

	#region Properties

	private void WriteProperties(ISerializedProperties propInfo)
	{
		sb.AppendLine("Properties {");
		sb.Indent();
		foreach (ISerializedProperty property in propInfo.Props)
		{
			WriteProperty(property);
		}
		sb.Unindent();
		sb.AppendLine("}");
	}

	private void WriteProperty(ISerializedProperty property)
	{
		string typeString;
		string defaultValue;
		try
		{
			typeString = property.GetTypeString();
			defaultValue = property.GetDefaultValue();
		}
		catch (NotSupportedException)
		{
			sb.AppendLine($"//{property.Name} : unsupported property type {property.Type}");
			return;
		}

		sb.Append("");
		foreach (string attribute in property.GetAttributes())
		{
			sb.AppendNoIndent($"[{attribute}] ");
		}
		sb.AppendNoIndent($"{property.Name} (\"{property.Description}\", {typeString}) = {defaultValue}\n");
	}

	#endregion

	#region SubShader

	private void WriteSubShader(ISerializedShader shader, ISerializedSubShader subShader, SubProgramDecompiler? decompileSubProgram)
	{
		sb.AppendLine("SubShader {");
		sb.Indent();
		{
			WriteTags(subShader.Tags);

			if (subShader.LOD != 0)
			{
				sb.AppendLine($"LOD {subShader.LOD}");
			}

			foreach (ISerializedPass pass in subShader.Passes)
			{
				WritePass(shader, pass, decompileSubProgram);
			}
		}
		sb.Unindent();
		sb.AppendLine("}");
	}

	/// <summary>
	/// Writes a sub shader that draws nothing, for a shader left with no passes at all.
	/// </summary>
	/// <remarks>
	/// Such a shader still has to import: materials reference it by GUID, and a file that fails to parse would cost
	/// them their binding as well as their properties. The tags of the original first sub shader are kept so the
	/// render pipeline still recognises it as its own.
	/// </remarks>
	private void WritePlaceholderSubShader(ISerializedShader shader)
	{
		sb.AppendLine("SubShader {");
		sb.Indent();
		{
			if (shader.SubShaders.Count > 0)
			{
				WriteTags(shader.SubShaders[0].Tags);
			}
			sb.AppendLine("Pass { }");
		}
		sb.Unindent();
		sb.AppendLine("}");
	}

	private void WriteTags(ISerializedTagMap tagMap)
	{
		AccessDictionaryBase<Utf8String, Utf8String> tags = tagMap.Tags;
		if (tags.Count == 0)
		{
			return;
		}

		sb.AppendLine("Tags {");
		sb.Indent();
		for (int i = 0; i < tags.Count; i++)
		{
			sb.AppendLine($"\"{tags.GetKey(i)}\" = \"{tags.GetValue(i)}\"");
		}
		sb.Unindent();
		sb.AppendLine("}");
	}

	#endregion

	#region Pass

	private void WritePass(ISerializedShader shader, ISerializedPass pass, SubProgramDecompiler? decompileSubProgram)
	{
		SerializedPassType passType = pass.GetType_();
		if (passType == SerializedPassType.UsePass || pass.UseName.String.Length > 0)
		{
			sb.AppendLine($"UsePass \"{pass.UseName}\"");
			return;
		}
		if (passType == SerializedPassType.GrabPass)
		{
			// A grab pass without a texture name grabs into the implicit _GrabTexture.
			if (pass.TextureName.String.Length > 0)
			{
				sb.AppendLine("GrabPass {");
				sb.Indent();
				sb.AppendLine($"\"{pass.TextureName}\"");
				sb.Unindent();
				sb.AppendLine("}");
			}
			else
			{
				sb.AppendLine("GrabPass { }");
			}
			return;
		}

		sb.AppendLine("Pass {");
		sb.Indent();
		{
			WritePassState(pass.State);
			WritePassPrograms(shader, pass, decompileSubProgram);
		}
		sb.Unindent();
		sb.AppendLine("}");
	}

	private void WritePassState(ISerializedShaderState state)
	{
		if (state.Name.String.Length > 0)
		{
			sb.AppendLine($"Name \"{state.Name}\"");
		}
		if (state.LOD != 0)
		{
			sb.AppendLine($"LOD {state.LOD}");
		}

		if (state.RtSeparateBlend)
		{
			for (int i = 0; i < 8; i++)
			{
				WriteRtBlend(GetRtBlend(state, i), i);
			}
		}
		else
		{
			WriteRtBlend(state.RtBlend0, -1);
		}

		if (!state.AlphaToMask.IsZeroAndNameless)
		{
			sb.AppendLine($"AlphaToMask {GetToggleString(state.AlphaToMask)}");
		}
		if (state.Has_Conservative() && !((ISerializedShaderFloatValue)state.Conservative).IsZeroAndNameless)
		{
			sb.AppendLine($"Conservative {GetToggleString(state.Conservative)}");
		}
		if (state.Has_ZClip() && !((ISerializedShaderFloatValue)state.ZClip).IsDefault(ZClip.Off))
		{
			sb.AppendLine($"ZClip {GetToggleString(state.ZClip)}");
		}

		// ZTest 0 means "not set" rather than a real comparison function, so it is skipped along with the LEqual default.
		ZTest zTest = state.ZTest.GetValue<ZTest>();
		if (state.ZTest.HasName || (zTest != ZTest.None && zTest != ZTest.LEqual))
		{
			sb.AppendLine($"ZTest {state.ZTest.GetNameOrEnumString<ZTest>()}");
		}
		if (!state.ZWrite.IsDefault(ZWrite.On))
		{
			sb.AppendLine($"ZWrite {state.ZWrite.GetNameOrEnumString<ZWrite>()}");
		}
		if (!state.Culling.IsDefault(CullMode.Back))
		{
			sb.AppendLine($"Cull {state.Culling.GetNameOrEnumString<CullMode>()}");
		}
		if (!state.OffsetFactor.IsZeroAndNameless || !state.OffsetUnits.IsZeroAndNameless)
		{
			sb.AppendLine($"Offset {state.OffsetFactor.GetNameOrFloatString()}, {state.OffsetUnits.GetNameOrFloatString()}");
		}

		WriteStencil(state);
		WriteFog(state);

		if (state.Lighting)
		{
			sb.AppendLine("Lighting On");
		}

		WriteTags(state.Tags);
	}

	private static ISerializedShaderRTBlendState GetRtBlend(ISerializedShaderState state, int index) => index switch
	{
		0 => state.RtBlend0,
		1 => state.RtBlend1,
		2 => state.RtBlend2,
		3 => state.RtBlend3,
		4 => state.RtBlend4,
		5 => state.RtBlend5,
		6 => state.RtBlend6,
		_ => state.RtBlend7,
	};

	private void WriteRtBlend(ISerializedShaderRTBlendState rtBlend, int index)
	{
		string indexString = index >= 0 ? $"{index} " : string.Empty;

		bool colorIsDefault = rtBlend.SourceBlend.IsDefault(BlendMode.One) && rtBlend.DestinationBlend.IsDefault(BlendMode.Zero);
		bool alphaIsDefault = rtBlend.SourceBlendAlpha.IsDefault(BlendMode.One) && rtBlend.DestinationBlendAlpha.IsDefault(BlendMode.Zero);
		if (!colorIsDefault || !alphaIsDefault)
		{
			sb.Append($"Blend {indexString}{rtBlend.SourceBlend.GetNameOrEnumString<BlendMode>()} {rtBlend.DestinationBlend.GetNameOrEnumString<BlendMode>()}");
			if (!alphaIsDefault)
			{
				sb.AppendNoIndent($", {rtBlend.SourceBlendAlpha.GetNameOrEnumString<BlendMode>()} {rtBlend.DestinationBlendAlpha.GetNameOrEnumString<BlendMode>()}");
			}
			sb.AppendNoIndent("\n");
		}

		bool blendOpIsDefault = rtBlend.BlendOp.IsDefault(BlendOp.Add);
		bool blendOpAlphaIsDefault = rtBlend.BlendOpAlpha.IsDefault(BlendOp.Add);
		if (!blendOpIsDefault || !blendOpAlphaIsDefault)
		{
			sb.Append($"BlendOp {indexString}{rtBlend.BlendOp.GetNameOrEnumString<BlendOp>()}");
			if (!blendOpAlphaIsDefault)
			{
				sb.AppendNoIndent($", {rtBlend.BlendOpAlpha.GetNameOrEnumString<BlendOp>()}");
			}
			sb.AppendNoIndent("\n");
		}

		if (rtBlend.ColMask.HasName)
		{
			sb.AppendLine($"ColorMask [{rtBlend.ColMask.Name}]{(index >= 0 ? $" {index}" : "")}");
		}
		else
		{
			ColorWriteMask colMask = rtBlend.ColMask.GetValue<ColorWriteMask>();
			if (colMask != ColorWriteMask.All)
			{
				sb.Append("ColorMask ");
				if (colMask == ColorWriteMask.None)
				{
					sb.AppendNoIndent("0");
				}
				else
				{
					// ShaderLab spells the mask as an unordered run of channel letters, e.g. "RGB" or "RA".
					if ((colMask & ColorWriteMask.Red) != 0)
					{
						sb.AppendNoIndent("R");
					}
					if ((colMask & ColorWriteMask.Green) != 0)
					{
						sb.AppendNoIndent("G");
					}
					if ((colMask & ColorWriteMask.Blue) != 0)
					{
						sb.AppendNoIndent("B");
					}
					if ((colMask & ColorWriteMask.Alpha) != 0)
					{
						sb.AppendNoIndent("A");
					}
				}
				if (index >= 0)
				{
					sb.AppendNoIndent($" {index}");
				}
				sb.AppendNoIndent("\n");
			}
		}
	}

	private void WriteStencil(ISerializedShaderState state)
	{
		if (state.StencilIsDefault)
		{
			return;
		}

		sb.AppendLine("Stencil {");
		sb.Indent();
		{
			if (!state.StencilRefIsDefault)
			{
				sb.AppendLine($"Ref {state.StencilRef.GetNameOrFloatString()}");
			}
			if (!state.StencilReadMaskIsDefault)
			{
				sb.AppendLine($"ReadMask {state.StencilReadMask.GetNameOrFloatString()}");
			}
			if (!state.StencilWriteMaskIsDefault)
			{
				sb.AppendLine($"WriteMask {state.StencilWriteMask.GetNameOrFloatString()}");
			}
			WriteStencilOp(state.StencilOp, "");
			WriteStencilOp(state.StencilOpFront, "Front");
			WriteStencilOp(state.StencilOpBack, "Back");
		}
		sb.Unindent();
		sb.AppendLine("}");
	}

	private void WriteStencilOp(ISerializedStencilOp stencilOp, string suffix)
	{
		bool named = stencilOp.Comp.HasName || stencilOp.Pass.HasName || stencilOp.Fail.HasName || stencilOp.ZFail.HasName;
		StencilComp comp = stencilOp.Comp.GetValue<StencilComp>();
		bool opsAreKeep = stencilOp.Pass.GetValue<StencilOp>() == StencilOp.Keep
			&& stencilOp.Fail.GetValue<StencilOp>() == StencilOp.Keep
			&& stencilOp.ZFail.GetValue<StencilOp>() == StencilOp.Keep;

		// Disabled is Unity's "never serialized" sentinel and has no ShaderLab spelling, so it is treated as a default.
		if (!named && opsAreKeep && comp is StencilComp.Always or StencilComp.Disabled)
		{
			return;
		}

		sb.AppendLine($"Comp{suffix} {stencilOp.Comp.GetNameOrEnumString<StencilComp>()}");
		sb.AppendLine($"Pass{suffix} {stencilOp.Pass.GetNameOrEnumString<StencilOp>()}");
		sb.AppendLine($"Fail{suffix} {stencilOp.Fail.GetNameOrEnumString<StencilOp>()}");
		sb.AppendLine($"ZFail{suffix} {stencilOp.ZFail.GetNameOrEnumString<StencilOp>()}");
	}

	private void WriteFog(ISerializedShaderState state)
	{
		FogMode fogMode = (FogMode)state.FogMode;
		bool hasColor = !state.FogColor.IsZeroAndNameless;
		bool hasRange = !state.FogStart.IsZeroAndNameless || !state.FogEnd.IsZeroAndNameless;
		bool hasDensity = !state.FogDensity.IsZeroAndNameless;
		if (fogMode == FogMode.Unknown && !hasColor && !hasRange && !hasDensity)
		{
			return;
		}

		sb.AppendLine("Fog {");
		sb.Indent();
		{
			if (fogMode != FogMode.Unknown)
			{
				sb.AppendLine($"Mode {fogMode}");
			}
			if (hasColor)
			{
				sb.AppendLine($"Color {state.FogColor.GetNameOrVectorString()}");
			}
			if (hasDensity)
			{
				sb.AppendLine($"Density {state.FogDensity.GetNameOrFloatString()}");
			}
			if (hasRange)
			{
				sb.AppendLine($"Range {state.FogStart.GetNameOrFloatString()}, {state.FogEnd.GetNameOrFloatString()}");
			}
		}
		sb.Unindent();
		sb.AppendLine("}");
	}

	private static string GetToggleString(ISerializedShaderFloatValue value)
	{
		return value.HasName ? $"[{value.Name}]" : value.Value != 0f ? "On" : "Off";
	}

	#endregion

	#region Programs

	private readonly record struct Variant(ShaderType ProgramType, uint BlobIndex, string[] Keywords);

	/// <summary>
	/// A variant together with whatever came back for it, so that the block can be opened knowing which language it
	/// will contain.
	/// </summary>
	private readonly record struct ResolvedVariant(Variant Variant, DecompiledProgram? Program, string? Error);

	private void WritePassPrograms(ISerializedShader shader, ISerializedPass pass, SubProgramDecompiler? decompileSubProgram)
	{
		List<Variant> variants = [];
		foreach ((ISerializedProgram program, ShaderType programType) in pass.GetProgramsWithType())
		{
			foreach (ISerializedSubProgram subProgram in program.SubPrograms)
			{
				variants.Add(new Variant(programType, subProgram.BlobIndex, GetKeywords(shader, pass, subProgram)));
			}

			// From 2021.2 onwards a program's variants live in m_PlayerSubPrograms instead. They index the same blob,
			// so they decompile the same way once their blob index is known.
			foreach (ISerializedPlayerSubProgram subProgram in program.GetPlayerSubPrograms())
			{
				variants.Add(new Variant(programType, subProgram.BlobIndex, GetKeywords(shader, subProgram)));
			}
		}

		if (variants.Count == 0)
		{
			return;
		}

		// Everything is recovered before anything is written, because the language decides which block encloses it.
		List<ResolvedVariant> resolved = [];
		foreach (Variant variant in variants)
		{
			resolved.Add(Resolve(pass, variant, decompileSubProgram));
		}

		bool glsl = resolved.Any(r => r.Program?.Language == ShaderProgramLanguage.Glsl);

		sb.AppendLine(glsl ? "GLSLPROGRAM" : "CGPROGRAM");
		{
			// A GLSLPROGRAM block carries its own "#ifdef VERTEX" and "#ifdef FRAGMENT" sections and takes none of the
			// HLSL entry point pragmas, so emitting them would only break it.
			if (!glsl)
			{
				WritePassPragmas(pass, variants);
			}

			// Group by stage so that the variants of one stage form a single contiguous preprocessor chain.
			foreach (IGrouping<ShaderType, ResolvedVariant> group in resolved
				.OrderBy(r => (int)r.Variant.ProgramType)
				.ThenByDescending(r => r.Variant.Keywords.Length)
				.GroupBy(r => r.Variant.ProgramType))
			{
				WriteStageVariants([.. group], glsl);
			}
		}
		sb.AppendLine(glsl ? "ENDGLSL" : "ENDCG");
	}

	private static ResolvedVariant Resolve(ISerializedPass pass, Variant variant, SubProgramDecompiler? decompileSubProgram)
	{
		try
		{
			return new ResolvedVariant(variant, decompileSubProgram?.Invoke(pass, variant.ProgramType, variant.BlobIndex), null);
		}
		catch (Exception ex)
		{
			// One unsupported variant must never take the whole shader down.
			return new ResolvedVariant(variant, null, $"{ex.GetType().Name}: {SingleLine(ex.Message)}");
		}
	}

	private void WritePassPragmas(ISerializedPass pass, List<Variant> variants)
	{
		foreach (ShaderType programType in variants.Select(v => v.ProgramType).Distinct().OrderBy(t => (int)t))
		{
			string? entryPragma = programType switch
			{
				ShaderType.Vertex => "#pragma vertex vert",
				ShaderType.Fragment => "#pragma fragment frag",
				ShaderType.Geometry => "#pragma geometry geom",
				ShaderType.Hull => "#pragma hull hull",
				ShaderType.Domain => "#pragma domain domain",
				_ => null,
			};
			if (entryPragma is not null)
			{
				sb.AppendLine(entryPragma);
			}
		}

		int shaderModel = pass.MaxShaderModelVersion(version);
		if (shaderModel > 0)
		{
			sb.AppendLine($"#pragma target {shaderModel / 10}.{shaderModel % 10}");
		}

		if (variants.Count > 0)
		{
			// A keyword present in every variant was compiled unconditionally, so it must stay defined;
			// the rest select between variants and are therefore optional features.
			HashSet<string> allKeywords = [.. variants.SelectMany(v => v.Keywords)];
			foreach (string keyword in allKeywords.Order())
			{
				bool mandatory = variants.All(v => v.Keywords.Contains(keyword));
				sb.AppendLine(mandatory ? $"#pragma multi_compile {keyword}" : $"#pragma shader_feature {keyword}");
			}
		}

		sb.AppendLineNoIndent("");
	}

	private void WriteStageVariants(List<ResolvedVariant> resolved, bool glsl)
	{
		List<Variant> variants = [.. resolved.Select(r => r.Variant)];

		// A preprocessor chain can only separate the variants when each one has a distinct, non-empty keyword set.
		// GLSL is excluded because each variant carries its own #version directive, and only one of those may be
		// active in a block no matter which branch is taken.
		bool guarded = !glsl
			&& variants.Count > 1
			&& variants.All(v => v.Keywords.Length > 0)
			&& variants.Select(v => string.Join(" && ", v.Keywords)).Distinct().Count() == variants.Count;

		for (int i = 0; i < variants.Count; i++)
		{
			Variant variant = variants[i];
			string keywordComment = variant.Keywords.Length > 0
				? $" // Keywords: {string.Join(", ", variant.Keywords)}"
				: string.Empty;

			bool disabled = false;
			if (guarded)
			{
				if (i == variants.Count - 1)
				{
					sb.AppendLine("#else");
				}
				else
				{
					sb.AppendLine($"#{(i == 0 ? "if" : "elif")} {string.Join(" && ", variant.Keywords)}");
				}
			}
			else if (i > 0)
			{
				// Without keywords there is no condition that could select between duplicate entry points,
				// so the extra variants are kept for reference but excluded from compilation.
				sb.AppendLine($"// Additional {variant.ProgramType} variant, disabled to avoid duplicate definitions.{keywordComment}");
				sb.AppendLine("#if 0");
				disabled = true;
			}
			else if (keywordComment.Length > 0)
			{
				sb.AppendLine(keywordComment.TrimStart());
			}

			sb.Indent();
			WriteSubProgramBody(resolved[i]);
			sb.Unindent();

			if (disabled)
			{
				sb.AppendLine("#endif");
			}
		}

		if (guarded)
		{
			sb.AppendLine("#endif");
		}
		sb.AppendLineNoIndent("");
	}

	private void WriteSubProgramBody(ResolvedVariant resolved)
	{
		Variant variant = resolved.Variant;
		if (resolved.Error is not null)
		{
			sb.AppendLine($"// Recovery threw {resolved.Error}");
		}

		string? body = resolved.Program?.Body;
		if (string.IsNullOrEmpty(body))
		{
			sb.AppendLine($"// Sub program {variant.BlobIndex} ({variant.ProgramType}) could not be recovered.");
			return;
		}

		// Disassembly documents the program rather than being able to rebuild it, so it is commented out to keep the
		// surrounding ShaderLab compilable.
		bool comment = resolved.Program!.Value.Language == ShaderProgramLanguage.SpirvAssembly;
		if (comment)
		{
			sb.AppendLine($"// SPIR-V disassembly of sub program {variant.BlobIndex} ({variant.ProgramType}).");
			sb.AppendLine("// This is not shader source and cannot be recompiled as is.");
		}

		foreach (string line in body.Replace("\r\n", "\n").Split('\n'))
		{
			if (line.Length == 0)
			{
				sb.AppendLineNoIndent(comment ? "//" : "");
			}
			else
			{
				sb.AppendLine(comment ? $"// {line}" : line);
			}
		}
	}

	private static string SingleLine(string text) => text.Replace("\r", " ").Replace("\n", " ");

	/// <summary>
	/// Resolves the keyword names a sub program was compiled with.
	/// </summary>
	/// <remarks>
	/// Newer versions store the names once on the shader and reference them by index from the sub program.
	/// Older versions have no shader level table and instead index into the pass' name table.
	/// Every lookup is bounds checked because an unresolvable index only costs us a keyword, not the export.
	/// </remarks>
	/// <summary>
	/// Player sub programs only carry keyword indices, and they only exist in the versions that also store the keyword
	/// names on the shader, so there is no pass-level name table to fall back to.
	/// </summary>
	private static string[] GetKeywords(ISerializedShader shader, ISerializedPlayerSubProgram subProgram)
	{
		SortedSet<string> keywords = [];
		IReadOnlyList<Utf8String> keywordNames = shader.Has_KeywordNames() ? shader.KeywordNames : [];
		foreach (ushort index in subProgram.KeywordIndices)
		{
			if (index < keywordNames.Count && keywordNames[index].String.Length > 0)
			{
				keywords.Add(keywordNames[index].String);
			}
		}
		return [.. keywords];
	}

	private static string[] GetKeywords(ISerializedShader shader, ISerializedPass pass, ISerializedSubProgram subProgram)
	{
		SortedSet<string> keywords = [];

		IReadOnlyList<Utf8String> keywordNames = shader.Has_KeywordNames() ? shader.KeywordNames : [];
		if (keywordNames.Count > 0)
		{
			if (subProgram.Has_GlobalKeywordIndices())
			{
				Resolve(subProgram.GlobalKeywordIndices, keywordNames, keywords);
			}
			if (subProgram.Has_LocalKeywordIndices())
			{
				Resolve(subProgram.LocalKeywordIndices, keywordNames, keywords);
			}
			if (keywords.Count == 0 && subProgram.Has_KeywordIndices())
			{
				Resolve(subProgram.KeywordIndices, keywordNames, keywords);
			}
		}
		else if (subProgram.Has_KeywordIndices())
		{
			AccessDictionaryBase<Utf8String, int> nameIndices = pass.NameIndices;
			Dictionary<int, string> names = new(nameIndices.Count);
			for (int i = 0; i < nameIndices.Count; i++)
			{
				names[nameIndices.GetValue(i)] = nameIndices.GetKey(i).String;
			}

			foreach (ushort index in subProgram.KeywordIndices)
			{
				if (names.TryGetValue(index, out string? name) && name.Length > 0)
				{
					keywords.Add(name);
				}
			}
		}

		return [.. keywords];

		static void Resolve(IReadOnlyList<ushort> indices, IReadOnlyList<Utf8String> names, SortedSet<string> destination)
		{
			foreach (ushort index in indices)
			{
				if (index < names.Count && names[index].String.Length > 0)
				{
					destination.Add(names[index].String);
				}
			}
		}
	}

	#endregion
}
