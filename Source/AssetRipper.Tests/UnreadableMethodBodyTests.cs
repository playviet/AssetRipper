using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using AsmResolver.PE.DotNet.Metadata.Tables;
using AssetRipper.Processing.Assemblies;

namespace AssetRipper.Tests;

internal class UnreadableMethodBodyTests
{
	private static ModuleDefinition CreateModule(out TypeDefinition type)
	{
		ModuleDefinition module = new("Assembly-CSharp.dll", KnownCorLibs.SystemRuntime_v9_0_0_0);
		type = new TypeDefinition("Game", "Recovered", TypeAttributes.Public);
		module.TopLevelTypes.Add(type);
		return module;
	}

	private static MethodDefinition CreateMethod(ModuleDefinition module, string name)
	{
		MethodDefinition method = new(name, MethodAttributes.Public, MethodSignature.CreateInstance(module.CorLibTypeFactory.Void));
		method.CilMethodBody = new CilMethodBody();
		return method;
	}

	[Test]
	public void ABranchThatGoesNowhereIsDiscarded()
	{
		ModuleDefinition module = CreateModule(out TypeDefinition type);
		MethodDefinition method = CreateMethod(module, "Jump");

		// A label on an instruction that was never added to the body.
		CilInstruction elsewhere = new(CilOpCodes.Nop);
		method.CilMethodBody!.Instructions.Add(CilOpCodes.Br, new CilInstructionLabel(elsewhere));
		method.CilMethodBody.Instructions.Add(CilOpCodes.Ret);
		type.Methods.Add(method);

		int replaced = UnreadableMethodBodyProcessor.Process(module);

		using (Assert.EnterMultipleScope())
		{
			Assert.That(replaced, Is.EqualTo(1));
			Assert.That(method.CilMethodBody!.Instructions.Select(i => i.OpCode), Is.EqualTo(new[] { CilOpCodes.Ret }));
		}
	}

	[Test]
	public void AnImpossibleStackIsDiscarded()
	{
		ModuleDefinition module = CreateModule(out TypeDefinition type);
		MethodDefinition method = CreateMethod(module, "Underflow");

		// Popping from an empty stack.
		method.CilMethodBody!.Instructions.Add(CilOpCodes.Pop);
		method.CilMethodBody.Instructions.Add(CilOpCodes.Ret);
		type.Methods.Add(method);

		int replaced = UnreadableMethodBodyProcessor.Process(module);

		using (Assert.EnterMultipleScope())
		{
			Assert.That(replaced, Is.EqualTo(1));
			Assert.That(method.CilMethodBody!.Instructions.Select(i => i.OpCode), Is.EqualTo(new[] { CilOpCodes.Ret }));
		}
	}

	[Test]
	public void AReadableBodyIsKept()
	{
		ModuleDefinition module = CreateModule(out TypeDefinition type);
		MethodDefinition method = CreateMethod(module, "Loop");

		CilInstruction target = new(CilOpCodes.Nop);
		method.CilMethodBody!.Instructions.Add(target);
		method.CilMethodBody.Instructions.Add(CilOpCodes.Br, new CilInstructionLabel(target));
		type.Methods.Add(method);

		int replaced = UnreadableMethodBodyProcessor.Process(module);

		using (Assert.EnterMultipleScope())
		{
			Assert.That(replaced, Is.Zero);
			Assert.That(method.CilMethodBody!.Instructions.Select(i => i.OpCode), Is.EqualTo(new[] { CilOpCodes.Nop, CilOpCodes.Br }));
		}
	}
}
