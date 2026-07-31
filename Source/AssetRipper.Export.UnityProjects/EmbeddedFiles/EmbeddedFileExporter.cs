using AssetRipper.Assets;
using AssetRipper.Export.Configuration;
using AssetRipper.Import.Logging;
using AssetRipper.Import.Structure.Assembly;
using AssetRipper.Import.Structure.Assembly.Serializable;
using AssetRipper.SourceGenerated.Classes.ClassID_114;
using AssetRipper.SourceGenerated.Classes.ClassID_115;
using AssetRipper.SourceGenerated.Extensions;

namespace AssetRipper.Export.UnityProjects.EmbeddedFiles;

/// <summary>
/// Writes out payloads that a game stored inside a serialized field.
/// </summary>
/// <remarks>
/// A game's own data often lives in a byte array or string field on a ScriptableObject rather than as an asset Unity
/// understands. Exported normally that payload ends up inline in a YAML file, which nothing can open. A rule naming
/// the field writes it out as a real file instead.
/// </remarks>
public sealed class EmbeddedFileExporter : BinaryAssetExporter
{
	private readonly List<EmbeddedFileRule> rules;
	private int exportedCount;

	public EmbeddedFileExporter(EmbeddedFileData data)
	{
		rules = [.. data.Rules.Where(r => r.Field.Length > 0)];
	}

	public bool HasRules => rules.Count > 0;

	public override bool TryCreateCollection(IUnityObjectBase asset, [NotNullWhen(true)] out IExportCollection? exportCollection)
	{
		// The field is checked here rather than at export time. Claiming the asset and then failing would leave it
		// exported as nothing at all, which is worse than the yaml a later exporter would have written.
		if (rules.Count > 0
			&& asset is IMonoBehaviour monoBehaviour
			&& FindRule(monoBehaviour) is { } rule
			&& monoBehaviour.LoadStructure() is { } structure
			&& structure.TryGetField(rule.Field, out _))
		{
			exportCollection = new EmbeddedFileExportCollection(this, monoBehaviour, rule.Extension);
			return true;
		}
		exportCollection = null;
		return false;
	}

	public override bool Export(IExportContainer container, IUnityObjectBase asset, string path, FileSystem fileSystem)
	{
		if (asset is not IMonoBehaviour monoBehaviour || FindRule(monoBehaviour) is not { } rule)
		{
			return false;
		}

		SerializableStructure? structure = monoBehaviour.LoadStructure();
		if (structure is null || !structure.TryGetField(rule.Field, out SerializableValue value))
		{
			Logger.Warning(LogCategory.Export, $"'{asset.GetBestName()}' matched an embedded file rule but has no field '{rule.Field}'.");
			return false;
		}

		try
		{
			if (rule.Text)
			{
				fileSystem.File.WriteAllText(path, value.AsString);
			}
			else
			{
				fileSystem.File.WriteAllBytes(path, value.AsByteArray);
			}
		}
		catch (Exception ex)
		{
			// The field exists but does not hold what the rule claims. That is the user's rule to fix, not a reason to
			// fail the whole export.
			Logger.Warning(LogCategory.Export, $"Could not write field '{rule.Field}' of '{asset.GetBestName()}' as a file: {ex.GetType().Name}: {ex.Message}");
			return false;
		}

		exportedCount++;
		return true;
	}

	public void LogSummary()
	{
		if (rules.Count > 0)
		{
			Logger.Info(LogCategory.Export, $"Wrote {exportedCount} embedded {(exportedCount == 1 ? "file" : "files")}");
		}
	}

	private EmbeddedFileRule? FindRule(IMonoBehaviour monoBehaviour)
	{
		IMonoScript? script = monoBehaviour.ScriptP;
		foreach (EmbeddedFileRule rule in rules)
		{
			if (Matches(rule, monoBehaviour, script))
			{
				return rule;
			}
		}
		return null;
	}

	private static bool Matches(EmbeddedFileRule rule, IMonoBehaviour monoBehaviour, IMonoScript? script)
	{
		if (rule.ScriptNamespace is not null && script?.Namespace.String != rule.ScriptNamespace)
		{
			return false;
		}
		if (rule.ScriptClass is not null && script?.ClassName_R.String != rule.ScriptClass)
		{
			return false;
		}
		if (rule.NameSuffix is not null && !monoBehaviour.GetBestName().EndsWith(rule.NameSuffix, StringComparison.Ordinal))
		{
			return false;
		}
		if (rule.DirectoryPrefix is not null && !DirectoryMatches(monoBehaviour.OriginalDirectory, rule.DirectoryPrefix))
		{
			return false;
		}
		return true;
	}

	/// <summary>
	/// Compares directories with either slash style, because the recorded path comes from whichever platform built the
	/// game rather than the one running the export.
	/// </summary>
	private static bool DirectoryMatches(string? directory, string prefix)
	{
		return directory is not null
			&& directory.Replace('\\', '/').StartsWith(prefix.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);
	}

	private sealed class EmbeddedFileExportCollection(EmbeddedFileExporter exporter, IMonoBehaviour asset, string extension)
		: AssetExportCollection<IMonoBehaviour>(exporter, asset)
	{
		protected override string GetExportExtension(IUnityObjectBase asset) => extension;
	}
}
