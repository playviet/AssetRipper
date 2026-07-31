using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AssetRipper.CIL;
using AssetRipper.Import.Logging;
using AssetRipper.Import.Structure.Assembly.Managers;

namespace AssetRipper.Processing.Assemblies;

/// <summary>
/// Replaces the method bodies that a reader would reject with a minimal implementation.
/// </summary>
/// <remarks>
/// Il2Cpp method body recovery reconstructs CIL from native code, and it is not always able to produce a body that
/// holds together: a branch can end up pointing outside the method, or the stack can be left at a different height on
/// two paths that meet. The decompiler throws on such a body, and because it decompiles a whole file at once, the type
/// that contained the method is written out as an empty file. Everything referring to that type then fails to compile,
/// which costs far more than the one method did.
/// <para>
/// Dropping the body instead keeps the signature, so the file is written and the rest of the project still compiles.
/// </para>
/// </remarks>
public sealed class UnreadableMethodBodyProcessor : IAssetProcessor
{
	public void Process(GameData gameData) => Process(gameData.AssemblyManager);

	private static void Process(IAssemblyManager manager)
	{
		manager.ClearStreamCache();

		int replaced = 0;
		foreach (ModuleDefinition module in manager.GetAssemblies().SelectMany(a => a.Modules))
		{
			replaced += Process(module);
		}

		if (replaced > 0)
		{
			Logger.Info(LogCategory.Processing, $"Discarded {replaced} unreadable method {(replaced == 1 ? "body" : "bodies")}");
		}
	}

	/// <summary>
	/// Removes the unreadable method bodies from a single module.
	/// </summary>
	/// <returns>How many bodies were replaced.</returns>
	public static int Process(ModuleDefinition module)
	{
		int replaced = 0;

		foreach (TypeDefinition type in module.GetAllTypes())
		{
			foreach (MethodDefinition method in type.Methods)
			{
				if (method.CilMethodBody is { } body && !IsReadable(body))
				{
					method.ReplaceMethodBodyWithMinimalImplementation();
					replaced++;
				}
			}
		}

		return replaced;
	}

	/// <summary>
	/// Whether a body can be read back after being written.
	/// </summary>
	/// <remarks>
	/// Label verification covers the branches that go nowhere, and the max stack computation covers the paths that
	/// disagree about how much is on the stack. Between them they reject the bodies a reader chokes on.
	/// </remarks>
	private static bool IsReadable(CilMethodBody body)
	{
		try
		{
			body.Instructions.CalculateOffsets();
			body.VerifyLabels();
			body.ComputeMaxStack();
			return true;
		}
		catch (Exception)
		{
			return false;
		}
	}
}
