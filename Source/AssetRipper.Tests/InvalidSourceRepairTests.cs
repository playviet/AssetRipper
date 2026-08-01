using AssetRipper.Export.UnityProjects.Scripts;
using AssetRipper.IO.Files;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace AssetRipper.Tests;

internal class InvalidSourceRepairTests
{
	private const string OutputFolder = "/Scripts";
	private const string FileName = "Widget.cs";
	private const string Marker = "//AssetRipper: commented out, this could not be kept as code.";

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
	public void ANullCastToANativeIntegerBecomesZero()
	{
		//The editor has no conversion from null to a native integer, but the compiler this repair uses to check
		//the source does, so this shape has to be recognised outright rather than waited for as an error.
		//Against something else, what that something is decides the zero's type, and only `default` is right
		//whichever it turns out to be - the editor rejects both `int == IntPtr` and `IntPtr == 0`.
		string result = Repair("""
			using System;
			namespace Game
			{
				public class Widget
				{
					public IntPtr Held;

					public bool Compared(IntPtr handle)
					{
						return handle == unchecked((nint)null);
					}

					public void Assigned()
					{
						Held = unchecked((nint)null);
					}
				}
			}
			""", compile: true);

		using (Assert.EnterMultipleScope())
		{
			Assert.That(result, Does.Contain("return handle == unchecked(default);"));
			Assert.That(result, Does.Contain("Held = unchecked(global::System.IntPtr.Zero);"));
			Assert.That(result, Does.Not.Contain("null"));
			Assert.That(result, Does.Not.Contain(Marker));
		}
	}

	[Test]
	public void AnArrowBodiedMemberThatDoesNotCompileIsReplaced()
	{
		//A member written with an arrow holds no statement, so there is nothing to comment out: the only repair
		//available is to replace what it says. Without this the file keeps an error the editor refuses to build.
		string result = Repair("""
			namespace Game
			{
				public class Other
				{
					private const string Hidden = "x";
				}

				public class Widget
				{
					public string Name => Other.Hidden;

					public void Ping() => Other.Hidden.ToString();
				}
			}
			""", compile: true);

		using (Assert.EnterMultipleScope())
		{
			Assert.That(result, Does.Contain("public string Name => default;"));
			Assert.That(result, Does.Contain("public void Ping() => _ = 0;"));
			Assert.That(result, Does.Not.Contain("Other.Hidden;"));
		}
	}

	[Test]
	public void AnArrowBodiedMemberThatCompilesIsLeftAlone()
	{
		string result = Repair("""
			namespace Game
			{
				public class Widget
				{
					public int Count => 3;

					public void Ping() => Count.ToString();
				}
			}
			""", compile: true);

		using (Assert.EnterMultipleScope())
		{
			Assert.That(result, Does.Contain("public int Count => 3;"));
			Assert.That(result, Does.Contain("public void Ping() => Count.ToString();"));
		}
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
	public void AReferenceWrittenAsZeroBecomesNull()
	{
		//Native code has no null, so recovery writes one as a zero cast to the type the register was holding.
		string result = Repair("""
			namespace Game
			{
				public class Widget
				{
					public void Broken()
					{
						string text = "kept";
						text = (string)0;
						System.Collections.Generic.List<string> list = (System.Collections.Generic.List<string>)0;
						System.Console.WriteLine(text + list);
					}
				}
			}
			""", compile: true);

		using (Assert.EnterMultipleScope())
		{
			Assert.That(result, Does.Contain("\n\t\t\ttext = null;"));
			//The type is kept where the assignment does not already say it, because it may be telling overloads apart.
			Assert.That(result, Does.Contain("\n\t\t\tSystem.Collections.Generic.List<string> list = default(System.Collections.Generic.List<string>);"));
			Assert.That(result, Does.Not.Contain(Marker));
		}
	}

	[Test]
	public void AZeroThatIsNotAReferenceIsStillCommentedOut()
	{
		//A zeroed register is not the same thing as a zeroed struct, and a zero the language does accept a cast of is a
		//number that was meant to be one.
		string result = Repair("""
			namespace Game
			{
				public class Widget
				{
					public void Broken()
					{
						object boxed = (object)0;
						System.DateTime time = (System.DateTime)0;
						System.Console.WriteLine(boxed + time.ToString());
					}
				}
			}
			""", compile: true);

		using (Assert.EnterMultipleScope())
		{
			Assert.That(result, Does.Contain("\t\t\t//System.DateTime time = (System.DateTime)0;"));
			Assert.That(result, Does.Contain("\n\t\t\tobject boxed = (object)0;"));
		}
	}

	[Test]
	public void ANullThatIsBeingCalledIsStillCommentedOut()
	{
		//The exported project is meant to run as the game did. A statement that is not there does nothing, which is
		//wrong but harmless; a call on a null that has been written back out throws where the game did not.
		string result = Repair("""
			namespace Game
			{
				public class Tracker
				{
					public void Track(string name)
					{
					}
				}

				public class Widget
				{
					public void Broken()
					{
						((Tracker)0).Track("started");
						Tracker tracker = new Tracker();
						tracker = (Tracker)0;
					}
				}
			}
			""", compile: true);

		using (Assert.EnterMultipleScope())
		{
			Assert.That(result, Does.Contain("\t\t\t//((Tracker)0).Track(\"started\");"));
			//Everywhere else the null is only a value being passed about, and saying it properly changes nothing.
			Assert.That(result, Does.Contain("\n\t\t\ttracker = null;"));
		}
	}

	[Test]
	public void ANativeBooleanTestOfAReferenceBecomesANullCheck()
	{
		//Il2Cpp returns a boolean in the low byte of a register, which recovery writes as this chain of casts.
		string result = Repair("""
			namespace Game
			{
				public class Widget
				{
					public bool Broken(string value)
					{
						bool flag = (byte)(int)value != 0;
						bool gone = (byte)(int)value == 0;
						return flag || gone;
					}
				}
			}
			""", compile: true);

		using (Assert.EnterMultipleScope())
		{
			Assert.That(result, Does.Contain("\n\t\t\tbool flag = value != null;"));
			Assert.That(result, Does.Contain("\n\t\t\tbool gone = value == null;"));
			Assert.That(result, Does.Not.Contain(Marker));
		}
	}

	[Test]
	public void ANativeBooleanTestOfSomethingElseIsStillCommentedOut()
	{
		//A struct would compare against null quite happily and always come out the same way, and a boxed number is
		//being unboxed and tested as a number rather than asked whether it is there.
		string result = Repair("""
			namespace Game
			{
				public class Widget
				{
					public bool Broken(System.DateTime time, object boxed)
					{
						bool flag = (byte)(int)time != 0;
						bool kept = (byte)(int)boxed != 0;
						return flag || kept;
					}
				}
			}
			""", compile: true);

		using (Assert.EnterMultipleScope())
		{
			Assert.That(result, Does.Contain("\t\t\t//bool flag = (byte)(int)time != 0;"));
			Assert.That(result, Does.Contain("\n\t\t\tbool kept = (byte)(int)boxed != 0;"));
		}
	}

	[Test]
	public void AConstructorCalledByItsMetadataNameBecomesAnAssignment()
	{
		string result = Repair("""
			namespace Game
			{
				public class Widget
				{
					public void Broken()
					{
						System.Text.StringBuilder builder = null;
						builder._002Ector();
						System.Console.WriteLine(builder);
					}
				}
			}
			""", compile: true);

		using (Assert.EnterMultipleScope())
		{
			Assert.That(result, Does.Contain("\n\t\t\tbuilder = new global::System.Text.StringBuilder();"));
			Assert.That(result, Does.Not.Contain(Marker));
		}
	}

	[Test]
	public void AConstructorCallThatTwoConstructorsCouldTakeIsStillCommentedOut()
	{
		//Recovery loses argument types as readily as anything else, so a call more than one constructor could take is a
		//call whose meaning would be a guess.
		string result = Repair("""
			namespace Game
			{
				public class Amount
				{
					public Amount(int value)
					{
					}

					public Amount(long value)
					{
					}
				}

				public class Widget
				{
					public void Broken()
					{
						Amount amount = null;
						amount._002Ector(0);
						System.Console.WriteLine(amount);
					}
				}
			}
			""", compile: true);

		Assert.That(result, Does.Contain("\t\t\t//amount._002Ector(0);"));
	}

	[Test]
	public void ADelegateBuiltFromAFunctionPointerBecomesAMethodGroup()
	{
		string result = Repair("""
			namespace Game
			{
				public class Widget
				{
					private sealed class Closure
					{
						public static readonly Closure Shared = new Closure();

						internal int Read()
						{
							return 0;
						}
					}

					public void Broken()
					{
						Closure closure = new Closure();
						System.Func<int> getter = null;
						getter._002Ector((object)closure, (System.IntPtr)(nint)__ldftn(Closure.Read));
						System.Func<int> shared = null;
						shared._002Ector((object)Closure.Shared, (System.IntPtr)(nint)__ldftn(Closure.Read));
						System.Console.WriteLine(getter + shared.ToString());
					}
				}
			}
			""", compile: true);

		using (Assert.EnterMultipleScope())
		{
			Assert.That(result, Does.Contain("\n\t\t\tgetter = new global::System.Func<int>(closure.Read);"));
			//The target is as often the field a closure was cached in as it is a local.
			Assert.That(result, Does.Contain("\n\t\t\tshared = new global::System.Func<int>(Closure.Shared.Read);"));
			Assert.That(result, Does.Not.Contain(Marker));
		}
	}

	[Test]
	public void ADelegateOverAnOverloadedMethodIsStillCommentedOut()
	{
		//The function pointer says which method it was taken from, but nothing says which overload, and a delegate over
		//the wrong one would compile.
		string result = Repair("""
			namespace Game
			{
				public class Widget
				{
					private sealed class Closure
					{
						internal int Read()
						{
							return 0;
						}

						internal int Read(int index)
						{
							return index;
						}
					}

					public void Broken()
					{
						Closure closure = new Closure();
						System.Func<int> getter = null;
						getter._002Ector((object)closure, (System.IntPtr)(nint)__ldftn(Closure.Read));
						System.Console.WriteLine(getter);
					}
				}
			}
			""", compile: true);

		Assert.That(result, Does.Contain("\t\t\t//getter._002Ector((object)closure, (System.IntPtr)(nint)__ldftn(Closure.Read));"));
	}

	[Test]
	public void AStatementThatIsBrokenBeyondTheIdiomIsStillCommentedOut()
	{
		//Half a statement rewritten is of less use than the statement as recovery wrote it, so a statement that is going
		//to be commented out anyway is commented out untouched.
		string result = Repair("""
			namespace Game
			{
				public class Widget
				{
					public void Broken()
					{
						string text = (string)0 + missing;
					}
				}
			}
			""", compile: true);

		Assert.That(result, Does.Contain("\t\t\t//string text = (string)0 + missing;"));
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
