using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Collections;
using AsmResolver.PE.DotNet.Cil;
using AssetRipper.CIL;
using AssetRipper.Import.Logging;
using AssetRipper.Import.Structure.Assembly.Managers;
using System.Text;

namespace AssetRipper.Export.UnityProjects.Scripts;

/// <summary>
/// Writes the CIL that a decompiled script was made from into a file beside the script.
/// </summary>
/// <remarks>
/// Recovered Il2Cpp source is incomplete: statements are commented out where they did not parse, and whole methods come
/// out empty. The IL those methods were decompiled from still has what the C# lost - the exact types, the real call
/// targets, and the address to go back to in the binary - so it is written out next to the source rather than thrown
/// away, and whoever finishes the source by hand can read the two together.
/// <para>
/// The companion lands beside its script because the file name is computed the way the decompiler computes it, with the
/// decompiler's own helpers, and a companion is only written where the script it belongs to actually exists.
/// </para>
/// </remarks>
public static class ScriptIlCompanionExporter
{
	/// <summary>
	/// Appended to a script path in place of <c>.cs</c>.
	/// </summary>
	public const string FileExtension = ".il.md";

	private const string ScriptExtension = ".cs";

	/// <summary>
	/// Writes a companion file for every script the decompiler wrote for <paramref name="assembly"/>.
	/// </summary>
	/// <param name="assembly">The assembly that was just decompiled.</param>
	/// <param name="nestedDirectoriesForNamespaces">
	/// The decompiler setting of the same name, which decides whether a namespace becomes one directory or several.
	/// </param>
	/// <param name="outputFolder">The folder the decompiler wrote the scripts into.</param>
	/// <param name="fileSystem">The file system to write to.</param>
	public static void Export(AssemblyDefinition assembly, bool nestedDirectoriesForNamespaces, string outputFolder, FileSystem fileSystem)
	{
		int written = 0;

		foreach ((string relativePath, _, List<TypeDefinition> types) in ScriptFileLayout.GroupTypesByFile(assembly, nestedDirectoriesForNamespaces))
		{
			string scriptPath = fileSystem.Path.Join(outputFolder, relativePath);
			if (!fileSystem.File.Exists(scriptPath))
			{
				// The decompiler skipped this type, so there is no source for the IL to sit beside.
				continue;
			}

			string content = CreateContent(fileSystem.Path.GetFileName(scriptPath), types);
			fileSystem.File.WriteAllText(scriptPath[..^ScriptExtension.Length] + FileExtension, content);
			written++;
		}

		Logger.Info(LogCategory.Export, $"Wrote IL for {written} {(written == 1 ? "script" : "scripts")} of {assembly.Name}");
	}

	/// <summary>
	/// Builds the text of one companion file.
	/// </summary>
	/// <param name="scriptFileName">The name of the script the companion belongs to.</param>
	/// <param name="types">The types the decompiler put in that script, in the order it declared them.</param>
	public static string CreateContent(string scriptFileName, IEnumerable<TypeDefinition> types)
	{
		StringBuilder builder = new();

		builder.Append("# ").AppendLine(scriptFileName);
		builder.AppendLine();
		builder.AppendLine($"The CIL that `{scriptFileName}` was decompiled from, in declaration order.");

		foreach (TypeDefinition type in types)
		{
			builder.AppendLine();
			builder.Append("## ").AppendLine(type.FullName);

			if (type.Methods.Count == 0)
			{
				builder.AppendLine();
				builder.AppendLine("The type declares no methods.");
			}

			foreach (MethodDefinition method in type.Methods)
			{
				AppendMethod(builder, method);
			}
		}

		return builder.ToString();
	}

	private static void AppendMethod(StringBuilder builder, MethodDefinition method)
	{
		builder.AppendLine();
		builder.Append("### ").AppendLine(GetSignature(method));
		builder.AppendLine();

		if (Il2CppNativeAddresses.TryGet(method, out Il2CppNativeAddresses.NativeMethod native))
		{
			builder.Append($"Address: 0x{native.Address:X} (RVA 0x{native.Rva:X}");
			if (native.Size > 0)
			{
				builder.Append($", {native.Size} bytes");
			}
			builder.AppendLine(")");
		}
		else
		{
			builder.AppendLine("Address: unknown");
		}

		CilMethodBody? body = method.CilMethodBody;
		if (body is null)
		{
			builder.AppendLine();
			builder.AppendLine("Body: none - the method is abstract, external, or implemented by the runtime.");
			return;
		}

		try
		{
			AppendBody(builder, method, body);
		}
		catch (Exception exception)
		{
			// One body that cannot be read must not cost the other hundred thousand.
			builder.AppendLine();
			builder.AppendLine($"Body: unreadable - {Flatten(exception.Message)}");
		}
	}

	private static void AppendBody(StringBuilder builder, MethodDefinition method, CilMethodBody body)
	{
		body.Instructions.CalculateOffsets();

		if (TryGetAnalysisFailure(body, out string? failure))
		{
			builder.AppendLine();
			builder.AppendLine($"Body: analysis failed - {Flatten(failure)}");
		}
		else if (IsMinimalImplementation(method, body))
		{
			builder.AppendLine();
			builder.AppendLine("Body: stub - no code was recovered, so this is a minimal implementation rather than the method's own code.");
		}

		builder.AppendLine();
		builder.AppendLine("```il");

		if (body.LocalVariables.Count > 0)
		{
			builder.Append(body.InitializeLocals ? ".locals init (" : ".locals (");
			for (int i = 0; i < body.LocalVariables.Count; i++)
			{
				CilLocalVariable local = body.LocalVariables[i];
				builder.AppendLine();
				builder.Append($"\t[{local.Index}] {local.VariableType}");
				builder.Append(i == body.LocalVariables.Count - 1 ? ")" : ",");
			}
			builder.AppendLine();
		}

		foreach (CilExceptionHandler handler in body.ExceptionHandlers)
		{
			builder.AppendLine(Format(handler));
		}

		if (body.LocalVariables.Count > 0 || body.ExceptionHandlers.Count > 0)
		{
			builder.AppendLine();
		}

		foreach (CilInstruction instruction in body.Instructions)
		{
			builder.AppendLine(Format(instruction));
		}

		builder.AppendLine("```");
	}

	/// <summary>
	/// Cpp2IL replaces a method it could not analyze with a throw carrying the reason, which is worth saying plainly.
	/// </summary>
	private static bool TryGetAnalysisFailure(CilMethodBody body, [NotNullWhen(true)] out string? message)
	{
		CilInstructionCollection instructions = body.Instructions;
		if (instructions.Count == 3
			&& instructions[0].OpCode == CilOpCodes.Ldstr
			&& instructions[1].OpCode == CilOpCodes.Newobj
			&& instructions[2].OpCode == CilOpCodes.Throw
			&& instructions[0].Operand is string text)
		{
			message = text;
			return true;
		}

		message = null;
		return false;
	}

	/// <summary>
	/// Is this body the one <see cref="MethodDefinitionExtensions.ReplaceMethodBodyWithMinimalImplementation"/> writes?
	/// </summary>
	/// <remarks>
	/// Everything that gives up on a method uses that helper: Cpp2IL when it recovers no instructions,
	/// <c>MethodStubbingProcessor</c> below content level 3, and <c>UnreadableMethodBodyProcessor</c> when the recovered
	/// body is one a reader would reject. Generating the minimal implementation again and comparing is therefore the one
	/// test that agrees with all three, and it agrees for the right reason.
	/// <para>
	/// The generated body replaces the real one for as long as the comparison takes, because the helper works in place.
	/// The cheap shape test comes first so that the hundred thousand methods with real code never reach it.
	/// </para>
	/// </remarks>
	private static bool IsMinimalImplementation(MethodDefinition method, CilMethodBody body)
	{
		if (!CouldBeMinimalImplementation(body))
		{
			return false;
		}

		try
		{
			method.ReplaceMethodBodyWithMinimalImplementation();
			if (method.CilMethodBody is not { } generated)
			{
				return false;
			}

			//Both bodies are compared as they are written, so both have to know where their instructions are.
			generated.Instructions.CalculateOffsets();
			return HaveSameInstructions(body, generated);
		}
		catch (Exception)
		{
			return false;
		}
		finally
		{
			method.CilMethodBody = body;
		}
	}

	/// <summary>
	/// A minimal implementation stores default values and returns, and calls nothing but a base constructor.
	/// </summary>
	private static bool CouldBeMinimalImplementation(CilMethodBody body)
	{
		if (body.ExceptionHandlers.Count > 0)
		{
			return false;
		}

		foreach (CilInstruction instruction in body.Instructions)
		{
			switch (instruction.OpCode.FlowControl)
			{
				case CilFlowControl.Next when instruction.OpCode != CilOpCodes.Ldstr:
				case CilFlowControl.Return:
					break;
				case CilFlowControl.Call when instruction.OpCode == CilOpCodes.Call && (instruction.Operand as IMethodDescriptor)?.Name == ".ctor":
					break;
				default:
					return false;
			}
		}

		return true;
	}

	private static bool HaveSameInstructions(CilMethodBody left, CilMethodBody right)
	{
		if (left.Instructions.Count != right.Instructions.Count)
		{
			return false;
		}

		for (int i = 0; i < left.Instructions.Count; i++)
		{
			CilInstruction leftInstruction = left.Instructions[i];
			CilInstruction rightInstruction = right.Instructions[i];
			if (leftInstruction.OpCode != rightInstruction.OpCode || Format(leftInstruction) != Format(rightInstruction))
			{
				return false;
			}
		}

		return true;
	}

	private static string Format(CilInstruction instruction)
	{
		try
		{
			return instruction.ToString();
		}
		catch (Exception)
		{
			// A recovered branch can point at an instruction that is not in the body, which the formatter cannot resolve.
			return $"IL_{instruction.Offset:X4}: {instruction.OpCode.Mnemonic} <unresolvable operand>";
		}
	}

	private static string Format(CilExceptionHandler handler)
	{
		string range = $".try {Format(handler.TryStart)} to {Format(handler.TryEnd)}";
		string body = $"handler {Format(handler.HandlerStart)} to {Format(handler.HandlerEnd)}";
		return handler.HandlerType switch
		{
			CilExceptionHandlerType.Exception => $"{range} catch {handler.ExceptionType} {body}",
			CilExceptionHandlerType.Filter => $"{range} filter {Format(handler.FilterStart)} {body}",
			_ => $"{range} {handler.HandlerType.ToString().ToLowerInvariant()} {body}",
		};
	}

	private static string Format(ICilLabel? label)
	{
		try
		{
			return label?.ToString() ?? "IL_????";
		}
		catch (Exception)
		{
			return "IL_????";
		}
	}

	/// <summary>
	/// The method as the assembly declares it, spelled the way the C# beside it is.
	/// </summary>
	private static string GetSignature(MethodDefinition method)
	{
		StringBuilder builder = new();

		builder.Append(GetVisibility(method));
		if (method.IsStatic)
		{
			builder.Append("static ");
		}
		if (method.IsAbstract)
		{
			builder.Append("abstract ");
		}
		else if (method.IsVirtual)
		{
			builder.Append("virtual ");
		}

		builder.Append(method.Signature?.ReturnType.ToString() ?? "?").Append(' ').Append(method.Name);

		if (method.GenericParameters.Count > 0)
		{
			builder.Append('<');
			builder.AppendJoin(", ", method.GenericParameters.Select(p => p.Name?.ToString() ?? "?"));
			builder.Append('>');
		}

		builder.Append('(');
		bool first = true;
		foreach (Parameter parameter in method.Parameters)
		{
			if (!first)
			{
				builder.Append(", ");
			}
			first = false;
			builder.Append(parameter.ParameterType).Append(' ').Append(parameter.Name);
		}
		builder.Append(')');

		return builder.ToString();
	}

	private static string GetVisibility(MethodDefinition method)
	{
		if (method.IsPublic)
		{
			return "public ";
		}
		if (method.IsFamily)
		{
			return "protected ";
		}
		if (method.IsAssembly)
		{
			return "internal ";
		}
		if (method.IsFamilyOrAssembly)
		{
			return "protected internal ";
		}
		if (method.IsFamilyAndAssembly)
		{
			return "private protected ";
		}
		return "private ";
	}

	/// <summary>
	/// Puts a message on one line, so that it cannot break the file it is written into.
	/// </summary>
	private static string Flatten(string message)
	{
		return message.ReplaceLineEndings(" ").Replace('`', '\'');
	}
}
