using AssetRipper.Import.Logging;
using AssetRipper.IO.Files;
using AssetRipper.Primitives;
using AssetRipper.SourceGenerated;
using AssetRipper.SourceGenerated.Classes.ClassID_114;
using AssetRipper.SourceGenerated.Classes.ClassID_115;

namespace AssetRipper.Export.UserPackages;

/// <summary>
/// Decides which of a build's package assemblies can be replaced by a reference to the package itself.
/// </summary>
/// <remarks>
/// Replacing an assembly means two things happen together: its DLL is no longer written into the project, and every
/// component that pointed at a script inside it is pointed at the same script inside the package. Both have to happen,
/// because keeping the DLL alongside the package would declare every type twice, and dropping it without repointing
/// would leave those components with no script at all.
/// <para>
/// That is why the decision is made per assembly and only when every script the game actually references inside it can
/// be found in the package. One unresolved script is enough to leave the whole assembly alone, since there would be no
/// way to keep that one component bound.
/// </para>
/// </remarks>
public sealed class UnityPackageRelinker
{
	private readonly UnityPackageIndex index;
	private readonly Dictionary<string, string> packageByAssembly = new(StringComparer.Ordinal);
	private readonly Dictionary<string, string> guidByScript = new(StringComparer.Ordinal);

	private UnityPackageRelinker(UnityPackageIndex index)
	{
		this.index = index;
	}

	/// <summary>
	/// The packages the relinked assemblies came from, to be added to the project's manifest.
	/// </summary>
	public Dictionary<string, string> RequiredPackages { get; } = new(StringComparer.Ordinal);

	public bool IsRelinked(string assemblyName) => packageByAssembly.ContainsKey(assemblyName);

	public bool TryGetScriptGuid(IMonoScript script, [NotNullWhen(true)] out string? guid)
	{
		return guidByScript.TryGetValue(Key(script), out guid);
	}

	/// <summary>
	/// Works out what can be relinked, or returns null when nothing can.
	/// </summary>
	/// <param name="referencedScripts">
	/// The scripts something in the game actually points at. Types that are never referenced cannot lose a binding, so
	/// they do not affect the decision.
	/// </param>
	public static UnityPackageRelinker? TryCreate(UnityVersion version, IEnumerable<IMonoScript> referencedScripts, IEnumerable<string> presentAssemblies)
	{
		UnityPackageIndex? index = UnityPackageIndex.TryCreate(version);
		if (index is null)
		{
			return null;
		}

		UnityPackageRelinker relinker = new(index);

		// Every assembly the build carries that maps to a package is a candidate, not just the ones holding a
		// referenced script. An assembly with nothing referencing it still has to go: leaving it beside the package
		// that replaces it declares the same types twice, and a stripped copy shadowing the real one breaks the
		// package's own source.
		Dictionary<string, List<IMonoScript>> byAssembly = new(StringComparer.Ordinal);
		foreach (string assembly in presentAssemblies)
		{
			if (UnityPackageMap.TryGetPackage(assembly, out _))
			{
				byAssembly.TryAdd(assembly, []);
			}
		}

		foreach (IMonoScript script in referencedScripts)
		{
			string assembly = GetAssemblyName(script);
			if (byAssembly.TryGetValue(assembly, out List<IMonoScript>? scripts))
			{
				scripts.Add(script);
			}
		}

		foreach ((string assembly, List<IMonoScript> scripts) in byAssembly)
		{
			UnityPackageMap.TryGetPackage(assembly, out string? packageId);
			string? packageVersion = index.GetPackageVersion(packageId!);
			if (packageVersion is null)
			{
				Logger.Info(LogCategory.Export, $"'{assembly}' maps to '{packageId}', which this editor does not offer. Keeping the assembly.");
				continue;
			}

			Dictionary<string, string> resolved = new(StringComparer.Ordinal);
			string? unresolved = null;
			foreach (IMonoScript script in scripts)
			{
				if (index.TryGetScriptGuid(packageId!, script.ClassName_R.String, out string? guid))
				{
					resolved[Key(script)] = guid;
				}
				else
				{
					unresolved = script.ClassName_R.String;
					break;
				}
			}

			if (unresolved is not null)
			{
				Logger.Info(LogCategory.Export, $"'{assembly}' keeps its assembly: '{unresolved}' was not found in '{packageId}'.");
				continue;
			}

			relinker.packageByAssembly[assembly] = packageId!;
			relinker.RequiredPackages[packageId!] = packageVersion;
			foreach ((string key, string guid) in resolved)
			{
				relinker.guidByScript[key] = guid;
			}
			Logger.Info(LogCategory.Export, $"Relinking '{assembly}' to '{packageId}' {packageVersion} ({scripts.Count} referenced scripts)");
		}

		return relinker.packageByAssembly.Count > 0 ? relinker : null;
	}

	/// <summary>
	/// Collects the scripts that something in the game points at.
	/// </summary>
	public static IEnumerable<IMonoScript> GetReferencedScripts(AssetRipper.Processing.GameData gameData)
	{
		HashSet<IMonoScript> scripts = [];
		foreach (IMonoBehaviour behaviour in gameData.GameBundle.FetchAssets().OfType<IMonoBehaviour>())
		{
			if (behaviour.ScriptP is { } script)
			{
				scripts.Add(script);
			}
		}
		return scripts;
	}

	private static string GetAssemblyName(IMonoScript script)
	{
		return SpecialFileNames.RemoveAssemblyFileExtension(script.AssemblyName.String);
	}

	private static string Key(IMonoScript script)
	{
		return $"{GetAssemblyName(script)}|{script.Namespace.String}|{script.ClassName_R.String}";
	}
}
