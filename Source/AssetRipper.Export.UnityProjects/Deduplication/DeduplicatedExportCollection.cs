using AssetRipper.Assets;
using AssetRipper.Assets.Collections;
using AssetRipper.IO.Files.SerializedFiles;

namespace AssetRipper.Export.UnityProjects.Deduplication;

/// <summary>
/// A collection that redirects a duplicated <paramref name="Asset"/> to the <paramref name="Original"/> it duplicates.
/// </summary>
/// <remarks>
/// Unlike <see cref="SingleRedirectExportCollection"/>, the target is resolved at export time rather than up front.
/// The pointer to <paramref name="Original"/> is not known while collections are still being created, because it
/// depends on the collection that <paramref name="Original"/> itself ends up in.
/// </remarks>
/// <param name="Asset">The duplicate, which is not exported.</param>
/// <param name="Original">The asset that <paramref name="Asset"/> is redirected to.</param>
public sealed record class DeduplicatedExportCollection(IUnityObjectBase Asset, IUnityObjectBase Original) : IExportCollection
{
	AssetCollection IExportCollection.File => Asset.Collection;

	TransferInstructionFlags IExportCollection.Flags => Asset.Collection.Flags;

	IEnumerable<IUnityObjectBase> IExportCollection.Assets => [Asset];

	public string Name => Asset.GetBestName();

	bool IExportCollection.Exportable => false;

	bool IExportCollection.Contains(IUnityObjectBase asset) => ReferenceEquals(Asset, asset);

	MetaPtr IExportCollection.CreateExportPointer(IExportContainer container, IUnityObjectBase asset, bool isLocal)
	{
		return container.CreateExportPointer(Original);
	}

	long IExportCollection.GetExportID(IExportContainer container, IUnityObjectBase asset)
	{
		return container.GetExportID(Original);
	}

	bool IExportCollection.Export(IExportContainer container, string projectDirectory, FileSystem fileSystem)
	{
		throw new NotSupportedException();
	}
}
