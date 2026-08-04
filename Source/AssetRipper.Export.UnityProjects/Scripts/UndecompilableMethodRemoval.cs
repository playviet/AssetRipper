using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using AssetRipper.Import.Logging;
using AssetRipper.Import.Structure.Assembly.Managers;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.TypeSystem;

namespace AssetRipper.Export.UnityProjects.Scripts;

/// <summary>
/// Empties a method the decompiler cannot read, so that the rest of the assembly still gets written.
/// </summary>
/// <remarks>
/// An assembly is decompiled as a whole, its types shared out over as many threads as there are, and one type
/// that throws ends the run: the files not yet written are simply never written, and which ones those are
/// depends on how the work happened to be shared out. A single method the decompiler cannot read therefore
/// costs whatever was behind it in the queue - in one measured run, nineteen of the largest files in the
/// project, none of which had anything wrong with them.
///
/// Recovered bodies make that likelier than it would be for an assembly a compiler wrote, since they can hold
/// shapes no compiler emits. The body is the only thing actually lost, so it is the only thing given up: the
/// method is left declared and empty, the run is repeated, and everything else is written as it should be.
/// </remarks>
internal static class UndecompilableMethodRemoval
{
	/// <summary>
	/// Empties the method the decompiler failed on, if the failure names one that has not already been given
	/// up on. Returns whether it did, and so whether repeating the run is worth anything.
	/// </summary>
	public static bool EmptyTheMethodThatFailed(
		IAssemblyManager assemblyManager,
		AssemblyDefinition assembly,
		Exception failure,
		HashSet<string> alreadyEmptied)
	{
		if (FailedMethod(failure) is not { } failed || !alreadyEmptied.Add(failed.FullName))
		{
			return false;
		}

		MethodDefinition? method = FindMethod(assembly, failed);

		if (method is null)
		{
			return false;
		}

		Logger.Warning(LogCategory.Export, $"Emptying {method.DeclaringType?.FullName}.{method.Name}, which the decompiler could not read, so that the rest of the assembly is still written.");

		Empty(method);

		//The decompiler reads a serialised copy of the assembly, so the copy taken before the change has to go.
		assemblyManager.ClearStreamCache();

		return true;
	}

	/// <summary>
	/// The method a decompiler failure names, out of however deeply the failure is wrapped. The failure that
	/// ends the run wraps the one that names the method, so this looks the whole way down.
	/// </summary>
	private static IMethod? FailedMethod(Exception failure)
	{
		switch (failure)
		{
			case DecompilerException { DecompiledEntity: IMethod method }:
				return method;

			case AggregateException aggregate:
				foreach (Exception inner in aggregate.InnerExceptions)
				{
					if (FailedMethod(inner) is { } found)
					{
						return found;
					}
				}
				return null;

			default:
				return failure.InnerException is { } innerException ? FailedMethod(innerException) : null;
		}
	}

	/// <summary>
	/// The method the failure names, in the assembly the decompiler was given.
	/// </summary>
	/// <remarks>
	/// Matched by name rather than by metadata token: the assembly is built in memory and only given tokens
	/// when it is serialised for the decompiler to read, so the token the failure carries belongs to that copy
	/// and means nothing here. Two methods of one type can share a name, so the number of parameters is
	/// compared as well; a wrong match among overloads would cost one more body and nothing else.
	/// </remarks>
	private static MethodDefinition? FindMethod(AssemblyDefinition assembly, IMethod failed)
	{
		string? declaringTypeName = failed.DeclaringTypeDefinition?.Name;

		foreach (ModuleDefinition module in assembly.Modules)
		{
			foreach (TypeDefinition type in module.GetAllTypes())
			{
				if (declaringTypeName is not null && type.Name != declaringTypeName)
				{
					continue;
				}

				foreach (MethodDefinition method in type.Methods)
				{
					if (method.Name == failed.Name && method.Parameters.Count == failed.Parameters.Count)
					{
						return method;
					}
				}
			}
		}

		return null;
	}

	/// <summary>
	/// Replaces a body with one that does nothing but return whatever the method says it returns.
	/// </summary>
	private static void Empty(MethodDefinition method)
	{
		if (method.CilMethodBody is not { } body)
		{
			return;
		}

		body.Instructions.Clear();
		body.LocalVariables.Clear();
		body.ExceptionHandlers.Clear();

		TypeSignature? returnType = method.Signature?.ReturnType;

		if (returnType is not null && returnType.ElementType is not AsmResolver.PE.DotNet.Metadata.Tables.ElementType.Void)
		{
			if (returnType.IsValueType)
			{
				CilLocalVariable result = new(returnType);
				body.LocalVariables.Add(result);
				body.Instructions.Add(CilOpCodes.Ldloca, result);
				body.Instructions.Add(CilOpCodes.Initobj, returnType.ToTypeDefOrRef());
				body.Instructions.Add(CilOpCodes.Ldloc, result);
			}
			else
			{
				body.Instructions.Add(CilOpCodes.Ldnull);
			}
		}

		body.Instructions.Add(CilOpCodes.Ret);

	}
}
