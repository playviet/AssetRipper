using AssetRipper.Import.Logging;

namespace AssetRipper.Export.Configuration;

public sealed record class ExportSettings
{
	/// <summary>
	/// The file format that audio clips get exported in. Recommended: Ogg
	/// </summary>
	public AudioExportFormat AudioExportFormat { get; set; } = AudioExportFormat.Default;

	/// <summary>
	/// The file format that images (like textures) get exported in.
	/// </summary>
	public ImageExportFormat ImageExportFormat { get; set; } = ImageExportFormat.Png;

	/// <summary>
	/// The file format that images (like textures) get exported in.
	/// </summary>
	public LightmapTextureExportFormat LightmapTextureExportFormat { get; set; } = LightmapTextureExportFormat.Yaml;

	/// <summary>
	/// How are MonoScripts exported? Recommended: Decompiled
	/// </summary>
	public ScriptExportMode ScriptExportMode { get; set; } = ScriptExportMode.Hybrid;

	/// <summary>
	/// The C# language version of decompiled scripts.
	/// </summary>
	public ScriptLanguageVersion ScriptLanguageVersion { get; set; } = ScriptLanguageVersion.AutoSafe;

	/// <summary>
	/// If true, type references in scripts are fully qualified.
	/// </summary>
	public bool ScriptTypesFullyQualified { get; set; } = false;

	/// <summary>
	/// Is the IL a script was decompiled from written out beside it?
	/// </summary>
	public ScriptIlExportMode ScriptIlExportMode { get; set; } = ScriptIlExportMode.None;

	/// <summary>
	/// How to export shaders?
	/// </summary>
	public ShaderExportMode ShaderExportMode { get; set; } = ShaderExportMode.Dummy;

	/// <summary>
	/// What are exported shaders called?
	/// </summary>
	public ShaderNamingMode ShaderNamingMode { get; set; } = ShaderNamingMode.Original;

	/// <summary>
	/// Should sprites be exported as a texture? Recommended: Native
	/// </summary>
	public SpriteExportMode SpriteExportMode { get; set; } = SpriteExportMode.Yaml;

	/// <summary>
	/// How are text assets exported?
	/// </summary>
	public TextExportMode TextExportMode { get; set; } = TextExportMode.Parse;

	/// <summary>
	/// Replace a build's package assemblies with a reference to the package they came from.
	/// </summary>
	/// <remarks>
	/// Requires the matching editor version to be installed, because the package version and the script GUIDs are read
	/// from it rather than guessed. Off by default: it changes what the project references, and an assembly is only
	/// replaced when every script the game points at inside it can be found in the package.
	/// </remarks>
	public bool RelinkUnityPackages { get; set; } = false;

	public bool ExportUnreadableAssets { get; set; } = false;

	/// <summary>
	/// If true, the original texture extension (when available) will be preferred over the selected <see cref="ImageExportFormat"/>.
	/// </summary>
	public bool PreferOriginalTextureExtension { get; set; } = true;

	public bool SaveSettingsToDisk { get; set; }

	public string? LanguageCode { get; set; }

	public void Log()
	{
		Logger.Info(LogCategory.General, $"{nameof(AudioExportFormat)}: {AudioExportFormat}");
		Logger.Info(LogCategory.General, $"{nameof(ImageExportFormat)}: {ImageExportFormat}");
		Logger.Info(LogCategory.General, $"{nameof(LightmapTextureExportFormat)}: {LightmapTextureExportFormat}");
		Logger.Info(LogCategory.General, $"{nameof(ScriptExportMode)}: {ScriptExportMode}");
		Logger.Info(LogCategory.General, $"{nameof(ScriptLanguageVersion)}: {ScriptLanguageVersion}");
		Logger.Info(LogCategory.General, $"{nameof(ScriptIlExportMode)}: {ScriptIlExportMode}");
		Logger.Info(LogCategory.General, $"{nameof(ShaderExportMode)}: {ShaderExportMode}");
		Logger.Info(LogCategory.General, $"{nameof(ShaderNamingMode)}: {ShaderNamingMode}");
		Logger.Info(LogCategory.General, $"{nameof(RelinkUnityPackages)}: {RelinkUnityPackages}");
		Logger.Info(LogCategory.General, $"{nameof(SpriteExportMode)}: {SpriteExportMode}");
		Logger.Info(LogCategory.General, $"{nameof(TextExportMode)}: {TextExportMode}");
		Logger.Info(LogCategory.General, $"{nameof(ExportUnreadableAssets)}: {ExportUnreadableAssets}");
		Logger.Info(LogCategory.General, $"{nameof(PreferOriginalTextureExtension)}: {PreferOriginalTextureExtension}");
	}
}
