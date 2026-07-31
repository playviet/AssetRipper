using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;

namespace AssetRipper.Export.Modules.Shaders.Processor;

/// <summary>
/// Decoder for the SMOL-V container format.
/// </summary>
/// <remarks>
/// <para>
/// SMOL-V is a lossless, size-oriented re-encoding of SPIR-V shader modules created by Aras Pranckevičius
/// (https://github.com/aras-p/smol-v). Unity uses it to store Vulkan shader programs, so a Vulkan sub program
/// blob contains SMOL-V rather than SPIR-V and has to be decoded before it can be disassembled or cross compiled.
/// </para>
/// <para>
/// The encoding relies on: varint encoding of most words, delta encoding of result / decoration IDs relative to
/// previously seen ones, a remapping of the most common opcodes into the 0..15 range, a shortened instruction
/// length field, and special compact forms for <c>OpVectorShuffle</c> and runs of <c>OpMemberDecorate</c>.
/// </para>
/// <para>
/// This is a port of the decoding half of <c>smolv.cpp</c> (MIT / public domain).
/// </para>
/// </remarks>
public static class SmolV
{
	/// <summary>
	/// Determines whether a buffer looks like a SMOL-V container, i.e. whether it starts with the "SMOL" magic word.
	/// </summary>
	/// <param name="data">Buffer to inspect. May be empty.</param>
	/// <returns>True if the buffer starts with the SMOL-V magic word.</returns>
	public static bool IsSmolV(ReadOnlySpan<byte> data)
	{
		return data.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(data) == SmolHeaderMagic;
	}

	/// <summary>
	/// Decodes a SMOL-V container back into the SPIR-V module it was produced from.
	/// </summary>
	/// <remarks>
	/// Never throws for malformed or truncated input; it returns false instead. The decoded size is taken from the
	/// container header and the decode only succeeds if the instruction stream fills that buffer exactly, which makes
	/// a successful result a fairly strong indication that the input really was SMOL-V.
	/// </remarks>
	/// <param name="smolv">The SMOL-V data, starting at the "SMOL" magic word.</param>
	/// <param name="spirv">The decoded SPIR-V words as little endian bytes, or null on failure.</param>
	/// <returns>True if the input was a valid SMOL-V container and decoded successfully.</returns>
	public static bool TryDecode(ReadOnlySpan<byte> smolv, [NotNullWhen(true)] out byte[]? spirv)
	{
		return TryDecode(smolv, out spirv, out _);
	}

	/// <summary>
	/// Decodes a SMOL-V container back into the SPIR-V module it was produced from, and reports how much of the
	/// input the container occupied.
	/// </summary>
	/// <remarks>
	/// Unity concatenates several SMOL-V containers in a single Vulkan sub program blob, so <paramref name="bytesRead"/>
	/// is where the next one starts. Trailing data after the container is ignored, not an error.
	/// </remarks>
	/// <param name="smolv">The SMOL-V data, starting at the "SMOL" magic word.</param>
	/// <param name="spirv">The decoded SPIR-V words as little endian bytes, or null on failure.</param>
	/// <param name="bytesRead">Number of input bytes the container occupied, or zero on failure.</param>
	/// <returns>True if the input was a valid SMOL-V container and decoded successfully.</returns>
	public static bool TryDecode(ReadOnlySpan<byte> smolv, [NotNullWhen(true)] out byte[]? spirv, out int bytesRead)
	{
		bytesRead = 0;
		if (!TryGetDecodedBufferSize(smolv, out int size))
		{
			spirv = null;
			return false;
		}

		byte[] buffer = new byte[size];
		if (Decode(smolv, buffer, false, out bytesRead))
		{
			spirv = buffer;
			return true;
		}

		// Version zero containers come in two mutually incompatible flavours that are indistinguishable from the
		// header alone: the current one, and the 2016-08-31 encoding that Unity 2017-2020 happens to emit. Since a
		// wrong guess essentially never fills the output buffer exactly, simply trying the other one is reliable.
		if (GetSmolVersion(smolv) == 0)
		{
			Array.Clear(buffer);
			if (Decode(smolv, buffer, true, out bytesRead))
			{
				spirv = buffer;
				return true;
			}
		}

		bytesRead = 0;
		spirv = null;
		return false;
	}

	private const uint SpirVHeaderMagic = 0x07230203;
	private const uint SmolHeaderMagic = 0x534D4F4C; // "SMOL"
	private const int SmolCurrentEncodingVersion = 1;

	/// <summary>
	/// Sanity limit for the decoded size declared by the header, so that a corrupt container cannot request an
	/// absurd allocation. Real shader modules are orders of magnitude smaller than this.
	/// </summary>
	private const int MaxDecodedSize = 64 * 1024 * 1024;

	private static bool Decode(ReadOnlySpan<byte> data, Span<byte> output, bool beforeZeroVersion, out int bytesRead)
	{
		bytesRead = 0;
		int inPos = 0;
		int outPos = 0;

		// Header: the SPIR-V magic is not stored, and the SPIR-V version word doubles as the SMOL-V version in its
		// top byte. The sixth word (decoded size) is consumed by the caller and not part of the SPIR-V output.
		if (!Write4(output, ref outPos, SpirVHeaderMagic))
		{
			return false;
		}
		inPos += 4;
		if (!Read4(data, ref inPos, out uint val))
		{
			return false;
		}
		int smolVersion = (int)(val >> 24);
		if (!Write4(output, ref outPos, val & 0x00FFFFFF))
		{
			return false;
		}
		for (int i = 0; i < 3; i++) // generator, bound, schema
		{
			if (!Read4(data, ref inPos, out val) || !Write4(output, ref outPos, val))
			{
				return false;
			}
		}
		inPos += 4; // decoded buffer size

		beforeZeroVersion &= smolVersion == 0;
		int knownOpsCount = GetKnownOpsCount(smolVersion);

		uint prevResult = 0;
		uint prevDecorate = 0;

		// Decoding stops as soon as the declared output size is reached instead of at the end of the input: the
		// container carries its exact decoded size, and Unity stores several containers back to back in one blob.
		while (inPos < data.Length && outPos < output.Length)
		{
			ReadLengthOp(data, ref inPos, out uint instrLen, out int op);
			// VectorShuffleCompact is not a real SPIR-V opcode; it marks a shuffle whose components all fit in 2 bits.
			bool wasSwizzle = op == OpVectorShuffleCompact;
			if (wasSwizzle)
			{
				op = OpVectorShuffle;
			}
			if (!Write4(output, ref outPos, unchecked((instrLen << 16) | (uint)op)))
			{
				return false;
			}

			uint ioffs = 1;

			if (OpHasType(op, knownOpsCount))
			{
				ReadVarint(data, ref inPos, out val);
				if (!Write4(output, ref outPos, val))
				{
					return false;
				}
				ioffs++;
			}
			if (OpHasResult(op, knownOpsCount))
			{
				ReadVarint(data, ref inPos, out val);
				val = unchecked(prevResult + ZigDecode(val));
				if (!Write4(output, ref outPos, val))
				{
					return false;
				}
				prevResult = val;
				ioffs++;
			}

			// Decorations target IDs that are close to the previously decorated one.
			if (op == OpDecorate || op == OpMemberDecorate)
			{
				ReadVarint(data, ref inPos, out val);
				// The "before zero" encoding did not zig-zag this delta.
				val = unchecked(prevDecorate + (beforeZeroVersion ? val : ZigDecode(val)));
				if (!Write4(output, ref outPos, val))
				{
					return false;
				}
				prevDecorate = val;
				ioffs++;
			}

			// A run of OpMemberDecorate instructions that decorate the same type is folded into a single encoded
			// instruction; only the first one has its op+length and target written by the code above.
			if (op == OpMemberDecorate && !beforeZeroVersion)
			{
				if (inPos >= data.Length)
				{
					return false;
				}
				int count = data[inPos++];
				uint prevIndex = 0;
				uint prevOffset = 0;
				for (int m = 0; m < count; ++m)
				{
					ReadVarint(data, ref inPos, out uint memberIndex);
					memberIndex = unchecked(memberIndex + prevIndex);
					prevIndex = memberIndex;

					ReadVarint(data, ref inPos, out uint memberDec);
					int knownExtraOps = DecorationExtraOps(memberDec);
					uint memberLen;
					if (knownExtraOps == -1)
					{
						ReadVarint(data, ref inPos, out memberLen);
						memberLen = unchecked(memberLen + 4);
					}
					else
					{
						memberLen = (uint)(4 + knownExtraOps);
					}

					if (m != 0)
					{
						if (!Write4(output, ref outPos, unchecked((memberLen << 16) | (uint)op)) ||
							!Write4(output, ref outPos, prevDecorate))
						{
							return false;
						}
					}
					if (!Write4(output, ref outPos, memberIndex) || !Write4(output, ref outPos, memberDec))
					{
						return false;
					}

					if (memberDec == 35) // Offset decorations are delta encoded against the previous offset
					{
						if (memberLen != 5)
						{
							return false;
						}
						ReadVarint(data, ref inPos, out val);
						val = unchecked(val + prevOffset);
						if (!Write4(output, ref outPos, val))
						{
							return false;
						}
						prevOffset = val;
					}
					else
					{
						for (uint i = 4; i < memberLen; ++i)
						{
							ReadVarint(data, ref inPos, out val);
							if (!Write4(output, ref outPos, val))
							{
								return false;
							}
						}
					}
				}
				continue;
			}

			// Operands that tend to reference recently produced values are stored relative to the result ID.
			int relativeCount = OpDeltaFromResult(op, knownOpsCount);
			bool zigDecodeVals = true;
			if (beforeZeroVersion)
			{
				// Only control flow / barrier ops used zig-zag in that encoding.
				if (op is not OpControlBarrier and not OpMemoryBarrier and not OpLoopMerge and not OpSelectionMerge
					and not OpBranch and not OpBranchConditional and not OpMemoryNamedBarrier)
				{
					zigDecodeVals = false;
				}
			}
			for (int i = 0; i < relativeCount && ioffs < instrLen; ++i, ++ioffs)
			{
				ReadVarint(data, ref inPos, out val);
				if (zigDecodeVals)
				{
					val = ZigDecode(val);
				}
				if (!Write4(output, ref outPos, unchecked(prevResult - val)))
				{
					return false;
				}
			}

			if (wasSwizzle && instrLen <= 9)
			{
				// Up to four 2-bit shuffle components packed into a single byte, most significant pair first.
				if (inPos >= data.Length)
				{
					return false;
				}
				uint swizzle = data[inPos++];
				if (instrLen > 5 && !Write4(output, ref outPos, (swizzle >> 6) & 3))
				{
					return false;
				}
				if (instrLen > 6 && !Write4(output, ref outPos, (swizzle >> 4) & 3))
				{
					return false;
				}
				if (instrLen > 7 && !Write4(output, ref outPos, (swizzle >> 2) & 3))
				{
					return false;
				}
				if (instrLen > 8 && !Write4(output, ref outPos, swizzle & 3))
				{
					return false;
				}
			}
			else if (OpVarRest(op, knownOpsCount))
			{
				for (; ioffs < instrLen; ++ioffs)
				{
					ReadVarint(data, ref inPos, out val);
					if (!Write4(output, ref outPos, val))
					{
						return false;
					}
				}
			}
			else
			{
				for (; ioffs < instrLen; ++ioffs)
				{
					if (!Read4(data, ref inPos, out val) || !Write4(output, ref outPos, val))
					{
						return false;
					}
				}
			}
		}

		// The decoded stream has to fill the declared buffer exactly, otherwise something was misinterpreted.
		if (outPos != output.Length)
		{
			return false;
		}
		bytesRead = inPos;
		return true;
	}

	private static bool TryGetDecodedBufferSize(ReadOnlySpan<byte> data, out int size)
	{
		size = 0;
		if (!CheckSmolHeader(data))
		{
			return false;
		}
		uint declared = BinaryPrimitives.ReadUInt32LittleEndian(data[20..]);
		if (declared < 20 || declared % 4 != 0 || declared > MaxDecodedSize)
		{
			return false;
		}
		size = (int)declared;
		return true;
	}

	private static bool CheckSmolHeader(ReadOnlySpan<byte> data)
	{
		if (data.Length < 24) // five header words plus the decoded length word
		{
			return false;
		}
		if (BinaryPrimitives.ReadUInt32LittleEndian(data) != SmolHeaderMagic)
		{
			return false;
		}
		uint versionWord = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
		uint spirvVersion = versionWord & 0x00FFFFFF;
		if (spirvVersion is < 0x00010000 or > 0x00010600) // only SPIR-V 1.0 through 1.6
		{
			return false;
		}
		return versionWord >> 24 <= SmolCurrentEncodingVersion;
	}

	private static int GetSmolVersion(ReadOnlySpan<byte> data)
	{
		return data.Length < 8 ? -1 : (int)(BinaryPrimitives.ReadUInt32LittleEndian(data[4..]) >> 24);
	}

	private static bool Read4(ReadOnlySpan<byte> data, ref int pos, out uint value)
	{
		if (pos < 0 || pos + 4 > data.Length)
		{
			value = 0;
			return false;
		}
		value = BinaryPrimitives.ReadUInt32LittleEndian(data[pos..]);
		pos += 4;
		return true;
	}

	private static bool Write4(Span<byte> buffer, ref int pos, uint value)
	{
		if (pos + 4 > buffer.Length)
		{
			return false;
		}
		BinaryPrimitives.WriteUInt32LittleEndian(buffer[pos..], value);
		pos += 4;
		return true;
	}

	/// <summary>
	/// Reads a variable length unsigned integer: the high bit of each byte says whether more bytes follow, the
	/// remaining seven bits are payload, least significant group first.
	/// </summary>
	/// <remarks>
	/// Running out of data is deliberately not an error here, matching the reference implementation: the value read
	/// so far is returned and the position stays at the end. Truncated input is instead caught by the final
	/// output size check, which such a stream can no longer satisfy.
	/// </remarks>
	private static void ReadVarint(ReadOnlySpan<byte> data, ref int pos, out uint value)
	{
		uint v = 0;
		int shift = 0;
		while (pos < data.Length)
		{
			byte b = data[pos];
			v |= unchecked((uint)(b & 127) << shift);
			shift += 7;
			pos++;
			if ((b & 128) == 0)
			{
				break;
			}
		}
		value = v;
	}

	/// <summary>
	/// Undoes zig-zag encoding, which maps small negative deltas onto small unsigned values.
	/// The result is the bit pattern of the signed value; callers add it with wrapping arithmetic.
	/// </summary>
	private static uint ZigDecode(uint u)
	{
		return (u & 1) != 0 ? (u >> 1) ^ 0xFFFFFFFF : u >> 1;
	}

	/// <summary>
	/// Swaps the most frequently used opcodes with rarely used ones below 16, so that they fit into a single
	/// varint byte together with the instruction length. The mapping is its own inverse.
	/// </summary>
	private static int RemapOp(int op)
	{
		return op switch
		{
			OpDecorate => OpNop,
			OpNop => OpDecorate,
			OpLoad => OpUndef,
			OpUndef => OpLoad,
			OpStore => OpSourceContinued,
			OpSourceContinued => OpStore,
			OpAccessChain => OpSource,
			OpSource => OpAccessChain,
			OpVectorShuffle => OpSourceExtension,
			OpSourceExtension => OpVectorShuffle,
			OpMemberDecorate => OpString,
			OpString => OpMemberDecorate,
			OpLabel => OpLine,
			OpLine => OpLabel,
			OpVariable => 9,
			9 => OpVariable,
			OpFMul => OpExtension,
			OpExtension => OpFMul,
			OpFAdd => OpExtInstImport,
			OpExtInstImport => OpFAdd,
			OpTypePointer => OpMemoryModel,
			OpMemoryModel => OpTypePointer,
			OpFNegate => OpEntryPoint,
			OpEntryPoint => OpFNegate,
			_ => op,
		};
	}

	/// <summary>
	/// Restores the real instruction length. Lengths are stored biased so that the common instructions,
	/// which have a guaranteed minimum length, end up with a value below 8 and therefore fit in three bits.
	/// </summary>
	private static uint DecodeLen(int op, uint len)
	{
		unchecked
		{
			len++;
			if (op is OpVectorShuffle or OpVectorShuffleCompact)
			{
				len += 4;
			}
			if (op == OpDecorate)
			{
				len += 2;
			}
			if (op is OpLoad or OpAccessChain)
			{
				len += 3;
			}
			return len;
		}
	}

	/// <summary>
	/// Reads the combined length + opcode word. SPIR-V packs these as 0xLLLLOOOO; SMOL-V shuffles them into
	/// 0xLLLOOOLO so that the common case (op &lt; 16, length &lt; 8) occupies a single varint byte.
	/// </summary>
	private static void ReadLengthOp(ReadOnlySpan<byte> data, ref int pos, out uint outLen, out int outOp)
	{
		ReadVarint(data, ref pos, out uint val);
		uint len = ((val >> 20) << 4) | ((val >> 4) & 0xF);
		int op = (int)(((val >> 4) & 0xFFF0) | (val & 0xF));

		outOp = RemapOp(op);
		outLen = DecodeLen(outOp, len);
	}

	private static int DecorationExtraOps(uint dec)
	{
		if (dec == 0 || (dec >= 2 && dec <= 5)) // RelaxedPrecision, Block..ColMajor
		{
			return 0;
		}
		if (dec >= 29 && dec <= 37) // Stream..XfbStride
		{
			return 1;
		}
		return -1; // unknown decoration, the length was encoded explicitly
	}

	/// <summary>
	/// The instruction table grows with each SMOL-V version, and the encoding depends on it, so older files must
	/// only be looked up in the prefix of the table that existed when they were written.
	/// </summary>
	private static int GetKnownOpsCount(int version)
	{
		return version switch
		{
			0 => OpModuleProcessed + 1,
			1 => OpGroupNonUniformQuadSwap + 1, // 2020 February
			_ => 0,
		};
	}

	private static bool OpHasResult(int op, int opsCount) => (GetOpData(op, opsCount) & 1) != 0;

	private static bool OpHasType(int op, int opsCount) => (GetOpData(op, opsCount) & 2) != 0;

	private static bool OpVarRest(int op, int opsCount) => (GetOpData(op, opsCount) & 4) != 0;

	private static int OpDeltaFromResult(int op, int opsCount) => GetOpData(op, opsCount) >> 4;

	private static byte GetOpData(int op, int opsCount)
	{
		return op < 0 || op >= opsCount || op >= OpData.Length ? (byte)0 : OpData[op];
	}

	private const int OpNop = 0;
	private const int OpUndef = 1;
	private const int OpSourceContinued = 2;
	private const int OpSource = 3;
	private const int OpSourceExtension = 4;
	private const int OpString = 7;
	private const int OpLine = 8;
	private const int OpExtension = 10;
	private const int OpExtInstImport = 11;
	private const int OpVectorShuffleCompact = 13; // not in SPIR-V, added for SMOL-V
	private const int OpMemoryModel = 14;
	private const int OpEntryPoint = 15;
	private const int OpTypePointer = 32;
	private const int OpVariable = 59;
	private const int OpLoad = 61;
	private const int OpStore = 62;
	private const int OpAccessChain = 65;
	private const int OpDecorate = 71;
	private const int OpMemberDecorate = 72;
	private const int OpVectorShuffle = 79;
	private const int OpFNegate = 127;
	private const int OpFAdd = 129;
	private const int OpFMul = 133;
	private const int OpControlBarrier = 224;
	private const int OpMemoryBarrier = 225;
	private const int OpLoopMerge = 246;
	private const int OpSelectionMerge = 247;
	private const int OpLabel = 248;
	private const int OpBranch = 249;
	private const int OpBranchConditional = 250;
	private const int OpMemoryNamedBarrier = 329;
	private const int OpModuleProcessed = 330;
	private const int OpGroupNonUniformQuadSwap = 366;

	/// <summary>
	/// Per opcode encoding metadata, indexed by SPIR-V opcode. Bit 0: has a result ID. Bit 1: has a type ID.
	/// Bit 2: remaining words are varint encoded. Bits 4-7: how many words after the optional type and result IDs
	/// are stored as deltas from the result ID.
	/// </summary>
	private static ReadOnlySpan<byte> OpData =>
	[
		0x00, 0x03, 0x00, 0x04, 0x00, 0x00, 0x00, 0x00, // 0..7
		0x04, 0x03, 0x00, 0x01, 0x07, 0x27, 0x04, 0x04, // 8..15
		0x04, 0x04, 0x03, 0x05, 0x05, 0x05, 0x05, 0x05, // 16..23
		0x05, 0x05, 0x05, 0x05, 0x05, 0x05, 0x05, 0x05, // 24..31
		0x05, 0x05, 0x05, 0x05, 0x05, 0x05, 0x05, 0x04, // 32..39
		0x03, 0x03, 0x03, 0x03, 0x93, 0x07, 0x03, 0x03, // 40..47
		0x03, 0x03, 0x03, 0x93, 0x03, 0x03, 0x07, 0x03, // 48..55
		0x00, 0x93, 0x03, 0x07, 0x03, 0x17, 0x24, 0x00, // 56..63
		0x00, 0x07, 0x03, 0x03, 0x03, 0x03, 0x03, 0x04, // 64..71
		0x04, 0x01, 0x00, 0x00, 0x03, 0x17, 0x27, 0x27, // 72..79
		0x93, 0x17, 0x27, 0x13, 0x03, 0x03, 0x03, 0x27, // 80..87
		0x27, 0x37, 0x37, 0x27, 0x27, 0x37, 0x37, 0x27, // 88..95
		0x37, 0x37, 0x27, 0x34, 0x13, 0x13, 0x13, 0x23, // 96..103
		0x13, 0x23, 0x13, 0x13, 0x03, 0x13, 0x13, 0x13, // 104..111
		0x13, 0x13, 0x13, 0x13, 0x13, 0x13, 0x13, 0x13, // 112..119
		0x13, 0x13, 0x13, 0x17, 0x13, 0x03, 0x13, 0x13, // 120..127
		0x23, 0x23, 0x23, 0x23, 0x23, 0x23, 0x23, 0x23, // 128..135
		0x23, 0x23, 0x23, 0x23, 0x23, 0x23, 0x23, 0x23, // 136..143
		0x23, 0x23, 0x23, 0x23, 0x23, 0x23, 0x23, 0x23, // 144..151
		0x23, 0x03, 0x13, 0x13, 0x13, 0x13, 0x13, 0x13, // 152..159
		0x13, 0x23, 0x23, 0x23, 0x23, 0x23, 0x23, 0x23, // 160..167
		0x13, 0x33, 0x23, 0x23, 0x23, 0x23, 0x23, 0x23, // 168..175
		0x23, 0x23, 0x23, 0x23, 0x23, 0x23, 0x23, 0x23, // 176..183
		0x23, 0x23, 0x23, 0x23, 0x23, 0x23, 0x23, 0x23, // 184..191
		0x03, 0x03, 0x23, 0x23, 0x23, 0x23, 0x23, 0x23, // 192..199
		0x13, 0x43, 0x33, 0x33, 0x13, 0x13, 0x03, 0x03, // 200..207
		0x03, 0x03, 0x03, 0x03, 0x03, 0x03, 0x03, 0x03, // 208..215
		0x03, 0x03, 0x00, 0x00, 0x00, 0x00, 0x03, 0x03, // 216..223
		0x30, 0x20, 0x03, 0x03, 0x00, 0x03, 0x03, 0x03, // 224..231
		0x03, 0x03, 0x03, 0x03, 0x03, 0x03, 0x03, 0x03, // 232..239
		0x03, 0x03, 0x03, 0x03, 0x03, 0x03, 0x24, 0x14, // 240..247
		0x01, 0x10, 0x34, 0x00, 0x00, 0x00, 0x00, 0x00, // 248..255
		0x00, 0x00, 0x03, 0x03, 0x00, 0x03, 0x03, 0x03, // 256..263
		0x03, 0x03, 0x03, 0x03, 0x03, 0x03, 0x03, 0x03, // 264..271
		0x03, 0x03, 0x03, 0x03, 0x03, 0x03, 0x03, 0x03, // 272..279
		0x00, 0x00, 0x03, 0x03, 0x03, 0x03, 0x03, 0x00, // 280..287
		0x00, 0x03, 0x03, 0x03, 0x03, 0x03, 0x03, 0x03, // 288..295
		0x03, 0x00, 0x00, 0x03, 0x03, 0x00, 0x00, 0x03, // 296..303
		0x03, 0x27, 0x27, 0x37, 0x37, 0x27, 0x27, 0x37, // 304..311
		0x37, 0x27, 0x37, 0x37, 0x13, 0x00, 0x03, 0x00, // 312..319
		0x03, 0x03, 0x03, 0x03, 0x03, 0x03, 0x03, 0x03, // 320..327
		0x07, 0x24, 0x03, 0x04, 0x04, 0x17, 0x17, 0x17, // 328..335
		0x17, 0x17, 0x17, 0x17, 0x17, 0x17, 0x17, 0x17, // 336..343
		0x17, 0x17, 0x17, 0x17, 0x17, 0x17, 0x17, 0x17, // 344..351
		0x17, 0x17, 0x17, 0x17, 0x17, 0x17, 0x17, 0x17, // 352..359
		0x17, 0x17, 0x17, 0x17, 0x17, 0x17, 0x17, // 360..366
	];
}
