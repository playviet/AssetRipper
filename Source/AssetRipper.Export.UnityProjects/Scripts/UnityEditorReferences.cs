using AssetRipper.Import.Logging;
using Microsoft.CodeAnalysis;

namespace AssetRipper.Export.UnityProjects.Scripts;

/// <summary>
/// Compiles the recovered source against the editor's own Unity assemblies where that editor is installed, instead of
/// against the stripped copies the game shipped.
/// </summary>
/// <remarks>
/// An il2cpp build runs the managed linker over the engine assemblies, so what the game carries is only the members it
/// reached. Everything else is gone: <c>Mathf.PI</c> is a <c>const</c> that every caller inlined, so no IL refers to
/// it and the linker drops the field entirely.
/// <para>
/// That is a problem for the repair pass and for nothing else. The exported project is opened in a real editor, which
/// compiles it against the full assemblies, but the repair decides what to comment out by compiling against the game's
/// stripped ones - so it throws away statements that the editor would have accepted. In this game 22 statements
/// mentioning <c>Mathf.PI</c> were commented out and not one survived, and the same happened to
/// <c>Quaternion.Internal_FromEulerRad</c> and its neighbours.
/// </para>
/// <para>
/// Where the matching editor is installed, its assemblies are exactly the ones the project will be compiled against,
/// so using them makes this pass answer the question the editor is going to ask. It is the same reasoning that already
/// takes the core libraries from the runtime rather than from the game, extended to the engine. Where it is not
/// installed nothing changes, and a member that is genuinely private stays rejected either way - so a statement is
/// still only kept when a real compiler accepts it.
/// </para>
/// </remarks>
internal static class UnityEditorReferences
{
	/// <summary>
	/// Overrides where to look, for an editor the standard locations do not cover.
	/// </summary>
	private const string PathVariable = "ASSETRIPPER_UNITY_MANAGED";

	/// <summary>
	/// The game's own assemblies as references, with any that the installed editor also has replaced by the editor's.
	/// </summary>
	public static List<MetadataReference> Prefer(List<AssemblyMetadata> metadata, UnityVersion version)
	{
		Dictionary<string, string> editor = Available(version);
		List<MetadataReference> references = [];
		int replaced = 0;

		foreach (AssemblyMetadata assembly in metadata)
		{
			string name;
			try
			{
				name = assembly.GetModules()[0].Name;
			}
			catch (Exception)
			{
				//A file that is not a managed assembly after all; it was added on the same terms and is dropped here.
				continue;
			}

			if (editor.ContainsKey(name))
			{
				replaced++;
				continue;
			}

			references.Add(assembly.GetReference());
		}

		foreach (string path in editor.Values)
		{
			try
			{
				references.Add(MetadataReference.CreateFromFile(path));
			}
			catch (Exception)
			{
				//Unreadable, so the game's copy would have been the better reference - but it has already been
				//dropped, and a missing reference costs less than a corrupt one.
			}
		}

		if (replaced > 0)
		{
			Logger.Info(LogCategory.Export, $"Checking decompiled source against {replaced} unstripped assemblies from the installed Unity {version}");
		}

		return references;
	}

	/// <summary>
	/// The editor assemblies for this version, by file name.
	/// </summary>
	private static Dictionary<string, string> Available(UnityVersion version)
	{
		Dictionary<string, string> found = [];

		foreach (string directory in Directories(version))
		{
			if (!Directory.Exists(directory))
			{
				continue;
			}

			foreach (string path in Directory.EnumerateFiles(directory, "*.dll"))
			{
				//The first directory to hold an assembly wins, so the listing is in the order it should be preferred.
				found.TryAdd(Path.GetFileName(path), path);
			}
		}

		return found;
	}

	/// <summary>
	/// Where an editor of this version keeps the assemblies a project is compiled against.
	/// </summary>
	/// <remarks>
	/// Only the exact version, never a near one: a project compiled against a different minor version of the engine
	/// reports errors that belong to the mismatch rather than to the recovery, which is worse than the stripping this
	/// is here to undo.
	/// </remarks>
	private static IEnumerable<string> Directories(UnityVersion version)
	{
		if (Environment.GetEnvironmentVariable(PathVariable) is { Length: > 0 } configured)
		{
			yield return configured;
			yield return Path.Combine(configured, "UnityEngine");
		}

		if (version == default)
		{
			yield break;
		}

		string name = version.ToString();

		foreach (string root in Roots())
		{
			string install = Path.Combine(root, name);

			//macOS keeps the managed assemblies inside the application bundle; Windows and Linux do not.
			foreach (string managed in (string[])["Unity.app/Contents/Managed", "Editor/Data/Managed", "Editor/Unity.app/Contents/Managed"])
			{
				yield return Path.Combine(install, managed, "UnityEngine");
			}
		}
	}

	private static IEnumerable<string> Roots()
	{
		yield return "/Applications/Unity/Hub/Editor";
		yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Unity/Hub/Editor");
		yield return "C:/Program Files/Unity/Hub/Editor";
	}
}
