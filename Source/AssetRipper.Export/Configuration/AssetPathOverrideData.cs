using System.Text.Json.Serialization;

namespace AssetRipper.Export.Configuration;

/// <summary>
/// User supplied rules for where exported assets are written.
/// </summary>
/// <remarks>
/// Loaded from a JSON file through the configuration files page, under the key <c>AssetPathOverrideData</c>.
/// </remarks>
public sealed record class AssetPathOverrideData
{
	[JsonPropertyName("overrides")]
	public List<AssetPathOverrideRule> Overrides { get; set; } = [];
}

/// <summary>
/// One rule. An asset must match every condition that the rule specifies, and rules are applied in order, with the
/// first match winning.
/// </summary>
public sealed record class AssetPathOverrideRule
{
	/// <summary>
	/// Matches the asset's name exactly. Null matches any name.
	/// </summary>
	[JsonPropertyName("name")]
	public string? Name { get; set; }

	/// <summary>
	/// Matches the asset's class name, such as <c>Texture2D</c>. Null matches any class.
	/// </summary>
	[JsonPropertyName("className")]
	public string? ClassName { get; set; }

	/// <summary>
	/// Matches assets whose original path starts with this value. Null matches any path.
	/// </summary>
	[JsonPropertyName("originalPathPrefix")]
	public string? OriginalPathPrefix { get; set; }

	/// <summary>
	/// The directory to write the asset to, such as <c>Assets/Textures/UI</c>. Null leaves the directory alone.
	/// </summary>
	[JsonPropertyName("directory")]
	public string? Directory { get; set; }

	/// <summary>
	/// The file name, without extension, to write the asset as. Null leaves the name alone.
	/// </summary>
	[JsonPropertyName("fileName")]
	public string? FileName { get; set; }

	/// <summary>
	/// The file extension, without a leading dot. Null leaves the extension alone.
	/// </summary>
	[JsonPropertyName("extension")]
	public string? Extension { get; set; }
}
