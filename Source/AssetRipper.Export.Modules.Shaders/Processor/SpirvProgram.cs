using System.Text;

namespace AssetRipper.Export.Modules.Shaders.Processor;

/// <summary>
/// Recovers the SPIR-V that a Vulkan build stores for a shader program.
/// </summary>
public static class SpirvProgram
{
	/// <summary>
	/// Disassembles every SPIR-V module in a Vulkan shader program.
	/// </summary>
	/// <remarks>
	/// Unity compresses SPIR-V with SMOL-V, and one program holds several containers laid end to end, so they are
	/// walked until the data runs out and their disassemblies are concatenated.
	/// </remarks>
	/// <returns>The disassembly, or null when the program holds no readable SPIR-V.</returns>
	public static string? TryDisassemble(ReadOnlySpan<byte> programData)
	{
		StringBuilder builder = new();
		int offset = FindContainer(programData, 0);
		int count = 0;
		while (offset >= 0)
		{
			if (!SmolV.TryDecode(programData[offset..], out byte[]? spirv, out int consumed))
			{
				break;
			}

			string? assembly = SpirvDisassembler.TryDisassemble(spirv);
			if (assembly is not null)
			{
				if (count > 0)
				{
					builder.AppendLine();
				}
				builder.AppendLine($"; SPIR-V module {count}");
				builder.Append(assembly);
				count++;
			}

			// A container is at least one word long, so this always advances and the walk cannot spin.
			offset = FindContainer(programData, offset + Math.Max(consumed, 4));
		}
		return count > 0 ? builder.ToString() : null;
	}

	/// <summary>
	/// Finds the next SMOL-V container. Its magic is stored little endian, so the bytes read as "LOMS".
	/// </summary>
	private static int FindContainer(ReadOnlySpan<byte> data, int start)
	{
		for (int i = Math.Max(0, start); i + 4 <= data.Length; i++)
		{
			if (data[i] == 'L' && data[i + 1] == 'O' && data[i + 2] == 'M' && data[i + 3] == 'S')
			{
				return i;
			}
		}
		return -1;
	}
}
