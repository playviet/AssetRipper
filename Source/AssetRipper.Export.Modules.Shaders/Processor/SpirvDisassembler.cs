using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace AssetRipper.Export.Modules.Shaders.Processor;

/// <summary>
/// Disassembles a SPIR-V module into human readable text in the style of Khronos' <c>spirv-dis</c>.
/// </summary>
/// <remarks>
/// <para>
/// Unity emits Vulkan shader programs as SPIR-V. Recovering GLSL from them requires a full
/// cross compiler; this class instead produces a faithful disassembly, which needs no native
/// dependencies and is enough to inspect what a shader actually does.
/// </para>
/// <para>
/// The instruction, enumeration and generator tables live in the generated companion file
/// <c>SpirvDisassembler.Tables.cs</c> and mirror the published Khronos grammar.
/// </para>
/// <para>
/// Input is untrusted: it comes from arbitrary game files. No method here throws on malformed
/// data. Every word read is bounds checked, and each instruction is decoded strictly within the
/// word range declared by its own header, so a corrupt instruction cannot desynchronise the ones
/// that follow it.
/// </para>
/// </remarks>
public static partial class SpirvDisassembler
{
	/// <summary>The first word of every little endian SPIR-V module.</summary>
	private const uint MagicNumber = 0x07230203;

	/// <summary>The magic number as it appears when the module was written by a big endian producer.</summary>
	private const uint ReversedMagicNumber = 0x03022307;

	/// <summary>Words in the module header: magic, version, generator, id bound, schema.</summary>
	private const int HeaderWordCount = 5;

	/// <summary>
	/// Width of the result-id column. <c>spirv-dis</c> right aligns <c>%result</c> in this column
	/// and follows it with <c>" = "</c>, so instructions without a result are indented by 15.
	/// </summary>
	private const int ResultColumnWidth = 12;

	/// <summary>
	/// Disassembles a SPIR-V module.
	/// </summary>
	/// <param name="spirv">
	/// The raw module bytes. Both little endian and byte swapped (big endian) modules are accepted.
	/// </param>
	/// <returns>
	/// The disassembly text, or <see langword="null"/> when <paramref name="spirv"/> is not a SPIR-V
	/// module. A module that is valid but truncated yields a partial disassembly rather than
	/// <see langword="null"/>.
	/// </returns>
	public static string? TryDisassemble(ReadOnlySpan<byte> spirv)
	{
		uint[]? words = TryReadWords(spirv);
		return words is null ? null : Disassemble(words);
	}

	/// <summary>
	/// Copies the module into a word array, normalising byte order.
	/// </summary>
	/// <remarks>
	/// Working from a <see cref="uint"/> array rather than the original span keeps the endian fix up
	/// in one place and lets the rest of the disassembler make several passes over the module.
	/// </remarks>
	private static uint[]? TryReadWords(ReadOnlySpan<byte> spirv)
	{
		// A trailing partial word cannot belong to any instruction, so ignore it rather than reject
		// the module; the truncation is reported by the disassembly simply ending early.
		int wordCount = spirv.Length / sizeof(uint);
		if (wordCount < HeaderWordCount)
		{
			return null;
		}

		uint magic = BinaryPrimitives.ReadUInt32LittleEndian(spirv);
		bool swap;
		if (magic == MagicNumber)
		{
			swap = false;
		}
		else if (magic == ReversedMagicNumber)
		{
			swap = true;
		}
		else
		{
			return null;
		}

		uint[] words = new uint[wordCount];
		for (int i = 0; i < wordCount; i++)
		{
			uint word = BinaryPrimitives.ReadUInt32LittleEndian(spirv.Slice(i * sizeof(uint), sizeof(uint)));
			words[i] = swap ? BinaryPrimitives.ReverseEndianness(word) : word;
		}
		return words;
	}

	private static string Disassemble(uint[] words)
	{
		List<Instruction> instructions = ParseInstructions(words);
		Module module = new(words);
		module.Analyse(instructions);

		StringBuilder builder = new();
		WriteHeader(builder, words);
		foreach (Instruction instruction in instructions)
		{
			module.WriteInstruction(builder, instruction);
		}
		return builder.ToString();
	}

	private static void WriteHeader(StringBuilder builder, uint[] words)
	{
		uint version = words[1];
		uint generator = words[2];
		uint bound = words[3];
		uint schema = words[4];

		// The version word packs the major and minor numbers into its middle two bytes.
		uint major = (version >> 16) & 0xFF;
		uint minor = (version >> 8) & 0xFF;

		// The generator word packs the registered tool id above its own version number.
		uint generatorId = generator >> 16;
		uint generatorVersion = generator & 0xFFFF;

		builder.Append("; SPIR-V\n");
		builder.Append(CultureInfo.InvariantCulture, $"; Version: {major}.{minor}\n");
		builder.Append(CultureInfo.InvariantCulture, $"; Generator: {GeneratorName(generatorId)}; {generatorVersion}\n");
		builder.Append(CultureInfo.InvariantCulture, $"; Bound: {bound}\n");
		builder.Append(CultureInfo.InvariantCulture, $"; Schema: {schema}\n");
	}

	private static string GeneratorName(uint generatorId)
	{
		foreach ((uint id, string name) in GeneratorTable)
		{
			if (id == generatorId)
			{
				return name;
			}
		}
		return string.Create(CultureInfo.InvariantCulture, $"Unknown({generatorId})");
	}

	/// <summary>
	/// Splits the module body into instructions.
	/// </summary>
	/// <remarks>
	/// Each instruction begins with a word holding the opcode in its low half and the total word
	/// count, including that header word, in its high half. A zero word count would never advance,
	/// and a count running past the end of the buffer means the module was truncated; both end the
	/// walk, the latter after keeping the words that are actually present.
	/// </remarks>
	private static List<Instruction> ParseInstructions(uint[] words)
	{
		List<Instruction> instructions = [];
		int position = HeaderWordCount;
		while (position < words.Length)
		{
			uint header = words[position];
			uint opcode = header & 0xFFFF;
			int wordCount = (int)(header >> 16);
			if (wordCount < 1)
			{
				break;
			}

			// Subtracting from the length rather than adding to the position avoids any overflow.
			bool truncated = wordCount > words.Length - position;
			int end = truncated ? words.Length : position + wordCount;

			instructions.Add(new Instruction(opcode, position + 1, end));
			if (truncated)
			{
				break;
			}
			position = end;
		}
		return instructions;
	}

	/// <summary>An instruction located within the module, as the opcode plus its operand word range.</summary>
	private readonly record struct Instruction(uint Opcode, int OperandStart, int OperandEnd);

	/// <summary>
	/// Per-module state: the debug names recovered from the module and the type information needed
	/// to render literals whose meaning depends on their type.
	/// </summary>
	private sealed class Module(uint[] words)
	{
		private readonly uint[] words = words;

		/// <summary>Friendly name per id, from <c>OpName</c> where present and synthesised otherwise.</summary>
		private readonly Dictionary<uint, string> names = [];

		/// <summary>Names already handed out, so that duplicates can be given a numeric suffix.</summary>
		private readonly HashSet<string> usedNames = [];

		/// <summary>Extended instruction set name per <c>OpExtInstImport</c> result id.</summary>
		private readonly Dictionary<uint, string> extendedSets = [];

		/// <summary>Width and signedness of every <c>OpTypeInt</c>, keyed by type id.</summary>
		private readonly Dictionary<uint, (uint Width, bool Signed)> integerTypes = [];

		/// <summary>Width of every <c>OpTypeFloat</c>, keyed by type id.</summary>
		private readonly Dictionary<uint, uint> floatTypes = [];

		private uint Word(int index, int end) => (uint)index < (uint)end && index < words.Length ? words[index] : 0;

		/// <summary>
		/// Single analysis pass over the module: recovers debug names, records the declarations that
		/// later rendering depends on, and gives every result id a name.
		/// </summary>
		/// <remarks>
		/// <para>
		/// Everything happens in one ordered walk because the names a module ends up with depend on
		/// the order they are claimed in. <c>OpName</c> appears in the debug section, ahead of the
		/// decorations and the type and constant declarations, so an explicit name always wins over a
		/// synthesised one, and the numeric suffix that disambiguates repeated names follows the order
		/// the module itself lists them in.
		/// </para>
		/// <para>
		/// A single forward pass is enough because SPIR-V requires types and constants to be declared
		/// before use, so a name a nested type needs has already been assigned by the time it is read.
		/// </para>
		/// </remarks>
		public void Analyse(List<Instruction> instructions)
		{
			foreach (Instruction instruction in instructions)
			{
				int start = instruction.OperandStart;
				int end = instruction.OperandEnd;
				if (start >= end)
				{
					continue;
				}

				// Declarations that later instructions are rendered against.
				switch (instruction.Opcode)
				{
					case OpExtInstImport:
						extendedSets[Word(start, end)] = ReadString(start + 1, end, out _);
						break;
					case OpTypeInt:
						integerTypes[Word(start, end)] = (Word(start + 1, end), Word(start + 2, end) != 0);
						break;
					case OpTypeFloat:
						floatTypes[Word(start, end)] = Word(start + 1, end);
						break;
				}

				NameInstruction(instruction);
			}
		}

		private void NameInstruction(Instruction instruction)
		{
			int start = instruction.OperandStart;
			int end = instruction.OperandEnd;

			switch (instruction.Opcode)
			{
				case OpName:
					SaveName(Word(start, end), ReadString(start + 1, end, out _));
					return;
				case OpDecorate:
					// A variable that carries no OpName is still identifiable when it is decorated as a
					// built in, which is how gl_Position and friends stay readable in stripped modules.
					if (Word(start + 1, end) == DecorationBuiltIn && end - start > 2)
					{
						SaveBuiltInName(Word(start, end), Word(start + 2, end));
					}
					return;
			}

			if (!TryGetResultId(instruction, out uint result))
			{
				return;
			}

			// Types and constants describe themselves well enough to name themselves, which keeps a
			// module compiled without debug info readable as %v3float rather than %13.
			switch (instruction.Opcode)
			{
				case OpTypeVoid:
					SaveName(result, "void");
					break;
				case OpTypeBool:
					SaveName(result, "bool");
					break;
				case OpTypeInt:
					{
						uint width = Word(start + 1, end);
						string prefix = string.Empty;
						string root;
						switch (width)
						{
							case 8:
								root = "char";
								break;
							case 16:
								root = "short";
								break;
							case 32:
								root = "int";
								break;
							case 64:
								root = "long";
								break;
							default:
								root = width.ToString(CultureInfo.InvariantCulture);
								prefix = "i";
								break;
						}
						if (Word(start + 2, end) == 0)
						{
							prefix = "u";
						}
						SaveName(result, prefix + root);
					}
					break;
				case OpTypeFloat:
					// The optional encoding operand distinguishes formats that share a bit width,
					// such as the several eight bit floats, so it names the type when present.
					if (end - start > 2 && FloatEncodingName(Word(start + 2, end)) is string encoding)
					{
						SaveName(result, encoding);
						break;
					}
					SaveName(result, Word(start + 1, end) switch
					{
						16 => "half",
						32 => "float",
						64 => "double",
						uint width => string.Create(CultureInfo.InvariantCulture, $"fp{width}"),
					});
					break;
				case OpTypeVector:
					SaveName(result, string.Create(CultureInfo.InvariantCulture, $"v{Word(start + 2, end)}{NameOf(Word(start + 1, end))}"));
					break;
				case OpTypeMatrix:
					SaveName(result, string.Create(CultureInfo.InvariantCulture, $"mat{Word(start + 2, end)}{NameOf(Word(start + 1, end))}"));
					break;
				case OpTypeArray:
					SaveName(result, $"_arr_{NameOf(Word(start + 1, end))}_{NameOf(Word(start + 2, end))}");
					break;
				case OpTypeRuntimeArray:
					SaveName(result, $"_runtimearr_{NameOf(Word(start + 1, end))}");
					break;
				case OpTypePointer:
					SaveName(result, $"_ptr_{EnumerantName(StorageClassKind, Word(start + 1, end))}_{NameOf(Word(start + 2, end))}");
					break;
				case OpTypeUntypedPointerKHR:
					// An untyped pointer names no pointee, so the storage class is all there is.
					SaveName(result, "_ptr_" + EnumerantName(StorageClassKind, Word(start + 1, end)));
					break;
				case OpTypeNodePayloadArrayAMDX:
					SaveName(result, $"_payloadarr_{NameOf(Word(start + 1, end))}");
					break;
				case OpTypePipe:
					SaveName(result, "Pipe" + EnumerantName(AccessQualifierKind, Word(start + 1, end)));
					break;
				case OpTypeEvent:
					SaveName(result, "Event");
					break;
				case OpTypeDeviceEvent:
					SaveName(result, "DeviceEvent");
					break;
				case OpTypeReserveId:
					SaveName(result, "ReserveId");
					break;
				case OpTypeQueue:
					SaveName(result, "Queue");
					break;
				case OpTypeOpaque:
					SaveName(result, "Opaque_" + Sanitize(ReadString(start + 1, end, out _)));
					break;
				case OpTypePipeStorage:
					SaveName(result, "PipeStorage");
					break;
				case OpTypeNamedBarrier:
					SaveName(result, "NamedBarrier");
					break;
				case OpTypeStruct:
					// A struct has no concise description, so only mark it as one and keep its number.
					SaveName(result, string.Create(CultureInfo.InvariantCulture, $"_struct_{result}"));
					break;
				case OpConstantTrue:
					SaveName(result, "true");
					break;
				case OpConstantFalse:
					SaveName(result, "false");
					break;
				case OpConstant:
					{
						// Name a scalar constant after its type and value, giving %float_0_5 and %int_n1.
						uint typeId = Word(start, end);
						string value = FormatContextNumber(typeId, start + 2, end, out _).Replace('-', 'n');
						SaveName(result, $"{NameOf(typeId)}_{value}");
					}
					break;
				default:
					// Claim the id's own number, so that an OpName spelled like a number cannot take
					// the name another id would otherwise print under.
					SaveName(result, result.ToString(CultureInfo.InvariantCulture));
					break;
			}
		}

		/// <summary>Names the narrow floating point encodings, which the bit width alone cannot separate.</summary>
		private static string? FloatEncodingName(uint encoding) => encoding switch
		{
			0 => "bfloat16",
			4214 => "fp8e4m3",
			4215 => "fp8e5m2",
			4223 => "fp6e2m3",
			4224 => "fp6e3m2",
			4225 => "fp4e2m1",
			4226 => "fp8e8m0",
			4227 => "mxint8",
			_ => null,
		};

		private void SaveBuiltInName(uint target, uint builtIn)
		{
			foreach ((uint value, string name) in BuiltInNameTable)
			{
				if (value == builtIn)
				{
					SaveName(target, name);
					return;
				}
			}
		}

		/// <summary>
		/// Finds an instruction's result id, which is whichever operand the grammar marks as
		/// <c>IdResult</c>: the first operand, or the second when the instruction is also typed.
		/// </summary>
		private bool TryGetResultId(Instruction instruction, out uint result)
		{
			result = 0;
			if (!ParsedInstructions.TryGetValue(instruction.Opcode, out ParsedInstruction? info))
			{
				return false;
			}

			int index = instruction.OperandStart;
			foreach (Operand operand in info.Operands)
			{
				if (operand.Kind == OperandKind.Result)
				{
					result = Word(index, instruction.OperandEnd);
					return result != 0;
				}
				if (operand.Kind is not OperandKind.ResultType)
				{
					return false;
				}
				index++;
			}
			return false;
		}

		/// <summary>
		/// Claims a name for an id. The first claim wins, and a name already taken by another id gets
		/// the lowest free numeric suffix, so that every id prints under a distinct name.
		/// </summary>
		private void SaveName(uint id, string suggested)
		{
			if (names.ContainsKey(id))
			{
				return;
			}

			// An unbounded name from an untrusted module would be copied into every line that
			// mentions it, so fall back to the plain number once it stops being a useful label.
			string stem = suggested.Length > MaximumNameLength
				? id.ToString(CultureInfo.InvariantCulture)
				: Sanitize(suggested);

			string name = stem;
			if (!usedNames.Add(name))
			{
				for (uint index = 0; ; index++)
				{
					name = string.Create(CultureInfo.InvariantCulture, $"{stem}_{index}");
					if (usedNames.Add(name))
					{
						break;
					}
				}
			}
			names[id] = name;
		}

		/// <summary>
		/// Reduces an arbitrary <c>OpName</c> string to something usable as an identifier by
		/// replacing each character that cannot appear in one with an underscore.
		/// </summary>
		private static string Sanitize(string suggested)
		{
			if (suggested.Length == 0)
			{
				return "_";
			}

			return string.Create(suggested.Length, suggested, static (span, source) =>
			{
				for (int i = 0; i < span.Length; i++)
				{
					char c = source[i];
					span[i] = char.IsAsciiLetterOrDigit(c) || c == '_' ? c : '_';
				}
			});
		}

		private string NameOf(uint id)
		{
			return names.TryGetValue(id, out string? name) ? name : id.ToString(CultureInfo.InvariantCulture);
		}

		// ---- rendering -------------------------------------------------------------------------

		public void WriteInstruction(StringBuilder builder, Instruction instruction)
		{
			int start = instruction.OperandStart;
			int end = instruction.OperandEnd;

			if (!ParsedInstructions.TryGetValue(instruction.Opcode, out ParsedInstruction? info))
			{
				// Unknown opcode: name it by number and dump the operand words verbatim, so that an
				// extension this build predates degrades into something inspectable instead of failing.
				builder.Append(' ', ResultColumnWidth + 3);
				builder.Append(CultureInfo.InvariantCulture, $"Op{instruction.Opcode}");
				for (int i = start; i < end; i++)
				{
					builder.Append(CultureInfo.InvariantCulture, $" {words[i]}");
				}
				builder.Append('\n');
				return;
			}

			StringBuilder operands = new();
			int position = start;
			string? resultText = null;
			uint lastId = 0;

			foreach (Operand operand in info.Operands)
			{
				if (operand.Quantifier == Quantifier.One)
				{
					if (operand.Kind == OperandKind.Result)
					{
						resultText = "%" + NameOf(Word(position, end));
						position++;
						continue;
					}
					if (position >= end)
					{
						break;
					}
					WriteOperand(operands, operand, ref position, end, ref lastId);
				}
				else
				{
					// Optional operands appear at most once, variadic ones repeat; both are present
					// only for as long as the instruction's own word count says there is data left.
					do
					{
						if (position >= end)
						{
							break;
						}
						WriteOperand(operands, operand, ref position, end, ref lastId);
					}
					while (operand.Quantifier == Quantifier.Variadic);
				}
			}

			// OpSpecConstantOp carries the operands of the deferred opcode, which the grammar cannot
			// describe; anything left over there is an id. Elsewhere leftovers mean the module used a
			// newer form of the instruction, so show the raw words.
			bool trailingAreIds = instruction.Opcode == OpSpecConstantOp;
			while (position < end)
			{
				operands.Append(trailingAreIds
					? $" %{NameOf(words[position])}"
					: string.Create(CultureInfo.InvariantCulture, $" {words[position]}"));
				position++;
			}

			if (resultText is null)
			{
				builder.Append(' ', ResultColumnWidth + 3);
			}
			else
			{
				builder.Append(resultText.PadLeft(ResultColumnWidth)).Append(" = ");
			}
			builder.Append(info.Name).Append(operands).Append('\n');
		}

		private void WriteOperand(StringBuilder builder, Operand operand, ref int position, int end, ref uint lastId)
		{
			switch (operand.Kind)
			{
				case OperandKind.ResultType:
				case OperandKind.Id:
					{
						uint id = Word(position, end);
						position++;
						lastId = id;
						builder.Append(" %").Append(NameOf(id));
					}
					break;
				case OperandKind.LiteralInteger:
					builder.Append(CultureInfo.InvariantCulture, $" {Word(position, end)}");
					position++;
					break;
				case OperandKind.LiteralString:
					{
						string text = ReadString(position, end, out int consumed);
						position += consumed;
						builder.Append(' ').Append(Quote(text));
					}
					break;
				case OperandKind.ContextNumber:
					{
						// The width and signedness of the literal come from the instruction's result
						// type, which is always the operand before the result id.
						uint typeId = Word(position - 2, end);
						builder.Append(' ').Append(FormatContextNumber(typeId, position, end, out int consumed));
						position += consumed;
					}
					break;
				case OperandKind.ExtInstNumber:
					{
						// The set was named by the immediately preceding id operand.
						builder.Append(' ').Append(ExtendedInstructionName(lastId, Word(position, end)));
						position++;
					}
					break;
				case OperandKind.SpecOpNumber:
					{
						uint opcode = Word(position, end);
						position++;
						// The deferred opcode is spelled without its "Op" prefix, as OpSpecConstantOp
						// already names the category.
						builder.Append(' ').Append(ParsedInstructions.TryGetValue(opcode, out ParsedInstruction? info)
							? info.Name.StartsWith("Op", StringComparison.Ordinal) ? info.Name[2..] : info.Name
							: opcode.ToString(CultureInfo.InvariantCulture));
					}
					break;
				case OperandKind.PairLiteralId:
					builder.Append(CultureInfo.InvariantCulture, $" {Word(position, end)}");
					builder.Append(" %").Append(NameOf(Word(position + 1, end)));
					position += 2;
					break;
				case OperandKind.PairIdLiteral:
					builder.Append(" %").Append(NameOf(Word(position, end)));
					builder.Append(CultureInfo.InvariantCulture, $" {Word(position + 1, end)}");
					position += 2;
					break;
				case OperandKind.PairIdId:
					builder.Append(" %").Append(NameOf(Word(position, end)));
					builder.Append(" %").Append(NameOf(Word(position + 1, end)));
					position += 2;
					break;
				case OperandKind.Enum:
					WriteEnum(builder, ParsedEnumKinds[operand.EnumIndex], ref position, end, ref lastId);
					break;
			}
		}

		/// <summary>
		/// Renders an enumerated operand. Value enums map to a single name; bit enums decompose into
		/// their set bits joined by <c>|</c>. Either form may carry extra operands of its own, which
		/// follow in the word stream in the order the bits were listed.
		/// </summary>
		private void WriteEnum(StringBuilder builder, ParsedEnumKind kind, ref int position, int end, ref uint lastId)
		{
			uint value = Word(position, end);
			position++;

			if (!kind.IsBitEnum)
			{
				ParsedEnumerant? enumerant = kind.Find(value);
				builder.Append(' ').Append(enumerant?.Name ?? value.ToString(CultureInfo.InvariantCulture));
				if (enumerant is not null)
				{
					WriteParameters(builder, enumerant.Parameters, ref position, end, ref lastId);
				}
				return;
			}

			if (value == 0)
			{
				builder.Append(' ').Append(kind.Find(0)?.Name ?? "None");
				return;
			}

			builder.Append(' ');
			bool first = true;
			List<ParsedEnumerant> present = [];
			for (int bit = 0; bit < 32; bit++)
			{
				uint mask = 1u << bit;
				if ((value & mask) == 0)
				{
					continue;
				}
				ParsedEnumerant? enumerant = kind.Find(mask);
				if (!first)
				{
					builder.Append('|');
				}
				first = false;
				builder.Append(enumerant?.Name ?? mask.ToString(CultureInfo.InvariantCulture));
				if (enumerant is not null)
				{
					present.Add(enumerant);
				}
			}

			// Parameters for every set bit follow the mask word, in ascending bit order.
			foreach (ParsedEnumerant enumerant in present)
			{
				WriteParameters(builder, enumerant.Parameters, ref position, end, ref lastId);
			}
		}

		private void WriteParameters(StringBuilder builder, Operand[] parameters, ref int position, int end, ref uint lastId)
		{
			foreach (Operand parameter in parameters)
			{
				if (position >= end)
				{
					return;
				}
				WriteOperand(builder, parameter, ref position, end, ref lastId);
			}
		}

		private string ExtendedInstructionName(uint setId, uint number)
		{
			if (extendedSets.TryGetValue(setId, out string? set) && set == "GLSL.std.450")
			{
				foreach ((uint candidate, string name) in GlslStd450Table)
				{
					if (candidate == number)
					{
						return name;
					}
				}
			}
			return number.ToString(CultureInfo.InvariantCulture);
		}

		/// <summary>
		/// Reads a <c>LiteralString</c>: UTF-8 bytes packed four per word, low byte first, ending at
		/// the first zero byte. The terminator is part of the last word, which is why the number of
		/// words consumed has to be reported back rather than derived from the text length.
		/// </summary>
		private string ReadString(int position, int end, out int consumed)
		{
			consumed = 0;
			if (position >= end)
			{
				return string.Empty;
			}

			List<byte> bytes = [];
			for (int index = position; index < end; index++)
			{
				uint word = words[index];
				consumed++;
				for (int shift = 0; shift < 32; shift += 8)
				{
					// Mask before narrowing: the project compiles with checked arithmetic, under which
					// a plain cast of the upper bytes would throw instead of truncating.
					byte b = (byte)((word >> shift) & 0xFF);
					if (b == 0)
					{
						return Encoding.UTF8.GetString(bytes.ToArray());
					}
					bytes.Add(b);
				}
			}
			// Unterminated string: the module is truncated, so return what was there.
			return Encoding.UTF8.GetString(bytes.ToArray());
		}

		/// <summary>
		/// Formats a <c>LiteralContextDependentNumber</c>, whose width, signedness and
		/// interpretation are all dictated by the type the constant was declared with.
		/// </summary>
		private string FormatContextNumber(uint typeId, int position, int end, out int consumed)
		{
			if (floatTypes.TryGetValue(typeId, out uint floatWidth))
			{
				switch (floatWidth)
				{
					case 64 when end - position >= 2:
						{
							consumed = 2;
							// A 64-bit literal is stored low word first.
							ulong bits = Word(position, end) | ((ulong)Word(position + 1, end) << 32);
							return FormatDouble(BitConverter.UInt64BitsToDouble(bits));
						}
					case 16:
						consumed = 1;
						return FormatHalf((ushort)(Word(position, end) & 0xFFFF));
					default:
						consumed = 1;
						return FormatSingle(BitConverter.UInt32BitsToSingle(Word(position, end)));
				}
			}

			if (integerTypes.TryGetValue(typeId, out (uint Width, bool Signed) integer))
			{
				if (integer.Width == 64 && end - position >= 2)
				{
					consumed = 2;
					ulong bits = Word(position, end) | ((ulong)Word(position + 1, end) << 32);
					// Reinterpreting the bits as signed is the whole point here, so it must not be checked.
					return integer.Signed
						? unchecked((long)bits).ToString(CultureInfo.InvariantCulture)
						: bits.ToString(CultureInfo.InvariantCulture);
				}

				consumed = 1;
				uint word = Word(position, end);
				if (!integer.Signed)
				{
					return word.ToString(CultureInfo.InvariantCulture);
				}
				// Narrow types are sign extended from their declared width before printing. The shift
				// pair is a bit manipulation rather than arithmetic, so it must not be checked.
				int shift = integer.Width is > 0 and < 32 ? 32 - (int)integer.Width : 0;
				return unchecked((int)word << shift >> shift).ToString(CultureInfo.InvariantCulture);
			}

			consumed = 1;
			return Word(position, end).ToString(CultureInfo.InvariantCulture);
		}
	}

	/// <summary>
	/// Formats a float with enough significant digits to round trip.
	/// </summary>
	/// <remarks>
	/// Values that decimal notation cannot state exactly, meaning infinities, NaNs and subnormals,
	/// are written in the hexadecimal significand form instead, which is what <c>spirv-dis</c> does
	/// and what keeps the text reassemblable.
	/// </remarks>
	private static string FormatSingle(float value)
	{
		if (!float.IsNormal(value) && value != 0)
		{
			return HexFloat(BitConverter.SingleToUInt32Bits(value), SingleMantissaBits, SingleExponentBits);
		}
		return NormaliseExponent(value.ToString("G9", CultureInfo.InvariantCulture));
	}

	private static string FormatDouble(double value)
	{
		if (!double.IsNormal(value) && value != 0)
		{
			return HexFloat(BitConverter.DoubleToUInt64Bits(value), DoubleMantissaBits, DoubleExponentBits);
		}
		return NormaliseExponent(value.ToString("G17", CultureInfo.InvariantCulture));
	}

	/// <summary>
	/// Formats a 16 bit float, always in the hexadecimal significand form.
	/// </summary>
	/// <remarks>
	/// Unlike the wider types, half has no decimal spelling that is both short and exactly round
	/// tripping, so <c>spirv-dis</c> writes every half in hex and this follows suit.
	/// </remarks>
	private static string FormatHalf(ushort bits) => HexFloat(bits, HalfMantissaBits, HalfExponentBits);

	private const int HalfMantissaBits = 10;
	private const int HalfExponentBits = 5;
	private const int SingleMantissaBits = 23;
	private const int SingleExponentBits = 8;
	private const int DoubleMantissaBits = 52;
	private const int DoubleExponentBits = 11;

	/// <summary>
	/// Rewrites .NET's <c>E+20</c> exponent as the lower case <c>e+20</c> form that C, and therefore
	/// <c>spirv-dis</c>, produces. Both pad the exponent to at least two digits.
	/// </summary>
	private static string NormaliseExponent(string text)
	{
		int index = text.IndexOf('E');
		if (index < 0)
		{
			return text;
		}

		string mantissa = text[..index];
		string exponent = text[(index + 1)..];
		char sign = '+';
		if (exponent.Length > 0 && (exponent[0] == '+' || exponent[0] == '-'))
		{
			sign = exponent[0];
			exponent = exponent[1..];
		}
		return $"{mantissa}e{sign}{exponent.PadLeft(2, '0')}";
	}

	/// <summary>
	/// Renders a float in the <c>0x1.8p+128</c> form: an explicit leading one, the significand as
	/// hexadecimal, and the unbiased power of two.
	/// </summary>
	/// <remarks>
	/// The exponent is printed unbiased, so infinities and NaNs come out one past the largest finite
	/// exponent. A subnormal has no implicit leading one, so it is first renormalised by shifting its
	/// significand up until the leading one appears, lowering the exponent to compensate.
	/// </remarks>
	private static string HexFloat(ulong bits, int mantissaBits, int exponentBits)
	{
		ulong mantissaMask = (1UL << mantissaBits) - 1;
		bool negative = (bits >> (mantissaBits + exponentBits)) != 0;
		ulong mantissa = bits & mantissaMask;
		int biasedExponent = (int)((bits >> mantissaBits) & ((1UL << exponentBits) - 1));
		int bias = (1 << (exponentBits - 1)) - 1;

		if (biasedExponent == 0 && mantissa == 0)
		{
			// Zero has no leading one to make explicit.
			return negative ? "-0x0p+0" : "0x0p+0";
		}

		int exponent;
		if (biasedExponent == 0)
		{
			// Subnormal: the value is 0.mantissa x 2^(1-bias), so shift the highest set bit up into
			// the implicit position and drop the exponent by one for every place moved.
			exponent = 1 - bias;
			while (mantissa != 0 && (mantissa & (1UL << mantissaBits)) == 0)
			{
				mantissa <<= 1;
				exponent--;
			}
			mantissa &= mantissaMask;
		}
		else
		{
			exponent = biasedExponent - bias;
		}

		StringBuilder builder = new();
		if (negative)
		{
			builder.Append('-');
		}
		builder.Append("0x1");
		if (mantissa != 0)
		{
			// The significand is printed as whole nibbles, left aligned, with trailing zeroes dropped.
			int nibbles = (mantissaBits + 3) / 4;
			ulong aligned = mantissa << (nibbles * 4 - mantissaBits);
			string digits = aligned.ToString("x", CultureInfo.InvariantCulture).PadLeft(nibbles, '0').TrimEnd('0');
			if (digits.Length > 0)
			{
				builder.Append('.').Append(digits);
			}
		}
		builder.Append(CultureInfo.InvariantCulture, $"p{(exponent < 0 ? '-' : '+')}{Math.Abs(exponent)}");
		return builder.ToString();
	}

	/// <summary>Quotes a string literal the way the SPIR-V assembly grammar expects it.</summary>
	private static string Quote(string text)
	{
		StringBuilder builder = new(text.Length + 2);
		builder.Append('"');
		foreach (char c in text)
		{
			if (c is '"' or '\\')
			{
				builder.Append('\\');
			}
			builder.Append(c);
		}
		builder.Append('"');
		return builder.ToString();
	}

	// ---- opcodes the disassembler itself has to understand -------------------------------------
	// Everything else is handled generically through the tables; these are the instructions whose
	// contents feed naming and literal formatting.

	private const uint OpName = 5;
	private const uint OpExtInstImport = 11;
	private const uint OpDecorate = 71;
	private const uint OpTypeVoid = 19;
	private const uint OpTypeBool = 20;
	private const uint OpTypeInt = 21;
	private const uint OpTypeFloat = 22;
	private const uint OpTypeVector = 23;
	private const uint OpTypeMatrix = 24;
	private const uint OpTypeArray = 28;
	private const uint OpTypeRuntimeArray = 29;
	private const uint OpTypeStruct = 30;
	private const uint OpTypeOpaque = 31;
	private const uint OpTypePointer = 32;
	private const uint OpTypePipe = 38;
	private const uint OpTypeUntypedPointerKHR = 4417;
	private const uint OpTypeNodePayloadArrayAMDX = 5076;
	private const uint OpTypeEvent = 34;
	private const uint OpTypeDeviceEvent = 35;
	private const uint OpTypeReserveId = 36;
	private const uint OpTypeQueue = 37;
	private const uint OpTypePipeStorage = 322;
	private const uint OpTypeNamedBarrier = 327;
	private const uint OpConstantTrue = 41;
	private const uint OpConstantFalse = 42;
	private const uint OpConstant = 43;
	private const uint OpSpecConstantOp = 52;

	/// <summary>The <c>Decoration</c> enumerant that attaches a built in role to a variable.</summary>
	private const uint DecorationBuiltIn = 11;

	/// <summary>
	/// Longest <c>OpName</c> accepted as a label. A name is repeated on every line that mentions the
	/// id, so an absurdly long one from a corrupt module is dropped in favour of the id number.
	/// </summary>
	private const int MaximumNameLength = 256;

	// ---- table model ---------------------------------------------------------------------------

	/// <summary>A row of the generated enumerated operand kind table.</summary>
	private sealed record EnumKind(string Name, bool IsBitEnum, Enumerant[] Enumerants);

	/// <summary>A single value of an enumerated operand kind, with any operands it introduces.</summary>
	private sealed record Enumerant(uint Value, string Name, string Parameters);

	/// <summary>A row of the generated core instruction table.</summary>
	private sealed record InstructionInfo(uint Opcode, string Name, string Signature);

	/// <summary>How many times an operand may occur.</summary>
	private enum Quantifier : byte
	{
		One,
		Optional,
		Variadic,
	}

	/// <summary>The operand shapes the disassembler distinguishes when decoding words.</summary>
	private enum OperandKind : byte
	{
		ResultType,
		Result,
		Id,
		LiteralInteger,
		LiteralString,
		ContextNumber,
		ExtInstNumber,
		SpecOpNumber,
		PairLiteralId,
		PairIdLiteral,
		PairIdId,
		Enum,
	}

	private readonly record struct Operand(OperandKind Kind, int EnumIndex, Quantifier Quantifier);

	private sealed class ParsedEnumerant(uint value, string name, Operand[] parameters)
	{
		public uint Value { get; } = value;
		public string Name { get; } = name;
		public Operand[] Parameters { get; } = parameters;
	}

	private sealed class ParsedEnumKind(string name, bool isBitEnum, Dictionary<uint, ParsedEnumerant> byValue)
	{
		public string Name { get; } = name;
		public bool IsBitEnum { get; } = isBitEnum;
		private readonly Dictionary<uint, ParsedEnumerant> byValue = byValue;

		public ParsedEnumerant? Find(uint value) => byValue.GetValueOrDefault(value);
	}

	private sealed class ParsedInstruction(string name, Operand[] operands)
	{
		public string Name { get; } = name;
		public Operand[] Operands { get; } = operands;
	}

	private static readonly ParsedEnumKind[] ParsedEnumKinds;
	private static readonly Dictionary<uint, ParsedInstruction> ParsedInstructions;
	private static readonly int StorageClassKind;
	private static readonly int AccessQualifierKind;

	static SpirvDisassembler()
	{
		// Enumerated kinds are resolved by name from the signature strings, so index them first.
		Dictionary<string, int> kindIndices = new(EnumKindTable.Length);
		for (int i = 0; i < EnumKindTable.Length; i++)
		{
			kindIndices[EnumKindTable[i].Name] = i;
		}
		EnumKindIndices = kindIndices;
		StorageClassKind = kindIndices.GetValueOrDefault("StorageClass", -1);
		AccessQualifierKind = kindIndices.GetValueOrDefault("AccessQualifier", -1);

		ParsedEnumKinds = new ParsedEnumKind[EnumKindTable.Length];
		for (int i = 0; i < EnumKindTable.Length; i++)
		{
			EnumKind kind = EnumKindTable[i];
			Dictionary<uint, ParsedEnumerant> byValue = new(kind.Enumerants.Length);
			foreach (Enumerant enumerant in kind.Enumerants)
			{
				byValue[enumerant.Value] = new ParsedEnumerant(enumerant.Value, enumerant.Name, ParseSignature(enumerant.Parameters));
			}
			ParsedEnumKinds[i] = new ParsedEnumKind(kind.Name, kind.IsBitEnum, byValue);
		}

		ParsedInstructions = new Dictionary<uint, ParsedInstruction>(InstructionTable.Length);
		foreach (InstructionInfo info in InstructionTable)
		{
			ParsedInstructions[info.Opcode] = new ParsedInstruction(info.Name, ParseSignature(info.Signature));
		}
	}

	private static readonly Dictionary<string, int> EnumKindIndices;

	/// <summary>
	/// Parses an operand signature from the generated tables.
	/// </summary>
	/// <remarks>
	/// A signature is a space separated list of operands. Fixed operand shapes use a single
	/// lower case letter; an enumerated operand is spelled with its kind name from the grammar,
	/// which keeps the generated tables legible. A trailing <c>?</c> marks an optional operand and
	/// <c>*</c> a variadic one.
	/// </remarks>
	private static Operand[] ParseSignature(string signature)
	{
		if (signature.Length == 0)
		{
			return [];
		}

		string[] tokens = signature.Split(' ', StringSplitOptions.RemoveEmptyEntries);
		List<Operand> operands = new(tokens.Length);
		foreach (string rawToken in tokens)
		{
			string token = rawToken;
			Quantifier quantifier = Quantifier.One;
			if (token.EndsWith('?'))
			{
				quantifier = Quantifier.Optional;
				token = token[..^1];
			}
			else if (token.EndsWith('*'))
			{
				quantifier = Quantifier.Variadic;
				token = token[..^1];
			}

			if (token.Length == 1)
			{
				OperandKind kind = token[0] switch
				{
					't' => OperandKind.ResultType,
					'd' => OperandKind.Result,
					'r' => OperandKind.Id,
					'i' => OperandKind.LiteralInteger,
					's' => OperandKind.LiteralString,
					'c' => OperandKind.ContextNumber,
					'x' => OperandKind.ExtInstNumber,
					'o' => OperandKind.SpecOpNumber,
					'p' => OperandKind.PairLiteralId,
					'q' => OperandKind.PairIdLiteral,
					'n' => OperandKind.PairIdId,
					_ => OperandKind.LiteralInteger,
				};
				operands.Add(new Operand(kind, -1, quantifier));
			}
			else if (EnumKindIndices.TryGetValue(token, out int index))
			{
				operands.Add(new Operand(OperandKind.Enum, index, quantifier));
			}
			else
			{
				// An unrecognised kind still occupies exactly one word, so keep the operand stream aligned.
				operands.Add(new Operand(OperandKind.LiteralInteger, -1, quantifier));
			}
		}
		return operands.ToArray();
	}

	/// <summary>Looks up the spelling of a value in an enumerated operand kind.</summary>
	private static string EnumerantName(int kindIndex, uint value)
	{
		if ((uint)kindIndex < (uint)ParsedEnumKinds.Length && ParsedEnumKinds[kindIndex].Find(value) is ParsedEnumerant enumerant)
		{
			return enumerant.Name;
		}
		return value.ToString(CultureInfo.InvariantCulture);
	}
}
