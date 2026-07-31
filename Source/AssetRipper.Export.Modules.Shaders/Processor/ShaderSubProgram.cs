using AssetRipper.IO.Endian;
using AssetRipper.Primitives;

namespace AssetRipper.Export.Modules.Shaders.Processor;

/// <summary>
/// A single compiled program entry of the shader blob: its stats, keywords, bytecode and reflection data.
/// </summary>
public sealed class ShaderSubProgram
{
	public int ProgramType { get; set; }
	public int StatsALU { get; set; }
	public int StatsTEX { get; set; }
	public int StatsFlow { get; set; }
	public int StatsTempRegister { get; set; }
	public List<string> GlobalKeywords { get; set; } = [];
	public List<string> LocalKeywords { get; set; } = [];
	public byte[] ProgramData { get; set; } = [];
	public ParserBindChannels BindChannels { get; set; } = new();

	/// <summary>
	/// Only present before 2021, where the reflection data is stored alongside the program instead of separately.
	/// </summary>
	public ShaderParams? ShaderParams { get; set; }

	public static ShaderSubProgram Read(ref EndianSpanReader reader, UnityVersion version)
	{
		ShaderSubProgram result = new();

		bool hasStatsTempRegister = version.GreaterThanOrEquals(5, 5);
		bool hasLocalKeywords = version.LessThan(2021, 2) && version.GreaterThanOrEquals(2019, 1);

		_ = reader.ReadInt32(); // blob version
		result.ProgramType = reader.ReadInt32();
		result.StatsALU = reader.ReadInt32();
		result.StatsTEX = reader.ReadInt32();
		result.StatsFlow = reader.ReadInt32();
		if (hasStatsTempRegister)
		{
			result.StatsTempRegister = reader.ReadInt32();
		}

		int globalKeywordCount = reader.ReadInt32();
		result.GlobalKeywords = new List<string>(globalKeywordCount);
		for (int i = 0; i < globalKeywordCount; i++)
		{
			result.GlobalKeywords.Add(reader.ReadAlignedCountString());
		}

		if (hasLocalKeywords)
		{
			int localKeywordCount = reader.ReadInt32();
			result.LocalKeywords = new List<string>(localKeywordCount);
			for (int i = 0; i < localKeywordCount; i++)
			{
				result.LocalKeywords.Add(reader.ReadAlignedCountString());
			}
		}
		else
		{
			result.LocalKeywords = [];
		}

		int programDataSize = reader.ReadInt32();
		result.ProgramData = reader.ReadBytes(programDataSize);
		reader.Align();

		result.BindChannels = ParserBindChannels.Read(ref reader);

		if (version.LessThan(2021))
		{
			result.ShaderParams = ShaderParams.Read(ref reader, version, false);
		}

		return result;
	}

	public ShaderGpuProgramType GetProgramType(UnityVersion version)
	{
		if (version.GreaterThanOrEquals(5, 5))
		{
			return ((ShaderGpuProgramType55)ProgramType).ToGpuProgramType();
		}
		else
		{
			return ((ShaderGpuProgramType53)ProgramType).ToGpuProgramType();
		}
	}
}
