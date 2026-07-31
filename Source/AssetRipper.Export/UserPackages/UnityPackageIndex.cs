using AssetRipper.Import.Logging;
using AssetRipper.Primitives;
using System.Formats.Tar;
using System.IO.Compression;
using System.Text.Json;

namespace AssetRipper.Export.UserPackages;

/// <summary>
/// Reads package information out of an installed Unity editor.
/// </summary>
/// <remarks>
/// Every editor ships the packages it recommends, as tarballs, alongside a manifest naming the exact version of each.
/// Reading that is the only way to relink a build's package assemblies without guessing: a wrong version in
/// <c>manifest.json</c> stops the project opening at all, and a wrong script GUID silently unbinds every component
/// that used it.
/// <para>
/// The packages ship as source, so each script has a <c>.meta</c> beside it holding the GUID that the exported project
/// has to point at.
/// </para>
/// </remarks>
public sealed class UnityPackageIndex
{
	/// <summary>
	/// The file ID Unity gives a MonoBehaviour script asset. Constant for every source script.
	/// </summary>
	public const long ScriptFileID = 11500000;

	/// <summary>
	/// Marks a class name that more than one file in the package declares, so it cannot be resolved from a name alone.
	/// </summary>
	private const string Ambiguous = "";

	private readonly string packageDirectory;
	private readonly Dictionary<string, string> packageVersions;
	private readonly Dictionary<string, Dictionary<string, string>> guidsByPackage = new(StringComparer.Ordinal);

	private UnityPackageIndex(string packageDirectory, Dictionary<string, string> packageVersions)
	{
		this.packageDirectory = packageDirectory;
		this.packageVersions = packageVersions;
	}

	/// <summary>
	/// Finds the editor matching <paramref name="version"/> and reads what packages it offers.
	/// </summary>
	/// <returns>Null when that editor is not installed, in which case relinking has to be skipped.</returns>
	public static UnityPackageIndex? TryCreate(UnityVersion version)
	{
		string versionString = version.ToString();
		foreach (string candidate in GetEditorPackageDirectories(versionString))
		{
			string manifestPath = Path.Join(candidate, "manifest.json");
			if (!File.Exists(manifestPath))
			{
				continue;
			}

			Dictionary<string, string> versions = ReadPackageVersions(manifestPath);
			if (versions.Count == 0)
			{
				continue;
			}

			Logger.Info(LogCategory.Export, $"Found Unity {versionString} package data at '{candidate}'");
			return new UnityPackageIndex(candidate, versions);
		}

		Logger.Warning(LogCategory.Export, $"No installed Unity {versionString} was found, so package relinking is unavailable. Install that editor version to enable it.");
		return null;
	}

	/// <summary>
	/// The version of <paramref name="packageId"/> that this editor recommends, or null when it does not offer it.
	/// </summary>
	public string? GetPackageVersion(string packageId)
	{
		return packageVersions.TryGetValue(packageId, out string? version) ? version : null;
	}

	/// <summary>
	/// The GUID of the script named <paramref name="className"/> inside <paramref name="packageId"/>.
	/// </summary>
	/// <remarks>
	/// Scripts are matched by file name, because Unity requires a MonoBehaviour's file name to match its class name,
	/// and MonoBehaviours are the only scripts whose GUID an exported asset can refer to.
	/// </remarks>
	public bool TryGetScriptGuid(string packageId, string className, [NotNullWhen(true)] out string? guid)
	{
		if (!guidsByPackage.TryGetValue(packageId, out Dictionary<string, string>? guids))
		{
			guids = ReadScriptGuids(packageId);
			guidsByPackage.Add(packageId, guids);
		}

		// An ambiguous name is reported as not found. Binding a component to whichever file happened to be read first
		// would be wrong in a way nothing downstream could detect.
		if (guids.TryGetValue(className, out guid) && guid.Length == 32)
		{
			return true;
		}
		guid = null;
		return false;
	}

	private Dictionary<string, string> ReadScriptGuids(string packageId)
	{
		Dictionary<string, string> guids = new(StringComparer.Ordinal);
		int duplicates = 0;

		// An editor holds its packages two ways: the ones it bundles are extracted under BuiltInPackages, and the rest
		// sit beside the manifest as tarballs. The scriptable render pipelines and uGUI are bundled, so a tarball only
		// lookup finds nothing for exactly the packages a project is most likely to use.
		string builtInDirectory = Path.Join(packageDirectory, "..", "BuiltInPackages", packageId);
		if (Directory.Exists(builtInDirectory))
		{
			foreach (string metaPath in Directory.EnumerateFiles(builtInDirectory, "*.cs.meta", SearchOption.AllDirectories))
			{
				using FileStream stream = File.OpenRead(metaPath);
				if (TryReadGuid(stream, out string? guid) && !guids.TryAdd(GetClassName(metaPath), guid))
				{
					duplicates++;
					guids[GetClassName(metaPath)] = Ambiguous;
				}
			}
			Report(packageId, guids.Count, duplicates);
			return guids;
		}

		string? version = GetPackageVersion(packageId);
		if (version is null)
		{
			return guids;
		}

		string tarballPath = Path.Join(packageDirectory, $"{packageId}-{version}.tgz");
		if (!File.Exists(tarballPath))
		{
			Logger.Warning(LogCategory.Export, $"Package '{packageId}' {version} is recommended by the editor but neither its tarball nor a bundled copy was found, so its scripts cannot be relinked.");
			return guids;
		}

		try
		{
			using FileStream file = File.OpenRead(tarballPath);
			using GZipStream gzip = new(file, CompressionMode.Decompress);
			using TarReader tar = new(gzip);

			while (tar.GetNextEntry() is TarEntry entry)
			{
				if (entry.DataStream is null || !entry.Name.EndsWith(".cs.meta", StringComparison.Ordinal))
				{
					continue;
				}

				if (TryReadGuid(entry.DataStream, out string? guid) && !guids.TryAdd(GetClassName(entry.Name), guid))
				{
					duplicates++;
					guids[GetClassName(entry.Name)] = Ambiguous;
				}
			}
		}
		catch (Exception ex)
		{
			Logger.Warning(LogCategory.Export, $"Could not read package '{packageId}': {ex.GetType().Name}: {ex.Message}");
			return guids;
		}

		Report(packageId, guids.Count, duplicates);
		return guids;
	}

	private static string GetClassName(string metaPath) => Path.GetFileName(metaPath)[..^".cs.meta".Length];

	private static void Report(string packageId, int found, int duplicates)
	{
		if (duplicates > 0)
		{
			// Two scripts of the same name in different folders cannot be told apart from a class name alone, so
			// neither is offered. Binding to whichever was read first would be wrong in a way nothing downstream could
			// detect.
			Logger.Info(LogCategory.Export, $"Package '{packageId}' declares {duplicates} class names in more than one file. Those cannot be relinked and keep their existing binding.");
		}
		Logger.Info(LogCategory.Export, $"Read {found} script GUIDs from package '{packageId}'");
	}

	private static bool TryReadGuid(Stream stream, [NotNullWhen(true)] out string? guid)
	{
		using StreamReader reader = new(stream);
		while (reader.ReadLine() is string line)
		{
			if (line.StartsWith("guid:", StringComparison.Ordinal))
			{
				guid = line["guid:".Length..].Trim();
				return guid.Length == 32;
			}
		}
		guid = null;
		return false;
	}

	private static Dictionary<string, string> ReadPackageVersions(string manifestPath)
	{
		Dictionary<string, string> versions = new(StringComparer.Ordinal);
		try
		{
			using FileStream stream = File.OpenRead(manifestPath);
			using JsonDocument document = JsonDocument.Parse(stream);
			if (!document.RootElement.TryGetProperty("packages", out JsonElement packages))
			{
				return versions;
			}

			foreach (JsonProperty package in packages.EnumerateObject())
			{
				// "version" is what the editor ships. Entries without one are deprecated or metadata only.
				if (package.Value.TryGetProperty("version", out JsonElement version) && version.GetString() is string value)
				{
					versions[package.Name] = value;
				}
			}
		}
		catch (Exception ex)
		{
			Logger.Warning(LogCategory.Export, $"Could not read '{manifestPath}': {ex.GetType().Name}: {ex.Message}");
		}
		return versions;
	}

	/// <summary>
	/// Where an editor of the given version keeps its packages, on each platform Unity Hub installs to.
	/// </summary>
	private static IEnumerable<string> GetEditorPackageDirectories(string version)
	{
		const string Suffix = "PackageManager/Editor";

		string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		yield return Path.Join("/Applications/Unity/Hub/Editor", version, "Unity.app/Contents/Resources", Suffix);
		yield return Path.Join(home, "Applications/Unity/Hub/Editor", version, "Unity.app/Contents/Resources", Suffix);
		yield return Path.Join(@"C:\Program Files\Unity\Hub\Editor", version, @"Editor\Data\Resources", Suffix);
		yield return Path.Join(home, "Unity/Hub/Editor", version, "Editor/Data/Resources", Suffix);

		// Honour an explicit override, for installs that are not where the Hub puts them.
		if (Environment.GetEnvironmentVariable("ASSETRIPPER_UNITY_EDITOR") is string custom && custom.Length > 0)
		{
			yield return Path.Join(custom, "Resources", Suffix);
			yield return Path.Join(custom, Suffix);
		}
	}
}
