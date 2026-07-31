using System.Text.Json.Serialization;

namespace AssetRipper.Export.Configuration;

/// <summary>
/// User supplied rules for pulling a file out of a serialized field.
/// </summary>
/// <remarks>
/// Games often keep their own data — maps, level definitions, dialogue, custom mesh formats — inside a byte array or
/// string field on a ScriptableObject rather than as an asset Unity understands. Exported normally, that payload ends
/// up inline in a YAML file, which is not something any tool can open. A rule writes it out as a real file instead.
/// <para>
/// Loaded from a JSON file through the configuration files page, under the key <c>EmbeddedFileData</c>.
/// </para>
/// </remarks>
public sealed record class EmbeddedFileData
{
	[JsonPropertyName("rules")]
	public List<EmbeddedFileRule> Rules { get; set; } = [];
}

/// <summary>
/// One rule. A MonoBehaviour must match every condition the rule specifies.
/// </summary>
public sealed record class EmbeddedFileRule
{
	/// <summary>
	/// Matches the namespace of the script the MonoBehaviour uses, such as <c>Timberborn.BlueprintSystem</c>.
	/// Null matches any namespace.
	/// </summary>
	[JsonPropertyName("scriptNamespace")]
	public string? ScriptNamespace { get; set; }

	/// <summary>
	/// Matches the class name of that script. Null matches any class.
	/// </summary>
	[JsonPropertyName("scriptClass")]
	public string? ScriptClass { get; set; }

	/// <summary>
	/// Matches assets whose name ends with this value. Null matches any name.
	/// </summary>
	[JsonPropertyName("nameSuffix")]
	public string? NameSuffix { get; set; }

	/// <summary>
	/// Matches assets whose original directory starts with this value. Null matches any directory.
	/// Both slash styles are accepted, since the value comes from whichever platform built the game.
	/// </summary>
	[JsonPropertyName("directoryPrefix")]
	public string? DirectoryPrefix { get; set; }

	/// <summary>
	/// The serialized field to write out, such as <c>_bytes</c> or <c>_content</c>. Required.
	/// </summary>
	[JsonPropertyName("field")]
	public string Field { get; set; } = "";

	/// <summary>
	/// The extension to give the written file, without a leading dot. Defaults to <c>bin</c>.
	/// </summary>
	[JsonPropertyName("extension")]
	public string Extension { get; set; } = "bin";

	/// <summary>
	/// Whether the field holds text rather than bytes. Text is written as UTF-8.
	/// </summary>
	[JsonPropertyName("text")]
	public bool Text { get; set; }
}
