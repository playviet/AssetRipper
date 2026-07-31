namespace AssetRipper.Export.UserPackages;

/// <summary>
/// Maps the assemblies a build contains to the Unity packages they came from.
/// </summary>
/// <remarks>
/// A build ships the compiled assemblies of every package it used. Exporting those as plugins gives the project a
/// stripped copy of a package it could simply reference instead, which duplicates every type in the package and, for
/// some of them, makes Unity's own API updater fail outright. This map is what lets the exporter recognise them.
/// <para>
/// Package identifiers only, deliberately. Versions differ per editor version and are read from the installed editor
/// rather than guessed; see <see cref="UnityPackageIndex"/>.
/// </para>
/// </remarks>
public static class UnityPackageMap
{
	/// <summary>
	/// Assembly name to package identifier. Assemblies not listed here are exported as usual.
	/// </summary>
	private static readonly Dictionary<string, string> AssemblyToPackage = new(StringComparer.Ordinal)
	{
		// Addressables ships its runtime split across two assemblies.
		{ "Unity.Addressables", "com.unity.addressables" },
		{ "Unity.ResourceManager", "com.unity.addressables" },

		{ "Unity.Burst", "com.unity.burst" },
		{ "Unity.Burst.Unsafe", "com.unity.burst" },
		{ "Unity.Cinemachine", "com.unity.cinemachine" },
		{ "Unity.Collections", "com.unity.collections" },

		{ "Unity.InputSystem", "com.unity.inputsystem" },
		{ "Unity.InputSystem.ForUI", "com.unity.inputsystem" },

		{ "Unity.Localization", "com.unity.localization" },
		{ "Unity.Mathematics", "com.unity.mathematics" },

		{ "Unity.Purchasing", "com.unity.purchasing" },
		{ "Unity.Purchasing.AppleMacosStub", "com.unity.purchasing" },
		{ "Unity.Purchasing.AppleStub", "com.unity.purchasing" },
		{ "Unity.Purchasing.Security", "com.unity.purchasing" },
		{ "Unity.Purchasing.SecurityCore", "com.unity.purchasing" },
		{ "Unity.Purchasing.Stores", "com.unity.purchasing" },
		{ "Unity.Purchasing.Utilities", "com.unity.purchasing" },

		// The scriptable render pipeline splits its runtime across core and per pipeline assemblies.
		{ "Unity.RenderPipelines.Core.Runtime", "com.unity.render-pipelines.core" },
		{ "Unity.RenderPipelines.Core.Runtime.Shared", "com.unity.render-pipelines.core" },
		{ "Unity.RenderPipelines.GPUDriven.Runtime", "com.unity.render-pipelines.core" },
		{ "Unity.RenderPipeline.Universal.ShaderLibrary", "com.unity.render-pipelines.universal" },
		{ "Unity.RenderPipelines.Universal.2D.Runtime", "com.unity.render-pipelines.universal" },
		{ "Unity.RenderPipelines.Universal.Runtime", "com.unity.render-pipelines.universal" },
		{ "Unity.RenderPipelines.HighDefinition.Runtime", "com.unity.render-pipelines.high-definition" },
		{ "Unity.RenderPipelines.HighDefinition.Config.Runtime", "com.unity.render-pipelines.high-definition-config" },

		{ "Unity.Services.Analytics", "com.unity.services.analytics" },
		{ "Unity.Services.Core", "com.unity.services.core" },
		{ "Unity.Services.Core.Configuration", "com.unity.services.core" },
		{ "Unity.Services.Core.Device", "com.unity.services.core" },
		{ "Unity.Services.Core.Environments.Internal", "com.unity.services.core" },
		{ "Unity.Services.Core.Internal", "com.unity.services.core" },
		{ "Unity.Services.Core.Registration", "com.unity.services.core" },
		{ "Unity.Services.Core.Scheduler", "com.unity.services.core" },
		{ "Unity.Services.Core.Telemetry", "com.unity.services.core" },
		{ "Unity.Services.Core.Threading", "com.unity.services.core" },

		// Unity redistributes Newtonsoft.Json as a package, and several other packages compile against it. A build's
		// stripped copy left in the project shadows the real one and breaks those packages' own source.
		{ "Newtonsoft.Json", "com.unity.nuget.newtonsoft-json" },

		{ "Unity.Timeline", "com.unity.timeline" },
		{ "Unity.VisualScripting.Core", "com.unity.visualscripting" },
		{ "Unity.VisualScripting.Flow", "com.unity.visualscripting" },
		{ "Unity.VisualScripting.State", "com.unity.visualscripting" },

		// uGUI absorbed TextMeshPro in Unity 6. Both names resolve to whichever of the two the editor still ships,
		// which UnityPackageIndex decides by looking at what the editor actually offers.
		{ "Unity.TextMeshPro", "com.unity.ugui" },
		{ "UnityEngine.UI", "com.unity.ugui" },
	};

	public static bool TryGetPackage(string assemblyName, [NotNullWhen(true)] out string? packageId)
	{
		return AssemblyToPackage.TryGetValue(assemblyName, out packageId);
	}

	public static IReadOnlyDictionary<string, string> All => AssemblyToPackage;
}
