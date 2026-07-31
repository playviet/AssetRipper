using AssetRipper.Assets;
using AssetRipper.Assets.Bundles;
using AssetRipper.Assets.Cloning;
using AssetRipper.Assets.Collections;
using AssetRipper.Assets.Generics;
using AssetRipper.Assets.Metadata;
using AssetRipper.Assets.Traversal;
using AssetRipper.Import.Logging;
using AssetRipper.Primitives;
using AssetRipper.SourceGenerated;
using AssetRipper.SourceGenerated.Classes.ClassID_0;
using AssetRipper.SourceGenerated.Classes.ClassID_1;
using AssetRipper.SourceGenerated.Classes.ClassID_1001;
using AssetRipper.SourceGenerated.Classes.ClassID_18;
using AssetRipper.SourceGenerated.Classes.ClassID_4;
using AssetRipper.SourceGenerated.Extensions;
using AssetRipper.SourceGenerated.Subclasses.PropertyModification;
using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AssetRipper.Processing.Prefabs;

/// <summary>
/// Reverses the prefab inlining that happens when a scene is built.
/// </summary>
/// <remarks>
/// Unity writes each prefab instance into the scene as plain GameObjects, so a prefab used many times appears as many
/// identical hierarchies. This processor finds those repetitions, extracts one copy into a prefab asset, and turns
/// every occurrence into an instance of it.
/// <para>
/// Two hierarchies are only outlined together when they are identical apart from the root's name and its position,
/// rotation, scale, and place in the parent, which become the instance's modifications. References that leave the
/// hierarchy have to point at the very same asset, not merely an equal one, so that hierarchies which differ only in,
/// say, which material they use are never merged.
/// </para>
/// </remarks>
public sealed class PrefabOutliningProcessor : IAssetProcessor
{
	/// <summary>
	/// Outlining a single GameObject is rarely worth a prefab asset of its own.
	/// </summary>
	private const int MinimumGameObjectCount = 2;

	public void Process(GameData gameData)
	{
		List<Occurrence> occurrences = [];
		ExternalAssetIdentity externalIdentity = new();

		foreach (AssetCollection collection in gameData.GameBundle.FetchAssetCollections())
		{
			if (!collection.IsScene)
			{
				continue;
			}
			foreach (IUnityObjectBase asset in collection)
			{
				if (asset is IGameObject gameObject && TryDescribe(gameObject, externalIdentity, out Occurrence occurrence))
				{
					occurrences.Add(occurrence);
				}
			}
		}

		// Larger hierarchies are outlined first so that an outlined hierarchy wins over the pieces inside it.
		List<List<Occurrence>> groups = occurrences
			.GroupBy(o => o.Signature)
			.Where(g => g.Count() >= 2)
			.Select(g => g.ToList())
			.Where(g => g[0].GameObjectCount >= MinimumGameObjectCount)
			.OrderByDescending(g => g[0].Elements.Count)
			.ToList();

		if (groups.Count == 0)
		{
			return;
		}

		ProcessedBundle bundle = gameData.GameBundle.AddNewProcessedBundle("Outlined Prefabs");
		ProcessedAssetCollection prefabCollection = bundle.AddNewProcessedCollection("Outlined Prefab Assets", gameData.ProjectVersion);
		ProcessedAssetCollection hierarchyCollection = bundle.AddNewProcessedCollection("Outlined Prefab Hierarchies", gameData.ProjectVersion);
		Dictionary<SceneDefinition, ProcessedAssetCollection> sceneCollections = new();

		HashSet<IUnityObjectBase> claimed = new();
		int prefabCount = 0;
		int instanceCount = 0;

		foreach (List<Occurrence> group in groups)
		{
			List<Occurrence> available = group.Where(o => !o.Elements.Any(claimed.Contains)).ToList();
			if (available.Count < 2)
			{
				continue;
			}

			Occurrence canonical = available[0];
			IGameObject prefabRoot = CloneHierarchy(canonical.Elements, prefabCollection, out Dictionary<IUnityObjectBase, IUnityObjectBase> sourceMap);
			DetachFromParent(prefabRoot);

			IPrefabInstance prefabAsset = prefabRoot.CreatePrefabForRoot(prefabCollection);
			PrefabHierarchyObject.Create(hierarchyCollection, prefabRoot, prefabAsset);
			prefabCount++;

			foreach (Occurrence occurrence in available)
			{
				if (occurrence.Root.MainAsset is not SceneHierarchyObject sceneHierarchy)
				{
					// Without the scene's hierarchy object there is nowhere to record the instance.
					continue;
				}

				ProcessedAssetCollection sceneCollection = GetOrCreateSceneCollection(gameData, bundle, sceneCollections, occurrence.Root.Collection.Scene);
				CreateInstance(sceneCollection, sceneHierarchy, occurrence, prefabAsset, prefabRoot, sourceMap);
				claimed.AddRange(occurrence.Elements);
				instanceCount++;
			}
		}

		Logger.Info(LogCategory.Processing, $"Outlined {prefabCount} {(prefabCount == 1 ? "prefab" : "prefabs")} covering {instanceCount} instances");
	}

	private static void CreateInstance(
		ProcessedAssetCollection sceneCollection,
		SceneHierarchyObject sceneHierarchy,
		Occurrence occurrence,
		IPrefabInstance prefabAsset,
		IGameObject prefabRoot,
		Dictionary<IUnityObjectBase, IUnityObjectBase> sourceMap)
	{
		IPrefabInstance instance = sceneCollection.CreatePrefabInstance();
		instance.RootGameObjectP = occurrence.Root;
		instance.IsPrefabAsset = false;
		instance.SourcePrefabP = prefabAsset;

		ITransform? occurrenceTransform = occurrence.Root.TryGetComponent<ITransform>();
		if (occurrenceTransform?.Father_C4P is { } father)
		{
			instance.Modification.TransformParent.SetAsset(sceneCollection, father);
		}

		AddModifications(instance, occurrence.Root, occurrenceTransform, prefabRoot, sceneCollection);

		foreach (IEditorExtension element in occurrence.Elements)
		{
			if (sourceMap.TryGetValue(element, out IUnityObjectBase? source) && source is IEditorExtension sourceExtension)
			{
				element.CorrespondingSourceObject_C18P = sourceExtension;
			}
			element.PrefabInstance_C18P = instance;
			sceneHierarchy.StrippedAssets.Add(element);
		}

		sceneHierarchy.PrefabInstances.Add(instance);
	}

	/// <summary>
	/// Records the values that make this instance differ from the prefab it was extracted from.
	/// </summary>
	private static void AddModifications(
		IPrefabInstance instance,
		IGameObject occurrenceRoot,
		ITransform? occurrenceTransform,
		IGameObject prefabRoot,
		AssetCollection sceneCollection)
	{
		AccessListBase<IPropertyModification> modifications = instance.Modification.Modifications;

		Add(modifications, sceneCollection, prefabRoot, "m_Name", occurrenceRoot.Name.String);

		if (occurrenceTransform is null || prefabRoot.TryGetComponent<ITransform>() is not { } prefabTransform)
		{
			return;
		}

		Add(modifications, sceneCollection, prefabTransform, "m_LocalPosition.x", occurrenceTransform.LocalPosition_C4.X);
		Add(modifications, sceneCollection, prefabTransform, "m_LocalPosition.y", occurrenceTransform.LocalPosition_C4.Y);
		Add(modifications, sceneCollection, prefabTransform, "m_LocalPosition.z", occurrenceTransform.LocalPosition_C4.Z);

		Add(modifications, sceneCollection, prefabTransform, "m_LocalRotation.x", occurrenceTransform.LocalRotation_C4.X);
		Add(modifications, sceneCollection, prefabTransform, "m_LocalRotation.y", occurrenceTransform.LocalRotation_C4.Y);
		Add(modifications, sceneCollection, prefabTransform, "m_LocalRotation.z", occurrenceTransform.LocalRotation_C4.Z);
		Add(modifications, sceneCollection, prefabTransform, "m_LocalRotation.w", occurrenceTransform.LocalRotation_C4.W);

		Add(modifications, sceneCollection, prefabTransform, "m_LocalScale.x", occurrenceTransform.LocalScale_C4.X);
		Add(modifications, sceneCollection, prefabTransform, "m_LocalScale.y", occurrenceTransform.LocalScale_C4.Y);
		Add(modifications, sceneCollection, prefabTransform, "m_LocalScale.z", occurrenceTransform.LocalScale_C4.Z);

		Add(modifications, sceneCollection, prefabTransform, "m_RootOrder", occurrenceTransform.RootOrder_C4);

		static void Add(AccessListBase<IPropertyModification> modifications, AssetCollection collection, IObject target, string path, object value)
		{
			IPropertyModification modification = modifications.AddNew();
			modification.Target.SetAsset(collection, target);
			modification.PropertyPath = path;
			modification.Value = value switch
			{
				float f => f.ToString("G9", CultureInfo.InvariantCulture),
				int i => i.ToString(CultureInfo.InvariantCulture),
				string s => s,
				_ => value.ToString() ?? "",
			};
		}
	}

	/// <summary>
	/// Copies a hierarchy into <paramref name="target"/>, rewriting the references between its own members to the copies
	/// while leaving references to the outside pointing at the originals.
	/// </summary>
	private static IGameObject CloneHierarchy(
		IReadOnlyList<IEditorExtension> elements,
		ProcessedAssetCollection target,
		out Dictionary<IUnityObjectBase, IUnityObjectBase> sourceMap)
	{
		Dictionary<IUnityObjectBase, IUnityObjectBase> map = new(elements.Count);
		List<IUnityObjectBase> clones = new(elements.Count);
		foreach (IEditorExtension element in elements)
		{
			IUnityObjectBase clone = target.CreateAsset(element.ClassID, AssetFactory.Create);
			map.Add(element, clone);
			clones.Add(clone);
		}

		MultipleReplacementAssetResolver resolver = new(map);
		for (int i = 0; i < elements.Count; i++)
		{
			clones[i].CopyValues(elements[i], new PPtrConverter(elements[i].Collection, target, resolver));
		}

		// The map is keyed by original and valued by clone, which is the direction the caller needs for
		// m_CorrespondingSourceObject.
		sourceMap = map;
		return (IGameObject)clones[0];
	}

	/// <summary>
	/// A prefab asset has no parent, so the copied root must not keep pointing into the scene it came from.
	/// </summary>
	private static void DetachFromParent(IGameObject prefabRoot)
	{
		if (prefabRoot.TryGetComponent<ITransform>() is { } transform)
		{
			transform.Father_C4P = null;
			transform.RootOrder_C4 = 0;
		}
	}

	private static ProcessedAssetCollection GetOrCreateSceneCollection(
		GameData gameData,
		ProcessedBundle bundle,
		Dictionary<SceneDefinition, ProcessedAssetCollection> sceneCollections,
		SceneDefinition scene)
	{
		if (sceneCollections.TryGetValue(scene, out ProcessedAssetCollection? existing))
		{
			return existing;
		}

		ProcessedAssetCollection collection = bundle.AddNewProcessedCollection(scene.Name + " (Outlined Prefab Instances)", gameData.ProjectVersion);
		scene.AddCollection(collection);
		sceneCollections.Add(scene, collection);
		return collection;
	}

	private static bool TryDescribe(IGameObject root, ExternalAssetIdentity externalIdentity, out Occurrence occurrence)
	{
		occurrence = default;

		List<IEditorExtension> elements;
		try
		{
			elements = root.FetchHierarchy().ToList();
		}
		catch (NullReferenceException)
		{
			// FetchHierarchy throws when the hierarchy references an asset that could not be found.
			return false;
		}

		int gameObjectCount = 0;
		foreach (IEditorExtension element in elements)
		{
			if (element is IGameObject)
			{
				gameObjectCount++;
			}
		}
		if (gameObjectCount < MinimumGameObjectCount)
		{
			return false;
		}

		Dictionary<IUnityObjectBase, int> indices = new(elements.Count);
		for (int i = 0; i < elements.Count; i++)
		{
			// A malformed hierarchy could list the same asset twice.
			indices.TryAdd(elements[i], i);
		}

		ITransform? rootTransform = root.TryGetComponent<ITransform>();

		using IncrementalHash incremental = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
		SignatureWalker walker = new(incremental, indices, externalIdentity);
		Span<byte> classID = stackalloc byte[4];
		foreach (IEditorExtension element in elements)
		{
			BinaryPrimitives.WriteInt32LittleEndian(classID, element.ClassID);
			incremental.AppendData(classID);

			walker.Begin(element, GetExcludedFields(element, root, rootTransform));
			element.WalkRelease(walker);
		}

		Span<byte> digest = stackalloc byte[32];
		incremental.GetHashAndReset(digest);
		HierarchySignature signature = new(
			BinaryPrimitives.ReadUInt64LittleEndian(digest),
			BinaryPrimitives.ReadUInt64LittleEndian(digest[8..]));

		occurrence = new Occurrence(root, elements, gameObjectCount, signature);
		return true;
	}

	private static IReadOnlySet<string> GetExcludedFields(IEditorExtension element, IGameObject root, ITransform? rootTransform)
	{
		if (ReferenceEquals(element, root))
		{
			return RootGameObjectExclusions;
		}
		if (rootTransform is not null && ReferenceEquals(element, rootTransform))
		{
			return RootTransformExclusions;
		}
		return NoExclusions;
	}

	private static readonly HashSet<string> NoExclusions = [];

	/// <summary>
	/// The name becomes a modification, so instances named "Tree" and "Tree (1)" still share a prefab.
	/// </summary>
	private static readonly HashSet<string> RootGameObjectExclusions = ["m_Name"];

	/// <summary>
	/// Where the instance sits in the scene is what makes it an instance rather than a copy.
	/// </summary>
	private static readonly HashSet<string> RootTransformExclusions =
	[
		"m_LocalPosition",
		"m_LocalRotation",
		"m_LocalScale",
		"m_Father",
		"m_RootOrder",
	];

	private readonly record struct Occurrence(
		IGameObject Root,
		IReadOnlyList<IEditorExtension> Elements,
		int GameObjectCount,
		HierarchySignature Signature);

	private readonly record struct HierarchySignature(ulong Low, ulong High);

	/// <summary>
	/// Gives each asset outside of the hierarchies being compared a number, so that two hierarchies match only when
	/// they reference the same instance rather than an equal-looking one.
	/// </summary>
	private sealed class ExternalAssetIdentity
	{
		private readonly Dictionary<IUnityObjectBase, int> ids = new();

		public int GetID(IUnityObjectBase asset)
		{
			if (ids.TryGetValue(asset, out int id))
			{
				return id;
			}
			id = ids.Count;
			ids.Add(asset, id);
			return id;
		}
	}

	private sealed class SignatureWalker(
		IncrementalHash incremental,
		IReadOnlyDictionary<IUnityObjectBase, int> indices,
		ExternalAssetIdentity externalIdentity) : AssetWalker
	{
		private AssetCollection collection = null!;
		private IReadOnlySet<string> excluded = NoExclusions;

		public void Begin(IUnityObjectBase element, IReadOnlySet<string> excludedFields)
		{
			collection = element.Collection;
			excluded = excludedFields;
		}

		public override bool EnterField(IUnityAssetBase asset, string name)
		{
			if (excluded.Contains(name))
			{
				return false;
			}
			Append(name);
			return true;
		}

		public override void VisitPrimitive<T>(T value)
		{
			switch (value)
			{
				case byte[] bytes:
					incremental.AppendData(bytes);
					break;
				case Utf8String utf8String:
					incremental.AppendData(utf8String.Data);
					break;
				case string text:
					Append(text);
					break;
				case bool b:
					incremental.AppendData([b ? (byte)1 : (byte)0]);
					break;
				default:
					Append(Convert.ToString(value, CultureInfo.InvariantCulture) ?? "");
					break;
			}
		}

		public override void VisitPPtr<TAsset>(PPtr<TAsset> pptr)
		{
			IUnityObjectBase? target = collection.TryGetAsset(pptr.FileID, pptr.PathID);
			if (target is null)
			{
				Append("null");
			}
			else if (indices.TryGetValue(target, out int index))
			{
				// Internal reference. Its position in the hierarchy is what matters, not its path ID.
				Append("in");
				AppendInt32(index);
			}
			else
			{
				Append("ex");
				AppendInt32(externalIdentity.GetID(target));
			}
		}

		private void AppendInt32(int value)
		{
			Span<byte> buffer = stackalloc byte[4];
			BinaryPrimitives.WriteInt32LittleEndian(buffer, value);
			incremental.AppendData(buffer);
		}

		private void Append(string text)
		{
			int byteCount = Encoding.UTF8.GetByteCount(text);
			byte[]? rented = byteCount > 256 ? System.Buffers.ArrayPool<byte>.Shared.Rent(byteCount) : null;
			Span<byte> buffer = rented ?? stackalloc byte[256];
			int written = Encoding.UTF8.GetBytes(text, buffer);
			incremental.AppendData(buffer[..written]);
			if (rented is not null)
			{
				System.Buffers.ArrayPool<byte>.Shared.Return(rented);
			}
		}
	}
}
