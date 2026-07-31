namespace AssetRipper.Tools.ExportRunner;

internal static class ExportProfileResolver
{
	public static ResolvedExportSettings Resolve(string? mode, string? profile)
	{
		string? normalizedMode = Normalize(mode);
		string? normalizedProfile = Normalize(profile);

		if (!string.IsNullOrWhiteSpace(normalizedProfile))
		{
			return normalizedProfile switch
			{
				"full-project" => new ResolvedExportSettings("dump", normalizedProfile, "Profile 'full-project' currently maps to backend mode 'dump'."),
				"full-raw" => new ResolvedExportSettings("primary", normalizedProfile, "Profile 'full-raw' currently maps to backend mode 'primary'."),
				"player-art" => new ResolvedExportSettings("primary", normalizedProfile, "Profile 'player-art' applies coarse visual-art selection over primary export."),
				"characters" => new ResolvedExportSettings("primary", normalizedProfile, "Profile 'characters' applies coarse character-asset selection over primary export."),
				"ui" => new ResolvedExportSettings("primary", normalizedProfile, "Profile 'ui' applies coarse UI-oriented selection over primary export."),
				"audio" => new ResolvedExportSettings("primary", normalizedProfile, "Profile 'audio' applies coarse audio-oriented selection over primary export."),
				"narrative" => new ResolvedExportSettings("primary", normalizedProfile, "Profile 'narrative' applies coarse narrative-oriented selection over primary export."),
				"cg" => new ResolvedExportSettings("primary", normalizedProfile, "Profile 'cg' applies coarse CG-oriented selection over primary export."),
				"backgrounds" => new ResolvedExportSettings("primary", normalizedProfile, "Profile 'backgrounds' applies coarse background-oriented selection over primary export."),
				"sprites" => new ResolvedExportSettings("primary", normalizedProfile, "Profile 'sprites' applies coarse sprite-oriented selection over primary export."),
				_ => throw new InvalidOperationException($"Unknown export profile '{profile}'."),
			};
		}

		if (!string.IsNullOrWhiteSpace(normalizedMode))
		{
			return normalizedMode switch
			{
				"primary" => new ResolvedExportSettings("primary", null, null),
				"dump" => new ResolvedExportSettings("dump", null, null),
				_ => throw new InvalidOperationException($"Unknown export mode '{mode}'. Expected 'primary' or 'dump'."),
			};
		}

		throw new InvalidOperationException("No export mode or profile was provided.");
	}

	public static bool IsRecognizedModeToken(string value)
	{
		string normalized = Normalize(value) ?? string.Empty;
		return normalized is "primary" or "dump";
	}

	private static string? Normalize(string? value)
	{
		return string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();
	}
}

internal sealed record ResolvedExportSettings(string Mode, string? Profile, string? Note);
