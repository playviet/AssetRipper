using AssetRipper.Export.UnityProjects.Scripts;
using AssetRipper.IO.Files;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace AssetRipper.Tests;

internal class InvalidSourceRepairTests
{
	private const string OutputFolder = "/Scripts";
	private const string FileName = "Widget.cs";

	/// <summary>
	/// The running runtime, which is enough to compile the snippets here.
	/// </summary>
	private static List<MetadataReference> CreateReferences()
	{
		string directory = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
		List<MetadataReference> references = [];
		foreach (string path in Directory.EnumerateFiles(directory, "*.dll"))
		{
			try
			{
				references.Add(MetadataReference.CreateFromFile(path));
			}
			catch (Exception)
			{
				//Not every file next to the runtime is a managed assembly.
			}
		}
		return references;
	}

	private static string Repair(string source, bool compile = false)
	{
		VirtualFileSystem fileSystem = new();
		fileSystem.Directory.Create(OutputFolder);
		string path = fileSystem.Path.Join(OutputFolder, FileName);
		fileSystem.File.WriteAllText(path, source);

		InvalidSourceRepair.Apply(compile ? CreateReferences() : [], LanguageVersion.CSharp9, OutputFolder, fileSystem);

		return fileSystem.File.ReadAllText(path);
	}

	[Test]
	public void AStatementThatDoesNotParseIsCommentedOut()
	{
		//A by-ref argument used as a value, which the decompiler writes as a complement of a ref expression.
		string result = Repair("""
			namespace Game
			{
				public class Widget
				{
					public void Broken(ref int value)
					{
						int kept = 1;
						int result = ~(ref value);
						int alsoKept = 2;
					}
				}
			}
			""");

		using (Assert.EnterMultipleScope())
		{
			Assert.That(result, Does.Contain("\t\t\t//int result = ~(ref value);"));
			Assert.That(result, Does.Contain("\n\t\t\tint kept = 1;"));
			Assert.That(result, Does.Contain("\n\t\t\tint alsoKept = 2;"));
		}
	}

	[Test]
	public void AnUnboundGenericNameIsCommentedOut()
	{
		//This parses, but a generic name without its arguments is only valid inside a typeof.
		string result = Repair("""
			namespace Game
			{
				public class Widget
				{
					public void Broken()
					{
						System.Type kept = typeof(System.Collections.Generic.List<>);
						object value = (System.Collections.Generic.List<>)(object)this;
					}
				}
			}
			""");

		using (Assert.EnterMultipleScope())
		{
			Assert.That(result, Does.Contain("\t\t\t//object value = (System.Collections.Generic.List<>)(object)this;"));
			Assert.That(result, Does.Contain("\n\t\t\tSystem.Type kept = typeof(System.Collections.Generic.List<>);"));
		}
	}

	[Test]
	public void AMethodThatNoLongerReturnsIsGivenSomethingToReturn()
	{
		string result = Repair("""
			namespace Game
			{
				public class Widget
				{
					public int Broken(ref int value)
					{
						return ~(ref value);
					}
				}
			}
			""", compile: true);

		using (Assert.EnterMultipleScope())
		{
			Assert.That(result, Does.Contain("\t\t\t//return ~(ref value);"));
			Assert.That(result, Does.Contain("return default;"));
		}
	}

	[Test]
	public void TheMessagesRecoveryLeavesBehindAreSilenced()
	{
		string result = Repair("""
			namespace Game
			{
				public class Widget
				{
					public void Trace()
					{
						System.Console.WriteLine("Method not found @2183D8C");
						System.Console.WriteLine("a message of the game's own");
					}
				}
			}
			""");

		using (Assert.EnterMultipleScope())
		{
			Assert.That(result, Does.Contain("_ = \"Method not found @2183D8C\";"));
			Assert.That(result, Does.Not.Contain("Console.WriteLine(\"Method not found"));
			Assert.That(result, Does.Contain("\n\t\t\tSystem.Console.WriteLine(\"a message of the game's own\");"));
		}
	}

	[Test]
	public void ValidSourceIsLeftAlone()
	{
		const string Source = """
			namespace Game
			{
				public class Widget
				{
					public int Kept()
					{
						return 1;
					}
				}
			}
			""";

		Assert.That(Repair(Source), Is.EqualTo(Source));
	}
}
