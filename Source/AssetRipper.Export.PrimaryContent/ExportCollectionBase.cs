using AssetRipper.Assets;

namespace AssetRipper.Export.PrimaryContent;

public abstract class ExportCollectionBase
{
	public abstract bool Contains(IUnityObjectBase asset);
	public abstract bool Export(string projectDirectory, FileSystem fileSystem);
	/// <summary>
	/// Identifies the output this collection writes to, so that parallel exporters can serialise collections that
	/// would otherwise write to the same file.
	/// </summary>
	public virtual string GetExportKey(string projectDirectory, FileSystem fileSystem) => Name;
	protected void ExportAsset(IUnityObjectBase asset, string directory, string name, FileSystem fileSystem)
	{
		if (!fileSystem.Directory.Exists(directory))
		{
			fileSystem.Directory.Create(directory);
		}

		string fullName = $"{name}.{ExportExtension}";
		string uniqueName = fileSystem.GetUniqueName(directory, fullName, FileSystem.MaxFileNameLength);
		string filePath = fileSystem.Path.Join(directory, uniqueName);
		ContentExtractor.Export(asset, filePath, fileSystem);
	}

	protected string GetUniqueFileName(IUnityObjectBase asset, string dirPath, FileSystem fileSystem)
	{
		string fileName = asset.GetBestName();
		fileName = FileSystem.RemoveCloneSuffixes(fileName);
		fileName = FileSystem.RemoveInstanceSuffixes(fileName);
		fileName = fileName.Trim();
		if (string.IsNullOrEmpty(fileName))
		{
			fileName = asset.ClassName;
		}
		else
		{
			fileName = FileSystem.FixInvalidFileNameCharacters(fileName);
		}

		fileName = $"{fileName}.{ExportExtension}";
		return GetUniqueFileName(dirPath, fileName, fileSystem);
	}

	/// <summary>
	/// The name <see cref="GetUniqueFileName(IUnityObjectBase, string, FileSystem)"/> starts from, before it is made
	/// unique against what is already on disk.
	/// </summary>
	/// <remarks>
	/// Uniquifying reads the directory, so two collections whose base names collide must not run at the same time or
	/// both can pick the same "unique" name. This is what lets a parallel exporter tell those collections apart.
	/// </remarks>
	protected string GetBaseFileName(IUnityObjectBase asset)
	{
		string fileName = asset.GetBestName();
		fileName = FileSystem.RemoveCloneSuffixes(fileName);
		fileName = FileSystem.RemoveInstanceSuffixes(fileName);
		fileName = fileName.Trim();
		fileName = string.IsNullOrEmpty(fileName)
			? asset.ClassName
			: FileSystem.FixInvalidFileNameCharacters(fileName);
		return $"{fileName}.{ExportExtension}";
	}

	protected virtual string ExportExtension => "asset";

	protected static string GetUniqueFileName(string directoryPath, string fileName, FileSystem fileSystem)
	{
		return fileSystem.GetUniqueName(directoryPath, fileName, FileSystem.MaxFileNameLength);
	}

	public abstract IContentExtractor ContentExtractor { get; }
	public abstract IEnumerable<IUnityObjectBase> Assets { get; }
	public virtual IEnumerable<IUnityObjectBase> ExportableAssets => Assets;
	public virtual bool Exportable => true;
	public abstract string Name { get; }
}
