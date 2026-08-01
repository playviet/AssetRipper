using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Metadata.Tables;
using AssetRipper.Export.UnityProjects.Scripts;
using AssetRipper.IO.Files;
using AssetRipper.Primitives;

namespace AssetRipper.Tests;

internal class OriginalSourceSubstitutionTests
{
	private const string SourceFolder = "/source";
	private const string OutputFolder = "/out";
	private const string Decompiled = "//The decompiled script.";

	private static readonly UnityVersion Version = new(2019, 4, 0);

	private static AssemblyDefinition CreateAssembly(out ModuleDefinition module)
	{
		module = new ModuleDefinition("Assembly-CSharp.dll", KnownCorLibs.SystemRuntime_v9_0_0_0);
		AssemblyDefinition assembly = new("Assembly-CSharp", new Version(1, 0, 0, 0));
		assembly.Modules.Add(module);
		return assembly;
	}

	private static TypeDefinition AddType(ModuleDefinition module, string name)
	{
		TypeDefinition type = new("Lib", name, TypeAttributes.Public, module.CorLibTypeFactory.Object.ToTypeDefOrRef());
		module.TopLevelTypes.Add(type);
		return type;
	}

	private static void AddMethod(ModuleDefinition module, TypeDefinition type, string name)
	{
		type.Methods.Add(new MethodDefinition(name, MethodAttributes.Public, MethodSignature.CreateInstance(module.CorLibTypeFactory.Int32)));
	}

	private static void AddField(ModuleDefinition module, TypeDefinition type, string name)
	{
		type.Fields.Add(new FieldDefinition(name, FieldAttributes.Public, module.CorLibTypeFactory.Int32));
	}

	private static VirtualFileSystem CreateFileSystem(params string[] decompiledFileNames)
	{
		VirtualFileSystem fileSystem = new();
		fileSystem.Directory.Create(SourceFolder);
		fileSystem.Directory.Create($"{OutputFolder}/Lib");
		foreach (string name in decompiledFileNames)
		{
			fileSystem.File.WriteAllText($"{OutputFolder}/Lib/{name}", Decompiled);
		}
		return fileSystem;
	}

	private static OriginalSourceSubstitution.Report Apply(AssemblyDefinition assembly, VirtualFileSystem fileSystem)
	{
		return OriginalSourceSubstitution.Apply(assembly, [SourceFolder], Version, true, OutputFolder, fileSystem);
	}

	[Test]
	public void ATypeTheSourceAccountsForIsSubstituted()
	{
		AssemblyDefinition assembly = CreateAssembly(out ModuleDefinition module);
		TypeDefinition widget = AddType(module, "Widget");
		AddMethod(module, widget, "GetValue");
		AddField(module, widget, "count");

		VirtualFileSystem fileSystem = CreateFileSystem("Widget.cs");
		fileSystem.File.WriteAllText($"{SourceFolder}/Widget.cs", """
			namespace Lib
			{
				public class Widget
				{
					public int count;
					public int GetValue() => count;
				}
			}
			""");

		OriginalSourceSubstitution.Report report = Apply(assembly, fileSystem);

		using (Assert.EnterMultipleScope())
		{
			Assert.That(report.Substituted, Is.EqualTo(1));
			Assert.That(report.Rejected, Is.EqualTo(0));
			Assert.That(fileSystem.File.ReadAllText($"{OutputFolder}/Lib/Widget.cs"), Does.Contain("public int GetValue() => count;"));
		}
	}

	/// <summary>
	/// The whole point of the check: a library on disk of a different version than the one that was built.
	/// </summary>
	[Test]
	public void ATypeMissingAMethodTheAssemblyDeclaresIsNotSubstituted()
	{
		AssemblyDefinition assembly = CreateAssembly(out ModuleDefinition module);
		TypeDefinition widget = AddType(module, "Widget");
		AddMethod(module, widget, "GetValue");

		VirtualFileSystem fileSystem = CreateFileSystem("Widget.cs");
		fileSystem.File.WriteAllText($"{SourceFolder}/Widget.cs", """
			namespace Lib
			{
				public class Widget
				{
					public int GetSomethingElse() => 0;
				}
			}
			""");

		OriginalSourceSubstitution.Report report = Apply(assembly, fileSystem);

		using (Assert.EnterMultipleScope())
		{
			Assert.That(report.Substituted, Is.EqualTo(0));
			Assert.That(report.Rejected, Is.EqualTo(1));
			Assert.That(report.Rejections[0].Value, Does.Contain("GetValue"));
			Assert.That(fileSystem.File.ReadAllText($"{OutputFolder}/Lib/Widget.cs"), Is.EqualTo(Decompiled));
		}
	}

	/// <summary>
	/// A field is checked too, because a MonoBehaviour whose fields have moved reads its scenes back wrongly.
	/// </summary>
	[Test]
	public void ATypeMissingAFieldTheAssemblyDeclaresIsNotSubstituted()
	{
		AssemblyDefinition assembly = CreateAssembly(out ModuleDefinition module);
		TypeDefinition widget = AddType(module, "Widget");
		AddField(module, widget, "count");

		VirtualFileSystem fileSystem = CreateFileSystem("Widget.cs");
		fileSystem.File.WriteAllText($"{SourceFolder}/Widget.cs", """
			namespace Lib
			{
				public class Widget
				{
				}
			}
			""");

		OriginalSourceSubstitution.Report report = Apply(assembly, fileSystem);

		using (Assert.EnterMultipleScope())
		{
			Assert.That(report.Substituted, Is.EqualTo(0));
			Assert.That(report.Rejections[0].Value, Does.Contain("count"));
		}
	}

	/// <summary>
	/// One file is one decision, so the type that matches goes down with the one that does not.
	/// </summary>
	[Test]
	public void AFileWhereOnlyOneOfTwoTypesMatchesIsNotSubstituted()
	{
		AssemblyDefinition assembly = CreateAssembly(out ModuleDefinition module);
		AddMethod(module, AddType(module, "Widget"), "GetValue");
		AddMethod(module, AddType(module, "Gadget"), "GetOther");

		VirtualFileSystem fileSystem = CreateFileSystem("Widget.cs", "Gadget.cs");
		fileSystem.File.WriteAllText($"{SourceFolder}/Widget.cs", """
			namespace Lib
			{
				public class Widget
				{
					public int GetValue() => 0;
				}

				public class Gadget
				{
				}
			}
			""");

		OriginalSourceSubstitution.Report report = Apply(assembly, fileSystem);

		using (Assert.EnterMultipleScope())
		{
			Assert.That(report.Substituted, Is.EqualTo(0));
			Assert.That(report.Rejected, Is.EqualTo(2));
			Assert.That(fileSystem.File.ReadAllText($"{OutputFolder}/Lib/Widget.cs"), Is.EqualTo(Decompiled));
			Assert.That(fileSystem.File.ReadAllText($"{OutputFolder}/Lib/Gadget.cs"), Is.EqualTo(Decompiled));
		}
	}

	/// <summary>
	/// The exporter gives every type its own file and its own script GUID, so a file holding two of them is split.
	/// </summary>
	[Test]
	public void AFileDeclaringTwoTypesIsWrittenAsOneFilePerType()
	{
		AssemblyDefinition assembly = CreateAssembly(out ModuleDefinition module);
		AddMethod(module, AddType(module, "Widget"), "GetValue");
		AddMethod(module, AddType(module, "Gadget"), "GetOther");

		VirtualFileSystem fileSystem = CreateFileSystem("Widget.cs", "Gadget.cs");
		fileSystem.File.WriteAllText($"{SourceFolder}/Widget.cs", """
			using System;

			namespace Lib
			{
				public class Widget
				{
					public int GetValue() => 0;
				}

				public class Gadget
				{
					public int GetOther() => 0;
				}
			}
			""");

		OriginalSourceSubstitution.Report report = Apply(assembly, fileSystem);

		string widget = fileSystem.File.ReadAllText($"{OutputFolder}/Lib/Widget.cs");
		string gadget = fileSystem.File.ReadAllText($"{OutputFolder}/Lib/Gadget.cs");

		using (Assert.EnterMultipleScope())
		{
			Assert.That(report.Substituted, Is.EqualTo(2));
			Assert.That(report.SubstitutedFrom, Is.EqualTo(1));
			Assert.That(widget, Does.Contain("using System;").And.Contain("namespace Lib").And.Contain("GetValue"));
			Assert.That(widget, Does.Not.Contain("class Gadget"));
			Assert.That(gadget, Does.Contain("GetOther"));
			Assert.That(gadget, Does.Not.Contain("class Widget"));
		}
	}

	/// <summary>
	/// A nested type is part of the file, and the assembly is the authority on which ones there are.
	/// </summary>
	[Test]
	public void ASourceNestedTypeTheAssemblyDoesNotHaveRejectsTheFile()
	{
		AssemblyDefinition assembly = CreateAssembly(out ModuleDefinition module);
		AddType(module, "Widget");

		VirtualFileSystem fileSystem = CreateFileSystem("Widget.cs");
		fileSystem.File.WriteAllText($"{SourceFolder}/Widget.cs", """
			namespace Lib
			{
				public class Widget
				{
					public class Extra
					{
					}
				}
			}
			""");

		OriginalSourceSubstitution.Report report = Apply(assembly, fileSystem);

		using (Assert.EnterMultipleScope())
		{
			Assert.That(report.Substituted, Is.EqualTo(0));
			Assert.That(report.Rejections[0].Value, Does.Contain("Extra"));
		}
	}

	/// <summary>
	/// Two versions of a library in the declared directories cannot be told apart, so neither is used.
	/// </summary>
	[Test]
	public void ATypeDeclaredByTwoOriginalFilesIsNotSubstituted()
	{
		AssemblyDefinition assembly = CreateAssembly(out ModuleDefinition module);
		AddType(module, "Widget");

		VirtualFileSystem fileSystem = CreateFileSystem("Widget.cs");
		const string Content = """
			namespace Lib
			{
				public class Widget
				{
				}
			}
			""";
		fileSystem.Directory.Create($"{SourceFolder}/v2");
		fileSystem.File.WriteAllText($"{SourceFolder}/Widget.cs", Content);
		fileSystem.File.WriteAllText($"{SourceFolder}/v2/Widget.cs", Content);

		OriginalSourceSubstitution.Report report = Apply(assembly, fileSystem);

		using (Assert.EnterMultipleScope())
		{
			Assert.That(report.Substituted, Is.EqualTo(0));
			Assert.That(report.Rejections[0].Value, Does.Contain("2 original files"));
		}
	}

	/// <summary>
	/// A member behind a version gate is part of the type, because it was when the game was compiled.
	/// </summary>
	[Test]
	public void AMemberBehindAVersionGateTheBuildOpensCounts()
	{
		AssemblyDefinition assembly = CreateAssembly(out ModuleDefinition module);
		TypeDefinition widget = AddType(module, "Widget");
		AddMethod(module, widget, "GetValue");

		VirtualFileSystem fileSystem = CreateFileSystem("Widget.cs");
		fileSystem.File.WriteAllText($"{SourceFolder}/Widget.cs", """
			namespace Lib
			{
				public class Widget
				{
			#if UNITY_5_4_OR_NEWER
					public int GetValue() => 0;
			#endif
				}
			}
			""");

		OriginalSourceSubstitution.Report report = Apply(assembly, fileSystem);

		Assert.That(report.Substituted, Is.EqualTo(1));
	}

	[Test]
	public void GarbageInTheSourceDirectoryDoesNotCostTheSubstitutionBesideIt()
	{
		AssemblyDefinition assembly = CreateAssembly(out ModuleDefinition module);
		AddType(module, "Widget");

		VirtualFileSystem fileSystem = CreateFileSystem("Widget.cs");
		fileSystem.File.WriteAllText($"{SourceFolder}/Broken.cs", "namespace { class ??? public ]]] ");
		fileSystem.File.WriteAllText($"{SourceFolder}/Widget.cs", """
			namespace Lib
			{
				public class Widget
				{
				}
			}
			""");

		OriginalSourceSubstitution.Report report = Apply(assembly, fileSystem);

		Assert.That(report.Substituted, Is.EqualTo(1));
	}

	/// <summary>
	/// With nothing declared, the export has to be what it was.
	/// </summary>
	[Test]
	public void NoDeclaredDirectoriesChangesNothing()
	{
		AssemblyDefinition assembly = CreateAssembly(out ModuleDefinition module);
		AddType(module, "Widget");

		VirtualFileSystem fileSystem = CreateFileSystem("Widget.cs");
		fileSystem.File.WriteAllText($"{SourceFolder}/Widget.cs", """
			namespace Lib
			{
				public class Widget
				{
				}
			}
			""");

		OriginalSourceSubstitution.Report report = OriginalSourceSubstitution.Apply(assembly, [], Version, true, OutputFolder, fileSystem);

		using (Assert.EnterMultipleScope())
		{
			Assert.That(report.Substituted, Is.EqualTo(0));
			Assert.That(report.IndexedFiles, Is.EqualTo(0));
			Assert.That(fileSystem.File.ReadAllText($"{OutputFolder}/Lib/Widget.cs"), Is.EqualTo(Decompiled));
		}
	}

	/// <summary>
	/// A decompiled script is what a substitution replaces, so where there is none there is nothing to replace.
	/// </summary>
	[Test]
	public void ATypeTheDecompilerWroteNoFileForIsNotSubstituted()
	{
		AssemblyDefinition assembly = CreateAssembly(out ModuleDefinition module);
		AddType(module, "Widget");

		VirtualFileSystem fileSystem = CreateFileSystem();
		fileSystem.File.WriteAllText($"{SourceFolder}/Widget.cs", """
			namespace Lib
			{
				public class Widget
				{
				}
			}
			""");

		OriginalSourceSubstitution.Report report = Apply(assembly, fileSystem);

		using (Assert.EnterMultipleScope())
		{
			Assert.That(report.Substituted, Is.EqualTo(0));
			Assert.That(fileSystem.File.Exists($"{OutputFolder}/Lib/Widget.cs"), Is.False);
		}
	}

	[Test]
	public void ARegionAroundATypeDoesNotLeaveTheSplitFileUnparseable()
	{
		//A region opens before the type and closes after it, so a type taken out on its own takes the opening
		//without the closing. That cost the whole assembly its scripts, from one cosmetic directive.
		AssemblyDefinition assembly = CreateAssembly(out ModuleDefinition module);
		TypeDefinition alpha = AddType(module, "Alpha");
		AddMethod(module, alpha, "Run");
		TypeDefinition beta = AddType(module, "Beta");
		AddMethod(module, beta, "Run");

		VirtualFileSystem fileSystem = CreateFileSystem("Alpha.cs", "Beta.cs");
		fileSystem.File.WriteAllText($"{SourceFolder}/Widgets.cs", """
			namespace Lib
			{
				#region Widgets
				public class Alpha
				{
					public void Run() { }
				}

				public class Beta
				{
					public void Run() { }
				}
				#endregion
			}
			""");

		OriginalSourceSubstitution.Report report = Apply(assembly, fileSystem);
		string written = fileSystem.File.ReadAllText($"{OutputFolder}/Lib/Alpha.cs");

		using (Assert.EnterMultipleScope())
		{
			Assert.That(report.Substituted, Is.EqualTo(2));
			Assert.That(written, Does.Contain("class Alpha"));
			Assert.That(written, Does.Not.Contain("#region"));
			Assert.That(written, Does.Not.Contain("#endregion"));
			Assert.That(OriginalSourceSubstitution.Parses(written, Microsoft.CodeAnalysis.CSharp.CSharpParseOptions.Default), Is.True);
		}
	}

}
