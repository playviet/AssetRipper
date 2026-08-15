using AsmResolver.DotNet;
using AssetRipper.Import.Logging;
using AssetRipper.Import.Structure.Assembly.Managers;
using AssetRipper.IO.Files;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using System.Collections.Immutable;
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
/// <para>
/// Some of what is rejected is not lost, only misspelled: the analysis knew what the native code did and wrote it in a
/// form the language has no syntax for, such as a null pointer written as the number zero. Those are rewritten into
/// what C# calls the same thing, so the statement is kept rather than commented out. Only shapes that say what they
/// mean on their own are rewritten, because a statement that compiles and is wrong is worse than one that is not there.
/// </para>
/// </remarks>
internal static partial class InvalidSourceRepair
{
	/// <summary>
	/// How many times the source may be compiled and repaired.
	/// </summary>
	/// <remarks>
	/// Commenting a statement out can expose another error, most often a local that is now never assigned, so this
	/// takes more than one pass to settle. It always settles, because every pass removes something and a method with
	/// nothing left in it compiles.
	/// <para>
	/// The loop stops as soon as a pass finds nothing to repair, so a higher cap costs nothing where the source
	/// settles sooner - it is paid only by the files that were being given up on. At eight, <b>124 methods</b> in
	/// this game were still being emptied for not settling, including the four largest holders of branches left in
	/// the recovery.
	/// </para>
	/// </remarks>
	private const int MaxAttempts = 24;

	private const string Marker = "//AssetRipper: commented out, this could not be kept as code.";


	public static void Apply(AssemblyDefinition assembly, IAssemblyManager manager, LanguageVersion languageVersion, string outputFolder, FileSystem fileSystem)
		=> Apply(assembly, manager, languageVersion, default, outputFolder, fileSystem);

	/// <inheritdoc cref="Apply(AssemblyDefinition, IAssemblyManager, LanguageVersion, string, FileSystem)"/>
	/// <param name="unityVersion">
	/// The version the game was built with, which decides which of Unity's version gates are open. Without it this
	/// reads a different file from the one the editor will compile: code behind a gate is invisible here and present
	/// there, so a method that still yields can be given a return, and a statement that does not compile can be left.
	/// </param>
	public static void Apply(AssemblyDefinition assembly, IAssemblyManager manager, LanguageVersion languageVersion, UnityVersion unityVersion, string outputFolder, FileSystem fileSystem)
	{
		List<AssemblyMetadata> metadata = GetMetadata(assembly, manager);
		try
		{
			Apply(UnityEditorReferences.Prefer(metadata, unityVersion), languageVersion, unityVersion, outputFolder, fileSystem);
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
		=> Apply(references, languageVersion, default, outputFolder, fileSystem);

	/// <inheritdoc cref="Apply(AssemblyDefinition, IAssemblyManager, LanguageVersion, UnityVersion, string, FileSystem)"/>
	public static void Apply(List<MetadataReference> references, LanguageVersion languageVersion, UnityVersion unityVersion, string outputFolder, FileSystem fileSystem)
	{
		//The source was written for this version, and so is the editor going to read it. Checking it against a newer
		//one accepts what the editor will not: a struct whose fields go unassigned is an error before C# 11 and quietly
		//defaulted after it.
		CSharpParseOptions parseOptions = OriginalSourceSubstitution.GetParseOptions(unityVersion).WithLanguageVersion(languageVersion);

		SilenceTraces(outputFolder, fileSystem, parseOptions);

		int commented = 0;
		int rewritten = 0;
		int widened = 0;
		int emptied = 0;
		HashSet<string> repairedFiles = [];

		//Before the loop, not after it. C# forbids `unsafe` on an iterator outright, and the compiler says so
		//about the *declaration* - so every statement in the body is then unreachable code in a method that
		//does not compile, and the loop comments the lot. `AllDesignConfig.CRPullAll` and
		//`BoardController.PowerUp_Shuffle` are 46 statements between them, both faithful reconstructions,
		//both lost to a modifier over a body with nothing unsafe in it.
		//
		//This was written to run afterwards because the diagnostic was thought to be `CS1629`, which the loop
		//was measured never to see. It does not: at C# 9 Roslyn reports **`CS8773`** - "ref and unsafe in
		//async and iterator methods is not available in C# 9.0" - and that one the loop sees on its first
		//compilation. Nothing needed a compiler either way: the modifier over a body with no unsafe syntax
		//says nothing, whenever it is removed.
		RemoveUnsafeFromIterators(outputFolder, fileSystem, parseOptions, repairedFiles);

		for (int attempt = 0; attempt < MaxAttempts; attempt++)
		{
			List<SourceFile> files = Parse(outputFolder, fileSystem, parseOptions);
			if (files.Count == 0)
			{
				break;
			}

			CSharpCompilation? compilation = FindCompilationProblems(files, references);

			//A member of this same assembly that the language will not let another type reach is not a statement
			//to give up on, it is a modifier to widen. Done before anything is commented, and the loop then
			//compiles again with the wider member, so the statements that wanted it are never seen as broken.
			int widenedHere = WidenInaccessibleMembers(files, compilation, fileSystem, repairedFiles);

			if (widenedHere > 0)
			{
				widened += widenedHere;
				continue;
			}

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

				int rewrittenHere = edits.Count(edit => edit.Rewritten);
				rewritten += rewrittenHere;
				commented += edits.Count - rewrittenHere;
				repaired += edits.Count;
				emptied += emptiedHere;
			}

			if (repaired == 0)
			{
				break;
			}
		}

		//And again afterwards, because emptying a body that would not settle can leave a bare `yield` behind
		//in a method that was not an iterator when the sweep above ran.
		RemoveUnsafeFromIterators(outputFolder, fileSystem, parseOptions, repairedFiles);

		if (widened > 0)
		{
			Logger.Info(LogCategory.Export, $"Widened {widened} members another type in the same assembly reads but could not reach");
		}

		if (rewritten > 0)
		{
			Logger.Info(LogCategory.Export, $"Rewrote {rewritten} statements that recovery had written in a form the language has no syntax for");
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
		"Invalid instruction: ",
		"Unknown operand: ",
		"Constructor not found for: ",
	];

	/// <summary>
	/// Neutralises the calls recovery leaves behind to say what it could not translate.
	/// </summary>
	/// <remarks>
	/// They are written as calls to <see cref="Console.WriteLine(string)"/>, which compiles but also runs: a recovered
	/// method the editor happens to call writes one on every pass, and a recovered loop fills the log with them, which
	/// on a large game means gigabytes. The message is worth keeping, so the call becomes a discard of the same string:
	/// it still reads, it runs nothing, and unlike commenting the statement out it is valid wherever a statement is,
	/// including as the unbraced body of an if.
	/// </remarks>
	private static void SilenceTraces(string outputFolder, FileSystem fileSystem, CSharpParseOptions parseOptions)
	{
		int silenced = 0;

		foreach (SourceFile file in Parse(outputFolder, fileSystem, parseOptions))
		{
			List<(TextSpan Span, string Text)> edits = [];

			foreach (SyntaxNode node in file.Root.DescendantNodes())
			{
				//The call itself is replaced rather than the statement around it, because it is not always a
				//statement of its own: one can sit in the increment clause of a for, where it runs every iteration.
				if (node is InvocationExpressionSyntax invocation
					&& IsTrace(invocation)
					&& invocation.ArgumentList.Arguments is [{ Expression: { } message }])
				{
					edits.Add((invocation.Span, $"_ = {message}"));
				}

				//A zero native integer, which the decompiler writes as a null cast to one. The editor rejects that -
				//there is no conversion from null to a native integer - while the compiler used to check the source
				//here accepts it, so this is not something the compile loop below would ever find. Both spellings
				//mean the same value, and only one of them builds.
				if (node is CastExpressionSyntax cast && NativeIntegerZero(cast.Type) is { } zero && IsNullLiteral(cast.Expression))
				{
					//Which zero to write depends on what it is being compared with, and that is not visible here.
					//`default` is: it takes the type of whatever it meets, so it is right against a native integer
					//and against an ordinary one alike. Everywhere else the framework type is the clearer thing to
					//read, and there is only one type it could be.
					edits.Add((cast.Span, IsComparedWithSomething(cast) ? "default" : zero));
				}
			}

			if (edits.Count == 0)
			{
				continue;
			}

			StringBuilder builder = new(file.Text.Length);
			int position = 0;
			foreach ((TextSpan span, string text) in edits)
			{
				builder.Append(file.Text, position, span.Start - position).Append(text);
				position = span.End;
			}
			builder.Append(file.Text, position, file.Text.Length - position);

			fileSystem.File.WriteAllText(file.Path, builder.ToString());
			silenced += edits.Count;
		}

		if (silenced > 0)
		{
			Logger.Info(LogCategory.Export, $"Silenced {silenced} messages that recovery would otherwise have written at runtime");
		}
	}

	/// <summary>
	/// Whether the expression is one side of an equality test, where the other side decides what type a zero has.
	/// </summary>
	private static bool IsComparedWithSomething(ExpressionSyntax expression)
	{
		SyntaxNode? node = expression;

		while (node?.Parent is ParenthesizedExpressionSyntax or CheckedExpressionSyntax or CastExpressionSyntax)
		{
			node = node.Parent;
		}

		return node?.Parent is BinaryExpressionSyntax binary
			&& (binary.IsKind(SyntaxKind.EqualsExpression) || binary.IsKind(SyntaxKind.NotEqualsExpression));
	}

	/// <summary>
	/// How to write a zero of the type, if the type is a native integer. The keyword spelling has no members of
	/// its own in every language version this runs at, so the answer is always given as the framework type.
	/// </summary>
	private static string? NativeIntegerZero(TypeSyntax type)
	{
		string name = type switch
		{
			IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
			QualifiedNameSyntax qualified => qualified.Right.Identifier.ValueText,
			_ => "",
		};

		return name switch
		{
			"nint" or "IntPtr" => "global::System.IntPtr.Zero",
			"nuint" or "UIntPtr" => "global::System.UIntPtr.Zero",
			_ => null,
		};
	}

	private static bool IsNullLiteral(ExpressionSyntax expression)
	{
		while (true)
		{
			switch (expression)
			{
				case ParenthesizedExpressionSyntax parenthesized:
					expression = parenthesized.Expression;
					continue;
				case CheckedExpressionSyntax @checked:
					expression = @checked.Expression;
					continue;
				default:
					return expression.IsKind(SyntaxKind.NullLiteralExpression);
			}
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
	/// A stretch of source to comment out or replace, and what to put in its place.
	/// </summary>
	/// <param name="Replacement">
	/// What to leave behind: for a span being commented out, whatever the surrounding code still needs there, and for a
	/// span being replaced, the source that takes its place.
	/// </param>
	/// <param name="Rewritten">
	/// Whether the replacement is the same statement said in a way that compiles, rather than something to fall back on
	/// once the statement is gone. Such a span is replaced outright instead of being commented out.
	/// </param>
	private readonly record struct Edit(TextSpan Span, string? Replacement, bool Rewritten = false);

	/// <summary>
	/// The edits to make to one file: the statement around each problem, or the whole body of a method that cannot be
	/// narrowed down any further.
	/// </summary>
	private static List<Edit> FindEdits(SourceFile file, CSharpCompilation? compilation, bool lastAttempt, out int emptied)
	{
		emptied = 0;
		List<Edit> edits = [];
		HashSet<TextSpan> seen = [];
		List<(int Position, string Id)> problems = [.. GetProblemPositions(file)];

		foreach ((int position, string id) in problems)
		{
			SyntaxNode? node = file.Root.FindToken(position, true).Parent;
			if (node is null)
			{
				continue;
			}

			//A method that no longer returns on every path, or no longer assigns an out parameter, is missing
			//something rather than containing something wrong. Adding it back is cheaper than emptying the method.
			//A statement that only needs saying differently is rewritten in place, before anything is given up on.
			Edit? edit = id == "CS1729"
				? FindBaseInitializerEdit(node, compilation)
				: IsMissingSomething(id)
					? FindFixupEdit(node, id)
					: (lastAttempt ? null : FindStatement(node)) is { } statement
						? FindRewriteEdit(file, statement, problems, compilation) ?? CommentEdit(file, statement)
						: FindBodyEdit(node);

			if (edit is null)
			{
				continue;
			}

			//Only an edit that takes a whole body away. What `FindFixupEdit` returns is an **insertion** - a
			//return before the closing brace, an assignment after the opening one - and the method keeps
			//everything it had; counting those made the log report 121 emptied methods where one was.
			if (!edit.Value.Rewritten && !edit.Value.Span.IsEmpty && (lastAttempt || FindStatement(node) is null))
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
	/// <summary>What is written where a method that could not be repaired used to be.</summary>
	internal const string EmptiedNote = "AssetRipper: emptied, this method could not be repaired statement by statement.";

	private static Edit? FindBodyEdit(SyntaxNode node)
	{
		for (SyntaxNode? current = node; current is not null; current = current.Parent)
		{
			//A field's initialiser is the same case one step further out: there is no statement to comment, and
			//leaving it be fails the whole assembly rather than one method. It reaches here now that valid IL
			//lets the decompiler fold a static constructor into initialisers - the assignment that used to be
			//a commentable statement is a field declaration, and one of them referred to a private nested type
			//in a plugin, which no widening can reach because the declaration is not in the exported source.
			if (current is EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax } initialiser
				&& current.FirstAncestorOrSelf<BaseFieldDeclarationSyntax>() is not null)
			{
				return new Edit(initialiser.Value.Span, $"default /*{EmptiedNote}*/", Rewritten: true);
			}

			//A member written with an arrow has no statement anywhere in it, so there is nothing to comment out and
			//the loop below would walk past it. What it says has to be replaced instead, since deleting the arrow
			//would leave a member with no body at all.
			if (current is ArrowExpressionClauseSyntax arrow)
			{
				return new Edit(arrow.Expression.Span,
					(ReturnsValue(GetArrowReturnType(arrow)) ? "default" : "_ = 0") + $" /*{EmptiedNote}*/", Rewritten: true);
			}

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

			//Where the whole body goes and something has to stand in its place, the statements are replaced
			//rather than commented, so nothing in the file says the method used to do anything. It then reads
			//as a method that really was empty - and every measure taken of the export believes that, which is
			//how a hundred and sixty emptied methods were being counted as whole.
			string? missing = MissingReturn(body, returnType);
			return new Edit(span, missing is null ? null : $"//{EmptiedNote}\r\n\t\t\t{missing}");
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
	/// A statement said the way C# says it, for the idioms recovery writes in a form the language has no syntax for.
	/// </summary>
	/// <remarks>
	/// These are shapes where the native code left no doubt about what was meant and only the writing of it is wrong: a
	/// null pointer written as the number zero, a test of one written as a chain of casts, a constructor called by the
	/// name it has in metadata. Each is checked against the semantic model rather than read off the text, because what
	/// the shape means depends on the types in it, and any part of it that cannot be resolved leaves the statement to be
	/// commented out as before.
	/// </remarks>
	private static Edit? FindRewriteEdit(SourceFile file, StatementSyntax statement, List<(int Position, string Id)> problems, CSharpCompilation? compilation)
	{
		if (compilation is null)
		{
			//Without the references there is no semantic model, and none of this can be decided from the syntax alone.
			return null;
		}

		SemanticModel model = compilation.GetSemanticModel(statement.SyntaxTree);
		List<(TextSpan Span, string Text)> rewrites = [];
		int end = statement.SpanStart;

		foreach (SyntaxNode node in statement.DescendantNodesAndSelf())
		{
			if (node.SpanStart < end)
			{
				//Inside something already rewritten, which was rewritten from the text as it stands.
				continue;
			}

			string? text = RewriteNullPointer(node, model)
				?? RewriteNativeBooleanTest(node, model, compilation)
				?? RewriteConstructorCall(node, model)
				?? RewriteInaccessibleMember(node, model)
				?? RewriteEventBackingField(node, model)
				?? RewriteZeroedStruct(node, model)
				?? RewriteAmbiguousType(node, model)
				?? RewriteEmptyArray(node, model)
				?? RewriteQuaternionInternal(node, model)
				?? RewritePrimitiveStorage(node, model)
				?? RewriteNullValueType(node, model)
				?? RewriteNarrowingAssignment(node, model)
				?? RewriteStructPropertyMember(node, model)
				?? RewriteBackingField(node, model);

			if (text is not null)
			{
				rewrites.Add((node.Span, text));
				end = node.Span.End;
			}
		}

		if (rewrites.Count == 0)
		{
			return null;
		}

		//A rewrite is only worth making when it answers everything the compiler objected to in the statement. If
		//something else in there is broken as well the statement is going to be commented out regardless, and it is
		//more use commented out as recovery wrote it than as half of it rewritten.
		foreach ((int position, _) in problems)
		{
			if (statement.Span.Contains(position)
				&& file.Root.FindToken(position, true).Parent is { } node
				&& FindStatement(node)?.Span == statement.Span
				&& !rewrites.Exists(rewrite => rewrite.Span.Contains(position)))
			{
				return null;
			}
		}

		StringBuilder builder = new();
		int written = statement.SpanStart;
		foreach ((TextSpan span, string text) in rewrites)
		{
			builder.Append(file.Text, written, span.Start - written).Append(text);
			written = span.End;
		}
		builder.Append(file.Text, written, statement.Span.End - written);

		return new Edit(statement.Span, builder.ToString(), true);
	}

	/// <summary>
	/// A null reference written as the number zero.
	/// </summary>
	/// <remarks>
	/// Native code has no null: a null reference is a zero in a register, and recovery writes one back as that zero cast
	/// to whatever the register was holding. C# will not cast a number to a reference type, and what the cast says is a
	/// null of that type, so it becomes one. The type is kept, as <c>default(T)</c>, wherever it might still be needed
	/// to tell overloads apart; where the statement is an assignment it is the assignment that gives the type, and the
	/// plain <c>null</c> the original would have had is enough.
	/// <para>
	/// Not where the null is the thing being called, though. The exported project is meant to run as the game did, and a
	/// statement that was commented out does nothing, which is wrong but harmless. Calling a member on a null that was
	/// written back out throws where the game did not, and a crash in recovered code is far harder to trace than a
	/// statement that is visibly not there.
	/// </para>
	/// </remarks>
	private static string? RewriteNullPointer(SyntaxNode node, SemanticModel model)
	{
		if (node is not CastExpressionSyntax cast
			|| !IsZero(cast.Expression)
			|| cast.Type.DescendantNodesAndSelf().OfType<OmittedTypeArgumentSyntax>().Any()
			|| IsBeingCalled(cast))
		{
			return null;
		}

		//A cast of zero the language does accept means what it says: a boxed number, or a type that declares a
		//conversion from one. Only the cast it rejects can be read as a pointer that was null. Value types are left
		//alone as well, because a zeroed register is not the same thing as a zeroed struct.
		if (model.GetTypeInfo(cast.Type).Type is not { } type
			|| type.TypeKind is not (TypeKind.Class or TypeKind.Interface or TypeKind.Delegate or TypeKind.Array)
			|| model.ClassifyConversion(cast.Expression, type).Exists)
		{
			return null;
		}

		return node.Parent is AssignmentExpressionSyntax assignment
			&& assignment.IsKind(SyntaxKind.SimpleAssignmentExpression)
			&& assignment.Right == node
			&& model.GetTypeInfo(assignment.Left).Type is { IsReferenceType: true }
			? "null"
			: $"default({cast.Type})";
	}

	/// <summary>
	/// A type named by a name two namespaces both use.
	/// </summary>
	/// <remarks>
	/// A recovered file gets a <c>using</c> for every namespace anything in it mentions, and two of them
	/// declaring the same name makes every use of it ambiguous - <c>Debug</c> is <c>UnityEngine.Debug</c> and
	/// <c>System.Diagnostics.Debug</c>, so <c>Debug.Log(message)</c> does not compile and neither does the
	/// statement around it. 46 of those here, and the recovery had them all right.
	/// <para>
	/// Which one was meant is not a guess: the member being reached says so. <c>Log</c> is declared by one of
	/// the two and not the other, so where exactly one candidate has it, that is the type, written out in
	/// full. Where both have it, or neither, nothing is written - an ambiguity this cannot settle is left as
	/// the ambiguity it is.
	/// </para>
	/// </remarks>
	private static string? RewriteAmbiguousType(SyntaxNode node, SemanticModel model)
	{
		if (node is not IdentifierNameSyntax identifier
			|| identifier.Parent is not MemberAccessExpressionSyntax access
			|| access.Expression != identifier)
		{
			return null;
		}

		SymbolInfo info = model.GetSymbolInfo(identifier);

		if (info.CandidateReason is not CandidateReason.Ambiguous || info.CandidateSymbols.Length < 2)
		{
			return null;
		}

		string wanted = access.Name.Identifier.ValueText;
		INamedTypeSymbol? only = null;

		foreach (ISymbol candidate in info.CandidateSymbols)
		{
			if (candidate is not INamedTypeSymbol type || type.GetMembers(wanted).Length == 0)
			{
				continue;
			}

			if (only is not null)
			{
				//Both of them have it, so the member says nothing about which was meant.
				return null;
			}

			only = type;
		}

		return only?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
	}

	/// <summary>
	/// A struct written as the zero the register holding it was cleared to.
	/// </summary>
	/// <remarks>
	/// <c>Vector3.zero</c> is one instruction - the register is cleared - so what recovery has to write down is a
	/// zero where a <c>Vector3</c> belongs, and <c>(Vector3)0</c> is not C#. <b>40 of these in this game</b>, and
	/// <c>(Vector3)0</c> twenty of them.
	/// <para>
	/// The rule above declines to read a zero as a value type, on the grounds that a zeroed register is not the same
	/// thing as a zeroed struct - true where the struct is wider than the register that was cleared, and the reason
	/// this is a separate rule rather than one more type kind there. What decides it is that the alternative is not a
	/// more careful answer, it is no answer: the statement is commented out and everything reading the value goes
	/// with it. <c>default(T)</c> is what a cleared register says, and where the struct really was only half cleared
	/// the recovery had already lost the other half.
	/// </para>
	/// </remarks>
	private static string? RewriteZeroedStruct(SyntaxNode node, SemanticModel model)
	{
		if (node is not CastExpressionSyntax cast
			|| !IsZero(cast.Expression)
			|| cast.Type.DescendantNodesAndSelf().OfType<OmittedTypeArgumentSyntax>().Any()
			|| IsBeingCalled(cast))
		{
			return null;
		}

		//Only a struct the language will not let a zero become. An enum takes one, and so does every primitive, so
		//a cast it accepts means what it says and is left exactly as recovery wrote it.
		if (model.GetTypeInfo(cast.Type).Type is not { TypeKind: TypeKind.Struct } type
			|| type.SpecialType is not SpecialType.None
			|| type is INamedTypeSymbol { EnumUnderlyingType: not null }
			|| model.ClassifyConversion(cast.Expression, type).Exists)
		{
			return null;
		}

		return $"default({cast.Type})";
	}

	/// <summary>
	/// A test of whether a reference is null, written as the casts a native boolean goes through.
	/// </summary>
	/// <remarks>
	/// Il2Cpp returns a boolean in the low byte of a register, so recovery writes a test of one as
	/// <c>(byte)(int)x != 0</c>. Where x is a reference there is no such cast in C#, and what the native code was
	/// testing is whether the pointer was null. For a <c>UnityEngine.Object</c> that is also the right way to say it:
	/// the class overloads equality to ask the engine whether the object is still alive, which is the very call this
	/// idiom was written in place of.
	/// </remarks>
	private static string? RewriteNativeBooleanTest(SyntaxNode node, SemanticModel model, CSharpCompilation compilation)
	{
		if (node is not BinaryExpressionSyntax binary
			|| !binary.IsKind(SyntaxKind.NotEqualsExpression) && !binary.IsKind(SyntaxKind.EqualsExpression)
			|| !IsZero(binary.Right)
			|| binary.Left is not CastExpressionSyntax { Type: PredefinedTypeSyntax { Keyword.RawKind: (int)SyntaxKind.ByteKeyword } } outer
			|| outer.Expression is not CastExpressionSyntax { Type: PredefinedTypeSyntax { Keyword.RawKind: (int)SyntaxKind.IntKeyword } } inner)
		{
			return null;
		}

		//A value that does convert to a number is being tested as a number, whatever else it is, and the casts are
		//there to narrow it rather than to stand in for a null check.
		if (model.GetTypeInfo(inner.Expression).Type is not { IsReferenceType: true } operand
			|| operand.TypeKind is TypeKind.Error or TypeKind.Dynamic
			|| model.ClassifyConversion(inner.Expression, compilation.GetSpecialType(SpecialType.System_Int32)).Exists)
		{
			return null;
		}

		return $"{inner.Expression} {binary.OperatorToken} null";
	}

	/// <summary>
	/// An event's backing field, reached by the event's own name.
	/// </summary>
	/// <remarks>
	/// An event declared with its own <c>add</c> and <c>remove</c> has no backing field the language will let anyone
	/// name: <c>this.OnClicked</c> is the event, and an event may only appear on the left of <c>+=</c> or <c>-=</c>.
	/// Recovery does not write the event, it writes a read of the field behind it - and the field is right there in
	/// the same class, under the name the decompiler moved it to so that the two do not clash:
	/// <code>
	/// private Action m_OnClicked;
	/// public event Action OnClicked { add { … } remove { … } }
	/// </code>
	/// so <c>if (this.OnClicked != null) this.OnClicked();</c> is `m_OnClicked` twice, and says exactly what the
	/// binary does. The field has to be in the event's own type and of the event's own type, or this is guessing.
	/// </remarks>
	private static string? RewriteEventBackingField(SyntaxNode node, SemanticModel model)
	{
		if (node is not MemberAccessExpressionSyntax access)
		{
			return null;
		}

		SymbolInfo info = model.GetSymbolInfo(access);

		if ((info.Symbol ?? info.CandidateSymbols.FirstOrDefault()) is not IEventSymbol raised
			|| raised.ContainingType is not { } declaring)
		{
			return null;
		}

		foreach (ISymbol member in declaring.GetMembers())
		{
			if (member is IFieldSymbol field
				&& field.IsStatic == raised.IsStatic
				&& SymbolEqualityComparer.Default.Equals(field.Type, raised.Type)
				&& (field.Name == "m_" + raised.Name || field.Name == "_" + raised.Name
					|| field.Name == $"<{raised.Name}>k__BackingField"))
			{
				return $"{access.Expression}.{field.Name}";
			}
		}

		return null;
	}

	/// <summary>
	/// A constructor called by the name it has in metadata.
	/// </summary>
	/// <remarks>
	/// Recovery turns the instruction that makes an object into a call to <c>.ctor</c> on the variable the object was
	/// about to be stored in, with the name escaped to <c>_002Ector</c> so that it is an identifier. As C# that is a
	/// call to a method which does not exist; what it says is the construction and the store that the language writes
	/// as one assignment. A delegate is made the same way, from a target and a function pointer taken with
	/// <c>__ldftn</c>, and is written back as the method group C# takes a delegate over.
	/// </remarks>
	private static string? RewriteConstructorCall(SyntaxNode node, SemanticModel model)
	{
		if (node is not InvocationExpressionSyntax { Parent: ExpressionStatementSyntax } invocation
			|| invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.ValueText: "_002Ector" } access)
		{
			return null;
		}

		//What is being constructed has to be something the assignment can be written back to, and its type is what says
		//which type to construct. A cast receiver gives neither: recovery casts where it lost the type, so the type in
		//the cast is its guess rather than something it knew. An untyped one says nothing at all.
		if (access.Expression is not (IdentifierNameSyntax or MemberAccessExpressionSyntax)
			|| model.GetSymbolInfo(access.Expression).Symbol is not (ILocalSymbol or IFieldSymbol or IParameterSymbol)
			|| model.GetTypeInfo(access.Expression).Type is not INamedTypeSymbol type
			|| type.TypeKind is not (TypeKind.Class or TypeKind.Struct or TypeKind.Delegate)
			|| type.SpecialType is SpecialType.System_Object
			|| type.IsAbstract
			|| !IsResolved(type))
		{
			return null;
		}

		string? construction = type.TypeKind is TypeKind.Delegate
			? BuildDelegateCreation(invocation, type, model)
			: BuildObjectCreation(invocation, type, model);

		return construction is null ? null : $"{access.Expression} = {construction}";
	}

	/// <summary>
	/// The method group a delegate was built over, from the target and function pointer it was built from.
	/// </summary>
	private static string? BuildDelegateCreation(InvocationExpressionSyntax invocation, INamedTypeSymbol type, SemanticModel model)
	{
		if (invocation.ArgumentList.Arguments is not [{ Expression: { } target }, { Expression: { } pointer }]
			|| type.DelegateInvokeMethod is not { } signature)
		{
			return null;
		}

		//The function pointer is the only place the method's name survived, and it is written there qualified by the
		//type that declares it.
		if (Unwrap(pointer) is not InvocationExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.ValueText: "__ldftn" } } taken
			|| taken.ArgumentList.Arguments is not [{ Expression: MemberAccessExpressionSyntax method }]
			|| model.GetSymbolInfo(method.Expression).Symbol is not INamedTypeSymbol declaring)
		{
			return null;
		}

		//One method of that name and nothing else of it, so that the delegate cannot come out over a different overload
		//than the one the pointer was taken from, and a signature that is the delegate's own, so that it cannot come out
		//over a method the delegate would have to convert to reach.
		if (declaring.GetMembers(method.Name.Identifier.ValueText) is not [IMethodSymbol resolved]
			|| !model.IsAccessible(invocation.SpanStart, resolved)
			|| !HasSignature(resolved, signature))
		{
			return null;
		}

		if (resolved.IsStatic)
		{
			//A delegate over a static method is built with no target, and anything else in that argument is something
			//this does not understand.
			return IsNull(Unwrap(target)) ? $"new {Display(type)}({method})" : null;
		}

		//The instance the method is taken from has to be the one the target argument holds, or the delegate would come
		//out over a different object than the native code built it over.
		ExpressionSyntax instance = Unwrap(target);
		return model.GetTypeInfo(instance).Type is { } held && Inherits(held, declaring)
			? $"new {Display(type)}({instance}.{method.Name})"
			: null;
	}

	/// <summary>
	/// The construction of an object, from the arguments the constructor was called with.
	/// </summary>
	private static string? BuildObjectCreation(InvocationExpressionSyntax invocation, INamedTypeSymbol type, SemanticModel model)
	{
		SeparatedSyntaxList<ArgumentSyntax> arguments = invocation.ArgumentList.Arguments;

		//Named arguments and arguments passed by reference would each have to be matched to a parameter before it is
		//known which is which, and recovery has no reason to write either.
		if (arguments.Any(argument => argument.NameColon is not null || !argument.RefKindKeyword.IsKind(SyntaxKind.None)))
		{
			return null;
		}

		//Exactly one constructor may be able to take these arguments. Recovery loses argument types as readily as it
		//loses anything else, and a call that two constructors could take is a call whose meaning would be a guess.
		IMethodSymbol? chosen = null;
		foreach (IMethodSymbol candidate in type.InstanceConstructors)
		{
			if (candidate.Parameters.Length != arguments.Count
				|| !model.IsAccessible(invocation.SpanStart, candidate)
				|| !Takes(candidate, arguments, model))
			{
				continue;
			}

			if (chosen is not null)
			{
				return null;
			}

			chosen = candidate;
		}

		return chosen is null ? null : $"new {Display(type)}({arguments})";
	}

	/// <summary>
	/// Whether every argument would reach its parameter on its own, which is what the call has to do to be the call
	/// recovery meant.
	/// </summary>
	private static bool Takes(IMethodSymbol candidate, SeparatedSyntaxList<ArgumentSyntax> arguments, SemanticModel model)
	{
		for (int i = 0; i < arguments.Count; i++)
		{
			Conversion conversion = model.ClassifyConversion(arguments[i].Expression, candidate.Parameters[i].Type);
			if (!conversion.Exists || !conversion.IsImplicit)
			{
				return false;
			}
		}

		return true;
	}

	/// <summary>
	/// Whether a method could be reached through a delegate of this signature without anything being converted, which
	/// is the only case where the method the native code named is certainly the method C# would find.
	/// </summary>
	private static bool HasSignature(IMethodSymbol method, IMethodSymbol signature)
	{
		if (method.MethodKind is not MethodKind.Ordinary
			|| method.Parameters.Length != signature.Parameters.Length
			|| method.RefKind != signature.RefKind
			|| !SymbolEqualityComparer.Default.Equals(method.ReturnType, signature.ReturnType))
		{
			return false;
		}

		for (int i = 0; i < method.Parameters.Length; i++)
		{
			if (method.Parameters[i].RefKind != signature.Parameters[i].RefKind
				|| !SymbolEqualityComparer.Default.Equals(method.Parameters[i].Type, signature.Parameters[i].Type))
			{
				return false;
			}
		}

		return true;
	}

	/// <summary>
	/// Whether a type, and everything it is made of, is one the compilation knows about.
	/// </summary>
	/// <remarks>
	/// Recovery writes a placeholder wherever it lost a type argument, and a type named after one cannot be written back
	/// out: the name would resolve to nothing.
	/// </remarks>
	private static bool IsResolved(ITypeSymbol type)
	{
		return type.TypeKind is not TypeKind.Error
			&& (type is not INamedTypeSymbol named || named.TypeArguments.All(IsResolved))
			&& (type is not IArrayTypeSymbol array || IsResolved(array.ElementType));
	}

	private static bool Inherits(ITypeSymbol type, ITypeSymbol other)
	{
		for (ITypeSymbol? current = type; current is not null; current = current.BaseType)
		{
			if (SymbolEqualityComparer.Default.Equals(current, other))
			{
				return true;
			}
		}

		return false;
	}

	/// <summary>
	/// Whether an expression is the thing a member is being taken from, rather than a value being passed around.
	/// </summary>
	private static bool IsBeingCalled(ExpressionSyntax expression)
	{
		SyntaxNode current = expression;
		while (current.Parent is ParenthesizedExpressionSyntax parenthesized)
		{
			current = parenthesized;
		}

		return current.Parent switch
		{
			MemberAccessExpressionSyntax access => access.Expression == current,
			ConditionalAccessExpressionSyntax conditional => conditional.Expression == current,
			ElementAccessExpressionSyntax element => element.Expression == current,
			InvocationExpressionSyntax invocation => invocation.Expression == current,
			_ => false,
		};
	}

	/// <summary>
	/// What is left of an expression once the casts recovery wrapped it in are taken off.
	/// </summary>
	private static ExpressionSyntax Unwrap(ExpressionSyntax expression)
	{
		while (true)
		{
			switch (expression)
			{
				case CastExpressionSyntax cast:
					expression = cast.Expression;
					break;
				case ParenthesizedExpressionSyntax parenthesized:
					expression = parenthesized.Expression;
					break;
				default:
					return expression;
			}
		}
	}

	private static bool IsNull(ExpressionSyntax expression)
	{
		return expression.IsKind(SyntaxKind.NullLiteralExpression) || IsZero(expression);
	}

	private static bool IsZero(ExpressionSyntax expression)
	{
		return expression is LiteralExpressionSyntax literal
			&& literal.IsKind(SyntaxKind.NumericLiteralExpression)
			&& literal.Token.Value is 0 or 0L or 0u or 0uL;
	}

	private static string Display(ITypeSymbol type)
	{
		return type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
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
				? MissingReturn(body, returnType) is { } missing
					? new Edit(new TextSpan(body.CloseBraceToken.SpanStart, 0), missing)
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
	/// What has to be put back where a method no longer returns on every path. An iterator does not return a
	/// value at all - the values it produces come out of its yields - so returning one there does not compile,
	/// and what it needs is the statement that ends the iteration.
	/// </summary>
	private static string? MissingReturn(BlockSyntax body, TypeSyntax? returnType)
	{
		if (body.DescendantNodes().OfType<YieldStatementSyntax>().Any())
		{
			return "yield break;";
		}

		return ReturnsValue(returnType) ? "return default;" : null;
	}

	/// <summary>
	/// What the member an arrow belongs to returns. A property or an indexer written with an arrow is a getter, so
	/// the member's own type is what the expression has to produce.
	/// </summary>
	private static TypeSyntax? GetArrowReturnType(ArrowExpressionClauseSyntax arrow)
	{
		return arrow.Parent switch
		{
			MethodDeclarationSyntax method => method.ReturnType,
			LocalFunctionStatementSyntax function => function.ReturnType,
			OperatorDeclarationSyntax @operator => @operator.ReturnType,
			ConversionOperatorDeclarationSyntax conversion => conversion.Type,
			BasePropertyDeclarationSyntax property => property.Type,
			AccessorDeclarationSyntax accessor => GetAccessorReturnType(accessor),
			_ => null,
		};
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
	/// <summary>
	/// Widens the members of this assembly that another type in it reads and the language will not let it reach.
	/// </summary>
	/// <remarks>
	/// <para>
	/// il2cpp inlines an accessor into its callers, so what arrives here is a read of a <c>private</c> field from
	/// another type - true to the binary, and not something C# will compile. Where the field has a property to be
	/// said by, recovery says it that way; <b>106 statements</b> are left where it has none, and each takes its
	/// statement and everything that reads the value with it.
	/// </para>
	/// <para>
	/// The member is declared in this very export, so the answer is to write it wider rather than to give up on the
	/// statement: <c>private</c> becomes <c>internal</c>, <c>protected</c> becomes <c>protected internal</c>, and a
	/// member with no modifier at all is given one. Nothing outside the assembly can see any difference, and nothing
	/// inside it could have depended on the narrower form - a decompiled project is read and compiled, not published
	/// against.
	/// </para>
	/// <para>
	/// Only members declared in source, so a private member of the framework is still left alone, and only where the
	/// modifier is really the obstacle: an accessibility that is already wide enough means the compiler objected to
	/// something else, and widening would be pretending to fix it.
	/// </para>
	/// </remarks>
	/// <summary>
	/// Write every error the first compilation reports to the file named by <c>REPAIR_WHY</c>, id and message.
	/// </summary>
	/// <remarks>
	/// Off unless the variable is set, and it costs one pass over diagnostics that are already computed. The
	/// message matters as much as the id: <c>CS0030</c> alone says only "invalid cast" for 800 statements,
	/// while the message names both types and splits them into families that have separate answers.
	/// </remarks>
	private static void DumpDiagnostics(CSharpCompilation compilation, Dictionary<SyntaxTree, SourceFile> byTree)
	{
		if (Environment.GetEnvironmentVariable("REPAIR_WHY") is not { Length: > 0 } path)
		{
			return;
		}

		StringBuilder text = new();
		foreach (Diagnostic diagnostic in compilation.GetDiagnostics())
		{
			if (diagnostic.Severity == DiagnosticSeverity.Error)
			{
				//And where. Without it the dump says what the compiler objected to but not to which member,
				//so a family cannot be traced back to the one statement that started it - which is the
				//question worth asking, since one uncompilable declaration comments out every later
				//statement that used the local.
				//The tree, not the diagnostic's own path, which is empty: the files are parsed from text and
				//never carry one. `byTree` is the same map the repair uses to find the file to edit.
				string where = diagnostic.Location.SourceTree is { } tree && byTree.TryGetValue(tree, out SourceFile? found)
					? System.IO.Path.GetFileName(found.Path)
					: "?";

				//And the line itself, as a fourth field. The line numbers are the ones this compilation saw,
				//and the repair renames and comments afterwards, so reading them back off the exported file
				//shows a different statement - which has already cost one wrong conclusion. Carrying the text
				//along is the only reading of it that stays true.
				string said = diagnostic.Location.SourceTree is { } from
					&& diagnostic.Location.GetLineSpan().StartLinePosition.Line is int at
					&& at < from.GetText().Lines.Count
					? from.GetText().Lines[at].ToString().Trim()
					: "";

				text.Append(diagnostic.Id).Append('\t')
					.Append(where).Append(':')
					.Append(diagnostic.Location.GetLineSpan().StartLinePosition.Line + 1).Append('\t')
					.Append(diagnostic.GetMessage()).Append('\t')
					.Append(said).Append('\n');
			}
		}

		lock (typeof(InvalidSourceRepair))
		{
			System.IO.File.AppendAllText(path, text.ToString());
		}
	}

	private static int WidenInaccessibleMembers(List<SourceFile> files, CSharpCompilation? compilation,
		FileSystem fileSystem, HashSet<string> repairedFiles)
	{
		if (compilation is null)
		{
			return 0;
		}

		Dictionary<SyntaxTree, SourceFile> byTree = [];
		foreach (SourceFile file in files)
		{
			byTree[file.Root.SyntaxTree] = file;
		}

		DumpDiagnostics(compilation, byTree);

		Dictionary<SourceFile, List<Edit>> planned = [];
		HashSet<ISymbol> done = new(SymbolEqualityComparer.Default);

		foreach (Diagnostic diagnostic in compilation.GetDiagnostics())
		{
			//CS0122 is a member that cannot be seen at all; CS0271 and CS0272 are a property whose getter or
			//setter alone is out of reach. il2cpp inlines the method that would have done the writing -
			//`LevelManager.LoseOutOfTime()` sets `levelEndReason { get; private set; }` - so the recovered
			//write is correct and only the accessor's protection stands in the way.
			if (diagnostic.Location.SourceTree is not { } tree || !byTree.TryGetValue(tree, out SourceFile? seenIn))
			{
				continue;
			}

			//CS0122 is a member that cannot be seen at all; CS0271 and CS0272 are a property whose getter or
			//setter alone is out of reach. il2cpp inlines the method that would have done the writing, so the
			//recovered write is correct and only the accessor's protection stands in the way.
			//CS0191 is an assignment to a `readonly` field from outside a constructor. il2cpp does not keep
			//`readonly` - it is a compile-time promise, not a runtime one - so a struct whose fields the
			//compiler zero-filled into the return buffer comes back with those stores in the method body,
			//which is where they really are. Twenty of `SegmentRuleEvaluator.MakeIdent`'s statements are
			//exactly this, and the word is what stands in the way, not the write.
			if (diagnostic.Id is not ("CS0122" or "CS0271" or "CS0272" or "CS0191"))
			{
				continue;
			}

			SemanticModel model = compilation.GetSemanticModel(tree);
			SyntaxNode node = seenIn.Root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true);
			SymbolInfo about = model.GetSymbolInfo(node);

			//For an inaccessible accessor the property itself binds, so there are no candidates to look at -
			//the symbol is simply the one that was found.
			ImmutableArray<ISymbol> named = about.CandidateSymbols.IsEmpty && about.Symbol is { } bound
				? [bound]
				: about.CandidateSymbols;

			foreach (ISymbol found in named)
			{
				//A write to a property binds to the property, whose own declaration is `public` and has
				//nothing to widen. What the compiler objected to is the accessor, and that is a symbol of its
				//own with its own declaration - `private set;`.
				ISymbol symbol = found is IPropertySymbol property
					? (diagnostic.Id == "CS0271" ? property.GetMethod : property.SetMethod) ?? found
					: found;

				if (!done.Add(symbol))
				{
					continue;
				}

				foreach (SyntaxReference reference in symbol.DeclaringSyntaxReferences)
				{
					if (!byTree.TryGetValue(reference.SyntaxTree, out SourceFile? declaredIn))
					{
						continue;
					}

					foreach (Edit edit in diagnostic.Id == "CS0191"
						? MutableEdits(reference.GetSyntax(), byTree)
						: WideningEdit(reference.GetSyntax()) is { } widening ? [widening] : [])
					{
						if (!planned.TryGetValue(declaredIn, out List<Edit>? here))
						{
							planned[declaredIn] = here = [];
						}

						here.Add(edit);
					}
				}
			}
		}

		int made = 0;

		foreach ((SourceFile file, List<Edit> edits) in planned)
		{
			List<Edit> ordered = [.. edits.DistinctBy(edit => edit.Span).OrderBy(edit => edit.Span.Start)];

			fileSystem.File.WriteAllText(file.Path, ApplyEdits(file.Text, ordered));
			repairedFiles.Add(file.Path);
			made += ordered.Count;
		}

		return made;
	}

	/// <summary>The edits that make one field assignable: its own `readonly`, and its struct's.</summary>
	/// <remarks>
	/// C# requires every instance field of a `readonly struct` to be readonly (CS8340), so taking the word off
	/// the field alone would leave a file that does not compile at all - which costs the project every script
	/// in the assembly, not the one statement this is trying to recover. Both go together or neither does.
	/// </remarks>
	private static List<Edit> MutableEdits(SyntaxNode declaration, Dictionary<SyntaxTree, SourceFile> byTree)
	{
		List<Edit> edits = [];

		if (declaration.FirstAncestorOrSelf<FieldDeclarationSyntax>() is not { } field)
		{
			return edits;
		}

		foreach (SyntaxToken modifier in field.Modifiers)
		{
			if (modifier.IsKind(SyntaxKind.ReadOnlyKeyword))
			{
				//The trailing space goes with it, so `readonly int x` does not become ` int x`.
				edits.Add(new Edit(TextSpan.FromBounds(modifier.SpanStart, field.Declaration.Type.SpanStart), "", Rewritten: true));
			}
		}

		if (edits.Count > 0
			&& field.FirstAncestorOrSelf<StructDeclarationSyntax>() is { } owner
			&& byTree.ContainsKey(owner.SyntaxTree))
		{
			foreach (SyntaxToken modifier in owner.Modifiers)
			{
				if (modifier.IsKind(SyntaxKind.ReadOnlyKeyword))
				{
					edits.Add(new Edit(TextSpan.FromBounds(modifier.SpanStart, owner.Keyword.SpanStart), "", Rewritten: true));
				}
			}
		}

		return edits;
	}

	/// <summary>The edit that makes one declaration reachable from the rest of the assembly.</summary>
	private static Edit? WideningEdit(SyntaxNode declaration)
	{
		//An accessor carries its own protection, and the property around it is usually already public - so
		//walking out to the member would find nothing to widen and give up. `private set;` becomes
		//`internal set;` and the write compiles.
		if (declaration is AccessorDeclarationSyntax accessor)
		{
			foreach (SyntaxToken modifier in accessor.Modifiers)
			{
				if (modifier.IsKind(SyntaxKind.PrivateKeyword))
				{
					return new Edit(modifier.Span, "internal", Rewritten: true);
				}

				if (modifier.IsKind(SyntaxKind.ProtectedKeyword))
				{
					return new Edit(modifier.Span, "protected internal", Rewritten: true);
				}
			}

			return null;
		}

		//A field's declaring reference is the variable, and the modifiers belong to the declaration around it.
		MemberDeclarationSyntax? member = declaration as MemberDeclarationSyntax
			?? declaration.FirstAncestorOrSelf<MemberDeclarationSyntax>();

		if (member is null)
		{
			return null;
		}

		SyntaxTokenList modifiers = member.Modifiers;

		if (modifiers.Any(SyntaxKind.PublicKeyword) || modifiers.Any(SyntaxKind.InternalKeyword))
		{
			//Already wide enough, so the protection level was not what the compiler objected to.
			return null;
		}

		foreach (SyntaxToken modifier in modifiers)
		{
			if (modifier.IsKind(SyntaxKind.PrivateKeyword))
			{
				return new Edit(modifier.Span, "internal", Rewritten: true);
			}

			if (modifier.IsKind(SyntaxKind.ProtectedKeyword))
			{
				return new Edit(modifier.Span, "protected internal", Rewritten: true);
			}
		}

		//No accessibility at all, which for a member means private.
		return new Edit(new TextSpan(member.SpanStart, 0), "internal");
	}

	private static string ApplyEdits(string text, List<Edit> edits)
	{
		StringBuilder builder = new(text.Length + edits.Count * 128);
		int position = 0;

		foreach ((TextSpan span, string? replacement, bool rewritten) in edits)
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

			if (rewritten)
			{
				//The statement is being kept, so what was there is of no further use: the replacement says the same
				//thing.
				builder.Append(replacement);
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
