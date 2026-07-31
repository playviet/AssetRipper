using AssetRipper.Configuration;
using AssetRipper.Export.UnityProjects.Configuration;
using AssetRipper.Import.Configuration;
using AssetRipper.Mining.PredefinedAssets;
using AssetRipper.Processing.Configuration;

namespace AssetRipper.Export.Configuration;

public class FullConfiguration : CoreConfiguration
{
	public ProcessingSettings ProcessingSettings
	{
		get => SingletonData.GetStoredValue<ProcessingSettings>(nameof(ProcessingSettings));
		set => SingletonData.SetStoredValue(nameof(ProcessingSettings), value);
	}

	public ExportSettings ExportSettings
	{
		get => SingletonData.GetStoredValue<ExportSettings>(nameof(ExportSettings));
		set => SingletonData.SetStoredValue(nameof(ExportSettings), value);
	}

	public AssetPathOverrideData AssetPathOverrideData
	{
		get => SingletonData.GetStoredValue<AssetPathOverrideData>(nameof(AssetPathOverrideData));
		set => SingletonData.SetStoredValue(nameof(AssetPathOverrideData), value);
	}

	public UserPackageData UserPackageData
	{
		get => SingletonData.GetStoredValue<UserPackageData>(nameof(UserPackageData));
		set => SingletonData.SetStoredValue(nameof(UserPackageData), value);
	}

	public EmbeddedFileData EmbeddedFileData
	{
		get => SingletonData.GetStoredValue<EmbeddedFileData>(nameof(EmbeddedFileData));
		set => SingletonData.SetStoredValue(nameof(EmbeddedFileData), value);
	}

	/// <summary>
	/// Set for the duration of an export when package relinking is enabled and an editor was found to read from.
	/// Not a stored setting; it depends on the loaded game.
	/// </summary>
	public UserPackages.UnityPackageRelinker? UnityPackageRelinker { get; set; }

	public bool SaveSettingsToDisk => ExportSettings.SaveSettingsToDisk;

	public string? LanguageCode
	{
		get => ExportSettings.LanguageCode;
		set => ExportSettings.LanguageCode = value;
	}

	public FullConfiguration()
	{
		SingletonData.Add(nameof(ProcessingSettings), new JsonDataInstance<ProcessingSettings>(SerializedSettingsContext.Default.ProcessingSettings));
		SingletonData.Add(nameof(ExportSettings), new JsonDataInstance<ExportSettings>(SerializedSettingsContext.Default.ExportSettings));
		SingletonData.Add(nameof(EngineResourceData), new JsonDataInstance<EngineResourceData?>(EngineResourceDataContext.Default.NullableEngineResourceData));
		SingletonData.Add(nameof(AssetPathOverrideData), new JsonDataInstance<AssetPathOverrideData>(UserDataContext.Default.AssetPathOverrideData));
		SingletonData.Add(nameof(UserPackageData), new JsonDataInstance<UserPackageData>(UserDataContext.Default.UserPackageData));
		SingletonData.Add(nameof(EmbeddedFileData), new JsonDataInstance<EmbeddedFileData>(UserDataContext.Default.EmbeddedFileData));
	}

	public override void LogConfigurationValues()
	{
		base.LogConfigurationValues();
		ProcessingSettings.Log();
		ExportSettings.Log();
	}

	public void LoadFromDefaultPath()
	{
		if (SerializedSettings.TryLoadFromDefaultPath(out SerializedSettings settings))
		{
			ImportSettings = settings.Import;
			ProcessingSettings = settings.Processing;
			ExportSettings = settings.Export;
		}
	}

	public void SaveToDefaultPath()
	{
		new SerializedSettings(ImportSettings, ProcessingSettings, ExportSettings).SaveToDefaultPath();
	}

	public void MaybeSaveToDefaultPath()
	{
		if (SaveSettingsToDisk)
		{
			SaveToDefaultPath();
		}
	}
}
