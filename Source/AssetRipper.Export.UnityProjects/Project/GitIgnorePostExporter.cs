using AssetRipper.Export.Configuration;
using AssetRipper.Processing;

namespace AssetRipper.Export.UnityProjects.Project;

/// <summary>
/// Writes a .gitignore for the exported project.
/// </summary>
/// <remarks>
/// An exported project is normally the starting point for work that gets committed somewhere. Without this, the first
/// commit picks up Library, Temp and the generated solution files, which for a ripped game runs to gigabytes.
/// <para>
/// The contents follow the .gitignore Unity itself ships for new projects.
/// </para>
/// </remarks>
public sealed class GitIgnorePostExporter : IPostExporter
{
	private const string Contents = """
		# Unity generated
		[Ll]ibrary/
		[Tt]emp/
		[Oo]bj/
		[Bb]uild/
		[Bb]uilds/
		[Ll]ogs/
		[Uu]ser[Ss]ettings/
		[Mm]emoryCaptures/

		# Asset meta data should only be ignored when the corresponding asset is also ignored
		!/[Aa]ssets/**/*.meta

		# Visual Studio and Rider
		.vs/
		.idea/
		ExportedObj/
		*.csproj
		*.unityproj
		*.sln
		*.suo
		*.user
		*.userprefs
		*.pidb
		*.booproj
		*.svd
		*.pdb
		*.mdb
		*.opendb
		*.VC.db

		# OS
		.DS_Store
		Thumbs.db

		# Crash reports
		sysinfo.txt

		# Builds
		*.apk
		*.aab
		*.unitypackage
		*.app

		# Crashlytics
		crashlytics-build.properties

		# Addressables
		/[Aa]ssets/[Aa]ddressable[Aa]ssets[Dd]ata/*/*.bin*
		/[Aa]ssets/[Ss]treamingAssets/aa.meta
		/[Aa]ssets/[Ss]treamingAssets/aa/*

		""";

	public void DoPostExport(GameData gameData, FullConfiguration settings, FileSystem fileSystem)
	{
		string path = fileSystem.Path.Join(settings.ProjectRootPath, ".gitignore");

		// Never overwrite one that is already there. Exporting into an existing repository would otherwise throw away
		// whatever rules it had.
		if (!fileSystem.File.Exists(path))
		{
			fileSystem.File.WriteAllText(path, Contents);
		}
	}
}
