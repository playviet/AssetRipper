using AsmResolver.DotNet;
using AssetRipper.Import.Logging;
using AssetRipper.Import.Structure.Assembly.Managers;

namespace AssetRipper.Processing.Assemblies;

/// <summary>
/// Removes the members that Unity's own source generators produced, so the editor can generate them again.
/// </summary>
/// <remarks>
/// A generator's output is compiled into the assembly like any other code, so decompiling gives it back as source. The
/// editor then runs the same generator over that source and emits the members a second time, and the project no longer
/// compiles. Dropping them before decompilation leaves the editor free to produce its own.
/// <para>
/// Netcode for GameObjects is the common case: it generates RPC plumbing onto every NetworkBehaviour. The members are
/// removed as a set, since they only refer to each other.
/// </para>
/// </remarks>
public sealed class GeneratedCodeRemovalProcessor : IAssetProcessor
{
	/// <summary>
	/// A type Unity generates once per assembly to map its scripts. It has no other content.
	/// </summary>
	private const string GeneratedMonoScriptTypes = "UnitySourceGeneratedAssemblyMonoScriptTypes_v1";

	public void Process(GameData gameData) => Process(gameData.AssemblyManager);

	private static void Process(IAssemblyManager manager)
	{
		manager.ClearStreamCache();

		int methods = 0;
		int types = 0;

		foreach (ModuleDefinition module in manager.GetAssemblies().SelectMany(a => a.Modules))
		{
			(int removedMethods, int removedTypes) = Process(module);
			methods += removedMethods;
			types += removedTypes;
		}

		if (methods > 0 || types > 0)
		{
			Logger.Info(LogCategory.Processing, $"Removed {methods} generated {(methods == 1 ? "method" : "methods")} and {types} generated {(types == 1 ? "type" : "types")}");
		}
	}

	/// <summary>
	/// Removes the generated members from a single module.
	/// </summary>
	/// <returns>How many methods and how many types were removed.</returns>
	public static (int Methods, int Types) Process(ModuleDefinition module)
	{
		int methods = 0;
		int types = 0;

		for (int i = module.TopLevelTypes.Count - 1; i >= 0; i--)
		{
			if (module.TopLevelTypes[i].Name == GeneratedMonoScriptTypes)
			{
				module.TopLevelTypes.RemoveAt(i);
				types++;
			}
		}

		foreach (TypeDefinition type in module.GetAllTypes())
		{
			for (int i = type.Methods.Count - 1; i >= 0; i--)
			{
				if (IsGenerated(type.Methods[i].Name))
				{
					type.Methods.RemoveAt(i);
					methods++;
				}
			}
		}

		return (methods, types);
	}

	/// <summary>
	/// Whether a method is one a Unity generator produces.
	/// </summary>
	/// <remarks>
	/// Every name here is reserved by the generator that emits it. Two of them start with a double underscore, which
	/// C# reserves for exactly this purpose, and the other is prefixed rather than being a plausible identifier on its
	/// own, so hand written code is not going to collide with any of them.
	/// </remarks>
	private static bool IsGenerated(string? name) => name is not null
		&& (name is "__getTypeName" or "__initializeVariables"
			|| name.StartsWith("__rpc_handler_", StringComparison.Ordinal)
			|| name.StartsWith("InitializeRPCS_", StringComparison.Ordinal));
}
