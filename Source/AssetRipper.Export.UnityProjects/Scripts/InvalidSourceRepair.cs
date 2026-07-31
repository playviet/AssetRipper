using AsmResolver.DotNet;
using AssetRipper.CIL;
using AssetRipper.Import.Logging;
using AssetRipper.Import.Structure.Assembly.Managers;
using AssetRipper.IO.Files;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Text;

namespace AssetRipper.Export.UnityProjects.Scripts;

/// <summary>
/// Finds the decompiled source that a compiler would reject and removes the method bodies responsible.
/// </summary>
/// <remarks>
/// Il2Cpp method body recovery reconstructs CIL from native code, and a reconstructed body regularly uses a value at a
/// type the instruction does not accept. Such a body is still structurally sound, so it survives every check made
/// before the decompiler sees it, and the decompiler does not fail on it either: it writes out C# that says what the
/// native code did but does not compile. Left alone the exported project cannot be opened as a project at all, because
/// the editor compiles an assembly as a whole and one unusable body costs every file compiled with it.
/// <para>
/// Rather than trying to predict which recovered bodies a decompiler will mishandle, this compiles what was written
/// against the assemblies it was recovered alongside, and drops the bodies the errors point at. What is left is the
/// part of the recovery that holds up, in a project that builds.
/// </para>
/// </remarks>
internal static class InvalidSourceRepair
{
	/// <summary>
	/// Discards the method bodies whose decompiled source does not compile.
	/// </summary>
	/// <returns>Whether any body was discarded, meaning the assembly is worth decompiling again.</returns>
	public static bool Apply(AssemblyDefinition assembly, IAssemblyManager manager, string outputFolder, FileSystem fileSystem)
	{
		List<AssemblyMetadata> metadata = GetMetadata(assembly, manager);
		try
		{
			return Apply(assembly, metadata.ConvertAll(m => (MetadataReference)m.GetReference()), outputFolder, fileSystem);
		}
		finally
		{
			foreach (AssemblyMetadata item in metadata)
			{
				item.Dispose();
			}
		}
	}

	/// <inheritdoc cref="Apply(AssemblyDefinition, IAssemblyManager, string, FileSystem)"/>
	/// <param name="references">
	/// The other assemblies of the game. Without them only the source that is broken on its own can be found.
	/// </param>
	public static bool Apply(AssemblyDefinition assembly, List<MetadataReference> references, string outputFolder, FileSystem fileSystem)
	{
		List<(string Path, CompilationUnitSyntax Root)> files = Parse(outputFolder, fileSystem);
		if (files.Count == 0)
		{
			return false;
		}

		Dictionary<string, List<int>> positionsByPath = [];
		foreach ((string path, CompilationUnitSyntax root) in files)
		{
			List<int> positions = FindLocalProblems(root);
			if (positions.Count > 0)
			{
				positionsByPath[path] = positions;
			}
		}

		AddCompilationProblems(assembly, references, files, positionsByPath);

		int discarded = 0;
		foreach ((string path, CompilationUnitSyntax root) in files)
		{
			if (positionsByPath.TryGetValue(path, out List<int>? positions))
			{
				discarded += Discard(assembly, root, positions);
			}
		}

		if (discarded > 0)
		{
			Logger.Info(LogCategory.Export, $"Discarded {discarded} method {(discarded == 1 ? "body" : "bodies")} that did not compile, from {positionsByPath.Count} of {files.Count} files");
		}

		return discarded > 0;
	}

	private static List<(string, CompilationUnitSyntax)> Parse(string outputFolder, FileSystem fileSystem)
	{
		List<(string, CompilationUnitSyntax)> files = [];

		foreach (string path in fileSystem.Directory.EnumerateFiles(outputFolder, "*.cs", SearchOption.AllDirectories))
		{
			try
			{
				files.Add((path, CSharpSyntaxTree.ParseText(fileSystem.File.ReadAllText(path)).GetCompilationUnitRoot()));
			}
			catch (Exception exception)
			{
				Logger.Warning(LogCategory.Export, $"Could not read back {path}: {exception.Message}");
			}
		}

		return files;
	}

	/// <summary>
	/// The positions in one file that are wrong without needing to look at anything else.
	/// </summary>
	/// <remarks>
	/// These are found separately from the compilation, so that source which is broken outright is still repaired when
	/// no compilation can be built.
	/// </remarks>
	private static List<int> FindLocalProblems(CompilationUnitSyntax root)
	{
		List<int> positions = [];

		foreach (Diagnostic diagnostic in root.GetDiagnostics())
		{
			if (diagnostic.Severity == DiagnosticSeverity.Error)
			{
				positions.Add(diagnostic.Location.SourceSpan.Start);
			}
		}

		//A generic name without its arguments parses, and is only an error once it is seen outside a typeof. A
		//decompiler writing one means it lost track of an instantiation, and the name is never valid where it appears.
		foreach (SyntaxNode node in root.DescendantNodes())
		{
			if (node is OmittedTypeArgumentSyntax && node.FirstAncestorOrSelf<TypeOfExpressionSyntax>() is null)
			{
				positions.Add(node.SpanStart);
			}
		}

		return positions;
	}

	/// <summary>
	/// The other assemblies of the game, read as the compiler wants them.
	/// </summary>
	/// <remarks>
	/// Each one is read with the stream left open, because the manager hands out a cached stream that the rest of the
	/// export still needs, and the usual way of creating a reference from a stream closes it.
	/// </remarks>
	private static List<AssemblyMetadata> GetMetadata(AssemblyDefinition assembly, IAssemblyManager manager)
	{
		List<AssemblyMetadata> metadata = [];

		foreach (AssemblyDefinition other in manager.GetAssemblies())
		{
			if (ReferenceEquals(other, assembly))
			{
				//Its types are the ones being compiled, so referencing it as well would make every one of them ambiguous.
				continue;
			}

			try
			{
				Stream stream = manager.GetStreamForAssembly(other);
				stream.Position = 0;
				metadata.Add(AssemblyMetadata.CreateFromStream(stream, leaveOpen: true));
			}
			catch (Exception exception)
			{
				Logger.Warning(LogCategory.Export, $"Could not reference {other.Name} while checking decompiled source: {exception.Message}");
			}
		}

		return metadata;
	}

	/// <summary>
	/// Adds the positions that only a compiler finds, by compiling the files against the other assemblies of the game.
	/// </summary>
	/// <remarks>
	/// Those are the same assemblies the code was compiled against originally, so the errors that come back are the
	/// ones the editor is going to report, rather than an approximation of them.
	/// </remarks>
	private static void AddCompilationProblems(
		AssemblyDefinition assembly,
		List<MetadataReference> references,
		List<(string Path, CompilationUnitSyntax Root)> files,
		Dictionary<string, List<int>> positionsByPath)
	{
		if (references.Count == 0)
		{
			return;
		}

		Dictionary<SyntaxTree, string> pathsByTree = [];
		foreach ((string path, CompilationUnitSyntax root) in files)
		{
			pathsByTree[root.SyntaxTree] = path;
		}

		CSharpCompilation compilation = CSharpCompilation.Create(
			assembly.Name?.ToString() ?? "Decompiled",
			pathsByTree.Keys,
			references,
			new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

		foreach (Diagnostic diagnostic in compilation.GetDiagnostics())
		{
			if (diagnostic.Severity != DiagnosticSeverity.Error
				|| diagnostic.Location.SourceTree is not { } tree
				|| !pathsByTree.TryGetValue(tree, out string? path))
			{
				continue;
			}

			if (!positionsByPath.TryGetValue(path, out List<int>? positions))
			{
				positions = [];
				positionsByPath.Add(path, positions);
			}
			positions.Add(diagnostic.Location.SourceSpan.Start);
		}
	}

	private static int Discard(AssemblyDefinition assembly, CompilationUnitSyntax root, List<int> positions)
	{
		HashSet<MethodDefinition> methods = new();

		foreach (int position in positions)
		{
			SyntaxNode? node = root.FindToken(position, true).Parent;
			if (node?.FirstAncestorOrSelf<TypeDeclarationSyntax>() is not { } declaration)
			{
				continue;
			}

			//An error outside a method body cannot be repaired by discarding one, so it is left to be reported.
			HashSet<string> names = GetMemberNames(node);
			if (names.Count == 0 || FindType(assembly, declaration) is not { } type)
			{
				continue;
			}

			methods.UnionWith(type.Methods.Where(m => MethodMatches(m, names)));
		}

		int discarded = 0;
		foreach (MethodDefinition method in methods)
		{
			if (method.CilMethodBody is not null)
			{
				method.ReplaceMethodBodyWithMinimalImplementation();
				discarded++;
			}
		}

		return discarded;
	}

	/// <summary>
	/// The metadata names of the methods that a member declaration compiles to.
	/// </summary>
	/// <remarks>
	/// Accessors are covered by name rather than by which one the error was in, because a property's accessors are
	/// written as one member and the pair is small enough that keeping just one is not worth the extra matching.
	/// </remarks>
	private static HashSet<string> GetMemberNames(SyntaxNode node)
	{
		HashSet<string> names = new();

		for (SyntaxNode? current = node; current is not null; current = current.Parent)
		{
			switch (current)
			{
				case MethodDeclarationSyntax method:
					names.Add(method.Identifier.ValueText);
					return names;
				case ConstructorDeclarationSyntax constructor:
					names.Add(constructor.Modifiers.Any(SyntaxKind.StaticKeyword) ? ".cctor" : ".ctor");
					return names;
				case DestructorDeclarationSyntax:
					names.Add("Finalize");
					return names;
				case PropertyDeclarationSyntax property:
					names.Add($"get_{property.Identifier.ValueText}");
					names.Add($"set_{property.Identifier.ValueText}");
					return names;
				case IndexerDeclarationSyntax:
					names.Add("get_Item");
					names.Add("set_Item");
					return names;
				case EventDeclarationSyntax @event:
					names.Add($"add_{@event.Identifier.ValueText}");
					names.Add($"remove_{@event.Identifier.ValueText}");
					return names;
				case OperatorDeclarationSyntax @operator:
					names.Add(GetOperatorName(@operator.OperatorToken.Kind(), @operator.ParameterList.Parameters.Count));
					return names;
				case ConversionOperatorDeclarationSyntax conversion:
					names.Add(conversion.ImplicitOrExplicitKeyword.IsKind(SyntaxKind.ImplicitKeyword) ? "op_Implicit" : "op_Explicit");
					return names;
				case TypeDeclarationSyntax:
					//Reached the type without finding a member, so the error is not inside one.
					return names;
			}
		}

		return names;
	}

	/// <remarks>
	/// Plus and minus name two operators each, told apart by how many operands they take.
	/// </remarks>
	private static string GetOperatorName(SyntaxKind kind, int parameterCount) => kind switch
	{
		SyntaxKind.PlusToken => parameterCount == 1 ? "op_UnaryPlus" : "op_Addition",
		SyntaxKind.MinusToken => parameterCount == 1 ? "op_UnaryNegation" : "op_Subtraction",
		SyntaxKind.AsteriskToken => "op_Multiply",
		SyntaxKind.SlashToken => "op_Division",
		SyntaxKind.PercentToken => "op_Modulus",
		SyntaxKind.AmpersandToken => "op_BitwiseAnd",
		SyntaxKind.BarToken => "op_BitwiseOr",
		SyntaxKind.CaretToken => "op_ExclusiveOr",
		SyntaxKind.TildeToken => "op_OnesComplement",
		SyntaxKind.ExclamationToken => "op_LogicalNot",
		SyntaxKind.LessThanLessThanToken => "op_LeftShift",
		SyntaxKind.GreaterThanGreaterThanToken => "op_RightShift",
		SyntaxKind.EqualsEqualsToken => "op_Equality",
		SyntaxKind.ExclamationEqualsToken => "op_Inequality",
		SyntaxKind.LessThanToken => "op_LessThan",
		SyntaxKind.GreaterThanToken => "op_GreaterThan",
		SyntaxKind.LessThanEqualsToken => "op_LessThanOrEqual",
		SyntaxKind.GreaterThanEqualsToken => "op_GreaterThanOrEqual",
		SyntaxKind.PlusPlusToken => "op_Increment",
		SyntaxKind.MinusMinusToken => "op_Decrement",
		SyntaxKind.TrueKeyword => "op_True",
		SyntaxKind.FalseKeyword => "op_False",
		_ => "",
	};

	/// <summary>
	/// Whether a method is one of those a member declaration in the decompiled source compiles to.
	/// </summary>
	/// <remarks>
	/// A name written in the source is not always the metadata one. Compiler generated members are named with
	/// characters a C# identifier cannot contain, which the decompiler escapes, and an explicit interface
	/// implementation carries the interface in its metadata name but is written under the member name alone.
	/// </remarks>
	private static bool MethodMatches(MethodDefinition method, HashSet<string> names)
	{
		string metadataName = method.Name?.ToString() ?? "";
		if (names.Contains(metadataName) || names.Contains(EscapeIdentifier(metadataName)))
		{
			return true;
		}

		int lastDot = metadataName.LastIndexOf('.');
		return lastDot >= 0 && names.Contains(EscapeIdentifier(metadataName.AsSpan(lastDot + 1)));
	}

	/// <summary>
	/// Matches a declaration in the decompiled source back to the type it was decompiled from.
	/// </summary>
	private static TypeDefinition? FindType(AssemblyDefinition assembly, TypeDeclarationSyntax declaration)
	{
		List<string> names = [];
		List<string> namespaceParts = [];
		for (SyntaxNode? node = declaration; node is not null; node = node.Parent)
		{
			switch (node)
			{
				case TypeDeclarationSyntax type:
					names.Insert(0, type.Identifier.ValueText);
					break;
				case BaseNamespaceDeclarationSyntax @namespace:
					namespaceParts.Insert(0, @namespace.Name.ToString());
					break;
			}
		}

		string fullNamespace = string.Join('.', namespaceParts);
		TypeDefinition? current = assembly.Modules
			.SelectMany(m => m.TopLevelTypes)
			.FirstOrDefault(t => NameMatches(t, names[0]) && (t.Namespace?.ToString() ?? "") == fullNamespace);

		for (int i = 1; current is not null && i < names.Count; i++)
		{
			current = current.NestedTypes.FirstOrDefault(t => NameMatches(t, names[i]));
		}

		return current;
	}

	/// <summary>
	/// Whether a type has the name it was decompiled under.
	/// </summary>
	/// <remarks>
	/// A decompiled name differs from the metadata one in two ways: the arity a generic type carries is dropped, and
	/// the characters a C# identifier cannot contain are escaped. The latter matters because the types the compiler
	/// generates for iterators and async methods are named after the method they came from, in angle brackets.
	/// </remarks>
	private static bool NameMatches(TypeDefinition type, string name)
	{
		ReadOnlySpan<char> metadataName = type.Name?.ToString() ?? "";
		int backtick = metadataName.IndexOf('`');
		return EscapeIdentifier(backtick < 0 ? metadataName : metadataName[..backtick]) == name;
	}

	/// <summary>
	/// Rewrites a metadata name the way the decompiler does when it has to be a C# identifier.
	/// </summary>
	private static string EscapeIdentifier(ReadOnlySpan<char> name)
	{
		StringBuilder builder = new(name.Length);
		foreach (char character in name)
		{
			if (char.IsLetterOrDigit(character) || character == '_')
			{
				builder.Append(character);
			}
			else
			{
				builder.Append('_').Append(((int)character).ToString("X4"));
			}
		}
		return builder.ToString();
	}
}
