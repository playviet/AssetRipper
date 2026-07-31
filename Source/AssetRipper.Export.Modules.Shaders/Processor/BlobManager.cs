using AssetRipper.Assets.Generics;
using AssetRipper.IO.Endian;
using AssetRipper.Primitives;
using AssetRipper.SourceGenerated.Classes.ClassID_48;
using K4os.Compression.LZ4;

namespace AssetRipper.Export.Modules.Shaders.Processor;

/// <summary>
/// The decompressed sub program blob of a shader, split into its individual entries.
/// </summary>
/// <remarks>
/// The blob starts with a table of <see cref="BlobEntry"/> that gives the offset and length of every entry
/// within the same buffer. Entries are addressed by the blob indices stored on the serialized sub programs.
/// </remarks>
public sealed class BlobManager
{
	private readonly byte[] blob;
	private readonly UnityVersion version;

	public List<BlobEntry> Entries { get; }

	public BlobManager(byte[] blob, UnityVersion version)
	{
		this.blob = blob;
		this.version = version;

		Entries = [];
		if (blob.Length < sizeof(int))
		{
			return;
		}

		using MemoryStream stream = new(blob, false);
		using BinaryReader reader = new(stream);
		int count = reader.ReadInt32();
		Entries.EnsureCapacity(count);
		for (int i = 0; i < count; i++)
		{
			Entries.Add(new BlobEntry(reader, version));
		}
	}

	/// <summary>
	/// Decompresses the sub program blob that a shader stored for one of its platforms.
	/// </summary>
	/// <param name="shader">The shader to read the compressed blob from.</param>
	/// <param name="platformIndex">The index into the shader's <c>Platforms</c> list.</param>
	public static BlobManager FromShader(IShader shader, int platformIndex)
	{
		return FromShader(shader, platformIndex, shader.Collection.Version);
	}

	/// <inheritdoc cref="FromShader(IShader, int)"/>
	/// <param name="version">The Unity version the blob was serialized with.</param>
	public static BlobManager FromShader(IShader shader, int platformIndex, UnityVersion version)
	{
		if (!TryGetBlobRange(shader, platformIndex, out uint offset, out uint compressedLength, out uint decompressedLength))
		{
			return new BlobManager([], version);
		}

		byte[] compressed = shader.CompressedBlob;
		if (offset + compressedLength > (uint)compressed.Length)
		{
			throw new InvalidDataException($"Shader blob {platformIndex} extends past the end of the compressed blob.");
		}

		byte[] decompressed = new byte[decompressedLength];
		int bytesWritten = LZ4Codec.Decode(compressed.AsSpan((int)offset, (int)compressedLength), decompressed);
		if (bytesWritten != decompressed.Length)
		{
			throw new InvalidDataException($"Decompressed shader blob {platformIndex} to {bytesWritten} bytes instead of {decompressed.Length}.");
		}

		return new BlobManager(decompressed, version);
	}

	/// <summary>
	/// Gets the decompressed bytes of a single blob entry.
	/// </summary>
	public byte[] GetRawEntry(int index)
	{
		return GetEntrySpan(index).ToArray();
	}

	public ShaderParams GetShaderParams(int index)
	{
		EndianSpanReader reader = new(GetEntrySpan(index), EndianType.LittleEndian);
		return ShaderParams.Read(ref reader, version, true);
	}

	public ShaderSubProgram GetShaderSubProgram(int index)
	{
		EndianSpanReader reader = new(GetEntrySpan(index), EndianType.LittleEndian);
		return ShaderSubProgram.Read(ref reader, version);
	}

	private ReadOnlySpan<byte> GetEntrySpan(int index)
	{
		BlobEntry entry = Entries[index];
		return blob.AsSpan(entry.Offset, entry.Length);
	}

	private static bool TryGetBlobRange(IShader shader, int platformIndex, out uint offset, out uint compressedLength, out uint decompressedLength)
	{
		offset = 0;
		compressedLength = 0;
		decompressedLength = 0;

		if (!shader.Has_CompressedBlob())
		{
			return false;
		}

		// 2019.3 and later store a list per platform, with one entry per shader stage. The first entry is the
		// blob that holds the sub programs; the later ones only exist for ray tracing and compute stages.
		if (shader.Has_Offsets_AssetList_AssetList_UInt32())
		{
			return TryGetNestedValue(shader.Offsets_AssetList_AssetList_UInt32, platformIndex, out offset)
				&& TryGetNestedValue(shader.CompressedLengths_AssetList_AssetList_UInt32, platformIndex, out compressedLength)
				&& TryGetNestedValue(shader.DecompressedLengths_AssetList_AssetList_UInt32, platformIndex, out decompressedLength);
		}

		if (shader.Has_Offsets_AssetList_UInt32())
		{
			return TryGetValue(shader.Offsets_AssetList_UInt32, platformIndex, out offset)
				&& TryGetValue(shader.CompressedLengths_AssetList_UInt32, platformIndex, out compressedLength)
				&& TryGetValue(shader.DecompressedLengths_AssetList_UInt32, platformIndex, out decompressedLength);
		}

		return false;
	}

	private static bool TryGetValue(AssetList<uint> list, int index, out uint value)
	{
		if ((uint)index < (uint)list.Count)
		{
			value = list[index];
			return true;
		}
		value = 0;
		return false;
	}

	private static bool TryGetNestedValue(AssetList<AssetList<uint>> list, int index, out uint value)
	{
		if ((uint)index < (uint)list.Count && list[index].Count > 0)
		{
			value = list[index][0];
			return true;
		}
		value = 0;
		return false;
	}
}
