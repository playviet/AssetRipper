namespace AssetRipper.Export.UnityProjects.Scripts;

public enum AssemblyExportType
{
	/// <summary>
	/// The assembly should be decompiled.
	/// </summary>
	Decompile,
	/// <summary>
	/// The assembly should be saved as is.
	/// </summary>
	Save,
	/// <summary>
	/// The assembly is a framework assembly and should be skipped.
	/// </summary>
	Skip,
	/// <summary>
	/// The assembly belongs to a package the project references instead, so it is neither written nor decompiled and
	/// its scripts point into that package.
	/// </summary>
	Relink,
}
