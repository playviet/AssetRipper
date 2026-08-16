// probe <libil2cpp.so> <global-metadata.dat> <unityVersion> dump <typeSubstring> [methodName]
// probe <libil2cpp.so> <global-metadata.dat> <unityVersion> count [assemblySubstring]
//
// "dump" prints the converted ISIL of one method. "count" walks every method and reports how often
// each thing we cannot yet translate appears, which is the number the recovery work is measured on.
using AssetRipper.Primitives;
using Cpp2IL.Core;
using Cpp2IL.Core.Api;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using LibCpp2IL;
using Cpp2IL.Core.Utils;

InstructionSetRegistry.RegisterInstructionSet<X86InstructionSet>(DefaultInstructionSets.X86_32);
InstructionSetRegistry.RegisterInstructionSet<X86InstructionSet>(DefaultInstructionSets.X86_64);
InstructionSetRegistry.RegisterInstructionSet<WasmInstructionSet>(DefaultInstructionSets.WASM);
InstructionSetRegistry.RegisterInstructionSet<ArmV7InstructionSet>(DefaultInstructionSets.ARM_V7);
InstructionSetRegistry.RegisterInstructionSet<NewArmV8InstructionSet>(DefaultInstructionSets.ARM_V8);
LibCpp2IlBinaryRegistry.RegisterBuiltInBinarySupport();

Cpp2IlApi.InitializeLibCpp2Il(args[0], args[1], UnityVersion.Parse(args[2]), false);
ApplicationAnalysisContext app = Cpp2IlApi.CurrentAppContext;

foreach (Cpp2IlProcessingLayer layer in new Cpp2IlProcessingLayer[] { new Cpp2IL.Core.ProcessingLayers.AttributeAnalysisProcessingLayer() })
{
	layer.PreProcess(app, []);
	layer.Process(app);
}

// The export runs a second processing layer, AssetRipper's MethodOverrideNameFixer, which renames an override
// to the name of the method it overrides. Anything that resolves a method by name therefore sees a different
// model in the export than probe used to show. Replicated here so a dump predicts the export.
if (Environment.GetEnvironmentVariable("PROBE_NO_OVERRIDE_NAMES") != "1")
{
	HashSet<MethodAnalysisContext> renamed = new();
	void FixOverrideName(MethodAnalysisContext m)
	{
		if (!renamed.Add(m)) return;
		MethodAnalysisContext? based = m.BaseMethod;
		if (based is null) return;
		FixOverrideName(based is ConcreteGenericMethodAnalysisContext generic ? generic.BaseMethodContext : based);
		if (based.Name != m.Name) m.OverrideName = based.Name;
	}
	foreach (TypeAnalysisContext t in app.AllTypes)
		foreach (MethodAnalysisContext m in t.Methods)
			if (m is not ConcreteGenericMethodAnalysisContext)
				FixOverrideName(m);
}

if (args[3] == "sharedbody")
{
	// A generic method DEFINITION whose registered code pointer is also one of its own instantiations'
	// pointers has no body of its own - il2cpp shares nothing for ordinary value types, so what is
	// registered against the definition is one specialisation's code.
	var byBase = new Dictionary<MethodAnalysisContext, List<ConcreteGenericMethodAnalysisContext>>();

	foreach (ConcreteGenericMethodAnalysisContext concrete in app.ConcreteGenericMethodsByRef.Values)
	{
		if (!byBase.TryGetValue(concrete.BaseMethodContext, out var list))
			byBase[concrete.BaseMethodContext] = list = new();
		list.Add(concrete);
	}

	int defs = 0, shared = 0, borrowed = 0;

	foreach (TypeAnalysisContext type in app.AllTypes)
	foreach (MethodAnalysisContext m in type.Methods)
	{
		if (m is ConcreteGenericMethodAnalysisContext || m.GenericParameters.Count == 0 || m.UnderlyingPointer == 0)
			continue;

		defs++;
		byBase.TryGetValue(m, out var instantiations);
		var match = instantiations?.FirstOrDefault(c => c.UnderlyingPointer == m.UnderlyingPointer);

		if (match == null)
		{
			shared++;
			continue;
		}

		// A stand-in argument means the body really is the shared one and the definition may be written
		// generically. A CONCRETE argument means il2cpp compiled no shared body at all - what is registered
		// against the definition is that specialisation's code.
		bool StandIn(TypeAnalysisContext g) => g.FullName is "System.Object"
			or "Unity.IL2CPP.Metadata.__Il2CppFullySharedGenericType"
			or "System.Int32Enum" or "System.Int16Enum" or "System.SByteEnum"
			or "System.UInt32Enum" or "System.UInt16Enum" or "System.ByteEnum" or "System.Int64Enum" or "System.UInt64Enum";

		if (match.MethodGenericParameters.Count > 0 && match.MethodGenericParameters.All(StandIn))
		{
			shared++;
			continue;
		}

		borrowed++;
		string args2 = string.Join(", ", match.MethodGenericParameters.Select(g => g.FullName));
		bool ours = type.DeclaringAssembly?.Name == "Assembly-CSharp";
		Console.WriteLine($"{(ours ? "OURS     " : "elsewhere")} {type.FullName}::{m.Name} @ {m.UnderlyingPointer:X}  is <{args2}>  of {instantiations!.Count}");
	}

	Console.WriteLine($"\ngeneric method definitions with a body        : {defs}");
	Console.WriteLine($"  a genuine SHARED body (stand-in arguments)  : {shared}");
	Console.WriteLine($"  a SPECIALISATION's body under an open name  : {borrowed}");
	return;
}

if (args[3] == "getters")
{
	foreach (TypeAnalysisContext type in app.AllTypes)
	{
		if (type.FullName != args[4]) continue;
		foreach (PropertyAnalysisContext property in type.Properties)
		{
			if (property.Getter is not { IsStatic: true } getter || getter.UnderlyingPointer == 0) continue;
			Console.WriteLine($"--- {type.FullName}.{property.Name} : {property.PropertyType.FullName}  @ {getter.UnderlyingPointer:X} attrs={getter.Attributes}");
			foreach (Disarm.Arm64Instruction i in Cpp2IL.Core.Utils.NewArm64Utils.GetArm64MethodBodyAtVirtualAddress(app.Binary, getter.UnderlyingPointer, false, 20))
				Console.WriteLine("    " + i);
		}
		foreach (FieldAnalysisContext field in type.Fields)
			if (field.IsStatic) Console.WriteLine($"    field {field.Name} offset={field.BackingData?.FieldOffset:X} attrs={field.Attributes}");
	}
	return;
}

// badops - every instruction the disassembler could not decode, by its encoding rather than by its name.
// The lifter only ever sees the sentinel, so the raw word has to be read back out of the binary to find out
// what family of instruction is actually missing.
if (args[3] == "badops")
{
	Dictionary<string, int> byClass = new();
	Dictionary<string, string> example = new();
	int total = 0, hitMethods = 0;

	foreach (TypeAnalysisContext type in app.AllTypes)
	{
		foreach (MethodAnalysisContext m in type.Methods)
		{
			if (m.UnderlyingPointer == 0) continue;
			bool counted = false;

			foreach (Disarm.Arm64Instruction i in Cpp2IL.Core.Utils.NewArm64Utils.GetArm64MethodBodyAtVirtualAddress(app.Binary, m.UnderlyingPointer))
			{
				if (i.Mnemonic != Disarm.Arm64Mnemonic.INVALID && i.Mnemonic != Disarm.Arm64Mnemonic.UNIMPLEMENTED) continue;
				if (!app.Binary.TryMapVirtualAddressToRaw(i.Address, out long raw) || raw <= 0) continue;

				System.ReadOnlySpan<byte> content = app.Binary.GetRawBinaryContent();
				if (raw + 4 > content.Length) continue;

				uint word = System.BitConverter.ToUInt32(content.Slice((int)raw, 4));
				total++;
				if (!counted) { hitMethods++; counted = true; }

				// op0 (bits 28-25) is the architecture's own first split of the encoding space, and the
				// three bits above it separate the groups inside it.
				Console.Error.WriteLine($"{word:X8}\t{i.Address:X}\t{type.DeclaringAssembly.Name}\t{type.Name}::{m.Name}");
				string key = $"op0={(word >> 25) & 0xF:X1} hi={(word >> 29) & 0x7:X1}";
				byClass[key] = byClass.GetValueOrDefault(key) + 1;
				if (!example.ContainsKey(key)) example[key] = $"{word:X8} @ {i.Address:X} in {type.Name}::{m.Name}";
			}
		}
	}

	Console.WriteLine($"{total} undecoded instructions in {hitMethods} methods");
	foreach (KeyValuePair<string, int> e in byClass.OrderByDescending(x => x.Value).Take(14))
		Console.WriteLine($"{e.Value,6}  {e.Key}   e.g. {example[e.Key]}");
	return;
}

// opinfo <MNEMONIC> - how Disarm presents one mnemonic's operands, which is the only way to find out which
// property carries the condition code and the flag immediate before writing a case for it.
// refusals - every word the lane model would not take, with what it knew about the registers at the time.
if (args[3] == "refusals")
{
	typeof(Cpp2IL.Core.InstructionSets.NewArmV8InstructionSet).Assembly
		.GetType("Cpp2IL.Core.InstructionSets.VectorLanes")!
		.GetField("Reporting", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
		.SetValue(null, true);

	foreach (TypeAnalysisContext type in app.AllTypes)
	{
		foreach (MethodAnalysisContext m in type.Methods)
		{
			if (m.UnderlyingPointer == 0) continue;
			try { m.Analyze(); } catch { }
		}
	}

	object queue = typeof(Cpp2IL.Core.InstructionSets.NewArmV8InstructionSet).Assembly
		.GetType("Cpp2IL.Core.InstructionSets.VectorLanes")!
		.GetField("Refused", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
		.GetValue(null)!;

	foreach (string line in (System.Collections.Concurrent.ConcurrentQueue<string>)queue)
		Console.WriteLine(line);

	return;
}

// qcheck - does the disassembler scale a 128 bit load or store's immediate? Compared against the word.
if (args[3] == "qcheck")
{
	int agree = 0, scaledBy16 = 0, other = 0;
	System.Collections.Generic.List<string> examples = new();

	foreach (TypeAnalysisContext type in app.AllTypes)
	{
		foreach (MethodAnalysisContext m in type.Methods)
		{
			if (m.UnderlyingPointer == 0) continue;

			foreach (Disarm.Arm64Instruction i in Cpp2IL.Core.Utils.NewArm64Utils.GetArm64MethodBodyAtVirtualAddress(app.Binary, m.UnderlyingPointer))
			{
				if (i.Mnemonic is not (Disarm.Arm64Mnemonic.LDR or Disarm.Arm64Mnemonic.STR)) continue;
				if (i.Op0Reg is < Disarm.InternalDisassembly.Arm64Register.V0 or > Disarm.InternalDisassembly.Arm64Register.V31) continue;
				if (i.MemAddendReg != Disarm.InternalDisassembly.Arm64Register.INVALID || i.MemIsPreIndexed) continue;
				if (!app.Binary.TryMapVirtualAddressToRaw(i.Address, out long raw) || raw <= 0) continue;

				System.ReadOnlySpan<byte> content = app.Binary.GetRawBinaryContent();
				if (raw + 4 > content.Length) continue;

				uint word = System.BitConverter.ToUInt32(content.Slice((int)raw, 4));

				// scaled unsigned offset: size 111 1 01 opc imm12 Rn Rt, and 128 bit is size=00 opc=11
				if ((word >> 27 & 7) != 0b111 || (word >> 26 & 1) != 1 || (word >> 24 & 3) != 0b01) continue;
				if ((word >> 30 & 3) != 0 || (word >> 22 & 3) != 0b11) continue;

				long truth = (long)(word >> 10 & 0xFFF) * 16;

				if (truth == 0) continue;
				if (i.MemOffset == truth) agree++;
				else if (i.MemOffset * 16 == truth) scaledBy16++;
				else
				{
					other++;
					if (examples.Count < 5) examples.Add($"{i.Address:X} {word:X8} disarm={i.MemOffset} truth={truth}");
				}
			}
		}
	}

	Console.WriteLine($"128 bit, non-zero immediate: agree={agree} sixteen-times-small={scaledBy16} other={other}");

	// the pair forms, whose immediate is scaled by the element size too
	int pairOk = 0, pairBad = 0;
	System.Collections.Generic.List<string> pairs = new();

	foreach (TypeAnalysisContext type in app.AllTypes)
	{
		foreach (MethodAnalysisContext m in type.Methods)
		{
			if (m.UnderlyingPointer == 0) continue;

			foreach (Disarm.Arm64Instruction i in Cpp2IL.Core.Utils.NewArm64Utils.GetArm64MethodBodyAtVirtualAddress(app.Binary, m.UnderlyingPointer))
			{
				if (i.Mnemonic is not (Disarm.Arm64Mnemonic.LDP or Disarm.Arm64Mnemonic.STP)) continue;
				if (!app.Binary.TryMapVirtualAddressToRaw(i.Address, out long raw) || raw <= 0) continue;

				System.ReadOnlySpan<byte> content = app.Binary.GetRawBinaryContent();
				if (raw + 4 > content.Length) continue;

				uint word = System.BitConverter.ToUInt32(content.Slice((int)raw, 4));

				// opc 101 V 0 xx0 imm7 Rt2 Rn Rt, V at bit 26, opc at 31-30
				if ((word >> 27 & 7) != 0b101 || (word >> 26 & 1) != 1) continue;

				int scale = 4 << (int)(word >> 30 & 3);
				int imm7 = (int)(word >> 15 & 0x7F);
				long truth = (imm7 >= 0x40 ? imm7 - 0x80 : imm7) * (long)scale;

				if (truth == 0) continue;
				if (i.MemOffset == truth) pairOk++;
				else
				{
					pairBad++;
					if (pairs.Count < 5) pairs.Add($"{i.Address:X} {word:X8} scale={scale} disarm={i.MemOffset} truth={truth}");
				}
			}
		}
	}

	Console.WriteLine($"vector pairs, non-zero immediate: agree={pairOk} wrong={pairBad}");
	foreach (string e in pairs) Console.WriteLine("  " + e);
	foreach (string e in examples) Console.WriteLine("  " + e);
	return;
}

// shiftcheck - a load or store indexed by a register scales the index by the width it accesses, but only
// when the S bit says so. Compared against the word, because an index scaled wrongly is a wrong element.
// shifts - how often the binary shifts right logically against arithmetically. LSR/ASR with an immediate are
// aliases of UBFM/SBFM, so both encodings are counted; the register forms are LSRV/ASRV. Split by assembly,
// because only the game's own code is scored.
// truncation - a write through a register's 32-bit name zeroes its top half, so a result read back through
// the 64-bit name carries an implicit mask that is not an instruction. Counts only the pairs where that
// matters: a W destination read as X before anything redefines it, within the method, in program order.
if (args[3] == "truncation")
{
	System.Collections.Generic.Dictionary<string, int> byMnemonic = new();
	int pairs = 0, gamePairs = 0, wWrites = 0;
	System.Collections.Generic.List<string> examples = new();

	static int Number(Disarm.InternalDisassembly.Arm64Register r)
		=> r >= Disarm.InternalDisassembly.Arm64Register.W0 && r <= Disarm.InternalDisassembly.Arm64Register.W31
			? r - Disarm.InternalDisassembly.Arm64Register.W0
			: r >= Disarm.InternalDisassembly.Arm64Register.X0 && r <= Disarm.InternalDisassembly.Arm64Register.X31
				? r - Disarm.InternalDisassembly.Arm64Register.X0
				: -1;

	static bool IsW(Disarm.InternalDisassembly.Arm64Register r)
		=> r >= Disarm.InternalDisassembly.Arm64Register.W0 && r <= Disarm.InternalDisassembly.Arm64Register.W30;

	static bool IsX(Disarm.InternalDisassembly.Arm64Register r)
		=> r >= Disarm.InternalDisassembly.Arm64Register.X0 && r <= Disarm.InternalDisassembly.Arm64Register.X30;

	foreach (TypeAnalysisContext type in app.AllTypes)
	{
		bool own = type.DeclaringAssembly?.Name == "Assembly-CSharp";

		foreach (MethodAnalysisContext m in type.Methods)
		{
			if (m.UnderlyingPointer == 0) continue;

			Disarm.Arm64Instruction[] body;
			try { body = Cpp2IL.Core.Utils.NewArm64Utils.GetArm64MethodBodyAtVirtualAddress(app.Binary, m.UnderlyingPointer).ToArray(); }
			catch { continue; }

			for (int i = 0; i < body.Length; i++)
			{
				Disarm.Arm64Instruction w = body[i];
				if (w.Op0Kind != Disarm.Arm64OperandKind.Register || !IsW(w.Op0Reg)) continue;

				string wrote = w.Mnemonic.ToString();

				//Only the instructions that compute a value into the register, and only the ones whose 32-bit
				//form can differ from the 64-bit one. A load or a move assigns the whole value, and ISIL holds
				//values rather than registers with stale halves, so reading one back wide is already right.
				//A comparison writes flags and names its operand in Op0; a store writes memory. Neither
				//defines the register at all, and counting them was what first made this look enormous.
				if (wrote is not ("ADD" or "SUB" or "ORR" or "AND" or "EOR" or "BIC" or "ORN" or "EON"
					or "MUL" or "MADD" or "MSUB" or "LSLV" or "LSRV" or "ASRV" or "UBFM" or "SBFM" or "EXTR"
					or "ADC" or "SBC" or "NEG" or "MVN" or "RORV")) continue;

				wWrites++;
				int n = Number(w.Op0Reg);

				for (int j = i + 1; j < body.Length && j < i + 40; j++)
				{
					Disarm.Arm64Instruction u = body[j];

					//Straight-line only. A branch in between means the two may be on different paths, and
					//they usually are: 98 of the first 107 "pairs" were a conditional jumping over the write
					//to land on the read, which is no def-use relation at all.
					string mn = u.Mnemonic.ToString();
					if (mn[0] == 'B' || mn.StartsWith("CB") || mn.StartsWith("TB") || mn == "RET") break;

					//Redefined - whichever name it is written through, the old value is gone.
					if (u.Op0Kind == Disarm.Arm64OperandKind.Register && Number(u.Op0Reg) == n
						&& !u.Mnemonic.ToString().StartsWith("STR") && !u.Mnemonic.ToString().StartsWith("STP"))
						break;

					//A load names its destinations in the register operands, so finding the register there is
					//it being written, not read - counting those made a handful of real sites look like
					//hundreds. The base of the address is a genuine read and is checked separately.
					string using_ = u.Mnemonic.ToString();
					bool loads = using_.StartsWith("LD");

					if (loads)
					{
						if (u.MemBase != Disarm.InternalDisassembly.Arm64Register.INVALID && Number(u.MemBase) == n && IsX(u.MemBase))
						{
							pairs++;
							if (own)
							{
								gamePairs++;
								byMnemonic[wrote] = byMnemonic.TryGetValue(wrote, out int lc) ? lc + 1 : 1;
								if (examples.Count < 40) examples.Add($"{type.Namespace}.{type.Name}::{m.Name}  {w}   ->   {u}");
							}
							break;
						}

						continue;
					}

					bool readsWide =
						(u.Op1Kind == Disarm.Arm64OperandKind.Register && IsX(u.Op1Reg) && Number(u.Op1Reg) == n)
						|| (u.Op2Kind == Disarm.Arm64OperandKind.Register && IsX(u.Op2Reg) && Number(u.Op2Reg) == n)
						|| (u.Op3Kind == Disarm.Arm64OperandKind.Register && IsX(u.Op3Reg) && Number(u.Op3Reg) == n);

					if (!readsWide) continue;

					pairs++;
					if (own)
					{
						gamePairs++;
						byMnemonic[wrote] = byMnemonic.TryGetValue(wrote, out int c) ? c + 1 : 1;
						if (examples.Count < 40) examples.Add($"{type.Namespace}.{type.Name}::{m.Name}  {w}   ->   {u}");
					}
					break;
				}
			}
		}
	}

	Console.WriteLine($"W destinations seen        : {wWrites}");
	Console.WriteLine($"read back as X (whole bin): {pairs}");
	Console.WriteLine($"read back as X (game)     : {gamePairs}");
	Console.WriteLine("by the mnemonic that wrote W, in Assembly-CSharp:");
	foreach (var kv in byMnemonic.OrderByDescending(k => k.Value).Take(15))
		Console.WriteLine($"  {kv.Key,-10} {kv.Value}");
	Console.WriteLine("examples:");
	foreach (string e in examples) Console.WriteLine("  " + e);
	return;
}

if (args[3] == "shifts")
{
	System.Collections.Generic.Dictionary<string, int> all = new(), game = new();

	void Bump(System.Collections.Generic.Dictionary<string, int> into, string what)
		=> into[what] = into.TryGetValue(what, out int c) ? c + 1 : 1;

	foreach (TypeAnalysisContext type in app.AllTypes)
	{
		bool own = type.DeclaringAssembly?.Name == "Assembly-CSharp";

		foreach (MethodAnalysisContext m in type.Methods)
		{
			if (m.UnderlyingPointer == 0) continue;

			foreach (Disarm.Arm64Instruction i in Cpp2IL.Core.Utils.NewArm64Utils.GetArm64MethodBodyAtVirtualAddress(app.Binary, m.UnderlyingPointer))
			{
				string name = i.Mnemonic.ToString();
				if (name is not ("LSR" or "ASR" or "LSRV" or "ASRV" or "UBFM" or "SBFM" or "LSL" or "LSLV")) continue;

				//A bitfield move is a right shift when its second immediate is at least its first, and a left
				//shift otherwise - which is what decides whether it becomes a ShiftRight at all.
				if (name is "UBFM" or "SBFM")
				{
					long immr = i.Op2Imm, imms = i.Op3Imm;
					name += imms >= immr ? "-right" : "-left";
				}

				Bump(all, name);
				if (own) Bump(game, name);
			}
		}
	}

	Console.WriteLine("mnemonic      whole binary   Assembly-CSharp");
	foreach (string k in new[] { "LSR", "LSRV", "UBFM-right", "UBFM-left", "ASR", "ASRV", "SBFM-right", "SBFM-left", "LSL", "LSLV" })
		Console.WriteLine($"{k,-10} {(all.TryGetValue(k, out int a) ? a : 0),12} {(game.TryGetValue(k, out int g) ? g : 0),17}");

	return;
}

if (args[3] == "shiftcheck")
{
	int ok = 0, wrong = 0;
	System.Collections.Generic.List<string> examples = new();

	foreach (TypeAnalysisContext type in app.AllTypes)
	{
		foreach (MethodAnalysisContext m in type.Methods)
		{
			if (m.UnderlyingPointer == 0) continue;

			foreach (Disarm.Arm64Instruction i in Cpp2IL.Core.Utils.NewArm64Utils.GetArm64MethodBodyAtVirtualAddress(app.Binary, m.UnderlyingPointer))
			{
				if (i.MemAddendReg == Disarm.InternalDisassembly.Arm64Register.INVALID) continue;
				if (!app.Binary.TryMapVirtualAddressToRaw(i.Address, out long raw) || raw <= 0) continue;

				System.ReadOnlySpan<byte> content = app.Binary.GetRawBinaryContent();
				if (raw + 4 > content.Length) continue;

				uint word = System.BitConverter.ToUInt32(content.Slice((int)raw, 4));

				// register offset: size 111 V 00 opc 1 Rm option S 10 Rn Rt
				if ((word >> 27 & 7) != 0b111 || (word >> 24 & 3) != 0b00 || (word >> 21 & 1) != 1) continue;
				if ((word >> 10 & 3) != 0b10) continue;

				int size = (int)(word >> 30 & 3);
				int vector = (int)(word >> 26 & 1);
				int opc = (int)(word >> 22 & 3);
				int width = vector == 1 ? (size == 0 && opc >= 2 ? 4 : size) : size;
				int s = (int)(word >> 12 & 1);
				int truth = s == 1 ? width : 0;

				if (i.MemExtendOrShiftAmount == truth) ok++;
				else
				{
					wrong++;
					if (type.DeclaringAssembly.Name == "Assembly-CSharp" && examples.Count < 8)
						examples.Add($"{type.Name}::{m.Name} {i} disarm={i.MemExtendOrShiftAmount} truth={truth}");
				}
			}
		}
	}

	Console.WriteLine($"register-indexed loads/stores: agree={ok} wrong={wrong}");
	foreach (string e in examples) Console.WriteLine("  " + e);
	return;
}

// alltext - what the disassembler makes of every instruction in the game's own assembly, addressed, so it
// can be compared against another disassembler's account of the same bytes.
if (args[3] == "alltext")
{
	foreach (TypeAnalysisContext type in app.AllTypes)
	{
		if (type.DeclaringAssembly.Name != "Assembly-CSharp") continue;

		foreach (MethodAnalysisContext m in type.Methods)
		{
			if (m.UnderlyingPointer == 0) continue;

			foreach (Disarm.Arm64Instruction i in Cpp2IL.Core.Utils.NewArm64Utils.GetArm64MethodBodyAtVirtualAddress(app.Binary, m.UnderlyingPointer))
			{
				if (i.Address == 0) continue;
				Console.WriteLine($"{i.Address:x}\t{i}");
			}
		}
	}

	return;
}

// untyped - every memory read whose base has no type, grouped by what defined the base. A read through an
// untyped base cannot resolve to a field, so what matters is why the base was never typed.

// unwritten - of the untyped bases whose local has no defining instruction, how many have their register
// written by some *other* version, and by what. A local with no definition whose register is written
// elsewhere is an SSA versioning fault; one whose register is never written is a value the lifter never
// produced. The two want opposite fixes, so the count decides which.
if (args[3] == "unwritten")
{
	System.Collections.Generic.Dictionary<string, int> byKind = new();
	System.Collections.Generic.Dictionary<string, string> sample = new();
	int total = 0, undefined = 0;

	foreach (TypeAnalysisContext type in app.AllTypes)
	{
		if (type.DeclaringAssembly.Name != "Assembly-CSharp") continue;

		foreach (MethodAnalysisContext m in type.Methods)
		{
			if (m.UnderlyingPointer == 0) continue;
			try { m.Analyze(); } catch { continue; }
			if (m.ControlFlowGraph is null) continue;

			System.Collections.Generic.Dictionary<LocalVariable, Instruction> definition = new();
			System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<Instruction>> byRegister = new();

			foreach (Instruction i in m.ControlFlowGraph.Instructions)
				if (i.Destination is LocalVariable d)
				{
					definition[d] = i;
					string bare = d.Register.Name.Split('_')[0];
					if (!byRegister.TryGetValue(bare, out var list)) byRegister[bare] = list = new();
					list.Add(i);
				}

			// the parameters, which are seeded rather than defined
			System.Collections.Generic.HashSet<string> parameters = new();
			foreach (LocalVariable p in m.ParameterLocals) parameters.Add(p.Register.Name);

			foreach (Instruction i in m.ControlFlowGraph.Instructions)
				foreach (object operand in i.Operands)
				{
					if (operand is not MemoryOperand read) continue;
					if (read.Base is not LocalVariable local || local.Type is not null) continue;
					total++;
					if (definition.ContainsKey(local)) continue;
					undefined++;

					string name = local.Register.Name;
					string bareName = name.Split('_')[0];
					string kind = parameters.Contains(name) ? "a parameter"
						: name.StartsWith("stack") || bareName == "X31" ? "the frame"
						: byRegister.TryGetValue(bareName, out var written)
							? $"written elsewhere as {written[0].OpCode}"
							: "the register is never written at all";

					byKind[kind] = byKind.GetValueOrDefault(kind) + 1;
					if (!sample.ContainsKey(kind)) sample[kind] = $"{type.Name}::{m.Name} {name}";
				}
		}
	}

	Console.WriteLine($"{total} reads through an untyped base, {undefined} of them with no defining instruction");
	foreach (var e in byKind.OrderByDescending(x => x.Value).Take(14))
		Console.WriteLine($"{e.Value,6}  {e.Key,-46} {sample[e.Key]}");
	return;
}

if (args[3] == "untyped")
{
	System.Collections.Generic.Dictionary<string, int> byCause = new();
	System.Collections.Generic.Dictionary<string, string> example = new();
	int total = 0;

	foreach (TypeAnalysisContext type in app.AllTypes)
	{
		if (type.DeclaringAssembly.Name != "Assembly-CSharp") continue;

		foreach (MethodAnalysisContext m in type.Methods)
		{
			if (m.UnderlyingPointer == 0) continue;
			try { m.Analyze(); } catch { continue; }
			if (m.ControlFlowGraph is null) continue;

			// where each local was written, so a base can be traced back to what made it
			System.Collections.Generic.Dictionary<LocalVariable, Instruction> definition = new();
			foreach (Instruction i in m.ControlFlowGraph.Instructions)
				if (i.Destination is LocalVariable d) definition[d] = i;

			foreach (Instruction i in m.ControlFlowGraph.Instructions)
			{
				foreach (object operand in i.Operands)
				{
					if (operand is not MemoryOperand read) continue;
					if (read.Base is not LocalVariable local || local.Type is not null) continue;

					total++;
					string cause;

					//Follow the chain of untyped reads back to what it actually rests on. Classified one hop
					//at a time, the largest group is "moved from a memory read" - which says nothing, because
					//that read has an untyped base of its own. The root is what would have to be fixed.
					LocalVariable root = local;
					Instruction? made;
					for (int hop = 0; hop < 24; hop++)
					{
						if (!definition.TryGetValue(root, out made)) break;
						LocalVariable? next = made.OpCode == OpCode.Move && made.Operands.Count > 1
							? made.Operands[1] switch
							{
								MemoryOperand m2 when m2.Base is LocalVariable b2 && b2.Type is null => b2,
								LocalVariable l2 when l2.Type is null => l2,
								_ => null,
							}
							: null;
						if (next is null || ReferenceEquals(next, root)) break;
						root = next;
					}

					if (!definition.TryGetValue(root, out made))
						cause = root.Register.Name.StartsWith("X31") || root.Register.Name.StartsWith("stack")
							? "the stack pointer" : "a register never written here";
					else if (made.OpCode == OpCode.Move && made.Operands.Count > 1)
						cause = "moved from " + Describe(made.Operands[1]);
					else if (made.IsCall)
						cause = made.Operands[0] is MethodAnalysisContext ? "a call whose method is known"
							: made.Operands[0] is string named2 ? "the key function " + named2
							: "a call to an address";
					else
						cause = made.OpCode.ToString();

					byCause[cause] = byCause.GetValueOrDefault(cause) + 1;
					if (!example.ContainsKey(cause)) example[cause] = $"{type.Name}::{m.Name}  {i}";
				}
			}
		}
	}

	Console.WriteLine($"{total} reads through a base with no type");
	foreach (var e in byCause.OrderByDescending(x => x.Value).Take(16))
		Console.WriteLine($"{e.Value,6}  {e.Key,-34} {example[e.Key][..Math.Min(78, example[e.Key].Length)]}");
	return;

	static string Describe(object operand) => operand switch
	{
		MemoryOperand => "a memory read",
		LocalVariable { Type: null } => "another untyped local",
		LocalVariable => "a typed local",
		FieldReference => "a field",
		long or int or ulong or uint => "a constant",
		_ => operand.GetType().Name,
	};
}

// standins - every memory read whose destination is an il2cpp runtime stand-in (a class pointer, a MethodInfo,
// an rgctx table). Those have no managed value, so the generator writes each one out as an `Unmanaged memory
// load` marker - 1612 of them game-wide. Grouped by what else reads the local, which is what decides whether
// the read can simply be dropped.
if (args[3] == "standins")
{
	Dictionary<string, int> byUse = new();
	Dictionary<string, string> useExample = new();
	Dictionary<string, int> byType = new();
	int reads = 0;

	static bool IsStandIn(TypeAnalysisContext? t) =>
		t is not null && (t.Name.StartsWith("Il2Cpp") || (t is GenericInstanceTypeAnalysisContext g && g.GenericType.Name.StartsWith("Il2Cpp")));

	foreach (TypeAnalysisContext type in app.AllTypes)
	{
		foreach (MethodAnalysisContext m in type.Methods)
		{
			if (m.UnderlyingPointer == 0) continue;
			try { m.Analyze(); } catch { continue; }
			if (m.ControlFlowGraph is null) continue;

			List<Instruction> all = m.ControlFlowGraph.Instructions.ToList();

			foreach (Instruction i in all)
			{
				if (i.OpCode != OpCode.Move || i.Operands.Count < 2) continue;
				if (i.Operands[1] is not MemoryOperand) continue;
				if (i.Operands[0] is not LocalVariable destination || !IsStandIn(destination.Type)) continue;

				reads++;
				byType[destination.Type!.Name] = byType.GetValueOrDefault(destination.Type.Name) + 1;

				// what else reads it, and in what position - a class operand of a boxing or cast helper is
				// resolved from the operand's own type, so the read that produced it says nothing.
				List<string> uses = new();
				foreach (Instruction other in all)
				{
					if (ReferenceEquals(other, i)) continue;
					for (int op = 0; op < other.Operands.Count; op++)
					{
						object o = other.Operands[op];
						bool hit = ReferenceEquals(o, destination)
							|| (o is MemoryOperand mo && ReferenceEquals(mo.Base, destination));
						if (!hit) continue;
						string what = other.IsCall && other.Operands[0] is string named ? named
							: other.IsCall ? "call"
							: other.OpCode.ToString();
						uses.Add(o is MemoryOperand ? what + " base" : what + " op" + op);
					}
				}

				string key = uses.Count == 0 ? "(nothing reads it)" : string.Join(" + ", uses.Distinct().OrderBy(x => x));
				byUse[key] = byUse.GetValueOrDefault(key) + 1;
				if (!useExample.ContainsKey(key)) useExample[key] = $"{type.Name}::{m.Name}  {i}";
			}
		}
	}

	Console.WriteLine($"{reads} memory reads into an il2cpp stand-in");
	Console.WriteLine("-- by the stand-in read --");
	foreach (var e in byType.OrderByDescending(x => x.Value).Take(10))
		Console.WriteLine($"{e.Value,6}  {e.Key}");
	Console.WriteLine("-- by what else reads the local --");
	foreach (var e in byUse.OrderByDescending(x => x.Value).Take(20))
		Console.WriteLine($"{e.Value,6}  {e.Key,-52} {useExample[e.Key][..Math.Min(70, useExample[e.Key].Length)]}");
	return;
}

// thunks - a call target that is not a managed method may be one unconditional branch to something that is.
if (args[3] == "thunks")
{
	System.Collections.Generic.HashSet<ulong> managed = new();
	foreach (TypeAnalysisContext t in app.AllTypes)
		foreach (MethodAnalysisContext mm in t.Methods)
			if (mm.UnderlyingPointer != 0) managed.Add(mm.UnderlyingPointer);

	System.Collections.Generic.Dictionary<ulong, int> targets = new();

	foreach (TypeAnalysisContext type in app.AllTypes)
	{
		if (type.DeclaringAssembly.Name != "Assembly-CSharp") continue;

		foreach (MethodAnalysisContext m in type.Methods)
		{
			if (m.UnderlyingPointer == 0) continue;

			foreach (Disarm.Arm64Instruction i in Cpp2IL.Core.Utils.NewArm64Utils.GetArm64MethodBodyAtVirtualAddress(app.Binary, m.UnderlyingPointer))
			{
				if (i.Mnemonic != Disarm.Arm64Mnemonic.BL) continue;
				if (managed.Contains(i.BranchTarget)) continue;
				targets[i.BranchTarget] = targets.GetValueOrDefault(i.BranchTarget) + 1;
			}
		}
	}

	int oneHopToManaged = 0, oneHopElsewhere = 0, notAThunk = 0, callsFixed = 0;
	System.Collections.Generic.List<string> shown = new();

	foreach (var t in targets)
	{
		if (!app.Binary.TryMapVirtualAddressToRaw(t.Key, out long raw) || raw <= 0) { notAThunk++; continue; }

		System.ReadOnlySpan<byte> content = app.Binary.GetRawBinaryContent();
		if (raw + 4 > content.Length) { notAThunk++; continue; }

		uint word = System.BitConverter.ToUInt32(content.Slice((int)raw, 4));

		// b <imm26>, an unconditional branch and nothing else
		if ((word >> 26) != 0b000101) { notAThunk++; continue; }

		long offset = (int)(word & 0x3FFFFFF) << 6 >> 4;
		ulong destination = (ulong)((long)t.Key + offset);

		if (managed.Contains(destination))
		{
			oneHopToManaged++; callsFixed += t.Value;
			if (shown.Count < 4) shown.Add($"{t.Key:X} -> {destination:X}  ({t.Value} calls)");
		}
		else oneHopElsewhere++;
	}

	Console.WriteLine($"call targets in the game's assembly that are not managed methods: {targets.Count}");
	Console.WriteLine($"  one branch, landing on a managed method : {oneHopToManaged}  ({callsFixed} call sites)");
	Console.WriteLine($"  one branch, landing elsewhere           : {oneHopElsewhere}");
	Console.WriteLine($"  not a single branch                     : {notAThunk}");
	foreach (string e in shown) Console.WriteLine("    " + e);
	return;
}

if (args[3] == "conds")
{
	foreach (object v in Enum.GetValues(typeof(Disarm.Arm64ConditionCode)))
		Console.WriteLine(v);
	foreach (System.Reflection.MemberInfo mi in typeof(Disarm.Arm64VectorElement).GetMembers())
		Console.WriteLine("VE " + mi.MemberType + " " + mi.Name);
	return;
}

if (args[3] == "opinfo")
{
	int shown = 0;
	foreach (TypeAnalysisContext type in app.AllTypes)
	{
		foreach (MethodAnalysisContext m in type.Methods)
		{
			if (m.UnderlyingPointer == 0) continue;
			foreach (Disarm.Arm64Instruction i in Cpp2IL.Core.Utils.NewArm64Utils.GetArm64MethodBodyAtVirtualAddress(app.Binary, m.UnderlyingPointer))
			{
				if (!string.Equals(i.Mnemonic.ToString(), args[4], StringComparison.OrdinalIgnoreCase)) continue;
				Console.WriteLine($"{i.Address:X} {i}");
				Console.WriteLine($"   op0 {i.Op0Kind}/{i.Op0Reg}/{i.Op0Imm}  op1 {i.Op1Kind}/{i.Op1Reg}/{i.Op1Imm}  op2 {i.Op2Kind}/{i.Op2Reg}/{i.Op2Imm}  op3 {i.Op3Kind}/{i.Op3Reg}/{i.Op3Imm}");
				Console.WriteLine($"   mnemonicCond={i.MnemonicConditionCode} finalCond={i.FinalOpConditionCode}  arr0={i.Op0Arrangement} arr1={i.Op1Arrangement} elem0={i.Op0VectorElement}");
				Console.WriteLine($"   memBase={i.MemBase} memOffset={i.MemOffset} memAddend={i.MemAddendReg} memShift={i.MemExtendOrShiftAmount}");
				if (++shown >= (args.Length > 5 ? int.Parse(args[5]) : 6)) return;
			}
		}
	}
	return;
}

// carry - every condition code used against the flags of a preceding instruction, grouped by which
// instruction set the flags. ISIL has no unsigned comparison, so HI/LS/CS/CC are translated as signed - and
// after an addition the carry flag is not a fact about the result at all. This counts how much that costs.
if (args[3] == "carry")
{
	Dictionary<string, int> pairs = new();
	Dictionary<string, string> example = new();
	int walked = 0;

	static bool SetsFlags(Disarm.Arm64Mnemonic m) => m is Disarm.Arm64Mnemonic.ADDS or Disarm.Arm64Mnemonic.SUBS
		or Disarm.Arm64Mnemonic.ANDS or Disarm.Arm64Mnemonic.BICS or Disarm.Arm64Mnemonic.CCMP or Disarm.Arm64Mnemonic.CCMN
		or Disarm.Arm64Mnemonic.FCMP or Disarm.Arm64Mnemonic.FCMPE or Disarm.Arm64Mnemonic.ADCS or Disarm.Arm64Mnemonic.SBCS
		//CMP/CMN/TST were missing, and Disarm reports them under their own mnemonics rather than as the
		//SUBS/ADDS/ANDS they alias - so every comparison written as a comparison was invisible to this count.
		or Disarm.Arm64Mnemonic.CMP or Disarm.Arm64Mnemonic.CMN or Disarm.Arm64Mnemonic.TST;

	foreach (TypeAnalysisContext type in app.AllTypes)
		foreach (MethodAnalysisContext m in type.Methods)
		{
			if (m.UnderlyingPointer == 0) continue;
			walked++;
			Disarm.Arm64Mnemonic setter = Disarm.Arm64Mnemonic.INVALID;
			string setterText = "";
			int distance = 99;

			IEnumerable<Disarm.Arm64Instruction> body;
			try { body = Cpp2IL.Core.Utils.NewArm64Utils.GetArm64MethodBodyAtVirtualAddress(app.Binary, m.UnderlyingPointer); }
			catch { continue; }

			foreach (Disarm.Arm64Instruction i in body)
			{
				Disarm.Arm64ConditionCode used = i.Mnemonic switch
				{
					Disarm.Arm64Mnemonic.B => i.MnemonicConditionCode,
					Disarm.Arm64Mnemonic.CSEL or Disarm.Arm64Mnemonic.CSINC or Disarm.Arm64Mnemonic.CSINV
						or Disarm.Arm64Mnemonic.CSNEG or Disarm.Arm64Mnemonic.CSET or Disarm.Arm64Mnemonic.CSETM
						=> i.FinalOpConditionCode,
					_ => Disarm.Arm64ConditionCode.NONE,
				};

				distance++;

				//Only where the flags cannot have come from anywhere else: within three instructions and
				//with no branch in between, so a linear walk is not crossing a join in the control flow.
				if (used != Disarm.Arm64ConditionCode.NONE && setter != Disarm.Arm64Mnemonic.INVALID && distance <= 3)
				{
					bool carry = used is Disarm.Arm64ConditionCode.HI or Disarm.Arm64ConditionCode.LS
						or Disarm.Arm64ConditionCode.CS or Disarm.Arm64ConditionCode.CC;
					string key = $"{setter} + {used}" + (carry ? "   <- carry" : "");
					pairs[key] = pairs.GetValueOrDefault(key) + 1;
					if (carry && !example.ContainsKey(key)) example[key] = $"{type.Name}::{m.Name}  {setterText}  |  {i}";
				}

				if (SetsFlags(i.Mnemonic)) { setter = i.Mnemonic; setterText = i.ToString(); distance = 0; }
				else if (i.Mnemonic is Disarm.Arm64Mnemonic.B or Disarm.Arm64Mnemonic.BL or Disarm.Arm64Mnemonic.BR
					or Disarm.Arm64Mnemonic.BLR or Disarm.Arm64Mnemonic.CBZ or Disarm.Arm64Mnemonic.CBNZ
					or Disarm.Arm64Mnemonic.TBZ or Disarm.Arm64Mnemonic.TBNZ or Disarm.Arm64Mnemonic.RET)
					{ setter = Disarm.Arm64Mnemonic.INVALID; }
			}
		}

	Console.WriteLine($"{walked} methods");
	foreach (var e in pairs.OrderByDescending(x => x.Value).Take(24))
		Console.WriteLine($"{e.Value,7}  {e.Key,-28} {(example.TryGetValue(e.Key, out var x) ? x[..Math.Min(70, x.Length)] : "")}");
	return;
}

// wtrunc - a W-form data-processing instruction reading a register whose last straight-line definition
// produced more than thirty-two significant bits. On arm64 the W form takes the low word of each source and
// zero-extends its result; ISIL has no width, so the whole thing happens in `long` and the answer is wrong
// with no marker. `Corpus::DivMagic` is the shape: `smaddl x8,w0,w8,xzr` / `lsr x8,x8,#32` / `add w8,w8,w0`.
// The second column is the subset whose wide value descends from a WIDENING MULTIPLY - the magic-division
// population, which is what a division by a constant compiles to.
if (args[3] == "wtrunc")
{
	static int Index(Disarm.InternalDisassembly.Arm64Register r) =>
		r is >= Disarm.InternalDisassembly.Arm64Register.W0 and <= Disarm.InternalDisassembly.Arm64Register.W31 ? r - Disarm.InternalDisassembly.Arm64Register.W0
		: r is >= Disarm.InternalDisassembly.Arm64Register.X0 and <= Disarm.InternalDisassembly.Arm64Register.X31 ? r - Disarm.InternalDisassembly.Arm64Register.X0
		: -1;
	static bool IsW(Disarm.InternalDisassembly.Arm64Register r) => r is >= Disarm.InternalDisassembly.Arm64Register.W0 and <= Disarm.InternalDisassembly.Arm64Register.W31;
	static bool IsX(Disarm.InternalDisassembly.Arm64Register r) => r is >= Disarm.InternalDisassembly.Arm64Register.X0 and <= Disarm.InternalDisassembly.Arm64Register.X31;

	static bool WideningMultiply(Disarm.Arm64Mnemonic m) => m is Disarm.Arm64Mnemonic.SMADDL
		or Disarm.Arm64Mnemonic.UMADDL or Disarm.Arm64Mnemonic.SMULL or Disarm.Arm64Mnemonic.UMULL
		or Disarm.Arm64Mnemonic.SMSUBL or Disarm.Arm64Mnemonic.UMSUBL
		or Disarm.Arm64Mnemonic.SMULH or Disarm.Arm64Mnemonic.UMULH;

	//Data processing: the instructions whose W form really does truncate. A load or a move assigns the whole
	//value and ISIL holds values rather than registers with stale halves, so those are not counted - the same
	//correction that took the earlier truncation count from 1178 to 7.
	static bool DataProcessing(Disarm.Arm64Mnemonic m) => m is Disarm.Arm64Mnemonic.ADD or Disarm.Arm64Mnemonic.SUB
		or Disarm.Arm64Mnemonic.ADDS or Disarm.Arm64Mnemonic.SUBS or Disarm.Arm64Mnemonic.MUL
		or Disarm.Arm64Mnemonic.MADD or Disarm.Arm64Mnemonic.MSUB or Disarm.Arm64Mnemonic.NEG
		or Disarm.Arm64Mnemonic.AND or Disarm.Arm64Mnemonic.ORR or Disarm.Arm64Mnemonic.EOR
		or Disarm.Arm64Mnemonic.ANDS or Disarm.Arm64Mnemonic.BIC or Disarm.Arm64Mnemonic.ORN
		or Disarm.Arm64Mnemonic.UBFM or Disarm.Arm64Mnemonic.SBFM or Disarm.Arm64Mnemonic.BFM
		or Disarm.Arm64Mnemonic.LSLV or Disarm.Arm64Mnemonic.LSRV or Disarm.Arm64Mnemonic.ASRV
		or Disarm.Arm64Mnemonic.RORV or Disarm.Arm64Mnemonic.CMP or Disarm.Arm64Mnemonic.CMN
		or Disarm.Arm64Mnemonic.CSEL or Disarm.Arm64Mnemonic.CSINC or Disarm.Arm64Mnemonic.SDIV
		or Disarm.Arm64Mnemonic.UDIV;

	int sites = 0, magicSites = 0, walked = 0;
	HashSet<string> wtMethods = new(), magicMethods = new();
	Dictionary<string, int> byMnemonic = new();
	List<string> examples = new();

	foreach (TypeAnalysisContext type in app.AllTypes)
		foreach (MethodAnalysisContext m in type.Methods)
		{
			if (m.UnderlyingPointer == 0) continue;
			walked++;
			IEnumerable<Disarm.Arm64Instruction> body;
			try { body = Cpp2IL.Core.Utils.NewArm64Utils.GetArm64MethodBodyAtVirtualAddress(app.Binary, m.UnderlyingPointer); }
			catch { continue; }

			//0 = not wide, 1 = wide, 2 = wide and descended from a widening multiply.
			int[] wide = new int[32];

			foreach (Disarm.Arm64Instruction i in body)
			{
				//Straight-line only. A branch either way means the register could have been written on a
				//path this walk did not take, and a linear scan is not a def-use analysis.
				if (i.Mnemonic is Disarm.Arm64Mnemonic.B or Disarm.Arm64Mnemonic.BL or Disarm.Arm64Mnemonic.BR
					or Disarm.Arm64Mnemonic.BLR or Disarm.Arm64Mnemonic.CBZ or Disarm.Arm64Mnemonic.CBNZ
					or Disarm.Arm64Mnemonic.TBZ or Disarm.Arm64Mnemonic.TBNZ or Disarm.Arm64Mnemonic.RET)
				{
					Array.Clear(wide);
					continue;
				}

				Disarm.InternalDisassembly.Arm64Register[] srcs = [i.Op1Reg, i.Op2Reg, i.Op3Reg];

				if (DataProcessing(i.Mnemonic) && IsW(i.Op0Reg))
				{
					int worst = 0;
					foreach (Disarm.InternalDisassembly.Arm64Register s in srcs)
					{
						int ix = Index(s);
						if (ix >= 0 && ix != 31 && wide[ix] > worst) worst = wide[ix];
					}

					if (worst > 0)
					{
						sites++;
						wtMethods.Add($"{type.Name}::{m.Name}");
						byMnemonic[i.Mnemonic.ToString()] = byMnemonic.GetValueOrDefault(i.Mnemonic.ToString()) + 1;
						if (worst == 2)
						{
							magicSites++;
							magicMethods.Add($"{type.Name}::{m.Name}");
							if (examples.Count < 12) examples.Add($"{type.Name}::{m.Name}  0x{i.Address:X} {i}");
						}
					}
				}

				//What the instruction leaves behind.
				int dest = Index(i.Op0Reg);
				if (dest < 0 || dest == 31) continue;

				if (WideningMultiply(i.Mnemonic)) { wide[dest] = 2; continue; }

				if (IsW(i.Op0Reg)) { wide[dest] = 0; continue; }   //a W write zeroes the top half

				if (IsX(i.Op0Reg))
				{
					int worst = 0;
					foreach (Disarm.InternalDisassembly.Arm64Register s in srcs)
					{
						int ix = Index(s);
						if (ix >= 0 && ix != 31 && wide[ix] > worst) worst = wide[ix];
					}
					//An X-form shift/arith carries the width of what it read; anything else (a load, a move,
					//an address) starts fresh, because ISIL holds the whole value it assigns.
					wide[dest] = DataProcessing(i.Mnemonic) ? worst : 0;
				}
			}
		}

	Console.WriteLine($"{walked} methods walked");
	Console.WriteLine($"W-form reads of a value wider than 32 bits: {sites} sites in {wtMethods.Count} methods");
	Console.WriteLine($"   of those, descended from a widening multiply (magic division): {magicSites} sites in {magicMethods.Count} methods");
	foreach (var e in byMnemonic.OrderByDescending(x => x.Value).Take(14))
		Console.WriteLine($"   {e.Value,6}  {e.Key}");
	Console.WriteLine("examples:");
	foreach (string e in examples) Console.WriteLine("   " + e);
	return;
}

// xextend - a 64-bit data-processing instruction whose last operand is a w register widened by uxtw/sxtw.
// `Corpus::Shifts` is the shape: `add x8, x8, w8, uxtw #1` is a 64-bit multiply-by-three of a value the
// recovery kept 32 bits wide. The same encoding is how an array index is widened inside an address, so the
// second column separates the ones whose result is used as an address (a load or store follows on it).
if (args[3] == "xextend")
{
	static bool IsX(Disarm.InternalDisassembly.Arm64Register r) =>
		r is >= Disarm.InternalDisassembly.Arm64Register.X0 and <= Disarm.InternalDisassembly.Arm64Register.X31;

	int sites = 0, addressed = 0, walked = 0;
	Dictionary<string, int> byMnemonic = new();
	List<string> examples = new();

	foreach (TypeAnalysisContext type in app.AllTypes)
		foreach (MethodAnalysisContext m in type.Methods)
		{
			if (m.UnderlyingPointer == 0) continue;
			walked++;
			List<Disarm.Arm64Instruction> body;
			try { body = Cpp2IL.Core.Utils.NewArm64Utils.GetArm64MethodBodyAtVirtualAddress(app.Binary, m.UnderlyingPointer).ToList(); }
			catch { continue; }

			for (int k = 0; k < body.Count; k++)
			{
				Disarm.Arm64Instruction i = body[k];
				if (i.FinalOpExtendType is not (Disarm.Arm64ExtendType.UXTW or Disarm.Arm64ExtendType.SXTW)) continue;
				if (!IsX(i.Op0Reg)) continue;

				sites++;
				byMnemonic[i.Mnemonic + " " + i.FinalOpExtendType] = byMnemonic.GetValueOrDefault(i.Mnemonic + " " + i.FinalOpExtendType) + 1;

				//Used as an address: within four instructions something loads or stores through the register
				//this one wrote.
				bool asAddress = false;
				for (int j = k + 1; j < Math.Min(k + 5, body.Count); j++)
					if (body[j].MemBase == i.Op0Reg) { asAddress = true; break; }

				if (asAddress) addressed++;
				else if (examples.Count < 10) examples.Add($"{type.Name}::{m.Name}  0x{i.Address:X} {i}");
			}
		}

	Console.WriteLine($"{walked} methods walked");
	Console.WriteLine($"64-bit op with a uxtw/sxtw word operand: {sites} sites; {addressed} feed an address, {sites - addressed} do not");
	foreach (var e in byMnemonic.OrderByDescending(x => x.Value).Take(12))
		Console.WriteLine($"   {e.Value,7}  {e.Key}");
	Console.WriteLine("examples that are not an address:");
	foreach (string e in examples) Console.WriteLine("   " + e);
	return;
}

// sharedcalls - calls whose callee is on a shared generic instantiation (System.Object or a *Enum stand-in
// in its arguments), counted by whether the method itself carries a `methodof` naming the real
// instantiation of the same method. il2cpp hands a shared body the concrete MethodInfo, so where that is
// present the real callee is written down rather than inferred.
if (args[3] == "sharedcalls")
{
	int shared = 0, oneMatch = 0, manyMatches = 0, noMatch = 0, sameAlready = 0;
	Dictionary<string, int> byMethod = new();

	static bool IsStandIn(TypeAnalysisContext t) =>
		t.FullName == "System.Object" || t.Name.EndsWith("Enum");

	static bool Shared(TypeAnalysisContext? t) =>
		t is GenericInstanceTypeAnalysisContext g && g.GenericArguments.Any(IsStandIn);

	foreach (TypeAnalysisContext type in app.AllTypes)
		foreach (MethodAnalysisContext m in type.Methods)
		{
			if (m.UnderlyingPointer == 0) continue;
			try { m.Analyze(); } catch { continue; }
			if (m.ControlFlowGraph is null) continue;

			List<RuntimeMethodInfoAnalysisContext> named = new();
			foreach (Instruction i in m.ControlFlowGraph.Instructions)
				foreach (object o in i.Operands)
					if (o is RuntimeMethodInfoAnalysisContext runtime)
						named.Add(runtime);

			foreach (Instruction i in m.ControlFlowGraph.Instructions)
			{
				if (!i.IsCall || i.Operands.Count == 0) continue;
				if (i.Operands[0] is not MethodAnalysisContext callee) continue;
				if (!Shared(callee.DeclaringType)) continue;

				shared++;

				var definition = (callee.DeclaringType as GenericInstanceTypeAnalysisContext)!.GenericType;
				List<string> hits = new();

				foreach (var runtime in named)
				{
					MethodAnalysisContext real = runtime.RepresentedMethod;
					if (real.Name != callee.Name) continue;
					if (real.DeclaringType is not GenericInstanceTypeAnalysisContext instance) continue;
					if (!ReferenceEquals(instance.GenericType, definition)) continue;
					if (instance.GenericArguments.Any(IsStandIn)) { sameAlready++; continue; }
					if (!hits.Contains(real.FullName)) hits.Add(real.FullName);
				}

				if (hits.Count == 0) noMatch++;
				else if (hits.Count == 1) { oneMatch++; byMethod[callee.Name] = byMethod.GetValueOrDefault(callee.Name) + 1; }
				else manyMatches++;
			}
		}

	Console.WriteLine($"calls on a shared instantiation : {shared}");
	Console.WriteLine($"  exactly one methodof matches  : {oneMatch}");
	Console.WriteLine($"  more than one                 : {manyMatches}");
	Console.WriteLine($"  none                          : {noMatch}");
	Console.WriteLine($"  (methodof also shared)        : {sameAlready}");
	foreach (var e in byMethod.OrderByDescending(x => x.Value).Take(12))
		Console.WriteLine($"{e.Value,6}  {e.Key}");
	return;
}

// refarith - locals typed as a reference that are nonetheless operands of arithmetic. C# has no such
// operator, so every statement holding one is commented out; the question is how many there are and what
// the other operand looks like, which is what says whether the type or the arithmetic is the mistake.
if (args[3] == "refarith")
{
	Dictionary<string, int> byShape = new();
	Dictionary<string, string> example = new();
	int total = 0;

	static bool IsNumber(TypeAnalysisContext? t) => t is not null && t.IsValueType && t.Namespace == "System"
		&& t.Name is "Int32" or "UInt32" or "Int64" or "UInt64" or "Int16" or "UInt16" or "Byte" or "SByte"
			or "Single" or "Double" or "IntPtr" or "UIntPtr" or "Boolean" or "Char";

	foreach (TypeAnalysisContext type in app.AllTypes)
	{
		if (type.DeclaringAssembly.Name != "Assembly-CSharp") continue;

		foreach (MethodAnalysisContext m in type.Methods)
		{
			if (m.UnderlyingPointer == 0) continue;
			try { m.Analyze(); } catch { continue; }
			if (m.ControlFlowGraph is null) continue;

			Dictionary<LocalVariable, Instruction> definition = new();
			foreach (Instruction i in m.ControlFlowGraph.Instructions)
				if (i.Destination is LocalVariable d) definition[d] = i;

			foreach (Instruction i in m.ControlFlowGraph.Instructions)
			{
				if (i.OpCode is not (OpCode.Add or OpCode.Subtract or OpCode.Multiply or OpCode.Divide)) continue;
				if (i.Operands.Count < 3) continue;

				for (int op = 1; op <= 2; op++)
				{
					if (i.Operands[op] is not LocalVariable local || local.Type is not { IsValueType: false } held) continue;

					total++;

					object other = i.Operands[op == 1 ? 2 : 1];
					string otherText = other switch
					{
						LocalVariable o when IsNumber(o.Type) => "a number",
						LocalVariable { Type: null } => "an untyped local",
						LocalVariable o => "a " + (o.Type!.IsValueType ? "value type" : "reference"),
						long or int or ulong or uint => "a constant",
						_ => other.GetType().Name,
					};

					string made = definition.TryGetValue(local, out Instruction? d2)
						? d2.OpCode == OpCode.Move && d2.Operands.Count > 1 && Instruction.IsConstantValue(d2.Operands[1])
							? "assigned a constant" : "made by " + d2.OpCode
						: "never written here";

					string key = $"{otherText,-18} | {made}";
					byShape[key] = byShape.GetValueOrDefault(key) + 1;
					if (!example.ContainsKey(key)) example[key] = $"{type.Name}::{m.Name}  {i}";
				}
			}
		}
	}

	Console.WriteLine($"{total} arithmetic operands typed as a reference, in Assembly-CSharp");
	foreach (var e in byShape.OrderByDescending(x => x.Value).Take(14))
		Console.WriteLine($"{e.Value,6}  {e.Key,-52} {example[e.Key][..Math.Min(64, example[e.Key].Length)]}");
	return;
}

// sunkload - `add obj, this, 184` is the address of a field, and where several of them are chosen between
// and only then read through, no single field reference can speak for the choice. Sinking the read into the
// producers would: each becomes the field's own value and the choice picks between values. This counts how
// many such additions there are and what stands between them and the read.
// defaults - every `v = [absolute]` whose value is then read at `[v + N]`, tallied by the pointer the
// slot holds. il2cpp_defaults is a single global struct of Il2CppClass* for the builtin types, reached
// through a GOT slot, and it should stand out by sheer frequency.
// exacttest - in Assembly-CSharp only: every read of the runtime's table of built-in classes, split by
// what the reading instruction is. `Move dst, [table + N]` is the shape Il2CppDefaultsTable already
// answers for; a read sitting straight inside a comparison is the one it cannot see.
// unresolved - in Assembly-CSharp: every call whose target is still a bare address after the whole
// pipeline, bucketed by what the address maps to. An address that maps to several methods which are all
// one method under different instantiations is not really ambiguous.
// genericowner - `this + constant` in Assembly-CSharp, split by whether the type it is an offset into
// has any recorded field offsets at all. A generic definition has none: the layout depends on the
// arguments, so the metadata records nothing and every field reads as offset zero.
if (args[3] == "genericowner")
{
	int total = 0, noLayout = 0, hasLayout = 0, resolved = 0, reads = 0, readsNoLayout = 0;
	Dictionary<string, int> byOwner = new();
	Dictionary<string, int> byReadOwner = new();

	foreach (TypeAnalysisContext type in app.AllTypes)
	{
		if (type.DeclaringAssembly.Name != "Assembly-CSharp") continue;

		foreach (MethodAnalysisContext m in type.Methods)
		{
			if (m.UnderlyingPointer == 0) continue;
			try { m.Analyze(); } catch { continue; }
			if (m.ControlFlowGraph is null) continue;

			foreach (Instruction i in m.ControlFlowGraph.Instructions)
			{
				if (i.OpCode == OpCode.Move && i.Operands.Count == 2 && i.Operands[1] is FieldReference) resolved++;

				//A read straight through `this` at a distance, which is the same question one step earlier.
				foreach (object o in i.Operands)
					if (o is MemoryOperand { Base: LocalVariable { Name: "this", Type: { } through } } mo && mo.Addend != 0)
					{
						reads++;
						if (!through.Fields.Any(f => !f.IsStatic && f.BackingData?.FieldOffset > 0))
						{
							readsNoLayout++;
							string rk = through.FullName ?? "?";
							byReadOwner[rk] = byReadOwner.GetValueOrDefault(rk) + 1;
						}
					}

				if (i.OpCode != OpCode.Add || i.Operands.Count != 3) continue;
				if (i.Operands[0] is not LocalVariable) continue;
				if (i.Operands[1] is not LocalVariable { Name: "this", Type: { } owner }) continue;
				if (!Instruction.IsConstantValue(i.Operands[2])) continue;

				total++;
				bool any = owner.Fields.Any(f => !f.IsStatic && f.BackingData?.FieldOffset > 0);
				if (any) hasLayout++;
				else
				{
					noLayout++;
					string k = owner.FullName ?? "?";
					byOwner[k] = byOwner.GetValueOrDefault(k) + 1;
				}
			}
		}
	}

	Console.WriteLine($"{total} `this + constant` left in Assembly-CSharp: {hasLayout} into a type with recorded offsets, {noLayout} into one with none");
	Console.WriteLine($"({resolved} field references already resolved)");
	Console.WriteLine($"{reads} reads through `this` at a distance still unresolved, {readsNoLayout} of them into a type with no recorded layout");
	foreach (var e in byReadOwner.OrderByDescending(x => x.Value).Take(8))
		Console.WriteLine($"  read {e.Value,5}  {e.Key}");
	foreach (var e in byOwner.OrderByDescending(x => x.Value).Take(12))
		Console.WriteLine($"{e.Value,5}  {e.Key}");
	return;
}

if (args[3] == "unresolved")
{
	Dictionary<string, int> byKind = new();
	Dictionary<string, string> example = new();
	Dictionary<ulong, int> byAddress = new();

	foreach (TypeAnalysisContext type in app.AllTypes)
	{
		if (type.DeclaringAssembly.Name != "Assembly-CSharp") continue;

		foreach (MethodAnalysisContext m in type.Methods)
		{
			if (m.UnderlyingPointer == 0) continue;
			try { m.Analyze(); } catch { continue; }
			if (m.ControlFlowGraph is null) continue;

			foreach (Instruction i in m.ControlFlowGraph.Instructions)
			{
				if (!i.IsCall || i.Operands.Count == 0 || i.Operands[0] is not ulong at) continue;

				byAddress[at] = byAddress.GetValueOrDefault(at) + 1;

				string kind;
				string detail = "";

				if (!app.MethodsByAddress.TryGetValue(at, out var candidates) || candidates.Count == 0)
					kind = "no managed method at all";
				else
				{
					detail = string.Join(" | ", candidates.Take(4).Select(c => c.FullName));
					int definitions = candidates.Select(c => (object?)c.Definition ?? c).Distinct().Count();
					HashSet<string> shapes = new(candidates.Select(c => (c.Name ?? "?") + "/" + c.Parameters.Count + "/" + (c.IsStatic ? "s" : "i")));

					kind = definitions == 1 ? "one definition, several instantiations"
						: shapes.Count == 1 ? "several definitions, one name and shape"
						: $"{shapes.Count} different shapes";
				}

				byKind[kind] = byKind.GetValueOrDefault(kind) + 1;
				if (!example.ContainsKey(kind)) example[kind] = $"{type.Name}::{m.Name} @{at:X}  {detail}";
			}
		}
	}

	Console.WriteLine($"{byKind.Values.Sum()} unresolved calls in Assembly-CSharp over {byAddress.Count} addresses");
	foreach (var a in byAddress.OrderByDescending(x => x.Value).Take(10))
		Console.WriteLine($"  {a.Value,4} @{a.Key:X}  {(app.MethodsByAddress.TryGetValue(a.Key, out var cs) ? string.Join(" | ", cs.Take(3).Select(c => c.FullName)) : "<none>")}");
	foreach (var e in byKind.OrderByDescending(x => x.Value).Take(10))
		Console.WriteLine($"{e.Value,5}  {e.Key,-34} {example[e.Key][..Math.Min(94, example[e.Key].Length)]}");
	return;
}

if (args[3] == "exacttest")
{
	Dictionary<string, int> byUse = new();
	Dictionary<long, int> byOffset = new();
	Dictionary<string, string> example = new();
	int compares = 0, selects = 0;

	foreach (TypeAnalysisContext type in app.AllTypes)
	{
		if (type.DeclaringAssembly.Name != "Assembly-CSharp") continue;

		foreach (MethodAnalysisContext m in type.Methods)
		{
			if (m.UnderlyingPointer == 0) continue;
			try { m.Analyze(); } catch { continue; }
			if (m.ControlFlowGraph is null) continue;

			List<Instruction> all = m.ControlFlowGraph.Instructions.ToList();
			HashSet<LocalVariable> tables = new();

			foreach (Instruction i in all)
				if (i is { OpCode: OpCode.Move, Operands.Count: 2 }
					&& i.Operands[0] is LocalVariable made
					&& i.Operands[1] is MemoryOperand { Base: null, Index: null } mo && mo.Addend != 0)
				{
					ulong behind;
					try { behind = app.Binary.ReadPointerAtVirtualAddress((ulong)mo.Addend); } catch { continue; }
					if (behind == 0x50E66C8) tables.Add(made);
				}

			foreach (Instruction i in all)
			{
				for (int op = 0; op < i.Operands.Count; op++)
				{
					if (i.Operands[op] is not MemoryOperand r || r.Base is not LocalVariable b || !tables.Contains(b)) continue;
					byOffset[r.Addend] = byOffset.GetValueOrDefault(r.Addend) + 1;
					string key = i.OpCode + " op" + op + (i.OpCode is OpCode.Move && op == 1 ? " (table handles)" : "");
					byUse[key] = byUse.GetValueOrDefault(key) + 1;
					if (!example.ContainsKey(key)) example[key] = $"{type.Name}::{m.Name}  {i}";

					//A comparison whose other side is a read at distance nought - the object's class.
					if (i.OpCode is OpCode.CheckEqual or OpCode.CheckNotEqual && i.Operands.Count >= 3)
					{
						object other = op == 1 ? i.Operands[2] : i.Operands[1];
						if (other is MemoryOperand { Index: null, Addend: 0 }) compares++;
					}
				}

				if (i.OpCode == OpCode.Select) selects++;
			}
		}
	}

	Console.WriteLine($"{byOffset.Values.Sum()} reads of the table in Assembly-CSharp, {compares} of them a comparison against an object's class");
	Console.WriteLine("by the instruction reading it:");
	foreach (var e in byUse.OrderByDescending(x => x.Value).Take(12))
		Console.WriteLine($"{e.Value,5}  {e.Key,-26} {example[e.Key][..Math.Min(64, example[e.Key].Length)]}");
	Console.WriteLine("by offset:");
	foreach (var e in byOffset.OrderByDescending(x => x.Value).Take(20))
		Console.WriteLine($"{e.Value,5}  +0x{e.Key:X2}");
	return;
}

if (args[3] == "defaults")
{
	Dictionary<ulong, int> byTarget = new();
	Dictionary<ulong, Dictionary<long, int>> offsets = new();
	Dictionary<ulong, string> example = new();
	Dictionary<long, List<string>> sites = new();

	foreach (TypeAnalysisContext type in app.AllTypes)
	{
		foreach (MethodAnalysisContext m in type.Methods)
		{
			if (m.UnderlyingPointer == 0) continue;
			try { m.Analyze(); } catch { continue; }
			if (m.ControlFlowGraph is null) continue;

			List<Instruction> all = m.ControlFlowGraph.Instructions.ToList();
			Dictionary<LocalVariable, ulong> fromSlot = new();

			foreach (Instruction i in all)
			{
				if (i.OpCode != OpCode.Move || i.Operands.Count != 2) continue;
				if (i.Operands[0] is not LocalVariable made) continue;
				if (i.Operands[1] is not MemoryOperand { Base: null, Index: null } mo) continue;
				ulong slot = (ulong)mo.Addend;
				if (slot == 0) continue;
				ulong behind;
				try { behind = app.Binary.ReadPointerAtVirtualAddress(slot); } catch { continue; }
				if (behind == 0) continue;
				fromSlot[made] = behind;
			}

			foreach (Instruction i in all)
				foreach (object o in i.Operands)
					if (o is MemoryOperand r && r.Base is LocalVariable b && fromSlot.TryGetValue(b, out ulong target) && r.Addend != 0)
					{
						byTarget[target] = byTarget.GetValueOrDefault(target) + 1;
						if (!offsets.TryGetValue(target, out var byOffset)) offsets[target] = byOffset = new();
						byOffset[r.Addend] = byOffset.GetValueOrDefault(r.Addend) + 1;
						if (!example.ContainsKey(target)) example[target] = $"{type.Name}::{m.Name}  {i}";
						if (type.DeclaringAssembly.Name == "Assembly-CSharp")
						{
							if (!sites.TryGetValue(r.Addend, out var here)) sites[r.Addend] = here = new();
							if (here.Count < 30) here.Add($"{type.FullName}::{m.Name}  {i}");
						}
					}
		}
	}

	if (args.Length > 4)
	{
		long wanted = Convert.ToInt64(args[4], 16);
		foreach (var e in sites.Where(x => x.Key == wanted).SelectMany(x => x.Value).Take(30))
			Console.WriteLine(e);
		return;
	}

	foreach (var e in byTarget.OrderByDescending(x => x.Value).Take(8))
	{
		Console.WriteLine($"{e.Value,6} reads through 0x{e.Key:X}   {example[e.Key][..Math.Min(60, example[e.Key].Length)]}");
		foreach (var o in offsets[e.Key].OrderByDescending(x => x.Value).Take(12))
			Console.WriteLine($"          +0x{o.Key:X2}  x{o.Value}");
	}
	return;
}

if (args[3] == "sunkload")
{
	Dictionary<string, int> byShape = new();
	Dictionary<string, string> example = new();
	int additions = 0, viaCopy = 0, straight = 0;

	foreach (TypeAnalysisContext type in app.AllTypes)
	{
		if (type.DeclaringAssembly.Name != "Assembly-CSharp") continue;

		foreach (MethodAnalysisContext m in type.Methods)
		{
			if (m.UnderlyingPointer == 0) continue;
			try { m.Analyze(); } catch { continue; }
			if (m.ControlFlowGraph is null) continue;

			List<Instruction> all = m.ControlFlowGraph.Instructions.ToList();

			foreach (Instruction i in all)
			{
				if (i.OpCode != OpCode.Add || i.Operands.Count != 3) continue;
				if (i.Operands[0] is not LocalVariable made) continue;
				if (i.Operands[1] is not LocalVariable { Name: "this" }) continue;
				if (!Instruction.IsConstantValue(i.Operands[2])) continue;

				additions++;

				//Where does it go?
				List<string> uses = new();
				foreach (Instruction other in all)
				{
					if (ReferenceEquals(other, i)) continue;
					for (int op = 0; op < other.Operands.Count; op++)
					{
						object o = other.Operands[op];
						if (ReferenceEquals(o, made)) uses.Add(other.OpCode + " op" + op);
						else if (o is MemoryOperand mo && ReferenceEquals(mo.Base, made))
							uses.Add(mo.Addend == 0 && mo.Index == null ? "read at 0" : "read at a distance");
					}
				}

				string key = uses.Count == 0 ? "(unused)" : string.Join(" + ", uses.Distinct().OrderBy(x => x));
				byShape[key] = byShape.GetValueOrDefault(key) + 1;
				if (!example.ContainsKey(key)) example[key] = $"{type.Name}::{m.Name}  {i}";

				if (uses.Contains("read at 0")) straight++;
				if (uses.Any(u => u.StartsWith("Move op0")) || uses.Any(u => u.StartsWith("Phi")) || uses.Any(u => u.StartsWith("Select"))) viaCopy++;
			}
		}
	}

	Console.WriteLine($"{additions} `this + constant` in Assembly-CSharp; {straight} read at distance 0 directly, {viaCopy} reach a copy or a choice");
	foreach (var e in byShape.OrderByDescending(x => x.Value).Take(16))
		Console.WriteLine($"{e.Value,5}  {e.Key,-46} {example[e.Key][..Math.Min(58, example[e.Key].Length)]}");
	return;
}

// isil <typeNameSubstring> [methodNameSubstring] - the ISIL the fork's passes actually operate on,
// which is not what the export shows and not what `asm` shows.
// hfa - how often a call's homogeneous-float-struct argument has a local for every register it
// travels in, which is what decides whether the struct can be assembled at all.
if (args[3] == "hfa")
{
	int sites = 0, allPresent = 0, nonePresent = 0, somePresent = 0;
	Dictionary<string,int> byType = new(), refusalByType = new();
	Dictionary<string,string> example = new();

	foreach (TypeAnalysisContext type in app.AllTypes)
	{
		if (type.DeclaringAssembly?.Name != "Assembly-CSharp") continue;
		foreach (MethodAnalysisContext m in type.Methods)
		{
			if (m.UnderlyingPointer == 0) continue;
			try { m.Analyze(); } catch { continue; }
			if (m.ControlFlowGraph is null) continue;

			// every local that names a vector register, by register name
			HashSet<string> vectorLocals = new();
			foreach (Instruction i in m.ControlFlowGraph.Instructions)
				foreach (object o in i.Operands)
					if (o is LocalVariable lv && lv.Register is { Name: { } rn } && (rn[0] == 'V' || rn[0] == 'S' || rn[0] == 'D') && rn.Length > 1 && char.IsDigit(rn[1]))
						vectorLocals.Add("V" + new string(rn.Skip(1).TakeWhile(char.IsDigit).ToArray()));

			foreach (Instruction i in m.ControlFlowGraph.Instructions)
			{
				if (!i.IsCall || i.Operands.Count == 0 || i.Operands[0] is not MethodAnalysisContext callee) continue;
				int vector = 0;
				foreach (ParameterAnalysisContext p in callee.Parameters)
				{
					TypeAnalysisContext pt = p.ParameterType;
					if (pt is { Namespace: "System", Name: "Single" or "Double" }) { vector++; continue; }
					if (Cpp2IL.Core.Analysis.HomogeneousFloatStruct.Count(pt) is not { } floats) continue;
					int first = vector; vector += floats;
					if (floats < 2) continue;
					sites++;
					byType[pt.Name] = byType.GetValueOrDefault(pt.Name) + 1;
					int present = 0;
					for (int k = 0; k < floats; k++) if (vectorLocals.Contains("V" + (first + k))) present++;
					if (present == floats) { allPresent++; }
					else if (present <= 1) { nonePresent++; refusalByType[pt.Name] = refusalByType.GetValueOrDefault(pt.Name) + 1;
						example.TryAdd(pt.Name, type.Name + "::" + m.Name); }
					else somePresent++;
				}
			}
		}
	}
	Console.WriteLine($"HFA argument sites {sites}:  every register present {allPresent}, some {somePresent}, only the first {nonePresent}");
	foreach (var kv in byType.OrderByDescending(k => k.Value))
		Console.WriteLine($"  {kv.Key,-12} {kv.Value,5}   only the first {refusalByType.GetValueOrDefault(kv.Key),5}   eg {example.GetValueOrDefault(kv.Key, "")}");
	return;
}

// rawisil <type> [method] - the ISIL straight out of the lifter, before a single analysis pass.
//
// `dump` and `isil` both call Analyze() first, so neither can tell a lifter that never produced an
// instruction from a pass that deleted it. This one asks the instruction set directly.
if (args[3] == "rawisil")
{
	foreach (TypeAnalysisContext type in app.AllTypes)
	{
		if (!type.Name.Contains(args[4])) continue;
		foreach (MethodAnalysisContext m in type.Methods)
		{
			if (args.Length > 5 && !m.Name.Contains(args[5])) continue;
			if (m.UnderlyingPointer == 0) continue;

			Console.WriteLine($"===== {type.FullName}::{m.Name} @ {m.UnderlyingPointer:X}");

			try
			{
				System.Collections.Generic.List<Instruction> lifted = app.InstructionSet.GetIsilFromMethod(m);
				for (int i = 0; i < lifted.Count; i++) Console.WriteLine($"    {i,4} {lifted[i]}");
			}
			catch (Exception e) { Console.WriteLine("    threw " + e.Message); }
		}
	}
	return;
}

if (args[3] == "isil")
{
	foreach (TypeAnalysisContext type in app.AllTypes)
	{
		if (!type.Name.Contains(args[4])) continue;
		foreach (MethodAnalysisContext m in type.Methods)
		{
			if (args.Length > 5 && !m.Name.Contains(args[5])) continue;
			if (m.UnderlyingPointer == 0) continue;
			try { m.Analyze(); } catch (Exception e) { Console.WriteLine($"===== {type.FullName}::{m.Name} threw {e.Message}"); continue; }
			if (m.ControlFlowGraph is null) continue;
			Console.WriteLine($"===== {type.FullName}::{m.Name} @ {m.UnderlyingPointer:X}  {Signature(m)}{Registers(m)}");
			foreach (Instruction i in m.ControlFlowGraph.Instructions)
				Console.WriteLine("    " + i);
		}
	}
	return;
}

// invalid [assemblySubstring] - every arm64 word the disassembler refuses, by encoding.
// paramregs [assemblySubstring] - one line per method: its parameter operand assignment.
//
// For diffing a change to the argument allocator against itself. Run once with PARAMWIDEN_OFF=1 and once
// without, and `diff` says exactly which methods' naming moved - which is the only way to check that a
// method whose parameters all fit a register is byte-identical, and that the count that moved is the count
// predicted. No scorer can see a wrong parameter register.
if (args[3] == "paramregs")
{
	foreach (AssemblyAnalysisContext asm in app.Assemblies)
	{
		if (args.Length > 4 && !asm.Definition.AssemblyName.Name.Contains(args[4])) continue;
		foreach (TypeAnalysisContext type in asm.Types)
		foreach (MethodAnalysisContext m in type.Methods)
		{
			if (m.UnderlyingPointer == 0) continue;
			string regs;
			// ParameterOperands is filled in by Analyze, not on construction - reading it without analysing
			// gives an empty list for every method, which diffs as "nothing moved" and is worthless.
			try { regs = string.Join(",", app.InstructionSet.GetParameterOperandsFromMethod(m).Select(o => o?.ToString() ?? "?")); }
			catch (Exception e) { regs = "THREW " + e.GetType().Name; }
			int extra = 0;
			try { extra = Cpp2IL.Core.Analysis.ParametersOnTheStack.ExtraIntegerRegisters(m); } catch { }
			Console.WriteLine($"{type.FullName}::{m.Name}\t{Signature(m)}\t{regs}\textra={extra}");
		}
	}
	return;
}

// bigparams [assemblySubstring] - every method taking a by-value composite the argument allocator gives one
// register and the machine gives two, or passes by reference.
//
// AAPCS64: a composite of 9..16 bytes takes TWO general registers, and one over 16 bytes is passed by
// reference. `MethodAnalysisContext.ParameterOperands` hands out one register per parameter, so every
// parameter after such a one is off by at least one register. Homogeneous float structs are excluded: they
// go in the vector registers and the fork already models them.
if (args[3] == "bigparams")
{
	int seenMethods = 0, affected = 0;
	var byType = new Dictionary<string, int>();

	foreach (AssemblyAnalysisContext asm in app.Assemblies)
	{
		if (args.Length > 4 && !asm.Definition.AssemblyName.Name.Contains(args[4])) continue;
		foreach (TypeAnalysisContext type in asm.Types)
		foreach (MethodAnalysisContext m in type.Methods)
		{
			if (m.UnderlyingPointer == 0) continue;
			seenMethods++;
			bool hit = false;
			foreach (ParameterAnalysisContext p in m.Parameters)
			{
				TypeAnalysisContext t = p.ParameterType;
				if (t == null || !t.IsValueType || t.IsEnumType) continue;
				if (Cpp2IL.Core.Analysis.HomogeneousFloatStruct.Count(t) is int n && n > 0) continue;
				long? size = Cpp2IL.Core.Analysis.Aapcs64.SizeOf(t);
				if (size is not > 8) continue;
				hit = true;
				byType[t.FullName + " (" + size + "b)"] = byType.GetValueOrDefault(t.FullName + " (" + size + "b)") + 1;
			}
			if (hit) { affected++; Console.WriteLine($"{type.FullName}::{m.Name}  {Signature(m)}"); }
		}
	}

	Console.WriteLine($"\n{affected} of {seenMethods} methods take a by-value composite over 8 bytes that is not an HFA");
	foreach (var kv in byType.OrderByDescending(k => k.Value)) Console.WriteLine($"  {kv.Value,4}  {kv.Key}");
	return;
}

//
// An instruction Disarm will not decode is reported with mnemonic INVALID and address 0, so nothing
// downstream knows where it was and a branch to it loses its target outright. ARM64 is fixed width and the
// list comes back in order, so the word is at `start + 4*index` and can be read straight out of the binary.
// Grouping by the top bits says what FAMILY is refused rather than which method noticed.
if (args[3] == "invalid")
{
	string wanted = args.Length > 4 ? args[4] : "";
	Dictionary<uint, int> byWord = new();
	Dictionary<uint, string> anExample = new();
	Dictionary<uint, int> branchedTo = new();
	HashSet<string> withAny = new(), withBranch = new();
	int seen = 0, refused = 0;
	byte[] raw = app.Binary.GetRawBinaryContent().ToArray();

	foreach (AssemblyAnalysisContext assembly in app.Assemblies)
	{
		if (wanted.Length > 0 && !assembly.Definition.AssemblyName.Name.Contains(wanted)) continue;
		foreach (TypeAnalysisContext type in assembly.Types)
		foreach (MethodAnalysisContext m in type.Methods)
		{
			if (m.UnderlyingPointer == 0) continue;
			List<Disarm.Arm64Instruction> body;
			try { body = Cpp2IL.Core.Utils.NewArm64Utils.GetArm64MethodBodyAtVirtualAddress(app.Binary, m.UnderlyingPointer); }
			catch { continue; }
			seen++;

			HashSet<ulong> targets = new();
			foreach (Disarm.Arm64Instruction i in body)
				if (i.Mnemonic is Disarm.Arm64Mnemonic.B or Disarm.Arm64Mnemonic.BL)
					try { targets.Add(i.BranchTarget); } catch { }

			for (int at = 0; at < body.Count; at++)
			{
				if (body[at].Mnemonic is not (Disarm.Arm64Mnemonic.INVALID or Disarm.Arm64Mnemonic.UNIMPLEMENTED)) continue;
				ulong address = m.UnderlyingPointer + (ulong)(4 * at);
				long file;
				try { file = app.Binary.MapVirtualAddressToRaw(address); } catch { continue; }
				if (file <= 0 || file + 4 > raw.Length) continue;
				uint word = BitConverter.ToUInt32(raw, (int)file);
				refused++;
				byWord.TryGetValue(word, out int n);
				byWord[word] = n + 1;
				if (!anExample.ContainsKey(word))
					anExample[word] = $"{type.FullName}::{m.Name} @ {address:X}";
				withAny.Add($"{type.FullName}::{m.Name}");
				if (targets.Contains(address))
				{
					branchedTo.TryGetValue(word, out int b);
					branchedTo[word] = b + 1;
					withBranch.Add($"{type.FullName}::{m.Name}");
				}
			}
		}
	}

	// Rn is bits 9-5 and Rd bits 4-0, so masking the bottom ten bits leaves exactly what names the
	// instruction and drops which registers it happened to use.
	Dictionary<uint, int> byFamily = new(), familyBranched = new();
	Dictionary<uint, string> familyExample = new();
	foreach (KeyValuePair<uint, int> e in byWord)
	{
		uint family = e.Key & 0xFFFFFC00u;
		byFamily.TryGetValue(family, out int n);
		byFamily[family] = n + e.Value;
		branchedTo.TryGetValue(e.Key, out int b);
		familyBranched.TryGetValue(family, out int fb);
		familyBranched[family] = fb + b;
		if (!familyExample.ContainsKey(family)) familyExample[family] = anExample[e.Key];
	}

	Console.WriteLine($"{seen} methods, {refused} refused words, {byWord.Count} distinct encodings, {byFamily.Count} families");
	Console.WriteLine($"{withAny.Count} methods hold at least one; {withBranch.Count} hold one that is BRANCHED TO");
	Console.WriteLine("  count  branched-to  masked    example");
	foreach (KeyValuePair<uint, int> e in byFamily.OrderByDescending(e => familyBranched.TryGetValue(e.Key, out int b) ? b : 0).ThenByDescending(e => e.Value).Take(40))
	{
		familyBranched.TryGetValue(e.Key, out int b);
		Console.WriteLine($"{e.Value,7}  {b,11}  {e.Key:X8}  {familyExample[e.Key]}");
	}
	return;
}

// bare - every method whose compiled body is a single RET. The source may well have statements: writing to
// a by-value struct parameter is what `void SetX(this Vector2 v, float x) => v.x = x;` does, and the C#
// compiler discards it too. An empty export is the right answer for these, so counting them as owed is
// counting a defect that is not there.
if (args[3] == "bare")
{
	int bare = 0, walked = 0;
	foreach (AssemblyAnalysisContext assembly in app.Assemblies)
	{
		foreach (TypeAnalysisContext type in assembly.Types)
		{
			foreach (MethodAnalysisContext m in type.Methods)
			{
				if (m.UnderlyingPointer == 0) continue;
				walked++;
				List<Disarm.Arm64Instruction> body;
				try { body = Cpp2IL.Core.Utils.NewArm64Utils.GetArm64MethodBodyAtVirtualAddress(app.Binary, m.UnderlyingPointer).ToList(); }
				catch { continue; }
				if (body.Count != 1 || body[0].Mnemonic != Disarm.Arm64Mnemonic.RET) continue;
				bare++;
				Console.WriteLine($"{assembly.CleanAssemblyName}\t{type.FullName}\t{m.Name}\t{m.Parameters.Count}");
			}
		}
	}
	Console.Error.WriteLine($"{bare} of {walked} methods are a bare RET");
	return;
}

if (args[3] == "asm")
{
	foreach (TypeAnalysisContext type in app.AllTypes)
	{
		if (!type.Name.Contains(args[4])) continue;
		foreach (MethodAnalysisContext m in type.Methods)
		{
			if (args.Length > 5 && !m.Name.Contains(args[5])) continue;
			Console.WriteLine($"===== {type.FullName}::{m.Name} @ {m.UnderlyingPointer:X}  {Signature(m)}{Registers(m)}");
			foreach (Disarm.Arm64Instruction i in Cpp2IL.Core.Utils.NewArm64Utils.GetArm64MethodBodyAtVirtualAddress(app.Binary, m.UnderlyingPointer))
				Console.WriteLine("    " + i);
		}
	}
	return;
}

// mrgctx <typeFullNameSubstring> [methodNameSubstring] - the method's own runtime generic context,
// which is what a body reads through MethodInfo::rgctx_data.
if (args[3] == "trgctx")
{
	foreach (TypeAnalysisContext type in app.AllTypes)
	{
		if (!type.FullName.Contains(args[4])) continue;
		LibCpp2IL.BinaryStructures.Il2CppRGCTXDefinition[] entries = type.Definition?.RgctXs ?? [];
		Console.WriteLine($"===== {type.FullName}  token={type.Definition?.Token:X}  entries={entries.Length}");
		for (int i = 0; i < entries.Length; i++)
		{
			LibCpp2IL.BinaryStructures.Il2CppRGCTXDefinition e = entries[i];
			string what;
			try
			{
				what = e.type switch
				{
					LibCpp2IL.BinaryStructures.Il2CppRGCTXDataType.IL2CPP_RGCTX_DATA_METHOD
						=> "method " + e.MethodSpec.MethodDefinition?.GlobalKey + "  spec=" + e.MethodSpec,
					_ => "type " + app.ResolveIl2CppType(e.Type)?.FullName,
				};
			}
			catch (Exception ex) { what = "<" + ex.GetType().Name + ": " + ex.Message + ">"; }
			Console.WriteLine($"  [{i}] +{i * 8:X}  {e.type}  {what}");
		}
	}
	return;
}

if (args[3] == "mrgctx")
{
	foreach (TypeAnalysisContext type in app.AllTypes)
	{
		if (!type.FullName.Contains(args[4])) continue;
		foreach (MethodAnalysisContext method in type.Methods)
		{
			if (args.Length > 5 && !method.Name.Contains(args[5])) continue;
			LibCpp2IL.BinaryStructures.Il2CppRGCTXDefinition[] entries = method.Definition?.RgctXs ?? [];
			Console.WriteLine($"===== {type.FullName}::{method.Name}  {Signature(method)}  token={method.Definition?.token:X}  entries={entries.Length}");
			for (int i = 0; i < entries.Length; i++)
			{
				LibCpp2IL.BinaryStructures.Il2CppRGCTXDefinition e = entries[i];
				string what;
				try
				{
					what = e.type switch
					{
						LibCpp2IL.BinaryStructures.Il2CppRGCTXDataType.IL2CPP_RGCTX_DATA_METHOD
							=> "method " + e.MethodSpec.MethodDefinition?.GlobalKey,
						_ => "type " + app.ResolveIl2CppType(e.Type)?.FullName,
					};
				}
				catch (Exception ex) { what = "<" + ex.GetType().Name + ">"; }
				Console.WriteLine($"  [{i}] +{i * 8:X}  {e.type}  {what}");
			}
		}
	}
	return;
}

// slots <typeFullNameSubstring> - every method's vtable slot, walking up the base types.
if (args[3] == "slots")
{
	foreach (TypeAnalysisContext type in app.AllTypes)
	{
		if (!type.FullName.Contains(args[4])) continue;
		Console.WriteLine($"===== {type.FullName}");
		for (TypeAnalysisContext? t = type; t is not null; t = t.BaseType)
		{
			Console.WriteLine($"  -- {t.FullName}");
			foreach (MethodAnalysisContext m in (t is GenericInstanceTypeAnalysisContext g ? g.GenericType : t).Methods)
				Console.WriteLine($"     slot={m.Definition?.slot} {m.Name}({string.Join(", ", m.Parameters.Select(p => p.ParameterType.FullName))}) -> {m.ReturnType?.FullName}");
		}
	}
	return;
}

if (args[3] == "fields")
{
	foreach (TypeAnalysisContext type in app.AllTypes)
	{
		if (!type.FullName.Contains(args[4])) continue;
		string sizes = "";
		try { LibCpp2IL.BinaryStructures.Il2CppTypeDefinitionSizes sz = type.Definition!.RawSizes; sizes = $" instance={sz.instance_size:X} static={sz.static_fields_size:X}"; } catch (Exception e) { sizes = " <" + e.GetType().Name + ">"; }
		Console.WriteLine($"===== {type.FullName} base={type.BaseType?.FullName ?? "<null>"} generic={type.GenericParameters.Count}{sizes}");
		foreach (FieldAnalysisContext f in type.Fields)
			Console.WriteLine($"    offset={f.BackingData?.FieldOffset:X} static={f.IsStatic} {f.Name} : {f.FieldType.FullName}");
	}
	return;
}

// owner <addr> - the registered method an address falls inside, and its neighbours either side.
// An address `MethodsByAddress` does not have is either a runtime helper with no metadata or a piece of a
// method that *is* registered; only the neighbours say which.
if (args[3] == "owner")
{
	ulong want = Convert.ToUInt64(args[4], 16);
	List<(ulong At, string Name)> all = new();
	foreach (TypeAnalysisContext t in app.AllTypes)
		foreach (MethodAnalysisContext m in t.Methods)
			if (m.UnderlyingPointer != 0)
				all.Add((m.UnderlyingPointer, t.FullName + "::" + m.Name));
	all.Sort((a, b) => a.At.CompareTo(b.At));
	int i = all.FindLastIndex(e => e.At <= want);
	for (int k = Math.Max(0, i - 2); k <= Math.Min(all.Count - 1, i + 2); k++)
		Console.WriteLine($"    {all[k].At:X}  {(all[k].At == want ? "<== " : "    ")}{all[k].Name}");
	Console.WriteLine($"    asked {want:X}: {(i >= 0 ? $"{want - all[i].At} bytes after {all[i].Name}" : "before everything")}");
	return;
}

if (args[3] == "at")
{
	ulong at = Convert.ToUInt64(args[4], 16);
	int count = args.Length > 5 ? int.Parse(args[5]) : 14;
	Console.WriteLine($"===== {at:X}  known={string.Join(",", app.MethodsByAddress.TryGetValue(at, out var ms) ? ms.Select(m => m.FullName) : new string[0])}");
	int shown = 0;
	foreach (Disarm.Arm64Instruction ins in Cpp2IL.Core.Utils.NewArm64Utils.GetArm64MethodBodyAtVirtualAddress(app.Binary, at))
	{
		Console.WriteLine("    " + ins);
		if (++shown >= count) break;
	}
	return;
}

if (args[3] == "global")
{
	ulong g = Convert.ToUInt64(args[4], 16);
	LibCpp2IL.LibCpp2IlContext ctx = app.LibCpp2IlContext;
	Console.WriteLine($"literal   = {ctx.GetLiteralByAddress(g) ?? "<null>"}");
	Console.WriteLine($"typeGlob  = {ctx.GetTypeGlobalByAddress(g)?.ToString() ?? "<null>"}");
	Console.WriteLine($"methGlob  = {ctx.GetMethodGlobalByAddress(g)?.Type.ToString() ?? "<null>"}");
	Console.WriteLine($"raw ok    = {app.Binary.TryMapVirtualAddressToRaw(g, out long raw)} raw={raw:X}");
	try { Console.WriteLine($"word      = {app.Binary.ReadPointerAtVirtualAddress(g):X}"); } catch (Exception e) { Console.WriteLine("word = <" + e.GetType().Name + ">"); }
	try
	{
		ulong behind = app.Binary.ReadPointerAtVirtualAddress(g);
		Console.WriteLine($"behind.literal = {ctx.GetLiteralByAddress(behind) ?? "<null>"}");
		Console.WriteLine($"behind.type    = {ctx.GetTypeGlobalByAddress(behind)?.ToString() ?? "<null>"}");
		Console.WriteLine($"behind.method  = {ctx.GetMethodGlobalByAddress(behind)?.Type.ToString() ?? "<null>"}");
		Console.WriteLine($"behind known method = {string.Join(",", app.MethodsByAddress.TryGetValue(behind, out var mm) ? mm.Select(x => x.FullName) : new string[0])}");
	}
	catch (Exception e) { Console.WriteLine("behind = <" + e.GetType().Name + ">"); }
	return;
}

// "calls" prints, for each call in a method, what the callee context actually is - its CLR class, its
// declaring type's class, what it returns, and what it is an instantiation of. Answers "why is this typed
// object" without another export.
// helpers - every call that still resolves to nothing but an address, ranked by how often it is made, with
// what the caller does with the result. These are the runtime helpers no symbol names.
// helperkind <addr> - what a helper does, judged by what it touches: the Il2CppClass offsets it reads, the
// known key functions it reaches, and any managed method it lands on. Follows thunks and calls two deep.
if (args[3] == "helperkind")
{
	var kfa = app.GetOrCreateKeyFunctionAddresses();
	Dictionary<ulong, string> known = [];
	foreach (var field in kfa.GetType().GetFields())
		if (field.GetValue(kfa) is ulong ka && ka != 0 && !known.ContainsKey(ka)) known[ka] = field.Name;

	Dictionary<long, string> classOffsets = new()
	{
		[0xB8] = "static_fields", [0xC0] = "rgctx_data", [0x12E] = "interface_offsets_count",
		[0x135] = "bitflags", [0x136] = "bitflags2", [0xE4] = "initialized?", [0xB0] = "interface_offsets",
		[0x138] = "vtable", [0x28] = "byval_type?", [0x60] = "cctor?",
	};

	foreach (string spec in args[4].Split(','))
	{
		ulong start = Convert.ToUInt64(spec, 16);
		HashSet<ulong> seen = [];
		Queue<(ulong Addr, int Depth)> queue = new();
		queue.Enqueue((start, 0));
		SortedSet<string> touches = [], reaches = [], lands = [];

		while (queue.Count > 0)
		{
			(ulong at, int depth) = queue.Dequeue();
			if (depth > 2 || !seen.Add(at)) continue;
			if (known.TryGetValue(at, out string? kn)) { reaches.Add(kn); continue; }
			if (app.MethodsByAddress.TryGetValue(at, out var ms) && ms.Count > 0) { lands.Add(ms[0].FullName ?? "?"); continue; }

			int n = 0;
			foreach (Disarm.Arm64Instruction ins in Cpp2IL.Core.Utils.NewArm64Utils.GetArm64MethodBodyAtVirtualAddress(app.Binary, at))
			{
				if (++n > 60) break;
				if (ins.Op2Kind == Disarm.Arm64OperandKind.Immediate && classOffsets.TryGetValue(ins.MemOffset, out string? name)) touches.Add($"+{ins.MemOffset:X}={name}");
				else if (classOffsets.TryGetValue(ins.MemOffset, out string? nm2) && ins.MemOffset != 0) touches.Add($"+{ins.MemOffset:X}={nm2}");
				if (ins.Mnemonic is Disarm.Arm64Mnemonic.BL or Disarm.Arm64Mnemonic.B && ins.BranchTarget != 0)
					queue.Enqueue((ins.BranchTarget, depth + (ins.Mnemonic == Disarm.Arm64Mnemonic.BL ? 1 : 0)));
				if (ins.Mnemonic is Disarm.Arm64Mnemonic.RET) break;
			}
		}

		Console.WriteLine($"0x{start:X}  reaches=[{string.Join(" ", reaches)}]  lands=[{string.Join(" ", lands.Take(2))}]  touches=[{string.Join(" ", touches.Take(6))}]");
	}
	return;
}

// rank2 - how much multidimensional array there is: fields, parameters, returns and locals of one.
if (args[3] == "rank2")
{
	int fields = 0, parameters = 0, returns = 0;
	Dictionary<string, int> byType = [];
	foreach (TypeAnalysisContext type in app.AllTypes)
	{
		foreach (FieldAnalysisContext f in type.Fields)
			if (f.FieldType is ArrayTypeAnalysisContext { Rank: > 1 } a)
			{ fields++; byType[a.FullName] = byType.GetValueOrDefault(a.FullName) + 1; }
		foreach (MethodAnalysisContext m in type.Methods)
		{
			if (m.ReturnType is ArrayTypeAnalysisContext { Rank: > 1 } r)
			{ returns++; byType[r.FullName] = byType.GetValueOrDefault(r.FullName) + 1; }
			foreach (ParameterAnalysisContext p in m.Parameters)
				if (p.ParameterType is ArrayTypeAnalysisContext { Rank: > 1 } pa)
				{ parameters++; byType[pa.FullName] = byType.GetValueOrDefault(pa.FullName) + 1; }
		}
	}
	Console.WriteLine($"Assembly-CSharp: {fields} fields, {parameters} parameters, {returns} returns of rank > 1");
	foreach (KeyValuePair<string,int> pair in byType.OrderByDescending(p => p.Value).Take(12))
		Console.WriteLine($"  {pair.Value,4}  {pair.Key}");
	return;
}

if (args[3] == "helpers")
{
	Dictionary<ulong, int> made = [];
	Dictionary<ulong, int> resultUsed = [];
	Dictionary<ulong, string> anExample = [];

	foreach (TypeAnalysisContext type in app.AllTypes)
	{
		if (type.DeclaringAssembly?.Name != "Assembly-CSharp") continue;
		foreach (MethodAnalysisContext method in type.Methods)
		{
			try { method.Analyze(); } catch { continue; }
			if (method.ControlFlowGraph is null) continue;
			foreach (Instruction instruction in method.ControlFlowGraph.Instructions)
			{
				if (!instruction.IsCall || instruction.Operands.Count == 0) continue;
				if (instruction.Operands[0] is not ulong address) continue;
				made[address] = made.GetValueOrDefault(address) + 1;
				// Only the results that go on to be read through - those are the untyped bases this is about.
				if (instruction.OpCode == OpCode.Call && instruction.Operands.Count > 1 && instruction.Operands[1] is LocalVariable produced && produced.Type is null)
				{
					foreach (Instruction later in method.ControlFlowGraph.Instructions)
					{
						bool basedOnIt = false;
						foreach (object operand in later.Operands)
							if (operand is MemoryOperand mem && (ReferenceEquals(mem.Base, produced) || ReferenceEquals(mem.Index, produced)))
								basedOnIt = true;
						if (basedOnIt) { resultUsed[address] = resultUsed.GetValueOrDefault(address) + 1; break; }
					}
				}
				if (!anExample.ContainsKey(address)) anExample[address] = $"{type.Name}::{method.Name}";
			}
			method.ReleaseAnalysisData();
		}
	}

	Console.WriteLine($"{made.Count} distinct addresses, {made.Values.Sum()} calls");
	foreach (KeyValuePair<ulong, int> pair in made.OrderByDescending(p => p.Value).Take(args.Length > 4 ? int.Parse(args[4]) : 30))
		Console.WriteLine($"{pair.Value,6}  0x{pair.Key:X}  withResult={resultUsed.GetValueOrDefault(pair.Key),5}  eg {anExample[pair.Key]}");
	return;
}

if (args[3] == "leftover")
{
	// The second word of a dispatch pair. A virtual or interface call reads `LDP X8, X1, [entry]`; the
	// pass that recovers the call takes the first word and leaves the second - a MethodInfo* sitting in an
	// argument register - and the next call picks it up as one of its own arguments. Silent: the local is
	// typed from the parameter it lands in, so no marker is produced and every scorer calls the body whole.
	int sites = 0, atEight = 0, undefinedBase = 0, untypedBase = 0, reachingAny = 0, neverRead = 0;
	System.Collections.Generic.List<string> deadIn = new();
	System.Collections.Generic.HashSet<string> bodies = new();
	System.Collections.Generic.List<string> lines = new();
	System.Collections.Generic.Dictionary<string, int> byRegister = new();

	foreach (TypeAnalysisContext type in app.AllTypes)
	{
		if (type.DeclaringAssembly.Name != "Assembly-CSharp") continue;
		if (args.Length > 4 && args[4].Length > 0 && !type.Name.Contains(args[4])) continue;

		foreach (MethodAnalysisContext m in type.Methods)
		{
			if (m.UnderlyingPointer == 0) continue;
			try { m.Analyze(); } catch { continue; }
			if (m.ControlFlowGraph is null) continue;

			System.Collections.Generic.HashSet<LocalVariable> defined = new();
			foreach (Instruction i in m.ControlFlowGraph.Instructions)
				if (i.Destination is LocalVariable d) defined.Add(d);

			System.Collections.Generic.HashSet<string> parameters = new();
			foreach (LocalVariable pl in m.ParameterLocals) parameters.Add(pl.Register.Name);

			foreach (Instruction read2 in m.ControlFlowGraph.Instructions)
			{
				if (read2.OpCode != OpCode.Move || read2.Operands.Count != 2) continue;
				if (read2.Operands[0] is not LocalVariable loaded) continue;
				if (read2.Operands[1] is not MemoryOperand { Index: null, Scale: 0, Addend: 8 } entry) continue;
				if (entry.Base is not LocalVariable rooted) continue;
				atEight++;
				bool undefined = !defined.Contains(rooted) && !parameters.Contains(rooted.Register.Name);
				if (undefined) undefinedBase++;
				if (rooted.Type is null) untypedBase++;

				// Does anything at all read what this loaded?
				bool read = false;
				foreach (Instruction other in m.ControlFlowGraph.Instructions)
				{
					if (ReferenceEquals(other, read2)) continue;
					for (int k = other.OpCode == OpCode.Call || other.OpCode == OpCode.CallVoid ? 0 : 1; k < other.Operands.Count; k++)
					{
						object o = other.Operands[k];
						if (ReferenceEquals(o, loaded)) { read = true; break; }
						if (o is MemoryOperand mo && (ReferenceEquals(mo.Base, loaded) || ReferenceEquals(mo.Index, loaded))) { read = true; break; }
					}
					if (read) break;
				}
				if (!read) { neverRead++; if (deadIn.Count < 30) deadIn.Add($"{type.Name}::{m.Name}"); }

				// Where it goes: an argument of a call that DID resolve, which is the silent part.
				foreach (Instruction call in m.ControlFlowGraph.Instructions)
				{
					if (!call.IsCall || call.Operands.Count == 0) continue;
					if (call.Operands[0] is not MethodAnalysisContext callee) continue;

					int at = -1;
					for (int k = 1; k < call.Operands.Count; k++)
						if (ReferenceEquals(call.Operands[k], loaded)) { at = k; break; }
					if (at < 0) continue;

					reachingAny++;
					if (!undefined) continue;
					sites++;
					bodies.Add($"{type.Name}::{m.Name}");
					string bare = loaded.Register.Name.Split('_')[0];
					byRegister[bare] = byRegister.GetValueOrDefault(bare) + 1;
					if (lines.Count < 40)
						lines.Add($"{type.Name}::{m.Name}  {loaded.Name} ({loaded.Type?.FullName ?? "<null>"}) -> operand {at} of {callee.FullName}");
				}
			}

			m.ReleaseAnalysisData();
		}
	}

	Console.WriteLine($"{atEight} reads at [base+8]; {undefinedBase} with an undefined base, {untypedBase} with an untyped base, {neverRead} whose result NOTHING reads");
	foreach (string d in deadIn) Console.WriteLine("  dead in " + d);
	Console.WriteLine($"{reachingAny} of them reach a resolved call as an argument; {sites} of those have an undefined base, in {bodies.Count} bodies");
	foreach (var e in byRegister.OrderByDescending(x => x.Value)) Console.WriteLine($"{e.Value,6}  {e.Key}");
	foreach (string l in lines) Console.WriteLine("  " + l);
	return;
}

if (args[3] == "calls")
{
	foreach (TypeAnalysisContext type in app.AllTypes)
	{
		if (!type.Name.Contains(args[4])) continue;
		foreach (MethodAnalysisContext method in type.Methods)
		{
			if (args.Length > 5 && !method.Name.Contains(args[5])) continue;
			method.Analyze();
			if (method.ControlFlowGraph is null) continue;
			foreach (Instruction instruction in method.ControlFlowGraph.Instructions)
			{
				if (!instruction.IsCall || instruction.Operands.Count == 0) continue;
				if (instruction.Operands[0] is not MethodAnalysisContext callee) continue;
				string basis = callee is ConcreteGenericMethodAnalysisContext concrete
					? $" base={concrete.BaseMethodContext.GetType().Name} baseDeclaring={concrete.BaseMethodContext.DeclaringType?.GetType().Name} baseReturn={concrete.BaseMethodContext.ReturnType.FullName}"
					: "";
				string destination = instruction.Destination is LocalVariable result
					? $" dest={result.Name} destType={result.Type?.FullName ?? "<null>"}"
					: $" dest=<{instruction.Destination?.GetType().Name ?? "none"}>";
				Console.WriteLine($"{callee.FullName} :: {callee.GetType().Name} returns={callee.ReturnType.FullName}{destination}{basis}");
			}
			method.ReleaseAnalysisData();
		}
	}
	return;
}

if (args[3] == "roundtrip")
{
	// One line of JSON per method: what the ISIL - which comes straight from the binary - says the method
	// does. Nothing here needs the original source, so the same dump can be taken of any game.
	foreach (AssemblyAnalysisContext assembly in app.Assemblies)
	{
		string assemblyName = assembly.Definition.AssemblyName.Name ?? "";

		// The analysis skips the framework, so those assemblies have no ISIL to compare anything against.
		if (assemblyName is "mscorlib" || assemblyName.StartsWith("System") || assemblyName.StartsWith("Unity"))
			continue;

		foreach (TypeAnalysisContext type in assembly.Types)
		{
			foreach (MethodAnalysisContext method in type.Methods)
			{
				List<string> calls = [];
				List<string> fields = [];
				List<string> literals = [];

				try
				{
					method.Analyze();
				}
				catch
				{
					method.ReleaseAnalysisData();
					continue;
				}

				if (method.ControlFlowGraph is null)
				{
					method.ReleaseAnalysisData();
					continue;
				}

				foreach (Instruction instruction in method.ControlFlowGraph.Instructions)
				{
					if (instruction.IsCall && instruction.Operands.Count > 0 && instruction.Operands[0] is MethodAnalysisContext callee)
						calls.Add(callee.Name);

					// These two carry a message the lifter wrote about itself, not anything the program holds.
					bool diagnostic = instruction.OpCode is OpCode.NotImplemented or OpCode.Invalid or OpCode.Interrupt;

					for (int operandIndex = 0; operandIndex < instruction.Operands.Count; operandIndex++)
					{
						switch (instruction.Operands[operandIndex])
						{
							case FieldReference field:
								fields.Add(field.Field.Name);
								break;
							// A literal is loaded into something or handed to something, so it is never the
							// first operand - which is where a call that resolved only to a name keeps it.
							case string literal when !diagnostic && operandIndex > 0:
								literals.Add(literal);
								break;
						}
					}
				}

				method.ReleaseAnalysisData();

				if (calls.Count == 0 && fields.Count == 0 && literals.Count == 0)
					continue;

				Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
				{
					assembly = assemblyName,
					type = type.FullName,
					method = method.Name,
					calls,
					fields,
					literals,
				}));
			}
		}
	}

	return;
}

if (args[3] == "keyfuncs")
{
	var kfa = app.GetOrCreateKeyFunctionAddresses();
	foreach (var f in kfa.GetType().GetFields())
	{
		object? v = f.GetValue(kfa);
		if (v is ulong u && u != 0) Console.WriteLine($"{u} (0x{u:X})\t{f.Name}");
	}
	return;
}

if (args[3] == "dump")
{
	if (Environment.GetEnvironmentVariable("PROBE_ANALYZE_ALL") == "1")
	{
		int warmed = 0;
		foreach (TypeAnalysisContext warmType in app.AllTypes)
			foreach (MethodAnalysisContext warmMethod in warmType.Methods)
			{
				try { warmMethod.Analyze(); warmMethod.ReleaseAnalysisData(); warmed++; } catch { }
			}
		Console.Error.WriteLine($"warmed {warmed} methods");
	}

	foreach (TypeAnalysisContext type in app.AllTypes)
	{
		if (!type.Name.Contains(args[4])) continue;
		foreach (MethodAnalysisContext method in type.Methods)
		{
			if (args.Length > 5 && !method.Name.Contains(args[5])) continue;
			Console.WriteLine($"===== {type.FullName}::{method.Name}  {Signature(method)}{Registers(method)}");
			method.Analyze();
			if (method.ControlFlowGraph is null) { Console.WriteLine("  (no isil)"); continue; }
			foreach (Block b in method.ControlFlowGraph.Blocks)
			{
				Console.WriteLine($"  b{b.ID} [{b.BlockType}] preds={string.Join(",", b.Predecessors.Select(x => "b" + x.ID))} succs={string.Join(",", b.Successors.Select(x => "b" + x.ID))}");
				foreach (Instruction instruction in b.Instructions)
					Console.WriteLine("      " + instruction);
			}
			method.ReleaseAnalysisData();
		}
	}
	return;
}

string filter = args.Length > 4 ? args[4] : "";
int methods = 0, analysed = 0, isil = 0, notImplemented = 0, memory = 0, unresolvedCall = 0, failed = 0;
Dictionary<string, int> mnemonics = [];
Dictionary<ulong, int> callTargets = [];
Dictionary<string, int> memoryShapes = [];
Dictionary<string, string> shapeExample = [];
Dictionary<string, int> indirect = [];
Dictionary<string, string> indirectExample = [];
Dictionary<string, int> failures = [];
Dictionary<string, string> failureExample = [];
string current = "";

foreach (AssemblyAnalysisContext assembly in app.Assemblies)
{
	if (filter.Length > 0 && !assembly.CleanAssemblyName.Contains(filter)) continue;

	foreach (TypeAnalysisContext type in assembly.Types)
	{
		foreach (MethodAnalysisContext method in type.Methods)
		{
			methods++;
			current = type.FullName + "::" + method.Name;
			try
			{
				method.Analyze();
			}
			catch (Exception e)
			{
				failed++;
				string key = e.GetType().Name + ": " + (e.Message.Length > 90 ? e.Message[..90] : e.Message);
				failures[key] = failures.TryGetValue(key, out int f) ? f + 1 : 1;
				if (!failureExample.ContainsKey(key)) failureExample[key] = current;
				continue;
			}

			if (method.ControlFlowGraph is null) continue;
			analysed++;

			// What IlGenerator emits from is the control flow graph, not the flat list: a pass that
			// excises a block leaves its instructions in the flat list, where they are already dead.
			foreach (Instruction instruction in method.ControlFlowGraph.Blocks.SelectMany(b => b.Instructions))
			{
				isil++;

				if (instruction.OpCode == OpCode.NotImplemented)
				{
					notImplemented++;
					string text = instruction.Operands.Count > 0 ? instruction.Operands[0]?.ToString() ?? "" : "";
					string mnemonic = Mnemonic(text);
					mnemonics[mnemonic] = mnemonics.TryGetValue(mnemonic, out int m) ? m + 1 : 1;
				}

				if (instruction.OpCode is OpCode.Call or OpCode.CallVoid
					&& instruction.Operands.Count > 0 && instruction.Operands[0] is ulong target)
				{
					unresolvedCall++;
					callTargets[target] = callTargets.TryGetValue(target, out int c) ? c + 1 : 1;
				}

				foreach (object operand in instruction.Operands)
				{
					if (operand is not MemoryOperand mem) continue;
					memory++;
					string shape = Shape(mem);
					memoryShapes[shape] = memoryShapes.TryGetValue(shape, out int s) ? s + 1 : 1;
					if (!shapeExample.ContainsKey(shape)) shapeExample[shape] = current;
				}
			}

			method.ReleaseAnalysisData();
		}
	}

	Console.Error.WriteLine($"{assembly.CleanAssemblyName}: {methods} methods so far");
}

Console.WriteLine($"methods               {methods}");
Console.WriteLine($"  analysed            {analysed}");
Console.WriteLine($"  threw               {failed}");
Console.WriteLine($"isil instructions     {isil}");
Console.WriteLine($"  not implemented     {notImplemented}");
Console.WriteLine($"  memory operands     {memory}");
Console.WriteLine($"  unresolved calls    {unresolvedCall}");

Console.WriteLine("\n--- indirect calls left ---");
foreach ((string k, int v) in indirect.OrderByDescending(p => p.Value))
	Console.WriteLine($"  {v,6}  {k}   e.g. {indirectExample[k]}");

Console.WriteLine("\n--- analysis failures ---");
foreach ((string k, int v) in failures.OrderByDescending(p => p.Value).Take(15))
	Console.WriteLine($"  {v,6}  {k}   e.g. {failureExample[k]}");

Console.WriteLine("\n--- not implemented, by mnemonic ---");
foreach ((string mnemonic, int count) in mnemonics.OrderByDescending(p => p.Value).Take(40))
	Console.WriteLine($"  {mnemonic,-16} {count}");

Console.WriteLine("\n--- memory operands, by shape ---");
foreach ((string shape, int count) in memoryShapes.OrderByDescending(p => p.Value).Take(40))
	Console.WriteLine($"  {shape,-46} {count,6}   e.g. {shapeExample[shape]}");

Console.WriteLine("\n--- unresolved call targets ---");
foreach ((ulong target, int count) in callTargets.OrderByDescending(p => p.Value).Take(40))
	Console.WriteLine($"  {target:X}  {count}");

// The lifter words its refusal as "Instruction BLR not yet implemented."; group on the mnemonic.
static string Mnemonic(string text)
{
	string[] words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
	if (words.Length > 1 && words[0] == "Instruction")
		return words[1];
	return words.Length > 0 ? words[0] : "(empty)";
}

// The base's kind plus the offset, which is what decides whether a field can be recovered from it.
static string Shape(MemoryOperand memory)
{
	string @base = memory.Base switch
	{
		null => "const",
		LocalVariable { Type: RuntimeClassTypeAnalysisContext } => "Il2CppClass",
		LocalVariable { Type: StaticFieldStorageTypeAnalysisContext } => "static_fields",
		LocalVariable { Type: null } => "local(untyped)",
		LocalVariable local => "local(" + local.Type!.Name + ")",
		_ => memory.Base.GetType().Name,
	};

	string index = memory.Index == null ? "" : "+index";
	return $"[{@base}+{memory.Addend:X}{index}]";
}

// The signature, and which register the analysis decided each parameter arrives in. Two overloads of one
// name are indistinguishable without this, and a whole line of enquiry into the interface-dispatch walk
// stalled because the register a method reads its generic context from could not be matched to the method
// that reads it.
static string Signature(MethodAnalysisContext m)
{
	string parameters = string.Join(", ", m.Parameters.Select(p => $"{p.ParameterType.Name} {p.ParameterName}"));
	string modifiers = (m.IsStatic ? "static " : "") + (m.Definition?.GenericContainer != null ? "generic " : "");
	return $"{modifiers}{m.ReturnType?.Name ?? "?"} ({parameters})";
}

static string Registers(MethodAnalysisContext m)
{
	try
	{
		List<object> operands = m.ParameterOperands;
		if (operands == null || operands.Count == 0) return "";
		return "  regs=[" + string.Join(",", operands.Select(o => o?.ToString() ?? "?")) + "]";
	}
	catch { return ""; }
}
