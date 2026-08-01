using AsmResolver.DotNet;
using ICSharpCode.Decompiler.CSharp.ProjectDecompiler;

namespace AssetRipper.Export.UnityProjects.Scripts;

/// <summary>
/// Where the decompiler puts each type of an assembly, worked out without decompiling anything.
/// </summary>
/// <remarks>
/// Everything that writes beside a decompiled script has to name the file the way the decompiler named it, so the
/// naming lives here once and is done with the decompiler's own helpers rather than with a guess at what they do.
/// </remarks>
internal static class ScriptFileLayout
{
	public const string ScriptExtension = ".cs";

	/// <param name="Path">The path of the file, relative to the folder the assembly was decompiled into.</param>
	/// <param name="TopLevelTypes">The types the decompiler declares at the top level of the file.</param>
	/// <param name="AllTypes">The same types with their nested types, in declaration order.</param>
	public readonly record struct ScriptFile(string Path, List<TypeDefinition> TopLevelTypes, List<TypeDefinition> AllTypes);

	/// <summary>
	/// Groups the types of an assembly the way the decompiler groups them into files.
	/// </summary>
	/// <remarks>
	/// A file is named after the top level type it holds, and a nested type is written inside its declaring type, so it
	/// belongs to the same file. Two top level types that differ only in case share a file, which is why the paths are
	/// compared without case: the decompiler groups them the same way, and one of the two would otherwise be lost.
	/// </remarks>
	public static List<ScriptFile> GroupTypesByFile(AssemblyDefinition assembly, bool nestedDirectoriesForNamespaces)
	{
		List<ScriptFile> files = new();
		Dictionary<string, int> indices = new(StringComparer.OrdinalIgnoreCase);

		foreach (ModuleDefinition module in assembly.Modules)
		{
			foreach (TypeDefinition type in module.TopLevelTypes)
			{
				if (type.Name == "<Module>")
				{
					continue;
				}

				string path = GetRelativeScriptPath(type, nestedDirectoriesForNamespaces);
				if (!indices.TryGetValue(path, out int index))
				{
					index = files.Count;
					indices.Add(path, index);
					files.Add(new ScriptFile(path, new List<TypeDefinition>(), new List<TypeDefinition>()));
				}

				files[index].TopLevelTypes.Add(type);
				AddWithNestedTypes(files[index].AllTypes, type);
			}
		}

		return files;
	}

	public static string GetRelativeScriptPath(TypeDefinition type, bool nestedDirectoriesForNamespaces)
	{
		string fileName = WholeProjectDecompiler.CleanUpFileName(type.Name?.ToString() ?? "", ScriptExtension);
		string @namespace = type.Namespace?.ToString() ?? "";
		if (@namespace.Length == 0)
		{
			return fileName;
		}

		string directory = nestedDirectoriesForNamespaces
			? WholeProjectDecompiler.CleanUpPath(@namespace)
			: WholeProjectDecompiler.CleanUpDirectoryName(@namespace);
		return Path.Combine(directory, fileName);
	}

	private static void AddWithNestedTypes(List<TypeDefinition> destination, TypeDefinition type)
	{
		destination.Add(type);
		foreach (TypeDefinition nested in type.NestedTypes)
		{
			AddWithNestedTypes(destination, nested);
		}
	}
}
