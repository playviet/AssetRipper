using AssetRipper.IO.Endian;
using System.Text;

namespace AssetRipper.Export.Modules.Shaders.Processor;

/// <summary>
/// The reading primitives that the compiled shader blob format needs on top of <see cref="EndianSpanReader"/>.
/// </summary>
internal static class ShaderBlobReader
{
	/// <summary>
	/// Reads a string stored as a length followed by that many bytes.
	/// </summary>
	extension(ref EndianSpanReader reader)
	{
		public string ReadCountString()
		{
			int length = reader.ReadInt32();
			if (length <= 0)
			{
				return "";
			}
			return Encoding.UTF8.GetString(reader.ReadBytesExact(length));
		}

		/// <summary>
		/// Reads a string and then aligns, which is how the blob stores almost all of its strings.
		/// </summary>
		public string ReadAlignedCountString()
		{
			string result = reader.ReadCountString();
			reader.Align();
			return result;
		}
	}
}
