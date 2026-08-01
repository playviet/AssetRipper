using AsmResolver.DotNet;
using AssetRipper.Import.Logging;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IlApi = Cpp2IL.Core.Cpp2IlApi;

namespace AssetRipper.Import.Structure.Assembly.Managers;

/// <summary>
/// Where the methods of an Il2Cpp assembly live in the game binary.
/// </summary>
/// <remarks>
/// Cpp2IL keeps the address on <see cref="MethodAnalysisContext.UnderlyingPointer"/>, and none of it reaches the
/// assemblies it builds: the attribute that would have carried it, <c>Cpp2ILInjected.AddressAttribute</c>, is written by
/// a processing layer that <see cref="IL2CppManager"/> does not run. So the only way back to the address is from the
/// Cpp2IL side, through the <c>AsmResolverMethod</c> entry the output format leaves on every analysis context.
/// <para>
/// The map is built once, on first use, from the application context Cpp2IL leaves behind after import. It is empty for
/// a Mono game and for anything loaded without Il2Cpp, which callers are expected to treat as "no address is known"
/// rather than as an error.
/// </para>
/// </remarks>
public static class Il2CppNativeAddresses
{
	/// <param name="Address">The virtual address of the method in the loaded binary.</param>
	/// <param name="Rva">The address relative to the image base, which is what a disassembler of the file shows.</param>
	/// <param name="Size">How many bytes of machine code the method has, or zero if it was never analyzed.</param>
	public readonly record struct NativeMethod(ulong Address, ulong Rva, int Size);

	private static Dictionary<MethodDefinition, NativeMethod>? map;

	public static bool TryGet(MethodDefinition method, out NativeMethod nativeMethod)
	{
		return (map ??= Build()).TryGetValue(method, out nativeMethod);
	}

	private static Dictionary<MethodDefinition, NativeMethod> Build()
	{
		Dictionary<MethodDefinition, NativeMethod> result = new();

		if (Cpp2IlApi.CurrentAppContext is not { } appContext)
		{
			return result;
		}

		foreach (TypeAnalysisContext type in appContext.AllTypes)
		{
			foreach (MethodAnalysisContext method in type.Methods)
			{
				if (method.GetExtraData<MethodDefinition>("AsmResolverMethod") is not { } definition)
				{
					continue;
				}

				try
				{
					// Subclasses that stand for a method with no metadata of its own throw here rather than returning zero.
					ulong address = method.UnderlyingPointer;
					if (address != 0)
					{
						result[definition] = new NativeMethod(address, method.Rva, method.RawBytes.Length);
					}
				}
				catch (Exception)
				{
					//An injected or generic instance method has no address, which is not worth reporting.
				}
			}
		}

		Logger.Verbose(LogCategory.Import, $"Found native addresses for {result.Count} methods");

		return result;
	}
}
