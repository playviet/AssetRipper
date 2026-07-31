using AssetRipper.Export.Configuration;
using AssetRipper.Export.UnityProjects.Project;
using AssetRipper.Import.Logging;

namespace AssetRipper.Export.UnityProjects.UserPackages;

/// <summary>
/// Writes the manifest, adding the packages that the user declared so that the exported project pulls them in.
/// </summary>
public sealed class UserPackageManifestPostExporter(UserPackageData data) : PackageManifestPostExporter
{
	protected override PackageManifest CreateManifest(UnityVersion version)
	{
		PackageManifest manifest = base.CreateManifest(version);
		foreach (UserPackage package in data.Packages)
		{
			if (string.IsNullOrEmpty(package.Name))
			{
				continue;
			}
			if (!manifest.Dependencies.TryAdd(package.Name, package.Version))
			{
				Logger.Warning(LogCategory.Export, $"Package '{package.Name}' is already a dependency. Keeping the existing version.");
			}
		}
		return manifest;
	}
}
