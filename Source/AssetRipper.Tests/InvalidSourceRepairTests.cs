using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using AsmResolver.PE.DotNet.Metadata.Tables;
using AssetRipper.Export.UnityProjects.Scripts;
using AssetRipper.IO.Files;
using Microsoft.CodeAnalysis;

namespace AssetRipper.Tests;

internal class InvalidSourceRepairTests
{
	private const string OutputFolder = "/Scripts";

	/// <summary>
	/// An assembly with one type whose two methods both have a body, standing in for a decompiled assembly.
	/// </summary>
	private static AssemblyDefinition CreateAssembly(out MethodDefinition kept, out MethodDefinition broken, string brokenName = "Broken")
	{
		AssemblyDefinition assembly = new("Assembly-CSharp", new Version(1, 0, 0, 0));
		ModuleDefinition module = new("Assembly-CSharp.dll", KnownCorLibs.SystemRuntime_v9_0_0_0);
		assembly.Modules.Add(module);

		TypeDefinition type = new("Game", "Widget", TypeAttributes.Public);
		module.TopLevelTypes.Add(type);

		kept = CreateMethod(module, "Kept");
		broken = CreateMethod(module, brokenName);
		type.Methods.Add(kept);
		type.Methods.Add(broken);

		return assembly;
	}

	private static MethodDefinition CreateMethod(ModuleDefinition module, string name)
	{
		MethodDefinition method = new(name, MethodAttributes.Public, MethodSignature.CreateInstance(module.CorLibTypeFactory.Void));
		method.CilMethodBody = new CilMethodBody();
		method.CilMethodBody.Instructions.Add(CilOpCodes.Nop);
		method.CilMethodBody.Instructions.Add(CilOpCodes.Ret);
		return method;
	}

	private static VirtualFileSystem CreateFileSystem(string source)
	{
		VirtualFileSystem fileSystem = new();
		fileSystem.Directory.Create(OutputFolder);
		fileSystem.File.WriteAllText(fileSystem.Path.Join(OutputFolder, "Widget.cs"), source);
		return fileSystem;
	}

	private static bool IsStubbed(MethodDefinition method)
	{
		return method.CilMethodBody!.Instructions.Select(i => i.OpCode).SequenceEqual([CilOpCodes.Ret]);
	}

	[Test]
	public void SourceThatDoesNotParseCostsItsMethodItsBody()
	{
		AssemblyDefinition assembly = CreateAssembly(out MethodDefinition kept, out MethodDefinition broken);
		// A by-ref argument used as a value, which the decompiler writes as a complement of a ref expression.
		VirtualFileSystem fileSystem = CreateFileSystem("""
			namespace Game
			{
				public class Widget
				{
					public void Kept()
					{
					}

					public void Broken(ref int value)
					{
						int result = ~(ref value);
					}
				}
			}
			""");

		bool repaired = InvalidSourceRepair.Apply(assembly, [], OutputFolder, fileSystem);

		using (Assert.EnterMultipleScope())
		{
			Assert.That(repaired, Is.True);
			Assert.That(IsStubbed(broken), Is.True);
			Assert.That(IsStubbed(kept), Is.False);
		}
	}

	[Test]
	public void AnUnboundGenericNameCostsItsMethodItsBody()
	{
		AssemblyDefinition assembly = CreateAssembly(out MethodDefinition kept, out MethodDefinition broken);
		// This parses, but a generic name without its arguments is only valid inside a typeof.
		VirtualFileSystem fileSystem = CreateFileSystem("""
			namespace Game
			{
				public class Widget
				{
					public void Kept()
					{
						System.Type type = typeof(System.Collections.Generic.List<>);
					}

					public void Broken()
					{
						object value = (System.Collections.Generic.List<>)(object)this;
					}
				}
			}
			""");

		bool repaired = InvalidSourceRepair.Apply(assembly, [], OutputFolder, fileSystem);

		using (Assert.EnterMultipleScope())
		{
			Assert.That(repaired, Is.True);
			Assert.That(IsStubbed(broken), Is.True);
			Assert.That(IsStubbed(kept), Is.False);
		}
	}

	[Test]
	public void AGeneratedMethodIsMatchedThroughTheNameItWasWrittenUnder()
	{
		// The compiler names the members it generates with characters a C# identifier cannot contain, and the
		// decompiler escapes each one as an underscore and its code point.
		AssemblyDefinition assembly = CreateAssembly(out MethodDefinition kept, out MethodDefinition broken, "<>m__Finally1");
		VirtualFileSystem fileSystem = CreateFileSystem("""
			namespace Game
			{
				public class Widget
				{
					public void Kept()
					{
					}

					private void _003C_003Em__Finally1(ref int value)
					{
						int result = ~(ref value);
					}
				}
			}
			""");

		bool repaired = InvalidSourceRepair.Apply(assembly, [], OutputFolder, fileSystem);

		using (Assert.EnterMultipleScope())
		{
			Assert.That(repaired, Is.True);
			Assert.That(IsStubbed(broken), Is.True);
			Assert.That(IsStubbed(kept), Is.False);
		}
	}

	[Test]
	public void ValidSourceIsLeftAlone()
	{
		AssemblyDefinition assembly = CreateAssembly(out MethodDefinition kept, out MethodDefinition broken);
		VirtualFileSystem fileSystem = CreateFileSystem("""
			namespace Game
			{
				public class Widget
				{
					public void Kept()
					{
					}

					public void Broken()
					{
						System.Type type = typeof(System.Collections.Generic.List<>);
					}
				}
			}
			""");

		bool repaired = InvalidSourceRepair.Apply(assembly, [], OutputFolder, fileSystem);

		using (Assert.EnterMultipleScope())
		{
			Assert.That(repaired, Is.False);
			Assert.That(IsStubbed(broken), Is.False);
			Assert.That(IsStubbed(kept), Is.False);
		}
	}
}
