using AssetRipper.Export.Configuration;

namespace AssetRipper.GUI.Web.Pages.Settings.DropDown;

public sealed class ShaderNamingModeDropDownSetting : DropDownSetting<ShaderNamingMode>
{
	public static ShaderNamingModeDropDownSetting Instance { get; } = new();

	public override string Title => Localization.ShaderNamingTitle;

	protected override string GetDisplayName(ShaderNamingMode value) => value switch
	{
		ShaderNamingMode.Original => Localization.ShaderNamingOriginal,
		ShaderNamingMode.Suffixed => Localization.ShaderNamingSuffixed,
		_ => base.GetDisplayName(value),
	};

	protected override string? GetDescription(ShaderNamingMode value) => value switch
	{
		ShaderNamingMode.Original => Localization.ShaderNamingOriginalDescription,
		ShaderNamingMode.Suffixed => Localization.ShaderNamingSuffixedDescription,
		_ => base.GetDescription(value),
	};
}
