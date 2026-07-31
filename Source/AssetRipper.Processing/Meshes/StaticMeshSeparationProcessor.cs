using AssetRipper.Assets;
using AssetRipper.Assets.Collections;
using AssetRipper.Import.Logging;
using AssetRipper.Numerics;
using AssetRipper.SourceGenerated.Classes.ClassID_1;
using AssetRipper.SourceGenerated.Classes.ClassID_25;
using AssetRipper.SourceGenerated.Classes.ClassID_33;
using AssetRipper.SourceGenerated.Classes.ClassID_4;
using AssetRipper.SourceGenerated.Classes.ClassID_43;
using AssetRipper.SourceGenerated.Extensions;
using System.Numerics;

namespace AssetRipper.Processing.Meshes;

/// <summary>
/// Reverses Unity's static batching.
/// </summary>
/// <remarks>
/// When a scene is built, Unity merges the meshes of static renderers into one combined mesh in order to reduce draw
/// calls. Each renderer keeps a reference to the combined mesh plus the range of submeshes belonging to it, and its
/// vertices are baked into the space of the static batch root. This processor gives every such renderer its own mesh
/// again, with the vertices transformed back into the renderer's local space.
/// </remarks>
public sealed class StaticMeshSeparationProcessor : IAssetProcessor
{
	public void Process(GameData gameData)
	{
		List<Candidate> candidates = [];
		foreach (IUnityObjectBase asset in gameData.GameBundle.FetchAssetCollections().SelectMany(c => c))
		{
			if (asset is IRenderer renderer && TryMakeCandidate(renderer, out Candidate candidate))
			{
				candidates.Add(candidate);
			}
		}

		if (candidates.Count == 0)
		{
			return;
		}

		ProcessedAssetCollection collection = gameData.AddNewProcessedCollection("Generated Separated Meshes");
		Dictionary<IMesh, MeshData> meshDataCache = new();
		int separated = 0;
		foreach (Candidate candidate in candidates)
		{
			if (!TryGetMeshData(meshDataCache, candidate.CombinedMesh, out MeshData combinedData))
			{
				continue;
			}

			if (!TrySeparate(combinedData, candidate, out MeshData separatedData))
			{
				continue;
			}

			IMesh separatedMesh = collection.CreateMesh();
			separatedMesh.Name = $"{candidate.GameObject.Name} Mesh";
			separatedMesh.FillWithCompressedMeshData(separatedData);

			candidate.MeshFilter.MeshP = separatedMesh;
			ClearStaticBatchInformation(candidate.Renderer);
			separated++;
		}

		Logger.Info(LogCategory.Processing, $"Separated {separated} static {(separated == 1 ? "mesh" : "meshes")}");
	}

	private static bool TryMakeCandidate(IRenderer renderer, out Candidate candidate)
	{
		candidate = default;

		int[] subsetIndices = GetSubsetIndices(renderer);
		if (subsetIndices.Length == 0)
		{
			// Not statically batched.
			return false;
		}

		if (renderer.GameObject_C2P is not { } gameObject
			|| !gameObject.TryGetComponent(out IMeshFilter? meshFilter)
			|| !meshFilter.TryGetMesh(out IMesh? mesh)
			|| !mesh.IsSet())
		{
			return false;
		}

		int subMeshCount = mesh.SubMeshes.Count;
		foreach (int index in subsetIndices)
		{
			if (index < 0 || index >= subMeshCount)
			{
				Logger.Warning(LogCategory.Processing, $"Renderer '{gameObject.Name}' references submesh {index} of '{mesh.Name}', which only has {subMeshCount}. Skipping separation.");
				return false;
			}
		}

		if (!mesh.IsCombinedMesh() && CoversEntireMesh(subsetIndices, subMeshCount))
		{
			// The renderer already owns the whole mesh, so there is nothing to separate.
			return false;
		}

		ITransform transform = gameObject.GetTransform();
		Transformation bakedToWorld = renderer.StaticBatchRoot_C25P is { } batchRoot
			? GetGlobalTransformation(batchRoot)
			: Transformation.Identity;
		Transformation bakedToLocal = bakedToWorld * GetGlobalTransformation(transform).Invert();

		candidate = new Candidate(renderer, gameObject, meshFilter, mesh, subsetIndices, bakedToLocal);
		return true;
	}

	/// <summary>
	/// Accumulates the local transformations from the scene root down to <paramref name="transform"/>.
	/// </summary>
	private static Transformation GetGlobalTransformation(ITransform transform)
	{
		Transformation result = transform.ToTransformation();
		ITransform? father = transform.Father_C4P;
		// A malformed hierarchy could be cyclic, so the visited set keeps this from looping forever.
		HashSet<ITransform> visited = [transform];
		while (father is not null && visited.Add(father))
		{
			result *= father.ToTransformation();
			father = father.Father_C4P;
		}
		return result;
	}

	private static bool CoversEntireMesh(int[] subsetIndices, int subMeshCount)
	{
		if (subsetIndices.Length != subMeshCount)
		{
			return false;
		}
		for (int i = 0; i < subsetIndices.Length; i++)
		{
			if (subsetIndices[i] != i)
			{
				return false;
			}
		}
		return true;
	}

	private static int[] GetSubsetIndices(IRenderer renderer)
	{
		if (renderer.Has_SubsetIndices_C25() && renderer.SubsetIndices_C25.Count > 0)
		{
			return renderer.SubsetIndices_C25.Select(i => (int)i).ToArray();
		}
		else if (renderer.Has_StaticBatchInfo_C25() && renderer.StaticBatchInfo_C25.SubMeshCount > 0)
		{
			return Enumerable.Range(renderer.StaticBatchInfo_C25.FirstSubMesh, renderer.StaticBatchInfo_C25.SubMeshCount).ToArray();
		}
		else
		{
			return [];
		}
	}

	private static void ClearStaticBatchInformation(IRenderer renderer)
	{
		if (renderer.Has_StaticBatchInfo_C25())
		{
			renderer.StaticBatchInfo_C25.FirstSubMesh = 0;
			renderer.StaticBatchInfo_C25.SubMeshCount = 0;
		}
		if (renderer.Has_SubsetIndices_C25())
		{
			renderer.SubsetIndices_C25.Clear();
		}
		renderer.StaticBatchRoot_C25P = null;
	}

	private static bool TryGetMeshData(Dictionary<IMesh, MeshData> cache, IMesh mesh, out MeshData meshData)
	{
		if (cache.TryGetValue(mesh, out meshData))
		{
			return meshData.Vertices.Length > 0;
		}

		if (!MeshData.TryMakeFromMesh(mesh, out meshData))
		{
			meshData = MeshData.Empty;
		}
		cache.Add(mesh, meshData);
		return meshData.Vertices.Length > 0;
	}

	/// <summary>
	/// Copies the submeshes referenced by <paramref name="candidate"/> into a new mesh, compacting the vertex buffer
	/// down to the vertices those submeshes actually use.
	/// </summary>
	private static bool TrySeparate(MeshData combined, Candidate candidate, out MeshData separated)
	{
		Transformation positionTransform = candidate.BakedToLocal;
		Transformation tangentTransform = positionTransform.RemoveTranslation();
		Transformation normalTransform = positionTransform.Invert().Transpose();

		// Maps a vertex index in the combined mesh to its index in the separated mesh.
		Dictionary<uint, uint> vertexMap = [];
		List<uint> sourceVertices = [];
		List<uint> indices = [];
		SubMeshData[] subMeshes = new SubMeshData[candidate.SubsetIndices.Length];

		for (int i = 0; i < candidate.SubsetIndices.Length; i++)
		{
			SubMeshData source = combined.SubMeshes[candidate.SubsetIndices[i]];
			int firstIndex = indices.Count;
			int firstVertex = sourceVertices.Count;

			for (int j = 0; j < source.IndexCount; j++)
			{
				int position = source.FirstIndex + j;
				if (position < 0 || position >= combined.ProcessedIndexBuffer.Length)
				{
					Logger.Warning(LogCategory.Processing, $"Submesh of '{candidate.CombinedMesh.Name}' indexes outside its index buffer. Skipping separation.");
					separated = default;
					return false;
				}

				uint sourceIndex = combined.ProcessedIndexBuffer[position] + source.BaseVertex;
				if (sourceIndex >= combined.Vertices.Length)
				{
					Logger.Warning(LogCategory.Processing, $"Submesh of '{candidate.CombinedMesh.Name}' references vertex {sourceIndex} of {combined.Vertices.Length}. Skipping separation.");
					separated = default;
					return false;
				}

				if (!vertexMap.TryGetValue(sourceIndex, out uint mapped))
				{
					mapped = (uint)sourceVertices.Count;
					vertexMap.Add(sourceIndex, mapped);
					sourceVertices.Add(sourceIndex);
				}
				indices.Add(mapped);
			}

			subMeshes[i] = new SubMeshData(
				BaseVertex: 0,
				FirstIndex: firstIndex,
				FirstVertex: firstVertex,
				IndexCount: source.IndexCount,
				TriangleCount: source.TriangleCount,
				VertexCount: sourceVertices.Count - firstVertex,
				Topology: source.Topology,
				LocalBounds: default);
		}

		Vector3[] vertices = new Vector3[sourceVertices.Count];
		for (int i = 0; i < sourceVertices.Count; i++)
		{
			vertices[i] = combined.Vertices[sourceVertices[i]] * positionTransform;
		}

		Vector3[]? normals = null;
		if (combined.HasNormals)
		{
			normals = new Vector3[sourceVertices.Count];
			for (int i = 0; i < sourceVertices.Count; i++)
			{
				normals[i] = Vector3.Normalize(combined.Normals[sourceVertices[i]] * normalTransform);
			}
		}

		Vector4[]? tangents = null;
		if (combined.HasTangents)
		{
			tangents = new Vector4[sourceVertices.Count];
			for (int i = 0; i < sourceVertices.Count; i++)
			{
				Vector4 tangent = combined.Tangents[sourceVertices[i]];
				// The W component stores bitangent handedness and must not be transformed.
				Vector3 transformed = Vector3.Normalize(new Vector3(tangent.X, tangent.Y, tangent.Z) * tangentTransform);
				tangents[i] = new Vector4(transformed, tangent.W);
			}
		}

		// Recalculate the bounds now that the vertices are in local space.
		for (int i = 0; i < subMeshes.Length; i++)
		{
			SubMeshData subMesh = subMeshes[i];
			subMeshes[i] = subMesh with
			{
				LocalBounds = Bounds.CalculateFromVertexArray(vertices.AsSpan(subMesh.FirstVertex, subMesh.VertexCount)),
			};
		}

		separated = new MeshData(
			vertices,
			normals,
			tangents,
			Gather(combined.Colors, sourceVertices),
			Gather(combined.UV0, sourceVertices),
			Gather(combined.UV1, sourceVertices),
			Gather(combined.UV2, sourceVertices),
			Gather(combined.UV3, sourceVertices),
			Gather(combined.UV4, sourceVertices),
			Gather(combined.UV5, sourceVertices),
			Gather(combined.UV6, sourceVertices),
			Gather(combined.UV7, sourceVertices),
			Gather(combined.Skin, sourceVertices),
			// Static batching only applies to non-skinned renderers, so there are no bind poses to carry over.
			null,
			[.. indices],
			subMeshes);
		return true;
	}

	private static T[]? Gather<T>(T[]? source, List<uint> sourceVertices) where T : struct
	{
		if (source is null || source.Length == 0)
		{
			return null;
		}

		T[] result = new T[sourceVertices.Count];
		for (int i = 0; i < sourceVertices.Count; i++)
		{
			uint index = sourceVertices[i];
			result[i] = index < source.Length ? source[index] : default;
		}
		return result;
	}

	private readonly record struct Candidate(
		IRenderer Renderer,
		IGameObject GameObject,
		IMeshFilter MeshFilter,
		IMesh CombinedMesh,
		int[] SubsetIndices,
		Transformation BakedToLocal);
}
