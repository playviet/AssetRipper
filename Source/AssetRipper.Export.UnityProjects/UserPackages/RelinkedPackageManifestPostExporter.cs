using AssetRipper.Export.Configuration;
using AssetRipper.Export.UnityProjects.Project;
using AssetRipper.Export.UserPackages;
using AssetRipper.Import.Logging;

namespace AssetRipper.Export.UnityProjects.UserPackages;

/// <summary>
/// Writes the manifest, adding the packages whose assemblies were replaced by a reference to them.
/// </summary>
/// <remarks>
/// Without these entries the project would have neither the assemblies nor the packages that replaced them, so every
/// script in them would be missing.
/// </remarks>
public sealed class RelinkedPackageManifestPostExporter(UnityPackageRelinker relinker, UserPackageData userPackages)
	: UserPackageManifestPostExporter(userPackages)
{
	protected override PackageManifest CreateManifest(UnityVersion version)
	{
		PackageManifest manifest = base.CreateManifest(version);
		foreach ((string packageId, string packageVersion) in relinker.RequiredPackages)
		{
			if (manifest.Dependencies.TryAdd(packageId, packageVersion))
			{
				Logger.Info(LogCategory.Export, $"Added '{packageId}' {packageVersion} to the project manifest");
			}
		}
		return manifest;
	}
}
