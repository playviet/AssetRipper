using AssetRipper.Export.Configuration;
using AssetRipper.Import.Logging;
using AssetRipper.Processing;
using AssetRipper.SourceGenerated.Classes.ClassID_48;

namespace AssetRipper.Export.UnityProjects.Shaders;

/// <summary>
/// Renames exported shaders so they cannot collide with shaders Unity already provides.
/// </summary>
/// <remarks>
/// A build contains the shaders of whatever render pipeline it used. The exported project normally references that
/// same pipeline as a package, so Unity ends up with two shaders declaring the same name. It resolves that silently by
/// picking one, and materials can bind to the package's shader rather than the exported one, which looks like the
/// export losing material settings. Renaming makes the exported shaders distinct.
/// </remarks>
public sealed class ShaderNamingProcessor(ShaderNamingMode mode, string suffix = " (Ripped)") : IAssetProcessor
{
	public void Process(GameData gameData)
	{
		if (mode != ShaderNamingMode.Suffixed || suffix.Length == 0)
		{
			return;
		}

		int renamed = 0;
		foreach (IShader shader in gameData.GameBundle.FetchAssets().OfType<IShader>())
		{
			// The name lives on the parsed form for 5.5 and later. Older shaders keep their name inside their
			// ShaderLab source instead, where renaming would mean rewriting that source, so they are left alone.
			if (!shader.Has_ParsedForm())
			{
				continue;
			}

			string name = shader.ParsedForm.Name.String;
			if (name.Length == 0 || name.EndsWith(suffix, StringComparison.Ordinal))
			{
				continue;
			}

			shader.ParsedForm.Name = name + suffix;
			renamed++;
		}

		Logger.Info(LogCategory.Processing, $"Renamed {renamed} {(renamed == 1 ? "shader" : "shaders")} with '{suffix}'");
	}
}
