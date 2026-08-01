using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using AsmResolver.PE.DotNet.Metadata.Tables;
using AssetRipper.Export.UnityProjects.Scripts;
using AssetRipper.IO.Files;
using AssetRipper.Processing.Assemblies;

namespace AssetRipper.Tests;

internal class ScriptIlCompanionTests
{
	private static TypeDefinition CreateType(out ModuleDefinition module, string? @namespace = null)
	{
		module = new ModuleDefinition("Assembly-CSharp.dll", KnownCorLibs.SystemRuntime_v9_0_0_0);
		TypeDefinition type = new(@namespace, "Widget", TypeAttributes.Public, module.CorLibTypeFactory.Object.ToTypeDefOrRef());
		module.TopLevelTypes.Add(type);
		return type;
	}

	private static MethodDefinition AddMethod(ModuleDefinition module, TypeDefinition type, string name, MethodAttributes attributes = MethodAttributes.Public)
	{
		MethodDefinition method = new(name, attributes, MethodSignature.CreateInstance(module.CorLibTypeFactory.Int32));
		type.Methods.Add(method);
		return method;
	}

	[Test]
	public void AMethodWithABodyGetsItsInstructions()
	{
		TypeDefinition type = CreateType(out ModuleDefinition module);
		MethodDefinition method = AddMethod(module, type, "GetValue");
		method.CilMethodBody = new CilMethodBody();
		method.CilMethodBody.Instructions.Add(CilOpCodes.Ldc_I4_5);
		method.CilMethodBody.Instructions.Add(CilOpCodes.Ret);

		string content = ScriptIlCompanionExporter.CreateContent("Widget.cs", [type]);

		using (Assert.EnterMultipleScope())
		{
			Assert.That(content, Does.Contain("## Widget"));
			Assert.That(content, Does.Contain("### public System.Int32 GetValue()"));
			Assert.That(content, Does.Contain("IL_0000: ldc.i4.5"));
			Assert.That(content, Does.Contain("IL_0001: ret"));
			Assert.That(content, Does.Not.Contain("Body:"));
		}
	}

	[Test]
	public void AMethodWithNoBodyIsMarkedRatherThanThrowing()
	{
		TypeDefinition type = CreateType(out ModuleDefinition module);
		AddMethod(module, type, "GetValue", MethodAttributes.Public | MethodAttributes.Abstract | MethodAttributes.Virtual);

		string content = ScriptIlCompanionExporter.CreateContent("Widget.cs", [type]);

		using (Assert.EnterMultipleScope())
		{
			Assert.That(content, Does.Contain("### public abstract System.Int32 GetValue()"));
			Assert.That(content, Does.Contain("Body: none"));
			Assert.That(content, Does.Not.Contain("```il"));
		}
	}

	[Test]
	public void ALocalAndAHandlerAreListed()
	{
		TypeDefinition type = CreateType(out ModuleDefinition module);
		MethodDefinition method = AddMethod(module, type, "Guarded");
		method.CilMethodBody = new CilMethodBody();
		CilInstructionCollection instructions = method.CilMethodBody.Instructions;
		CilLocalVariable local = new(module.CorLibTypeFactory.String);
		method.CilMethodBody.LocalVariables.Add(local);

		CilInstruction tryStart = instructions.Add(CilOpCodes.Nop);
		CilInstruction handlerStart = instructions.Add(CilOpCodes.Pop);
		CilInstruction handlerEnd = instructions.Add(CilOpCodes.Ldc_I4_0);
		instructions.Add(CilOpCodes.Ret);
		method.CilMethodBody.ExceptionHandlers.Add(new CilExceptionHandler
		{
			HandlerType = CilExceptionHandlerType.Exception,
			ExceptionType = module.CorLibTypeFactory.Object.ToTypeDefOrRef(),
			TryStart = new CilInstructionLabel(tryStart),
			TryEnd = new CilInstructionLabel(handlerStart),
			HandlerStart = new CilInstructionLabel(handlerStart),
			HandlerEnd = new CilInstructionLabel(handlerEnd),
		});

		string content = ScriptIlCompanionExporter.CreateContent("Widget.cs", [type]);

		using (Assert.EnterMultipleScope())
		{
			Assert.That(content, Does.Contain("[0] System.String"));
			Assert.That(content, Does.Contain(".try IL_0000 to IL_0001 catch System.Object handler IL_0001 to IL_0002"));
		}
	}

	/// <summary>
	/// The marker has to say the same thing the processor did, so the body it discarded is the one being described.
	/// </summary>
	[Test]
	public void ABodyTheProcessorDiscardedIsMarkedAsAStub()
	{
		TypeDefinition type = CreateType(out ModuleDefinition module);
		MethodDefinition method = AddMethod(module, type, "GetValue");
		method.CilMethodBody = new CilMethodBody();
		// Popping from an empty stack, which no reader accepts.
		method.CilMethodBody.Instructions.Add(CilOpCodes.Pop);
		method.CilMethodBody.Instructions.Add(CilOpCodes.Ret);

		int replaced = UnreadableMethodBodyProcessor.Process(module);
		string content = ScriptIlCompanionExporter.CreateContent("Widget.cs", [type]);

		using (Assert.EnterMultipleScope())
		{
			Assert.That(replaced, Is.EqualTo(1));
			Assert.That(content, Does.Contain("Body: stub"));
			// The comparison is not allowed to leave the generated body behind.
			Assert.That(method.CilMethodBody!.Instructions, Has.Count.EqualTo(2));
		}
	}

	/// <summary>
	/// Cpp2IL writes the reason analysis failed into the body it could not recover.
	/// </summary>
	[Test]
	public void AFailedAnalysisIsMarkedWithItsReason()
	{
		TypeDefinition type = CreateType(out ModuleDefinition module);
		MethodDefinition method = AddMethod(module, type, "GetValue");
		method.CilMethodBody = new CilMethodBody();
		IMethodDescriptor constructor = module.CorLibTypeFactory.CorLibScope
			.CreateTypeReference("System", "Exception")
			.CreateMemberReference(".ctor", MethodSignature.CreateInstance(module.CorLibTypeFactory.Void, [module.CorLibTypeFactory.String]));
		method.CilMethodBody.Instructions.Add(CilOpCodes.Ldstr, "Unsupported instruction\nat 0x1234");
		method.CilMethodBody.Instructions.Add(CilOpCodes.Newobj, constructor);
		method.CilMethodBody.Instructions.Add(CilOpCodes.Throw);

		string content = ScriptIlCompanionExporter.CreateContent("Widget.cs", [type]);

		using (Assert.EnterMultipleScope())
		{
			Assert.That(content, Does.Contain("Body: analysis failed - Unsupported instruction at 0x1234"));
			Assert.That(content, Does.Contain("IL_0000: ldstr"));
		}
	}

	[Test]
	public void AMissingAddressIsSaidToBeUnknown()
	{
		TypeDefinition type = CreateType(out ModuleDefinition module);
		MethodDefinition method = AddMethod(module, type, "GetValue");
		method.CilMethodBody = new CilMethodBody();
		method.CilMethodBody.Instructions.Add(CilOpCodes.Ldc_I4_0);
		method.CilMethodBody.Instructions.Add(CilOpCodes.Ret);

		string content = ScriptIlCompanionExporter.CreateContent("Widget.cs", [type]);

		Assert.That(content, Does.Contain("Address: unknown"));
	}

	[Test]
	public void TheCompanionLandsBesideTheScript()
	{
		TypeDefinition type = CreateType(out ModuleDefinition module);
		MethodDefinition method = AddMethod(module, type, "GetValue");
		method.CilMethodBody = new CilMethodBody();
		method.CilMethodBody.Instructions.Add(CilOpCodes.Ldc_I4_7);
		method.CilMethodBody.Instructions.Add(CilOpCodes.Ret);

		AssemblyDefinition assembly = new("Assembly-CSharp", new Version(1, 0, 0, 0));
		assembly.Modules.Add(module);

		VirtualFileSystem fileSystem = new();
		fileSystem.Directory.Create("/Scripts");
		fileSystem.File.WriteAllText("/Scripts/Widget.cs", "public class Widget { }");
		// A type the decompiler never wrote a file for.
		TypeDefinition orphan = new(null, "Gadget", TypeAttributes.Public, module.CorLibTypeFactory.Object.ToTypeDefOrRef());
		module.TopLevelTypes.Add(orphan);

		ScriptIlCompanionExporter.Export(assembly, true, "/Scripts", fileSystem);

		using (Assert.EnterMultipleScope())
		{
			Assert.That(fileSystem.File.Exists("/Scripts/Widget.il.md"));
			Assert.That(fileSystem.File.Exists("/Scripts/Gadget.il.md"), Is.False);
			Assert.That(fileSystem.File.ReadAllText("/Scripts/Widget.il.md"), Does.Contain("IL_0000: ldc.i4.7"));
		}
	}
}
