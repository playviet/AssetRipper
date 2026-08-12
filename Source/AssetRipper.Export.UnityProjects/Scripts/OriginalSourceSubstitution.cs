using AsmResolver.DotNet;
using AssetRipper.Import.Logging;
using AssetRipper.IO.Files;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Text;

namespace AssetRipper.Export.UnityProjects.Scripts;

/// <summary>
/// Replaces a decompiled script with the original source it was compiled from, where that source is on disk and can be
/// shown to describe the same type.
/// </summary>
/// <remarks>
/// A game that embeds a third party library as source rather than as an assembly gets that library compiled into
/// Assembly-CSharp, so recovery hands it back as decompiled C# like everything else, and for Il2Cpp that means a
/// quarter of its statements commented out. The library itself is not lost, though: whoever built the game had it, and
/// so does whoever is reading the export. Writing the real file is worth more than any amount of repair work on the
/// recovered one.
/// <para>
/// The danger is the opposite one. A substituted file that does not match the build says the game contains code it
/// does not, quietly, in a form that reads as authoritative - and the copy on disk is quite often a different version
/// of the library from the one that was compiled. So nothing is substituted on the strength of a matching file name.
/// A candidate is found by namespace and type name, and is then checked against what the recovered assembly says the
/// type has: every method it declares outside private scope, by name and parameter count; every property, event and
/// nested type; every field that is public or that Unity serialises, because a MonoBehaviour whose fields have moved
/// deserialises its scenes wrongly. Anything the source does not account for rejects it.
/// </para>
/// <para>
/// The check covers the whole source file, not just the type being substituted, because a file that declares types the
/// assembly does not have is a file from another version, and the type that appears to match cannot be trusted either.
/// </para>
/// <para>
/// A source file holding several top level types is not copied as it stands, because the exporter gives every type its
/// own file and its own script GUID, and two copies of one file would declare each of those types twice. Each type is
/// written into its own file instead, as the verbatim text of its own declaration under the file's using directives,
/// so what lands in the project is still the original code and the layout the rest of the export expects.
/// </para>
/// </remarks>
public static class OriginalSourceSubstitution
{
	/// <summary>
	/// What was substituted and what was turned down.
	/// </summary>
	public sealed class Report
	{
		/// <summary>
		/// Files found under the declared directories that could be read and parsed.
		/// </summary>
		public int IndexedFiles { get; internal set; }

		/// <summary>
		/// Types whose decompiled file was replaced by original source.
		/// </summary>
		public int Substituted { get; internal set; }

		/// <summary>
		/// The original files those types came from.
		/// </summary>
		public int SubstitutedFrom { get; internal set; }

		/// <summary>
		/// Why each rejected type was rejected, keyed by the type's full name.
		/// </summary>
		public List<KeyValuePair<string, string>> Rejections { get; } = new();

		public int Rejected => Rejections.Count;

		internal void Reject(TypeDefinition type, string reason) => Rejections.Add(new(type.FullName, reason));
	}

	/// <summary>
	/// Replaces what it can under <paramref name="outputFolder"/> with original source from <paramref name="sourceDirectories"/>.
	/// </summary>
	/// <param name="assembly">The assembly that was just decompiled, and the authority on what each type has.</param>
	/// <param name="sourceDirectories">Directories to search for original source. Nothing happens when this is empty.</param>
	/// <param name="unityVersion">The version the game was built with, which decides what a library's version gates see.</param>
	/// <param name="nestedDirectoriesForNamespaces">
	/// The decompiler setting of the same name, which decides where the file for a type is.
	/// </param>
	/// <param name="outputFolder">The folder the decompiler wrote the scripts into.</param>
	/// <param name="fileSystem">The file system holding both the source directories and the output.</param>
	public static Report Apply(
		AssemblyDefinition assembly,
		IReadOnlyList<string> sourceDirectories,
		UnityVersion unityVersion,
		bool nestedDirectoriesForNamespaces,
		string outputFolder,
		FileSystem fileSystem)
	{
		Report report = new();
		if (sourceDirectories.Count == 0)
		{
			return report;
		}

		CSharpParseOptions parseOptions = GetParseOptions(unityVersion, assembly);
		Dictionary<string, List<SourceType>> index = BuildIndex(sourceDirectories, parseOptions, fileSystem, report);
		if (index.Count == 0)
		{
			Logger.Info(LogCategory.Export, "No original source was found in the declared directories");
			return report;
		}

		Dictionary<string, TypeDefinition> assemblyTypes = new();
		List<ScriptFileLayout.ScriptFile> files = ScriptFileLayout.GroupTypesByFile(assembly, nestedDirectoriesForNamespaces);
		foreach (ScriptFileLayout.ScriptFile file in files)
		{
			foreach (TypeDefinition type in file.TopLevelTypes)
			{
				assemblyTypes[GetKey(type)] = type;
			}
		}

		Dictionary<SourceFile, string?> verdicts = new();
		HashSet<SourceFile> used = new();

		foreach (ScriptFileLayout.ScriptFile file in files)
		{
			//Two top level types sharing one file is a name collision the exporter resolves by writing both into it.
			//Replacing that file with the source of one of them would drop the other.
			if (file.TopLevelTypes.Count != 1)
			{
				continue;
			}

			TypeDefinition type = file.TopLevelTypes[0];
			if (!index.TryGetValue(GetKey(type), out List<SourceType>? candidates))
			{
				continue;
			}

			if (candidates.Count > 1)
			{
				report.Reject(type, $"{candidates.Count} original files declare it: {string.Join(", ", candidates.Select(c => c.File.Path))}");
				continue;
			}

			SourceType candidate = candidates[0];
			string path = fileSystem.Path.Join(outputFolder, file.Path);
			if (!fileSystem.File.Exists(path))
			{
				//The decompiler wrote nothing here, so there is no decompiled script this would be an improvement on,
				//and writing one would add a file the rest of the export does not know about.
				report.Reject(type, "the decompiler wrote no file for it");
				continue;
			}

			try
			{
				if (!verdicts.TryGetValue(candidate.File, out string? failure))
				{
					failure = Verify(candidate.File, assemblyTypes);
					verdicts.Add(candidate.File, failure);
				}

				if (failure is not null)
				{
					report.Reject(type, $"{candidate.File.Path}: {failure}");
					continue;
				}

				string content = GetContent(candidate);

				//Checked rather than assumed, because a file that does not parse costs the project every script
				//in the assembly - far more than the one decompiled file it was replacing.
				if (!Parses(content, parseOptions))
				{
					report.Reject(type, $"{candidate.File.Path}: what would be written for this type does not parse");
					continue;
				}

				fileSystem.File.WriteAllText(path, content);
			}
			catch (Exception exception)
			{
				//One candidate that cannot be read or written is one substitution lost, not an export lost.
				report.Reject(type, $"{candidate.File.Path} could not be used: {exception.Message}");
				continue;
			}

			report.Substituted++;
			used.Add(candidate.File);
		}

		report.SubstitutedFrom = used.Count;
		Log(report);
		return report;
	}

	/// <summary>
	/// The text to write for one type: its own declaration, verbatim, under the using directives it was written with.
	/// </summary>
	internal static string GetContent(SourceType type)
	{
		//A file that declares nothing else is copied as it stands, so that what lands in the project is the original
		//file rather than a reconstruction of it.
		if (type.File.TopLevelTypes.Count == 1)
		{
			return type.File.Text;
		}

		StringBuilder builder = new();
		foreach (string directive in type.Usings)
		{
			builder.AppendLine(directive);
		}

		if (type.Usings.Count > 0)
		{
			builder.AppendLine();
		}

		//A region is opened before the type and closed after it, so taking the type out on its own takes the
		//opening without the closing and the file no longer parses. They mark nothing the compiler reads, so the
		//pair goes rather than being reconstructed.
		MemberDeclarationSyntax withoutRegions = RemoveRegionDirectives(type.Declaration);

		string declaration = withoutRegions.ToFullString().Trim('\r', '\n').TrimEnd();
		if (type.Namespace.Length == 0)
		{
			builder.AppendLine(declaration);
		}
		else
		{
			builder.Append("namespace ").AppendLine(type.Namespace);
			builder.AppendLine("{");
			builder.AppendLine(declaration);
			builder.AppendLine("}");
		}

		return builder.ToString();
	}

	private static MemberDeclarationSyntax RemoveRegionDirectives(MemberDeclarationSyntax declaration)
	{
		List<SyntaxTrivia> regions = [];

		foreach (SyntaxTrivia trivia in declaration.DescendantTrivia(descendIntoTrivia: true))
		{
			if (trivia.IsKind(SyntaxKind.RegionDirectiveTrivia) || trivia.IsKind(SyntaxKind.EndRegionDirectiveTrivia))
			{
				regions.Add(trivia);
			}
		}

		return regions.Count == 0
			? declaration
			: declaration.ReplaceTrivia(regions, (_, _) => default);
	}

	/// <summary>
	/// Whether the text is a whole C# file the compiler can read. A substitution that does not parse costs the
	/// project every script in the assembly, which is worse than the decompiled version it would have replaced,
	/// so anything that comes out malformed is not used at all.
	/// </summary>
	internal static bool Parses(string text, CSharpParseOptions parseOptions)
	{
		foreach (Diagnostic diagnostic in CSharpSyntaxTree.ParseText(text, parseOptions).GetDiagnostics())
		{
			if (diagnostic.Severity == DiagnosticSeverity.Error)
			{
				return false;
			}
		}

		return true;
	}

	private static void Log(Report report)
	{
		Logger.Info(LogCategory.Export, $"Substituted {report.Substituted} {(report.Substituted == 1 ? "type" : "types")} with original source from {report.SubstitutedFrom} of {report.IndexedFiles} indexed files");

		if (report.Rejected == 0)
		{
			return;
		}

		Logger.Info(LogCategory.Export, $"Rejected original source for {report.Rejected} {(report.Rejected == 1 ? "type" : "types")}, which stay as decompiled:");
		foreach ((string type, string reason) in report.Rejections)
		{
			Logger.Debug(LogCategory.Export, $"  {type}: {reason}");
		}
	}

	#region Verification

	/// <summary>
	/// Checks every type the file declares against the assembly, and says what is wrong with the first one that is.
	/// </summary>
	private static string? Verify(SourceFile file, Dictionary<string, TypeDefinition> assemblyTypes)
	{
		foreach (SourceType type in file.TopLevelTypes)
		{
			//A partial type is only part of itself here, and the other parts are in files of their own.
			if (type.IsPartial)
			{
				return $"{type.Key} is declared partial, so this file may hold only part of it";
			}

			if (!assemblyTypes.TryGetValue(type.Key, out TypeDefinition? definition))
			{
				return $"the assembly has no {type.Key}, which this file declares";
			}

			if (Verify(definition, type) is { } failure)
			{
				return failure;
			}
		}

		return null;
	}

	/// <summary>
	/// Checks one type against what the assembly says it has, in both directions.
	/// </summary>
	private static string? Verify(TypeDefinition definition, SourceType source)
	{
		if (definition.IsEnum != (source.Kind == SourceKind.Enum) || definition.IsDelegate != (source.Kind == SourceKind.Delegate))
		{
			return $"{source.Key} is a {source.Kind} in the source and something else in the assembly";
		}

		//A delegate's members are all written by the compiler, so its declaration is the whole of it.
		if (definition.IsDelegate)
		{
			return null;
		}

		foreach (MethodDefinition method in definition.Methods)
		{
			if (!IsRequired(method))
			{
				continue;
			}

			string key = $"{GetSimpleName(method.Name?.ToString())}#{method.Parameters.Count}";
			if (!source.Methods.Contains(key))
			{
				return $"{source.Key} does not declare {key.Replace('#', '/')}";
			}
		}

		foreach (PropertyDefinition property in definition.Properties)
		{
			if (IsGenerated(property.Name?.ToString()) || !IsRequired(property))
			{
				continue;
			}

			if (!source.Members.Contains(GetSimpleName(property.Name?.ToString())))
			{
				return $"{source.Key} does not declare the property {property.Name}";
			}
		}

		foreach (EventDefinition @event in definition.Events)
		{
			if (IsGenerated(@event.Name?.ToString()))
			{
				continue;
			}

			if (!source.Members.Contains(GetSimpleName(@event.Name?.ToString())))
			{
				return $"{source.Key} does not declare the event {@event.Name}";
			}
		}

		foreach (FieldDefinition field in definition.Fields)
		{
			if (!IsRequired(field))
			{
				continue;
			}

			if (!source.Members.Contains(field.Name!.ToString()))
			{
				return $"{source.Key} does not declare the field {field.Name}";
			}
		}

		HashSet<string> matched = new();
		foreach (TypeDefinition nested in definition.NestedTypes)
		{
			if (IsGenerated(nested.Name?.ToString()) || HasCompilerGeneratedAttribute(nested))
			{
				continue;
			}

			string key = GetSimpleKey(nested);
			if (!source.Nested.TryGetValue(key, out SourceType? nestedSource))
			{
				return $"{source.Key} does not declare the nested type {key}";
			}

			matched.Add(key);
			if (Verify(nested, nestedSource) is { } failure)
			{
				return failure;
			}
		}

		foreach (string key in source.Nested.Keys)
		{
			if (!matched.Contains(key))
			{
				return $"the assembly's {source.Key} has no nested {key}, which the source declares";
			}
		}

		return null;
	}

	/// <summary>
	/// Is this method one the source has to account for?
	/// </summary>
	/// <remarks>
	/// Accessors are checked as the property or event they belong to, since that is how the source spells them. A
	/// parameterless constructor and a static constructor are both written by the compiler for types that declare
	/// neither. Private methods are left out because they are the ones a compiler moves around - a local function or
	/// an iterator becomes one - and because a version difference shows in the surface of a type, which is what the
	/// rest of the project is compiled against.
	/// </remarks>
	private static bool IsRequired(MethodDefinition method)
	{
		if (method.Semantics is not null || method.Name is null || IsGenerated(method.Name.ToString()))
		{
			return false;
		}

		if (method.Name == ".cctor" || (method.Name == ".ctor" && method.Parameters.Count == 0))
		{
			return false;
		}

		return (method.IsPublic || method.IsFamily || method.IsAssembly || method.IsFamilyOrAssembly)
			&& !HasCompilerGeneratedAttribute(method);
	}

	private static bool IsRequired(PropertyDefinition property)
	{
		return IsVisible(property.GetMethod) || IsVisible(property.SetMethod);

		static bool IsVisible(MethodDefinition? accessor)
		{
			return accessor is not null && (accessor.IsPublic || accessor.IsFamily || accessor.IsAssembly || accessor.IsFamilyOrAssembly);
		}
	}

	/// <summary>
	/// Is this field one the source has to account for?
	/// </summary>
	/// <remarks>
	/// A private field is normally the compiler's business, but a Unity one is not: a field carrying
	/// <c>SerializeField</c> is written into every scene and prefab that holds the component, so a source file whose
	/// private fields have moved reads those back wrongly and silently.
	/// </remarks>
	private static bool IsRequired(FieldDefinition field)
	{
		if (field.Name is null || IsGenerated(field.Name.ToString()) || field.Name == "value__" || HasCompilerGeneratedAttribute(field))
		{
			return false;
		}

		if (field.IsPublic || field.IsFamily || field.IsAssembly || field.IsFamilyOrAssembly)
		{
			return true;
		}

		foreach (CustomAttribute attribute in field.CustomAttributes)
		{
			if (attribute.Constructor?.DeclaringType?.Name?.ToString() is "SerializeField" or "SerializeReference")
			{
				return true;
			}
		}

		return false;
	}

	/// <summary>
	/// Names holding a character no C# identifier can hold belong to the compiler, not to the source.
	/// </summary>
	private static bool IsGenerated(string? name)
	{
		return name is null || name.AsSpan().ContainsAny('<', '>', '$');
	}

	private static bool HasCompilerGeneratedAttribute(IHasCustomAttribute member)
	{
		foreach (CustomAttribute attribute in member.CustomAttributes)
		{
			if (attribute.Constructor?.DeclaringType?.Name == "CompilerGeneratedAttribute")
			{
				return true;
			}
		}

		return false;
	}

	/// <summary>
	/// An explicit interface implementation carries the interface in its name, which the source writes separately.
	/// </summary>
	private static string GetSimpleName(string? name)
	{
		if (name is null)
		{
			return "";
		}

		int index = name.LastIndexOf('.');
		return index < 0 || name.StartsWith('.') ? name : name[(index + 1)..];
	}

	private static string GetKey(TypeDefinition type)
	{
		string @namespace = type.Namespace?.ToString() ?? "";
		string name = GetSimpleKey(type);
		return @namespace.Length == 0 ? name : $"{@namespace}.{name}";
	}

	/// <summary>
	/// A type's name and how many type parameters it takes, which is what tells two overloads of a name apart.
	/// </summary>
	private static string GetSimpleKey(TypeDefinition type)
	{
		string name = type.Name?.ToString() ?? "";
		int index = name.LastIndexOf('`');
		return index < 0 ? $"{name}`0" : name;
	}

	#endregion

	#region Indexing

	internal enum SourceKind
	{
		Type,
		Enum,
		Delegate,
	}

	/// <summary>
	/// One original file, and the types it declares outside any conditional compilation that is off.
	/// </summary>
	internal sealed class SourceFile(string path, string text)
	{
		public string Path { get; } = path;
		public string Text { get; } = text;
		public List<SourceType> TopLevelTypes { get; } = new();
	}

	/// <summary>
	/// One type declaration in an original file, reduced to what it declares.
	/// </summary>
	internal sealed class SourceType(SourceFile file, string @namespace, string key, SourceKind kind, MemberDeclarationSyntax declaration)
	{
		public SourceFile File { get; } = file;
		public string Namespace { get; } = @namespace;

		/// <summary>
		/// The full name of the type with its type parameter count, spelled the way the assembly spells it.
		/// </summary>
		public string Key { get; } = key;

		public SourceKind Kind { get; } = kind;
		public MemberDeclarationSyntax Declaration { get; } = declaration;
		public bool IsPartial { get; set; }

		/// <summary>
		/// The using directives in scope where the type is declared.
		/// </summary>
		public List<string> Usings { get; } = new();

		/// <summary>
		/// Every method, constructor and operator, as a name and a parameter count.
		/// </summary>
		public HashSet<string> Methods { get; } = new();

		/// <summary>
		/// Every field, property, event and enum member, by name.
		/// </summary>
		public HashSet<string> Members { get; } = new();

		public Dictionary<string, SourceType> Nested { get; } = new();
	}

	/// <summary>
	/// How to read the original source: as the compiler that built the game read it.
	/// </summary>
	/// <remarks>
	/// A library shipped as source carries version gates, and what is behind one of them is part of the type when the
	/// gate is open. Reading with no symbols defined would hide those members and reject the file for not declaring
	/// what it plainly does. The <c>UNITY_x_y_OR_NEWER</c> symbols are the ones Unity itself defines, and they follow
	/// from the version the project was built with, so they are defined here the same way.
	/// <para>
	/// Nothing else is defined. A symbol left out costs a substitution, which is the harmless direction: the member
	/// stays hidden, the file looks incomplete, and the decompiled script is kept.
	/// </para>
	/// <para>
	/// The language version is the newest the parser knows, because the source is only read here and never compiled.
	/// Rejecting a file for using syntax that postdates the version the export is written for would cost a
	/// substitution for nothing, and the compile pass that follows the substitution sees the file anyway.
	/// </para>
	/// </remarks>
	/// <summary>
	/// How the compiler will read a source file for this build: which of Unity's version gates are open. Anything
	/// that parses source without these sees a different file from the one the editor will compile.
	/// </summary>
	internal static CSharpParseOptions GetParseOptions(UnityVersion version, AssemblyDefinition? assembly = null)
	{
		List<string> symbols = new();

		//The class library the game was built against is a second family of gates, and it is not derivable from the
		//Unity version - it is a project setting. It can be read off the assembly, though: a build at the .NET
		//Standard level references `netstandard`, and one at the .NET Framework level references `mscorlib` and the
		//profile assemblies that only exist there.
		//
		//This cost the single worst file in the recovery queue. `DOTweenModuleUnityVersion` guards six methods with
		//`#if UNITY_2018_1_OR_NEWER && (NET_4_6 || NET_STANDARD_2_0)`; with neither symbol defined the source
		//appeared not to declare them, the whole file was rejected, and fourteen bodies stayed decompiled - while
		//its two sibling modules, which have no such gate, were substituted and are byte-identical to the original.
		if (assembly?.ManifestModule is { } module)
		{
			bool Names(string name) => module.AssemblyReferences.Any(reference => reference.Name == name);

			if (Names("netstandard"))
			{
				//Both are defined at either .NET Standard level; 2.1 is left out because nothing here distinguishes
				//2.0 from 2.1, and a symbol left out is the harmless direction.
				symbols.Add("NET_STANDARD");
				symbols.Add("NET_STANDARD_2_0");
			}
			else if (Names("mscorlib"))
			{
				symbols.Add("NET_4_6");
				symbols.Add("NET_UNITY_4_8");
			}
		}

		//Unity numbered its majors 5, then by year, then from 6000. A major that does not exist contributes symbols
		//no source refers to, which is cheaper than a table of real releases that goes stale.
		foreach (int major in Enumerable.Range(4, 2).Concat(Enumerable.Range(2017, 13)).Concat(Enumerable.Range(6000, 20)))
		{
			for (int minor = 0; minor <= 9; minor++)
			{
				if (major < version.Major || (major == version.Major && minor <= version.Minor))
				{
					symbols.Add($"UNITY_{major}_{minor}_OR_NEWER");
				}
			}
		}

		symbols.Add($"UNITY_{version.Major}");
		symbols.Add($"UNITY_{version.Major}_{version.Minor}");

		return new CSharpParseOptions(LanguageVersion.Latest, preprocessorSymbols: symbols);
	}

	private static Dictionary<string, List<SourceType>> BuildIndex(IReadOnlyList<string> directories, CSharpParseOptions parseOptions, FileSystem fileSystem, Report report)
	{
		Dictionary<string, List<SourceType>> index = new();

		foreach (string directory in directories)
		{
			if (string.IsNullOrWhiteSpace(directory) || !fileSystem.Directory.Exists(directory))
			{
				Logger.Warning(LogCategory.Export, $"No original source directory at {directory}");
				continue;
			}

			foreach (string path in Enumerate(directory, fileSystem))
			{
				SourceFile? file = Read(path, parseOptions, fileSystem);
				if (file is null)
				{
					continue;
				}

				report.IndexedFiles++;
				foreach (SourceType type in file.TopLevelTypes)
				{
					if (!index.TryGetValue(type.Key, out List<SourceType>? list))
					{
						list = new List<SourceType>();
						index.Add(type.Key, list);
					}

					list.Add(type);
				}
			}
		}

		return index;
	}

	private static IEnumerable<string> Enumerate(string directory, FileSystem fileSystem)
	{
		try
		{
			return fileSystem.Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories).ToList();
		}
		catch (Exception exception)
		{
			//A directory that cannot be walked is one source of substitutions lost, not a reason to lose the export.
			Logger.Warning(LogCategory.Export, $"Could not search {directory} for original source: {exception.Message}");
			return [];
		}
	}

	private static SourceFile? Read(string path, CSharpParseOptions parseOptions, FileSystem fileSystem)
	{
		try
		{
			string text = fileSystem.File.ReadAllText(path);
			SourceFile file = new(path, text);
			CompilationUnitSyntax root = CSharpSyntaxTree.ParseText(text, parseOptions).GetCompilationUnitRoot();
			Collect(root.Members, "", GetUsings(root, []), file, file.TopLevelTypes, null);
			return file;
		}
		catch (Exception exception)
		{
			Logger.Warning(LogCategory.Export, $"Could not read original source at {path}: {exception.Message}");
			return null;
		}
	}

	private static List<string> GetUsings(SyntaxNode container, List<string> inherited)
	{
		SyntaxList<UsingDirectiveSyntax> directives = container switch
		{
			CompilationUnitSyntax unit => unit.Usings,
			BaseNamespaceDeclarationSyntax @namespace => @namespace.Usings,
			_ => default,
		};

		if (directives.Count == 0)
		{
			return inherited;
		}

		List<string> result = new(inherited);
		foreach (UsingDirectiveSyntax directive in directives)
		{
			result.Add(directive.ToString());
		}

		return result;
	}

	private static void Collect(
		SyntaxList<MemberDeclarationSyntax> members,
		string @namespace,
		List<string> usings,
		SourceFile file,
		List<SourceType>? topLevel,
		SourceType? parent)
	{
		foreach (MemberDeclarationSyntax member in members)
		{
			switch (member)
			{
				case BaseNamespaceDeclarationSyntax declaration:
					string inner = declaration.Name.ToString();
					Collect(
						declaration.Members,
						@namespace.Length == 0 ? inner : $"{@namespace}.{inner}",
						GetUsings(declaration, usings),
						file,
						topLevel,
						parent);
					break;

				case TypeDeclarationSyntax declaration:
					{
						SourceType type = Create(file, @namespace, usings, parent, declaration.Identifier.Text, declaration.Arity, SourceKind.Type, declaration);
						type.IsPartial = declaration.Modifiers.Any(SyntaxKind.PartialKeyword);
						if (declaration.ParameterList is { } primary)
						{
							type.Methods.Add($".ctor#{primary.Parameters.Count}");
						}

						CollectMembers(declaration.Members, type, file, usings);
						Add(type, topLevel, parent);
					}
					break;

				case EnumDeclarationSyntax declaration:
					{
						SourceType type = Create(file, @namespace, usings, parent, declaration.Identifier.Text, 0, SourceKind.Enum, declaration);
						foreach (EnumMemberDeclarationSyntax value in declaration.Members)
						{
							type.Members.Add(value.Identifier.Text);
						}

						Add(type, topLevel, parent);
					}
					break;

				case DelegateDeclarationSyntax declaration:
					Add(Create(file, @namespace, usings, parent, declaration.Identifier.Text, declaration.Arity, SourceKind.Delegate, declaration), topLevel, parent);
					break;
			}
		}
	}

	private static SourceType Create(
		SourceFile file,
		string @namespace,
		List<string> usings,
		SourceType? parent,
		string name,
		int arity,
		SourceKind kind,
		MemberDeclarationSyntax declaration)
	{
		string simple = $"{name}`{arity}";
		string key = parent is not null
			? $"{parent.Key}+{simple}"
			: @namespace.Length == 0 ? simple : $"{@namespace}.{simple}";

		SourceType type = new(file, @namespace, key, kind, declaration);
		type.Usings.AddRange(usings);
		return type;
	}

	private static void Add(SourceType type, List<SourceType>? topLevel, SourceType? parent)
	{
		if (parent is not null)
		{
			//A nested type is keyed by its own name, because that is what the declaring type knows it by.
			parent.Nested[type.Key[(type.Key.LastIndexOf('+') + 1)..]] = type;
		}
		else
		{
			topLevel?.Add(type);
		}
	}

	private static void CollectMembers(SyntaxList<MemberDeclarationSyntax> members, SourceType type, SourceFile file, List<string> usings)
	{
		foreach (MemberDeclarationSyntax member in members)
		{
			switch (member)
			{
				case MethodDeclarationSyntax method:
					type.Methods.Add($"{method.Identifier.Text}#{method.ParameterList.Parameters.Count}");
					break;
				case ConstructorDeclarationSyntax constructor:
					type.Methods.Add($"{(constructor.Modifiers.Any(SyntaxKind.StaticKeyword) ? ".cctor" : ".ctor")}#{constructor.ParameterList.Parameters.Count}");
					break;
				case DestructorDeclarationSyntax:
					type.Methods.Add("Finalize#0");
					break;
				case OperatorDeclarationSyntax @operator:
					type.Methods.Add($"{GetOperatorName(@operator.OperatorToken.Kind(), @operator.ParameterList.Parameters.Count)}#{@operator.ParameterList.Parameters.Count}");
					break;
				case ConversionOperatorDeclarationSyntax conversion:
					type.Methods.Add($"{(conversion.ImplicitOrExplicitKeyword.IsKind(SyntaxKind.ImplicitKeyword) ? "op_Implicit" : "op_Explicit")}#1");
					break;
				case PropertyDeclarationSyntax property:
					type.Members.Add(property.Identifier.Text);
					break;
				case IndexerDeclarationSyntax:
					type.Members.Add("Item");
					break;
				case EventDeclarationSyntax @event:
					type.Members.Add(@event.Identifier.Text);
					break;
				case EventFieldDeclarationSyntax @event:
					foreach (VariableDeclaratorSyntax variable in @event.Declaration.Variables)
					{
						type.Members.Add(variable.Identifier.Text);
					}
					break;
				case FieldDeclarationSyntax field:
					foreach (VariableDeclaratorSyntax variable in field.Declaration.Variables)
					{
						type.Members.Add(variable.Identifier.Text);
					}
					break;
				case BaseTypeDeclarationSyntax or DelegateDeclarationSyntax:
					Collect(new SyntaxList<MemberDeclarationSyntax>(member), type.Namespace, usings, file, null, type);
					break;
			}
		}
	}

	/// <summary>
	/// The name the compiler gives an operator in metadata.
	/// </summary>
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
		SyntaxKind.LessThanLessThanToken => "op_LeftShift",
		SyntaxKind.GreaterThanGreaterThanToken => "op_RightShift",
		SyntaxKind.GreaterThanGreaterThanGreaterThanToken => "op_UnsignedRightShift",
		SyntaxKind.EqualsEqualsToken => "op_Equality",
		SyntaxKind.ExclamationEqualsToken => "op_Inequality",
		SyntaxKind.LessThanToken => "op_LessThan",
		SyntaxKind.GreaterThanToken => "op_GreaterThan",
		SyntaxKind.LessThanEqualsToken => "op_LessThanOrEqual",
		SyntaxKind.GreaterThanEqualsToken => "op_GreaterThanOrEqual",
		SyntaxKind.ExclamationToken => "op_LogicalNot",
		SyntaxKind.TildeToken => "op_OnesComplement",
		SyntaxKind.PlusPlusToken => "op_Increment",
		SyntaxKind.MinusMinusToken => "op_Decrement",
		SyntaxKind.TrueKeyword => "op_True",
		SyntaxKind.FalseKeyword => "op_False",
		_ => "op_Unknown",
	};

	#endregion
}
