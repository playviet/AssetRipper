using AssetRipper.Assets;
using AssetRipper.Export.PrimaryContent;

namespace AssetRipper.Tools.ExportRunner;

internal static class ProfileSelection
{
	private sealed record ProfileDescriptor(string Name, int Threshold, Func<IUnityObjectBase, int> Score);
	private static readonly string[] PlayerArtTokens = ["art", "illustration", "gallery", "event", "storycg", "character", "portrait", "face", "live2d", "spine"];
	private static readonly string[] CharacterTokens = ["character", "characters", "chara", "char", "role", "roles", "npc", "portrait", "face", "avatar", "live2d", "spine", "standing"];
	private static readonly string[] AudioTokens = ["audio", "voice", "voices", "bgm", "music", "sfx", "sound", "sounds", "vo", "se"];
	private static readonly string[] UiTokens = ["ui", "icon", "icons", "atlas", "atlases", "hud", "button", "buttons", "menu", "menus", "title", "common", "interface", "dialogueui"];
	private static readonly string[] NarrativeTokens = ["story", "stories", "scenario", "scenarios", "dialog", "dialogue", "script", "scripts", "novel", "utage", "naninovel", "text", "texts"];
	private static readonly string[] CgTokens = ["cg", "cgs", "gallery", "illustration", "illustrations", "event", "eventcg", "hcg", "sceneevent"];
	private static readonly string[] StaticCgContextTokens = ["memory", "memories", "showroom", "foreground", "foregrounds", "backdrop", "backdrops", "still", "stills", "scene", "scenes"];
	private static readonly string[] AnimatedVisualTokens = ["spine", "skeleton", "skeletonanimation", "live2d", "cubism", "motion", "motions", "anim", "animator", "timeline", "blink", "mouth", "lip", "lipsync"];
	private static readonly string[] ThumbnailTokens = ["thumb", "thumbnail", "thumbnails", "icon", "icons", "mini", "preview", "previews", "slot", "slots", "small"];
	private static readonly string[] BackgroundTokens = ["bg", "bgs", "background", "backgrounds", "scene", "scenes", "scenery", "location", "locations", "map", "maps", "stage", "stages"];
	private static readonly string[] SpriteTokens = ["sprite", "sprites", "atlas", "atlases", "sheet", "sheets", "tex", "texture", "textures"];
	private static readonly string[] FontTokens = ["font", "fonts", "sdf", "msdf", "tmp", "textmeshpro", "glyph", "glyphs", "material", "materials"];
	private static readonly string[] SystemTokens = ["editor", "builtin", "unity_builtin_extra", "resources", "defaultresources"];
	private static readonly HashSet<string> SemanticSignalTokens =
	[
		.. PlayerArtTokens,
		.. CharacterTokens,
		.. AudioTokens,
		.. UiTokens,
		.. NarrativeTokens,
		.. CgTokens,
		.. StaticCgContextTokens,
		.. AnimatedVisualTokens,
		.. ThumbnailTokens,
		.. BackgroundTokens,
		.. SpriteTokens,
		"portrait", "avatar", "standing", "bust", "logo", "badge", "cursor", "frame", "frames",
		"field", "room", "town", "forest", "city", "button", "buttons", "btn", "save", "load",
		"backlog", "system", "title", "interface", "menu", "menus", "gallery", "event", "story"
	];
	private static readonly HashSet<string> IgnoredSignalTokens = new(StringComparer.OrdinalIgnoreCase)
	{
		"assets", "resources", "resource", "sprite", "sprites", "texture", "textures", "tex", "assetbundle",
		"gameobject", "monobehaviour", "prefabhierarchyobject", "spriteinformationobject", "texture2d",
		"transform", "recttransform", "canvasrenderer", "script", "scripts", "common", "misc", "data",
		"asset", "noresources", "normal", "over", "diff", "com", "botm", "mask", "hlsunny",
		"spriteatlastexture"
	};
	private static readonly ProfileDescriptor[] InventoryProfiles =
	[
		new("player-art", 4, ScorePlayerArt),
		new("characters", 4, ScoreCharacters),
		new("audio", 3, ScoreAudio),
		new("ui", 4, ScoreUi),
		new("narrative", 3, ScoreNarrative),
		new("cg", 4, ScoreCg),
		new("backgrounds", 4, ScoreBackground),
		new("sprites", 3, ScoreSprites),
	];

	public static Func<ExportCollectionBase, ExportCollectionSelectionDecision>? CreatePredicate(string? profile)
	{
		string? normalized = string.IsNullOrWhiteSpace(profile) ? null : profile.Trim().ToLowerInvariant();
		ProfileDescriptor? descriptor = InventoryProfiles.FirstOrDefault(p => string.Equals(p.Name, normalized, StringComparison.Ordinal));
		return descriptor is null ? normalized switch
		{
			null or "full-raw" => null,
			_ => null,
		}
		: collection => Decide(collection, MatchesCollection(collection, descriptor), $"excluded-by-profile:{descriptor.Name}");
	}

	public static ProfileEvidence[] BuildInventoryEvidence(IEnumerable<IUnityObjectBase> assets)
	{
		List<IUnityObjectBase> assetList = assets.ToList();
		List<ProfileEvidence> evidence = new(InventoryProfiles.Length);
		foreach (ProfileDescriptor descriptor in InventoryProfiles)
		{
			int matchedAssets = 0;
			int strongMatches = 0;
			Dictionary<string, int> buckets = new(StringComparer.Ordinal);
			Dictionary<string, int> authoredBuckets = new(StringComparer.Ordinal);
			Dictionary<string, int> signals = new(StringComparer.Ordinal);
			foreach (IUnityObjectBase asset in assetList)
			{
				int score = descriptor.Score(asset);
				if (score < descriptor.Threshold)
				{
					continue;
				}

				matchedAssets++;
				if (score >= descriptor.Threshold + 2)
				{
					strongMatches++;
				}

				string directory = asset.GetBestDirectory();
				if (!string.IsNullOrWhiteSpace(directory))
				{
					buckets[directory] = buckets.TryGetValue(directory, out int count) ? count + 1 : 1;
					if (!IsGenericTypeBucket(directory))
					{
						authoredBuckets[directory] = authoredBuckets.TryGetValue(directory, out int authoredCount) ? authoredCount + 1 : 1;
					}
				}

				foreach (string token in GetTokens(asset).Where(IsUsefulSignalToken))
				{
					signals[token] = signals.TryGetValue(token, out int signalCount) ? signalCount + 1 : 1;
				}
			}

			IEnumerable<KeyValuePair<string, int>> preferredBuckets = authoredBuckets.Count > 0 ? authoredBuckets : buckets;
			evidence.Add(new ProfileEvidence(
				Profile: descriptor.Name,
				MatchedAssets: matchedAssets,
				StrongMatches: strongMatches,
				TopBuckets: preferredBuckets
					.OrderByDescending(pair => pair.Value)
					.ThenBy(pair => pair.Key, StringComparer.Ordinal)
					.Take(5)
					.Select(pair => new KeyCount(pair.Key, pair.Value))
					.ToArray(),
				TopSignals: FilterSignals(signals, matchedAssets)
					.OrderByDescending(pair => pair.Value)
					.ThenBy(pair => pair.Key, StringComparer.Ordinal)
					.Take(8)
					.Select(pair => new KeyCount(pair.Key, pair.Value))
					.ToArray()));
		}

		return evidence
			.OrderByDescending(item => item.StrongMatches)
			.ThenByDescending(item => item.MatchedAssets)
			.ThenBy(item => item.Profile, StringComparer.Ordinal)
			.ToArray();
	}

	public static string[] SuggestProfiles(
		IReadOnlyDictionary<string, int> classCounts,
		IReadOnlyDictionary<string, int> directoryCounts,
		ProfileEvidence[] evidence)
	{
		HashSet<string> suggestions = ["full-raw", "full-project"];
		int spriteCount = GetCount(classCounts, "Sprite");
		int textureCount = GetCount(classCounts, "Texture2D");
		int audioCount = GetCount(classCounts, "AudioClip");
		int textAssetCount = GetCount(classCounts, "TextAsset");
		int monoBehaviourCount = GetCount(classCounts, "MonoBehaviour");

		if (spriteCount + textureCount >= 500)
		{
			suggestions.Add("player-art");
			suggestions.Add("characters");
			suggestions.Add("sprites");
		}

		if (audioCount >= 100 || GetEvidence(evidence, "audio").MatchedAssets >= 100)
		{
			suggestions.Add("audio");
		}

		if (textAssetCount >= 50 || monoBehaviourCount >= 1000 || HasPathHint(directoryCounts, "story") || HasPathHint(directoryCounts, "scenario") || HasPathHint(directoryCounts, "dialog") || GetEvidence(evidence, "narrative").MatchedAssets >= 100)
		{
			suggestions.Add("narrative");
		}

		foreach (ProfileEvidence item in evidence)
		{
			int minimum = item.Profile switch
			{
				"audio" => 100,
				"narrative" => 100,
				_ => 25,
			};
			if (item.StrongMatches >= minimum || item.MatchedAssets >= minimum * 2)
			{
				suggestions.Add(item.Profile);
			}
		}

		return suggestions.ToArray();
	}

	private static ExportCollectionSelectionDecision Decide(ExportCollectionBase collection, bool include, string reason)
	{
		return include ? ExportCollectionSelectionDecision.Included() : ExportCollectionSelectionDecision.Excluded(reason);
	}

	private static bool MatchesCollection(ExportCollectionBase collection, ProfileDescriptor descriptor)
	{
		return collection.ExportableAssets.Any(asset =>
			descriptor.Score(asset) >= descriptor.Threshold);
	}

	private static bool IsVisualAsset(IUnityObjectBase asset)
	{
		return asset.ClassName is "Sprite" or "Texture2D" or "PrefabHierarchyObject";
	}

	private static int ScorePlayerArt(IUnityObjectBase asset)
	{
		int score = 0;
		if (IsVisualAsset(asset))
		{
			score += 1;
		}
		score += CountMatches(asset, PlayerArtTokens) * 2;
		score += CountMatches(asset, CgTokens) * 2;
		score += CountMatches(asset, CharacterTokens);
		score += CountMatches(asset, BackgroundTokens);
		score -= CountMatches(asset, UiTokens);
		score -= CountMatches(asset, FontTokens) * 2;
		return score;
	}

	private static int ScoreCharacters(IUnityObjectBase asset)
	{
		int score = 0;
		if (asset.ClassName is "Sprite" or "PrefabHierarchyObject")
		{
			score += 2;
		}
		else if (asset.ClassName is "Texture2D" or "TextAsset")
		{
			score += 1;
		}
		score += CountMatches(asset, CharacterTokens) * 2;
		score += CountMatches(asset, ["portrait", "avatar", "standing", "bust"]) * 2;
		score -= CountMatches(asset, BackgroundTokens) * 2;
		score -= CountMatches(asset, AudioTokens) * 2;
		score -= CountMatches(asset, FontTokens) * 2;
		return score;
	}

	private static int ScoreAudio(IUnityObjectBase asset)
	{
		int score = 0;
		if (asset.ClassName == "AudioClip")
		{
			score += 10;
		}
		if (asset.ClassName == "TextAsset")
		{
			score += 1;
		}
		score += CountMatches(asset, AudioTokens) * 2;
		return score;
	}

	private static int ScoreUi(IUnityObjectBase asset)
	{
		int score = 0;
		if (asset.ClassName is "Sprite" or "PrefabHierarchyObject" or "MonoBehaviour")
		{
			score += 2;
		}
		else if (asset.ClassName == "Texture2D")
		{
			score += 1;
		}
		score += CountMatches(asset, UiTokens) * 2;
		score += CountMatches(asset, ["logo", "logos", "badge", "badges", "cursor", "frame", "frames"]);
		score -= CountMatches(asset, CharacterTokens);
		score -= CountMatches(asset, BackgroundTokens);
		score -= CountMatches(asset, AudioTokens) * 2;
		return score;
	}

	private static int ScoreNarrative(IUnityObjectBase asset)
	{
		int score = 0;
		if (asset.ClassName is "TextAsset" or "MonoBehaviour")
		{
			score += 2;
		}
		score += CountMatches(asset, NarrativeTokens) * 2;
		score -= CountMatches(asset, AudioTokens) * 2;
		return score;
	}

	private static int ScoreCg(IUnityObjectBase asset)
	{
		int score = 0;
		if (asset.ClassName is "Sprite" or "Texture2D")
		{
			score += 2;
		}
		else if (asset.ClassName == "PrefabHierarchyObject")
		{
			score += 1;
		}

		int cgSignals = CountMatches(asset, CgTokens);
		int storySignals = CountMatches(asset, NarrativeTokens) + CountMatches(asset, StaticCgContextTokens);
		int animatedSignals = CountMatches(asset, AnimatedVisualTokens);
		int thumbnailSignals = CountMatches(asset, ThumbnailTokens);

		score += cgSignals * 3;
		score += CountMatches(asset, ["event", "gallery", "memory", "story"]) * 2;
		score += storySignals * 2;

		if (asset.ClassName == "PrefabHierarchyObject" && cgSignals + storySignals > 0 && animatedSignals == 0)
		{
			score += 3;
		}

		score -= CountMatches(asset, UiTokens) * 3;
		score -= CountMatches(asset, FontTokens) * 3;
		score -= animatedSignals * 4;
		score -= thumbnailSignals * 2;
		score -= CountMatches(asset, AudioTokens) * 2;
		score -= CountMatches(asset, ["atlas", "sheet", "sheets", "tmp", "font"]) * 2;
		return score;
	}

	private static int ScoreBackground(IUnityObjectBase asset)
	{
		int score = 0;
		if (IsVisualAsset(asset))
		{
			score += 1;
		}
		score += CountMatches(asset, BackgroundTokens) * 2;
		score += CountMatches(asset, ["field", "room", "town", "forest", "city"]);
		score -= CountMatches(asset, CharacterTokens) * 2;
		score -= CountMatches(asset, UiTokens);
		score -= CountMatches(asset, FontTokens) * 2;
		return score;
	}

	private static int ScoreSprites(IUnityObjectBase asset)
	{
		int score = 0;
		if (asset.ClassName == "Sprite")
		{
			score += 4;
		}
		else if (asset.ClassName == "Texture2D")
		{
			score += 1;
		}
		score += CountMatches(asset, SpriteTokens) * 2;
		score += CountMatches(asset, ["sheet", "sheets", "icon", "icons"]);
		score -= CountMatches(asset, FontTokens) * 3;
		score -= CountMatches(asset, SystemTokens) * 2;
		score -= CountMatches(asset, AudioTokens) * 2;
		return score;
	}

	private static int CountMatches(IUnityObjectBase asset, IEnumerable<string> hints)
	{
		HashSet<string> tokens = GetTokens(asset);
		return hints.Count(tokens.Contains);
	}

	private static ProfileEvidence GetEvidence(IEnumerable<ProfileEvidence> evidence, string profile)
	{
		return evidence.FirstOrDefault(item => string.Equals(item.Profile, profile, StringComparison.Ordinal))
			?? new ProfileEvidence(profile, 0, 0, [], []);
	}

	private static int GetCount(IReadOnlyDictionary<string, int> counts, string key)
	{
		return counts.TryGetValue(key, out int count) ? count : 0;
	}

	private static bool HasPathHint(IReadOnlyDictionary<string, int> directoryCounts, string needle)
	{
		return directoryCounts.Keys.Any(key => key.Contains(needle, StringComparison.OrdinalIgnoreCase));
	}

	private static bool IsGenericTypeBucket(string directory)
	{
		string[] parts = directory.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		if (parts.Length != 2)
		{
			return false;
		}

		return string.Equals(parts[0], "Assets", StringComparison.Ordinal);
	}

	private static bool IsUsefulSignalToken(string token)
	{
		if (token.Length < 3)
		{
			return false;
		}

		if (IgnoredSignalTokens.Contains(token))
		{
			return false;
		}

		if (token.Any(char.IsDigit) && !token.Any(char.IsLetter))
		{
			return false;
		}

		if (token.Any(char.IsDigit) && token.Any(char.IsLetter))
		{
			return false;
		}

		return token.Any(char.IsLetter);
	}

	private static IEnumerable<KeyValuePair<string, int>> FilterSignals(Dictionary<string, int> signals, int matchedAssets)
	{
		int dominantThreshold = Math.Max(10, matchedAssets / 5);
		foreach (KeyValuePair<string, int> pair in signals)
		{
			bool semantic = SemanticSignalTokens.Contains(pair.Key);
			if (!semantic && pair.Value >= dominantThreshold)
			{
				continue;
			}

			if (!semantic && pair.Key.Length <= 4)
			{
				continue;
			}

			yield return pair;
		}
	}

	private static HashSet<string> GetTokens(IUnityObjectBase asset)
	{
		return Tokenize(asset.GetBestDirectory())
			.Concat(Tokenize(asset.GetBestName()))
			.ToHashSet(StringComparer.OrdinalIgnoreCase);
	}

	private static IEnumerable<string> Tokenize(string value)
	{
		return value
			.Split(['/', '\\', '_', '-', '.', ' ', '(', ')', '[', ']', ':'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			.Select(token => token.Trim().ToLowerInvariant());
	}
}
