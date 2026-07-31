using System.Text.Json.Serialization;

namespace AssetRipper.Export.Configuration;

/// <summary>
/// User supplied declarations of third party packages that will be added to the exported project.
/// </summary>
/// <remarks>
/// Loaded from a JSON file through the configuration files page, under the key <c>UserPackageData</c>.
/// <para>
/// Assets that a declared package owns are not written out. References to them are emitted as references into the
/// package instead, so that adding the package to the exported project resolves them rather than leaving the
/// duplicated copies that would otherwise break.
/// </para>
/// </remarks>
public sealed record class UserPackageData
{
	[JsonPropertyName("packages")]
	public List<UserPackage> Packages { get; set; } = [];
}

public sealed record class UserPackage
{
	/// <summary>
	/// The package name, such as <c>com.unity.textmeshpro</c>. This is written into <c>Packages/manifest.json</c>.
	/// </summary>
	[JsonPropertyName("name")]
	public string Name { get; set; } = "";

	/// <summary>
	/// The package version, such as <c>3.0.6</c>.
	/// </summary>
	[JsonPropertyName("version")]
	public string Version { get; set; } = "";

	/// <summary>
	/// Assets owned by the package, each mapping an asset to its identity inside the package.
	/// </summary>
	[JsonPropertyName("assets")]
	public List<UserPackageAsset> Assets { get; set; } = [];
}

public sealed record class UserPackageAsset
{
	/// <summary>
	/// Matches the asset's name exactly.
	/// </summary>
	[JsonPropertyName("name")]
	public string Name { get; set; } = "";

	/// <summary>
	/// Matches the asset's class name, such as <c>Texture2D</c>. Null matches any class.
	/// </summary>
	[JsonPropertyName("className")]
	public string? ClassName { get; set; }

	/// <summary>
	/// The GUID the asset has inside the package, as 32 hexadecimal characters.
	/// </summary>
	[JsonPropertyName("guid")]
	public string Guid { get; set; } = "";

	/// <summary>
	/// The file ID the asset has inside the package. Defaults to the main asset's file ID for its class.
	/// </summary>
	[JsonPropertyName("fileID")]
	public long? FileID { get; set; }
}
