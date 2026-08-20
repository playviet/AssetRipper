using AsmResolver.DotNet;
using AssetRipper.Export.Configuration;
using AssetRipper.Export.Scripts;
using AssetRipper.Import.Configuration;
using AssetRipper.Import.Logging;
using AssetRipper.Import.Structure.Assembly;
using AssetRipper.Import.Structure.Assembly.Managers;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.CSharp.ProjectDecompiler;

namespace AssetRipper.Export.UnityProjects.Scripts;

internal class ScriptDecompiler
{
	private readonly IAssemblyManager assemblyManager;
	private readonly ILSpyAssemblyResolver assemblyResolver;
	public LanguageVersion LanguageVersion { get; set; } = LanguageVersion.CSharp7_3;
	public ScriptContentLevel ScriptContentLevel { get; set; } = ScriptContentLevel.Level2;
	public ScriptingBackend ScriptingBackend { get; set; } = ScriptingBackend.Unknown;
	public bool FullyQualifiedTypeNames { get; set; } = false;
	public ScriptIlExportMode IlExportMode { get; set; } = ScriptIlExportMode.None;
	public IReadOnlyList<string> OriginalSourceDirectories { get; set; } = [];
	public UnityVersion UnityVersion { get; set; }

	public ScriptDecompiler(IAssemblyManager assemblyManager)
	{
		this.assemblyManager = assemblyManager;
		assemblyResolver = new ILSpyAssemblyResolver(assemblyManager);
		ScriptingBackend = assemblyManager.ScriptingBackend;
	}

	public void DecompileWholeProject(AssemblyDefinition assembly, string outputFolder, FileSystem fileSystem)
	{
		CustomWholeProjectDecompiler decompiler = new(CreateSettings(), assemblyResolver, fileSystem);

		DecompileWholeProject(decompiler, assembly, outputFolder, fileSystem);

		//Before the repair below, so that the project is still compiled as a whole: a substituted file is real source
		//but not necessarily source this project can build, and everything that calls into it has to agree with it.
		OriginalSourceSubstitution.Apply(assembly, OriginalSourceDirectories, UnityVersion, decompiler.Settings.UseNestedDirectoriesForNamespaces, outputFolder, fileSystem);

		//Only recovered bodies produce source that does not compile, and only Level3 recovers any.
		if (ScriptContentLevel == ScriptContentLevel.Level3)
		{
			InvalidSourceRepair.Apply(assembly, assemblyManager, GetRoslynLanguageVersion(), UnityVersion, outputFolder, fileSystem);
		}

		//Written last, so that a companion describes the source that was actually kept.
		if (IlExportMode == ScriptIlExportMode.Companion)
		{
			ScriptIlCompanionExporter.Export(assembly, decompiler.Settings.UseNestedDirectoriesForNamespaces, outputFolder, fileSystem);
		}
	}

	/// <summary>
	/// The version the decompiled source is written for, in the compiler's own terms.
	/// </summary>
	/// <remarks>
	/// The two enums name the same versions, but the decompiler spells the whole numbered ones with a trailing zero:
	/// its CSharp9_0 is the compiler's CSharp9. A version too new for the compiler to know falls back to its latest.
	/// </remarks>
	private Microsoft.CodeAnalysis.CSharp.LanguageVersion GetRoslynLanguageVersion()
	{
		string name = LanguageVersion.ToString();
		if (name.EndsWith("_0", StringComparison.Ordinal))
		{
			name = name[..^2];
		}

		return Enum.TryParse(name, out Microsoft.CodeAnalysis.CSharp.LanguageVersion version)
			? version
			: Microsoft.CodeAnalysis.CSharp.LanguageVersion.Latest;
	}

	private DecompilerSettings CreateSettings()
	{
		DecompilerSettings settings = new();

		settings.SetLanguageVersion(LanguageVersion);

		settings.AlwaysShowEnumMemberValues = true;
		settings.ShowXmlDocumentation = true;

		settings.UseNestedDirectoriesForNamespaces = true;

		//An anonymous type is hidden from project output while this is on, because the decompiler expects to
		//have rewritten every use of it back into `new { X = a }`. On recovered IL it does not: the pattern it
		//matches is not there, so the uses survive as `_003C_003Ef__AnonymousType0<,>` and the declaration is
		//written as an EMPTY FILE beside them. Snacky Dash exported three such files and 40 of the 45
		//remaining compile errors were their references. Turning it off costs nothing measurable here -
		//`grep -c "new {"` over that export is **zero**, so the transform never once succeeded - and it makes
		//the declaration and the references agree.
		settings.AnonymousTypes = false;

		if (FullyQualifiedTypeNames)
		{
			settings.AlwaysUseGlobal = true;
			settings.UsingDeclarations = false;
		}

		return settings;
	}

	private void DecompileWholeProject(CustomWholeProjectDecompiler decompiler, AssemblyDefinition assembly, string outputFolder, FileSystem fileSystem)
	{
		//An assembly is decompiled as a whole and one type that throws ends the run, so a single method the
		//decompiler cannot read costs every file not yet written. Give up that method's body and run again.
		HashSet<string> emptied = [];

		for (int attempt = 0; attempt <= MaximumEmptiedMethods; attempt++)
		{
			//The resolver holds the copy of the assembly it read, so an emptied method needs a new one.
			ILSpyAssemblyResolver resolver = attempt == 0 ? assemblyResolver : new ILSpyAssemblyResolver(assemblyManager);
			CustomWholeProjectDecompiler attemptDecompiler = attempt == 0
				? decompiler
				: new CustomWholeProjectDecompiler(decompiler.Settings, resolver, fileSystem);

			try
			{
				attemptDecompiler.DecompileProject(resolver.Resolve(assembly), outputFolder, TextWriter.Null);
				return;
			}
			catch (Exception exception)
			{
				if (!UndecompilableMethodRemoval.EmptyTheMethodThatFailed(assemblyManager, assembly, exception, emptied))
				{
					Logger.Error(exception);
					return;
				}
			}
		}
	}

	/// <summary>
	/// How many methods will be given up before the assembly is written off. A run that keeps failing is
	/// failing for a reason other than one unreadable body, and repeating it forever would not find it.
	/// </summary>
	private const int MaximumEmptiedMethods = 16;

	private sealed class CustomWholeProjectDecompiler(DecompilerSettings settings, ILSpyAssemblyResolver assemblyResolver, FileSystem fileSystem) : ILSpyWholeProjectDecompiler(settings, assemblyResolver, NullProjectFileWriter.Instance, fileSystem)
	{
		protected override TextWriter CreateFile(string path)
		{
			if (FileSystem.Path.GetFileName(path) is "UnitySourceGeneratedAssemblyMonoScriptTypes_v1.cs")
			{
				// UnitySourceGeneratedAssemblyMonoScriptTypes_v1 is generated by Unity and should not be decompiled
				return TextWriter.Null;
			}

			return base.CreateFile(path);
		}
	}
}
