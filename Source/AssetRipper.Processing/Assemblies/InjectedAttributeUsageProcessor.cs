using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using AssetRipper.Import.Logging;
using AssetRipper.Import.Structure.Assembly.Managers;

namespace AssetRipper.Processing.Assemblies;

/// <summary>
/// Widens the declared targets of the attributes Cpp2IL injects, so that the decompiled scripts compile.
/// </summary>
/// <remarks>
/// Cpp2IL's analysis attributes are declared as <c>AttributeTargets.Method</c>, and it applies them to every method it
/// analysed. At the IL level a constructor is a method, so applying one to a <c>.ctor</c> is valid there. C# does not
/// agree: <c>AttributeTargets.Method</c> excludes constructors, and the compiler rejects the decompiled result with
/// CS0592. Since these attributes only record what the analysis found, restricting where they may appear buys nothing,
/// and the targets are widened to everything.
/// </remarks>
public sealed class InjectedAttributeUsageProcessor : IAssetProcessor
{
	/// <summary>
	/// <see cref="AttributeTargets.All"/>. Hard coded because the enum lives in the game's assemblies, not in ours.
	/// </summary>
	private const int AttributeTargetsAll = 32767;

	private const string InjectedNamespacePrefix = "Cpp2ILInjected";

	public void Process(GameData gameData) => Process(gameData.AssemblyManager);

	private static void Process(IAssemblyManager manager)
	{
		manager.ClearStreamCache();

		int widened = 0;
		foreach (TypeDefinition type in manager.GetAllTypes())
		{
			if (type.Namespace?.Value is not string ns || !ns.StartsWith(InjectedNamespacePrefix, StringComparison.Ordinal))
			{
				continue;
			}

			foreach (CustomAttribute attribute in type.CustomAttributes)
			{
				if (!attribute.IsType("System", "AttributeUsageAttribute"))
				{
					continue;
				}

				if (attribute.Signature is not { FixedArguments.Count: > 0 } signature)
				{
					continue;
				}

				CustomAttributeArgument argument = signature.FixedArguments[0];
				if (argument.Element is not int targets || targets == AttributeTargetsAll)
				{
					continue;
				}

				signature.FixedArguments[0] = new CustomAttributeArgument(argument.ArgumentType, AttributeTargetsAll);
				widened++;
			}
		}

		if (widened > 0)
		{
			Logger.Info(LogCategory.Processing, $"Widened the targets of {widened} injected analysis {(widened == 1 ? "attribute" : "attributes")}");
		}
	}
}
