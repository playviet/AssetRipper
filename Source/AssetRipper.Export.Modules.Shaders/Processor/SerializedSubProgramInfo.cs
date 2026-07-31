using AssetRipper.SourceGenerated.Subclasses.SerializedPlayerSubProgram;
using AssetRipper.SourceGenerated.Subclasses.SerializedSubProgram;

namespace AssetRipper.Export.Modules.Shaders.Processor;

/// <summary>
/// One compiled variant of a <see cref="SerializedProgramInfo"/>: which GPU program type it targets,
/// which keywords it was compiled with, and which blob entry holds its bytecode.
/// </summary>
public sealed class SerializedSubProgramInfo
{
	public List<ushort> KeywordIndices { get; set; } = [];
	public sbyte GpuProgramType { get; set; }
	public uint BlobIndex { get; set; }

	public SerializedSubProgramInfo(ISerializedSubProgram subProgram)
	{
		// Before 2019.1 the keywords were stored as strings on the sub program instead of
		// as indices into the pass name table, so the field is absent on older versions.
		KeywordIndices = subProgram.Has_KeywordIndices() ? [.. subProgram.KeywordIndices] : [];
		GpuProgramType = subProgram.GpuProgramType;
		BlobIndex = subProgram.BlobIndex;
	}

	public SerializedSubProgramInfo(ISerializedPlayerSubProgram subProgram)
	{
		KeywordIndices = [.. subProgram.KeywordIndices];
		GpuProgramType = subProgram.GpuProgramType;
		BlobIndex = subProgram.BlobIndex;
	}
}
