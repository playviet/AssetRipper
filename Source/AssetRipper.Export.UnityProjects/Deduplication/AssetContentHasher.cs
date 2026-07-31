using AssetRipper.Assets;
using AssetRipper.Assets.Traversal;
using AssetRipper.Primitives;
using AssetRipper.SourceGenerated.Classes.ClassID_28;
using AssetRipper.SourceGenerated.Classes.ClassID_43;
using AssetRipper.SourceGenerated.Classes.ClassID_49;
using AssetRipper.SourceGenerated.Classes.ClassID_83;
using AssetRipper.SourceGenerated.Extensions;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace AssetRipper.Export.UnityProjects.Deduplication;

/// <summary>
/// Computes a hash of an asset's content, so that copies of the same asset in different bundles hash equally.
/// </summary>
/// <remarks>
/// Two things stop a plain serialization hash from working here. Pointers are stored as a file index plus a path ID,
/// both of which are assigned per bundle, so equal assets in different bundles hold unequal pointers. Large buffers
/// such as textures, meshes, and audio are not stored on the asset at all; the asset holds a path into a bundle-specific
/// resource file. Pointers are therefore excluded from the hash, and the types with external buffers have their
/// resolved content hashed instead.
/// </remarks>
public static class AssetContentHasher
{
	public static bool TryComputeHash(IUnityObjectBase asset, out AssetContentHash hash)
	{
		using IncrementalHash incremental = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

		// Assets of different types must never collide, even if their content happens to serialize identically.
		Span<byte> classID = stackalloc byte[4];
		BinaryPrimitives.WriteInt32LittleEndian(classID, asset.ClassID);
		incremental.AppendData(classID);

		if (!TryAppendExternalContent(incremental, asset))
		{
			hash = default;
			return false;
		}

		ContentHashWalker walker = new(incremental);
		asset.WalkRelease(walker);

		Span<byte> digest = stackalloc byte[32];
		incremental.GetHashAndReset(digest);
		hash = new AssetContentHash(
			BinaryPrimitives.ReadUInt64LittleEndian(digest),
			BinaryPrimitives.ReadUInt64LittleEndian(digest[8..]));
		return true;
	}

	/// <summary>
	/// Appends the content that lives outside of the asset, and reports whether the asset is worth deduplicating.
	/// </summary>
	private static bool TryAppendExternalContent(IncrementalHash incremental, IUnityObjectBase asset)
	{
		switch (asset)
		{
			case ITexture2D texture:
				{
					if (!texture.CheckAssetIntegrity())
					{
						return false;
					}
					byte[] imageData = texture.GetImageData();
					if (imageData.Length == 0)
					{
						return false;
					}
					incremental.AppendData(imageData);
					return true;
				}
			case IAudioClip audioClip:
				{
					if (!audioClip.CheckAssetIntegrity())
					{
						return false;
					}
					byte[] audioData = audioClip.GetAudioData();
					if (audioData.Length == 0)
					{
						return false;
					}
					incremental.AppendData(audioData);
					return true;
				}
			case IMesh mesh:
				{
					if (!mesh.CheckAssetIntegrity() || !mesh.IsSet())
					{
						return false;
					}
					incremental.AppendData(mesh.GetChannelsData());
					incremental.AppendData(mesh.IndexBuffer);
					return true;
				}
			case ITextAsset textAsset:
				{
					return !textAsset.Script_C49.IsEmpty;
				}
			default:
				// Everything else is fully contained in its own serialized data.
				return true;
		}
	}

	private sealed class ContentHashWalker(IncrementalHash incremental) : AssetWalker
	{
		public override bool EnterField(IUnityAssetBase asset, string name)
		{
			// The location of external content is bundle-specific, so it would defeat the whole purpose of the hash.
			if (name is "m_StreamData" or "m_Resource" or "m_StreamingInfo")
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
				case byte or sbyte:
					incremental.AppendData([Convert.ToByte(value)]);
					break;
				case short or ushort or int or uint or long or ulong or float or double or char:
					Append(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "");
					break;
				default:
					Append(value.ToString() ?? "");
					break;
			}
		}

		// PPtr values are deliberately not hashed. The base implementation of VisitPPtr does nothing.

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

/// <summary>
/// The first 128 bits of a SHA-256 digest of an asset's content.
/// </summary>
public readonly record struct AssetContentHash(ulong Low, ulong High);
