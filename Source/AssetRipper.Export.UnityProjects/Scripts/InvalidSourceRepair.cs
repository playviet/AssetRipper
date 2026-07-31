using AsmResolver.DotNet;
using AssetRipper.Import.Logging;
using AssetRipper.Import.Structure.Assembly.Managers;
using AssetRipper.IO.Files;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using System.Text;

namespace AssetRipper.Export.UnityProjects.Scripts;

/// <summary>
/// Comments out the decompiled statements that a compiler would reject, so the rest of the file survives.
/// </summary>
/// <remarks>
/// Il2Cpp method body recovery reconstructs CIL from native code, and machine code carries no types, so wherever the
/// analysis cannot work out what a register held it falls back to an untyped value and raw offset arithmetic. The
/// decompiler writes that out faithfully, as C# that says what the native code did but does not compile. Left alone
/// the exported project cannot be opened at all, because the editor compiles an assembly as a whole and one unusable
/// statement costs the project every script in that assembly.
/// <para>
/// So the decompiled source is compiled here, against the assemblies it was recovered alongside, and each statement an
/// error points at is commented out rather than deleted. That keeps it readable while letting everything around it
/// compile, which is worth far more than the statement was: a method usually has one or two the analysis could not
/// type and dozens it could.
/// </para>
/// </remarks>
internal static class InvalidSourceRepair
{
	/// <summary>
	/// How many times the source may be compiled and repaired.
	/// </summary>
	/// <remarks>
	/// Commenting a statement out can expose another error, most often a local that is now never assigned, so this
	/// takes more than one pass to settle. It always settles, because every pass removes something and a method with
	/// nothing left in it compiles.
	/// </remarks>
	private const int MaxAttempts = 8;

	private const string Marker = "//AssetRipper: commented out, this could not be kept as code.";

	public static void Apply(AssemblyDefinition assembly, IAssemblyManager manager, LanguageVersion languageVersion, string outputFolder, FileSystem fileSystem)
	{
		List<AssemblyMetadata> metadata = GetMetadata(assembly, manager);
		try
		{
			Apply(metadata.ConvertAll(m => (MetadataReference)m.GetReference()), languageVersion, outputFolder, fileSystem);
		}
		finally
		{
			foreach (AssemblyMetadata item in metadata)
			{
				item.Dispose();
			}
		}
	}

	/// <inheritdoc cref="Apply(AssemblyDefinition, IAssemblyManager, LanguageVersion, string, FileSystem)"/>
	/// <param name="references">
	/// The assemblies to compile against. Without them only the source that is broken on its own can be found.
	/// </param>
	public static void Apply(List<MetadataReference> references, LanguageVersion languageVersion, string outputFolder, FileSystem fileSystem)
	{
		//The source was written for this version, and so is the editor going to read it. Checking it against a newer
		//one accepts what the editor will not: a struct whose fields go unassigned is an error before C# 11 and quietly
		//defaulted after it.
		CSharpParseOptions parseOptions = new(languageVersion);

		SilenceTraces(outputFolder, fileSystem, parseOptions);

		int commented = 0;
		int emptied = 0;
		HashSet<string> repairedFiles = [];

		for (int attempt = 0; attempt < MaxAttempts; attempt++)
		{
			List<SourceFile> files = Parse(outputFolder, fileSystem, parseOptions);
			if (files.Count == 0)
			{
				break;
			}

			CSharpCompilation? compilation = FindCompilationProblems(files, references);

			//The last pass has no chance to fix what it finds, so it empties the method instead of chipping at it.
			bool lastAttempt = attempt == MaxAttempts - 1;
			int repaired = 0;
			foreach (SourceFile file in files)
			{
				List<Edit> edits = FindEdits(file, compilation, lastAttempt, out int emptiedHere);
				if (edits.Count == 0)
				{
					continue;
				}

				fileSystem.File.WriteAllText(file.Path, ApplyEdits(file.Text, edits));
				repairedFiles.Add(file.Path);
				repaired += edits.Count;
				emptied += emptiedHere;
			}

			commented += repaired;
			if (repaired == 0)
			{
				break;
			}
		}

		if (commented > 0)
		{
			Logger.Info(LogCategory.Export, $"Commented out {commented} statements that did not compile, in {repairedFiles.Count} files{(emptied > 0 ? $", and emptied {emptied} methods that would not settle" : "")}");
		}
	}

	/// <summary>
	/// The messages recovery writes into a method when it cannot translate something.
	/// </summary>
	private static readonly string[] TracePrefixes =
	[
		"Method not found @",
		"Unmanaged memory load: ",
		"Not implemented instruction: ",
		"Unknown call target operand: ",
		"Indirect call: ",
		"Non static method called without",
		"Jump target not found",
	];

	/// <summary>
	/// Comments out the calls recovery leaves behind to say what it could not translate.
	/// </summary>
	/// <remarks>
	/// They are written as calls to <see cref="Console.WriteLine(string)"/>, which compiles but also runs: a recovered
	/// method that the editor happens to call will write one of these on every pass, and a recovered loop will fill the
	/// log with them. The message is worth keeping, so it stays as a comment rather than being removed.
	/// </remarks>
	private static void SilenceTraces(string outputFolder, FileSystem fileSystem, CSharpParseOptions parseOptions)
	{
		int silenced = 0;

		foreach (SourceFile file in Parse(outputFolder, fileSystem, parseOptions))
		{
			List<Edit> edits = [];
			foreach (SyntaxNode node in file.Root.DescendantNodes())
			{
				if (node is ExpressionStatementSyntax { Parent: BlockSyntax, Expression: InvocationExpressionSyntax invocation }
					&& IsTrace(invocation))
				{
					edits.Add(new Edit(node.Span, null));
				}
			}

			if (edits.Count > 0)
			{
				fileSystem.File.WriteAllText(file.Path, ApplyEdits(file.Text, edits));
				silenced += edits.Count;
			}
		}

		if (silenced > 0)
		{
			Logger.Info(LogCategory.Export, $"Commented out {silenced} messages that recovery would otherwise have written at runtime");
		}
	}

	private static bool IsTrace(InvocationExpressionSyntax invocation)
	{
		return invocation.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "WriteLine" }
			&& invocation.ArgumentList.Arguments is [{ Expression: LiteralExpressionSyntax literal }]
			&& literal.Token.ValueText is { } message
			&& TracePrefixes.Any(prefix => message.StartsWith(prefix, StringComparison.Ordinal));
	}

	private sealed class SourceFile(string path, string text, CompilationUnitSyntax root)
	{
		public string Path { get; } = path;
		public string Text { get; } = text;
		public CompilationUnitSyntax Root { get; } = root;

		/// <summary>
		/// Where in this file the compiler found something wrong, and what it was, filled in once all the files have
		/// been read.
		/// </summary>
		public List<(int Position, string Id)> Positions { get; } = [];
	}

	private static List<SourceFile> Parse(string outputFolder, FileSystem fileSystem, CSharpParseOptions parseOptions)
	{
		List<SourceFile> files = [];

		foreach (string path in fileSystem.Directory.EnumerateFiles(outputFolder, "*.cs", SearchOption.AllDirectories))
		{
			try
			{
				string text = fileSystem.File.ReadAllText(path);
				files.Add(new SourceFile(path, text, CSharpSyntaxTree.ParseText(text, parseOptions).GetCompilationUnitRoot()));
			}
			catch (Exception exception)
			{
				Logger.Warning(LogCategory.Export, $"Could not read back {path}: {exception.Message}");
			}
		}

		return files;
	}

	/// <summary>
	/// A stretch of source to comment out, and what to put in its place if leaving nothing there would not compile.
	/// </summary>
	private readonly record struct Edit(TextSpan Span, string? Replacement);

	/// <summary>
	/// The edits to make to one file: the statement around each problem, or the whole body of a method that cannot be
	/// narrowed down any further.
	/// </summary>
	private static List<Edit> FindEdits(SourceFile file, CSharpCompilation? compilation, bool lastAttempt, out int emptied)
	{
		emptied = 0;
		List<Edit> edits = [];
		HashSet<TextSpan> seen = [];

		foreach ((int position, string id) in GetProblemPositions(file))
		{
			SyntaxNode? node = file.Root.FindToken(position, true).Parent;
			if (node is null)
			{
				continue;
			}

			//A method that no longer returns on every path, or no longer assigns an out parameter, is missing
			//something rather than containing something wrong. Adding it back is cheaper than emptying the method.
			Edit? edit = id == "CS1729"
				? FindBaseInitializerEdit(node, compilation)
				: IsMissingSomething(id)
					? FindFixupEdit(node, id)
					: (lastAttempt ? null : FindStatement(node)) is { } statement
						? new Edit(statement.Span, null)
						: FindBodyEdit(node);

			if (edit is null)
			{
				continue;
			}

			if (edit.Value.Replacement is not null || lastAttempt || FindStatement(node) is null)
			{
				emptied++;
			}

			if (seen.Add(edit.Value.Span))
			{
				edits.Add(edit.Value);
			}
		}

		//The edits are applied in one pass over the text, so they have to be in order and must not overlap. An
		//insertion is sorted ahead of a span starting in the same place, so that it lands before rather than inside it.
		edits.Sort((a, b) => a.Span.Start != b.Span.Start
			? a.Span.Start.CompareTo(b.Span.Start)
			: a.Span.Length.CompareTo(b.Span.Length));

		List<Edit> result = [];
		int end = 0;
		foreach (Edit edit in edits)
		{
			if (edit.Span.Start >= end || (edit.Span.IsEmpty && edit.Span.Start == end))
			{
				result.Add(edit);
				end = edit.Span.End;
			}
		}
		return result;
	}

	private static IEnumerable<(int Position, string Id)> GetProblemPositions(SourceFile file)
	{
		foreach (Diagnostic diagnostic in file.Root.GetDiagnostics())
		{
			if (diagnostic.Severity == DiagnosticSeverity.Error)
			{
				yield return (diagnostic.Location.SourceSpan.Start, diagnostic.Id);
			}
		}

		//A generic name without its arguments parses, and is only an error once it is seen outside a typeof. A
		//decompiler writing one means it lost track of an instantiation, and the name is never valid where it appears.
		foreach (SyntaxNode node in file.Root.DescendantNodes())
		{
			if (node is OmittedTypeArgumentSyntax && node.FirstAncestorOrSelf<TypeOfExpressionSyntax>() is null)
			{
				yield return (node.SpanStart, "");
			}
		}

		foreach ((int position, string id) in file.Positions)
		{
			yield return (position, id);
		}
	}

	/// <summary>
	/// The smallest whole statement around a node, which is the smallest thing that can be commented out and still
	/// leave the file parsing.
	/// </summary>
	private static StatementSyntax? FindStatement(SyntaxNode node)
	{
		for (SyntaxNode? current = node; current is not null; current = current.Parent)
		{
			if (current is StatementSyntax statement && statement.Parent is BlockSyntax or SwitchSectionSyntax)
			{
				return statement;
			}

			if (current is MemberDeclarationSyntax)
			{
				//Reached the member without passing through a statement, so there is nothing here to comment out.
				return null;
			}
		}

		return null;
	}

	/// <summary>
	/// Everything between the braces of the method around a node, for an error that is about the method as a whole
	/// rather than about one statement in it.
	/// </summary>
	/// <remarks>
	/// The usual cause is a method that no longer returns on every path, because the return was one of the statements
	/// commented out earlier. Such a method needs something left behind to return, hence the replacement.
	/// </remarks>
	private static Edit? FindBodyEdit(SyntaxNode node)
	{
		for (SyntaxNode? current = node; current is not null; current = current.Parent)
		{
			(BlockSyntax? body, TypeSyntax? returnType) = current switch
			{
				MethodDeclarationSyntax method => (method.Body, method.ReturnType),
				ConstructorDeclarationSyntax constructor => (constructor.Body, null),
				OperatorDeclarationSyntax @operator => (@operator.Body, @operator.ReturnType),
				ConversionOperatorDeclarationSyntax conversion => (conversion.Body, conversion.Type),
				AccessorDeclarationSyntax accessor => (accessor.Body, GetAccessorReturnType(accessor)),
				LocalFunctionStatementSyntax function => (function.Body, function.ReturnType),
				_ => (null, null),
			};

			if (body is not { Statements.Count: > 0 })
			{
				continue;
			}

			TextSpan span = TextSpan.FromBounds(body.Statements[0].SpanStart, body.Statements[^1].Span.End);
			return new Edit(span, ReturnsValue(returnType) ? "return default;" : null);
		}

		return null;
	}

	/// <summary>
	/// An insertion at the end of the method around a node, giving back whatever the compiler says is missing.
	/// </summary>
	/// <remarks>
	/// A method whose return, or whose assignment to an out parameter, was among the statements commented out earlier
	/// no longer compiles even though nothing in it is wrong. Rather than empty it, which would throw away everything
	/// that did survive, the missing assignments and return are added at the end.
	/// </remarks>
	/// <summary>
	/// A call to a base constructor, for a constructor that does not chain to one and cannot fall back to a
	/// parameterless base.
	/// </summary>
	/// <remarks>
	/// Recovery loses the chained call often enough that the alternative, throwing the constructor away, would take a
	/// lot of otherwise sound code with it. Which base constructor was originally called is not known, so the shortest
	/// one is called with default arguments; that is a guess, but it is contained to the one call.
	/// </remarks>
	private static Edit? FindBaseInitializerEdit(SyntaxNode node, CSharpCompilation? compilation)
	{
		if (compilation is null || node.FirstAncestorOrSelf<ConstructorDeclarationSyntax>() is not { Initializer: null } constructor)
		{
			return null;
		}

		SemanticModel model = compilation.GetSemanticModel(constructor.SyntaxTree);
		if (model.GetDeclaredSymbol(constructor)?.ContainingType.BaseType is not { } baseType)
		{
			return null;
		}

		IMethodSymbol? chosen = baseType.InstanceConstructors
			.Where(c => c.Parameters.Length > 0 && c.DeclaredAccessibility is not Accessibility.Private)
			.OrderBy(c => c.Parameters.Length)
			.FirstOrDefault();

		if (chosen is null)
		{
			return null;
		}

		string arguments = string.Join(", ", chosen.Parameters.Select(
			p => $"default({p.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)})"));

		return new Edit(new TextSpan(constructor.ParameterList.Span.End, 0), $" : base({arguments})");
	}

	/// <summary>
	/// Whether an error says the method is missing something rather than that it contains something wrong.
	/// </summary>
	private static bool IsMissingSomething(string id)
	{
		return id is "CS0161" or "CS0126"    //Does not return a value on every path
			or "CS0177" or "CS0269"          //Does not assign an out parameter on every path
			or "CS0171" or "CS0188";         //Does not assign every field of the struct being constructed
	}

	private static Edit? FindFixupEdit(SyntaxNode node, string id)
	{
		for (SyntaxNode? current = node; current is not null; current = current.Parent)
		{
			(BlockSyntax? body, TypeSyntax? returnType, ParameterListSyntax? parameters) = current switch
			{
				MethodDeclarationSyntax method => (method.Body, method.ReturnType, method.ParameterList),
				OperatorDeclarationSyntax @operator => (@operator.Body, @operator.ReturnType, @operator.ParameterList),
				ConversionOperatorDeclarationSyntax conversion => (conversion.Body, conversion.Type, conversion.ParameterList),
				ConstructorDeclarationSyntax constructor => (constructor.Body, null, constructor.ParameterList),
				AccessorDeclarationSyntax accessor => (accessor.Body, GetAccessorReturnType(accessor), null),
				LocalFunctionStatementSyntax function => (function.Body, function.ReturnType, function.ParameterList),
				_ => (null, null, null),
			};

			if (body is null)
			{
				continue;
			}

			//A return only has to be there when the end of the method is reached, but an assignment has to have
			//happened however the method leaves, so it goes at the top where every path passes through it.
			return id is "CS0161" or "CS0126"
				? ReturnsValue(returnType)
					? new Edit(new TextSpan(body.CloseBraceToken.SpanStart, 0), "return default;")
					: null
				: BuildPrologue(current, parameters) is { Length: > 0 } prologue
					? new Edit(new TextSpan(body.OpenBraceToken.Span.End, 0), prologue)
					: null;
		}

		return null;
	}

	/// <summary>
	/// The assignments a method has to make before it can leave: its out parameters, and for a struct constructor the
	/// fields of the struct being built.
	/// </summary>
	private static string BuildPrologue(SyntaxNode declaration, ParameterListSyntax? parameters)
	{
		StringBuilder builder = new();

		if (declaration is ConstructorDeclarationSyntax && declaration.Parent is StructDeclarationSyntax)
		{
			builder.Append(" this = default;");
		}

		foreach (ParameterSyntax parameter in parameters?.Parameters ?? default)
		{
			if (parameter.Modifiers.Any(SyntaxKind.OutKeyword))
			{
				builder.Append(' ').Append(parameter.Identifier.ValueText).Append(" = default;");
			}
		}

		return builder.ToString();
	}

	private static bool ReturnsValue(TypeSyntax? returnType)
	{
		return returnType is not null and not PredefinedTypeSyntax { Keyword.RawKind: (int)SyntaxKind.VoidKeyword };
	}

	/// <summary>
	/// What an accessor returns, which is the property's type for a getter and nothing for anything else.
	/// </summary>
	private static TypeSyntax? GetAccessorReturnType(AccessorDeclarationSyntax accessor)
	{
		return accessor.IsKind(SyntaxKind.GetAccessorDeclaration)
			? accessor.FirstAncestorOrSelf<BasePropertyDeclarationSyntax>()?.Type
			: null;
	}

	/// <summary>
	/// Comments out each span, keeping the text so it can still be read, and puts any replacement after it.
	/// </summary>
	private static string ApplyEdits(string text, List<Edit> edits)
	{
		StringBuilder builder = new(text.Length + edits.Count * 128);
		int position = 0;

		foreach ((TextSpan span, string? replacement) in edits)
		{
			if (span.Start < position)
			{
				continue;
			}

			builder.Append(text, position, span.Start - position);

			if (span.IsEmpty)
			{
				//An insertion, with nothing to comment out.
				builder.Append(replacement).Append('\n');
				position = span.End;
				continue;
			}

			builder.Append(Marker);

			//The first line of the span has already had its indentation written, so the commented copy of it needs
			//that indentation put back to stay lined up with what is around it.
			string indentation = GetIndentation(text, span.Start);

			foreach (string line in text.Substring(span.Start, span.Length).Split('\n'))
			{
				string trimmed = line.TrimEnd('\r');
				builder.Append('\n');

				//A later pass can cover something an earlier one already commented, and commenting it twice only
				//makes it harder to read.
				if (!trimmed.TrimStart().StartsWith("//", StringComparison.Ordinal))
				{
					builder.Append(indentation).Append("//");
					indentation = "";
				}
				builder.Append(trimmed);
			}

			if (replacement is not null)
			{
				builder.Append('\n').Append(replacement);
			}

			position = span.End;
		}

		builder.Append(text, position, text.Length - position);
		return builder.ToString();
	}

	/// <summary>
	/// The whitespace at the start of the line a position is on, or nothing when the position is not the first thing
	/// on its line.
	/// </summary>
	private static string GetIndentation(string text, int position)
	{
		int lineStart = text.LastIndexOf('\n', Math.Max(position - 1, 0)) + 1;
		string indentation = text[lineStart..position];
		return indentation.Length > 0 && indentation.All(char.IsWhiteSpace) ? indentation : "";
	}

	/// <summary>
	/// Compiles the files together and records where the errors are, which is the only way to find the statements that
	/// parse but cannot be compiled.
	/// </summary>
	/// <remarks>
	/// The references are the assemblies the code was compiled against originally, so the errors that come back are the
	/// ones the editor is going to report, rather than an approximation of them.
	/// </remarks>
	private static CSharpCompilation? FindCompilationProblems(List<SourceFile> files, List<MetadataReference> references)
	{
		if (references.Count == 0)
		{
			return null;
		}

		Dictionary<SyntaxTree, SourceFile> filesByTree = [];
		foreach (SourceFile file in files)
		{
			filesByTree[file.Root.SyntaxTree] = file;
		}

		CSharpCompilation compilation = CSharpCompilation.Create(
			"Decompiled",
			filesByTree.Keys,
			references,
			new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

		foreach (Diagnostic diagnostic in compilation.GetDiagnostics())
		{
			if (diagnostic.Severity == DiagnosticSeverity.Error
				&& diagnostic.Location.SourceTree is { } tree
				&& filesByTree.TryGetValue(tree, out SourceFile? file))
			{
				file.Positions.Add((diagnostic.Location.SourceSpan.Start, diagnostic.Id));
			}
		}

		return compilation;
	}

	/// <summary>
	/// The other assemblies of the game, read as the compiler wants them, plus the runtime's core libraries.
	/// </summary>
	/// <remarks>
	/// Each game assembly is read with the stream left open, because the manager hands out a cached stream that the
	/// rest of the export still needs, and the usual way of creating a reference from a stream closes it.
	/// </remarks>
	private static List<AssemblyMetadata> GetMetadata(AssemblyDefinition assembly, IAssemblyManager manager)
	{
		List<AssemblyMetadata> metadata = [];

		foreach (AssemblyDefinition other in manager.GetAssemblies())
		{
			if (ReferenceEquals(other, assembly) || IsCoreLibrary(other.Name?.ToString()))
			{
				//The assembly being compiled provides its own types from source, and the core libraries come from the
				//runtime below rather than from the game.
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

		foreach (string path in GetRuntimeCoreLibraryPaths())
		{
			try
			{
				metadata.Add(AssemblyMetadata.CreateFromFile(path));
			}
			catch (Exception)
			{
				//Not every file next to the runtime is a managed assembly.
			}
		}

		return metadata;
	}

	/// <summary>
	/// Whether an assembly is one of the core libraries, which are taken from the runtime instead of from the game.
	/// </summary>
	/// <remarks>
	/// A build's core libraries have been stripped down to the members the game itself used, so they say a member does
	/// not exist when it merely went unused. Checking against them would reject the recovered statements that call one
	/// of those members, which the editor compiles without complaint because it uses its own complete copy. This is the
	/// same set that method body recovery skips.
	/// </remarks>
	private static bool IsCoreLibrary(string? name)
	{
		return name is "mscorlib" or "netstandard" or "System"
			|| (name is not null && name.StartsWith("System.", StringComparison.Ordinal));
	}

	private static IEnumerable<string> GetRuntimeCoreLibraryPaths()
	{
		string? directory = Path.GetDirectoryName(typeof(object).Assembly.Location);
		return string.IsNullOrEmpty(directory)
			? []
			: Directory.EnumerateFiles(directory, "*.dll");
	}
}
