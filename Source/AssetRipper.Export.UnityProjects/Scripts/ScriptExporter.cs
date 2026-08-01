using AssetRipper.Assets;
using AssetRipper.Export.Configuration;
using AssetRipper.Export.UserPackages;
using AssetRipper.Import.Structure.Assembly;
using AssetRipper.Import.Structure.Assembly.Managers;
using AssetRipper.SourceGenerated;
using AssetRipper.SourceGenerated.Classes.ClassID_115;

namespace AssetRipper.Export.UnityProjects.Scripts;

public class ScriptExporter : IAssetExporter
{
	public ScriptExporter(IAssemblyManager assemblyManager, FullConfiguration configuration)
	{
		AssemblyManager = assemblyManager;
		Decompiler = new ScriptDecompiler(AssemblyManager)
		{
			LanguageVersion = configuration.ExportSettings.ScriptLanguageVersion.ToCSharpLanguageVersion(configuration.Version),
			ScriptContentLevel = configuration.ImportSettings.ScriptContentLevel,
			FullyQualifiedTypeNames = configuration.ExportSettings.ScriptTypesFullyQualified,
			IlExportMode = configuration.ExportSettings.ScriptIlExportMode,
			OriginalSourceDirectories = configuration.ExportSettings.ScriptOriginalSourceDirectories,
			UnityVersion = configuration.Version,
		};
		ExportMode = configuration.ExportSettings.ScriptExportMode;
		ReferenceAssemblyDictionary = ReferenceAssemblies.GetReferenceAssemblies(AssemblyManager, configuration.Version);
		Relinker = configuration.UnityPackageRelinker;
	}

	public IAssemblyManager AssemblyManager { get; }
	public ScriptExportMode ExportMode { get; }
	internal ScriptDecompiler Decompiler { get; }
	internal Dictionary<string, UnityGuid> ReferenceAssemblyDictionary { get; }
	internal UnityPackageRelinker? Relinker { get; }
	private bool HasDecompiled { get; set; } = false;
	private static long MonoScriptDecompiledFileID { get; } = ExportIdHandler.GetMainExportID((int)ClassIDType.MonoScript);

	public bool TryCreateCollection(IUnityObjectBase asset, [NotNullWhen(true)] out IExportCollection? exportCollection)
	{
		if (asset is IMonoScript script)
		{
			if (HasDecompiled)
			{
				exportCollection = new SingleRedirectExportCollection(asset, CreateExportPointer(script));
			}
			else
			{
				HasDecompiled = true;
				if (AssemblyManager.IsSet)
				{
					exportCollection = new ScriptExportCollection(this, script);
				}
				else
				{
					exportCollection = new EmptyScriptExportCollection(this, script);
				}
			}
			return true;
		}
		else
		{
			exportCollection = null;
			return false;
		}
	}

	public AssemblyExportType GetExportType(IMonoScript script)
	{
		return GetExportType(script.GetAssemblyNameFixed());
	}

	public MetaPtr CreateExportPointer(IMonoScript script)
	{
		// A relinked script lives in a package now, and every package script has the same file ID.
		if (Relinker is not null && Relinker.TryGetScriptGuid(script, out string? packageGuid))
		{
			return new MetaPtr(UnityPackageIndex.ScriptFileID, UnityGuid.Parse(packageGuid), AssetType.Meta);
		}

		return GetExportType(script) switch
		{
			AssemblyExportType.Decompile => new(MonoScriptDecompiledFileID, ScriptHashing.CalculateScriptGuid(script), AssetType.Meta),
			AssemblyExportType.Skip => new(ScriptHashing.CalculateScriptFileID(script), ReferenceAssemblyDictionary[script.GetAssemblyNameFixed()], AssetType.Meta),
			_ => new(ScriptHashing.CalculateScriptFileID(script), ScriptHashing.CalculateAssemblyGuid(script), AssetType.Meta),
		};
	}

	public AssemblyExportType GetExportType(string assemblyName)
	{
		if (Relinker is not null && Relinker.IsRelinked(assemblyName))
		{
			return AssemblyExportType.Relink;
		}
		else if (ReferenceAssemblyDictionary.ContainsKey(assemblyName))
		{
			return AssemblyExportType.Skip;
		}
		else if (!AssemblyManager.IsSet)
		{
			return AssemblyExportType.Decompile;
		}
		else if (ExportMode is ScriptExportMode.Decompiled)
		{
			return AssemblyExportType.Decompile;
		}
		else if (ExportMode is ScriptExportMode.Hybrid)
		{
			return ReferenceAssemblies.IsPredefinedAssembly(assemblyName)
				? AssemblyExportType.Decompile
				: AssemblyExportType.Save;
		}
		else
		{
			return AssemblyExportType.Save;
		}
	}

	AssetType IAssetExporter.ToExportType(IUnityObjectBase asset) => AssetType.Meta;

	bool IAssetExporter.ToUnknownExportType(Type type, out AssetType assetType)
	{
		assetType = AssetType.Meta;
		return true;
	}
}
