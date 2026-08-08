using System.Collections.Generic;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Takes away the argument registers a runtime helper does not read.
/// </summary>
/// <remarks>
/// <para>
/// A call to something the analysis has not resolved is given every argument register there is, because
/// nothing says how many it takes. <see cref="MetadataResolver.TrimResolvedCallArguments"/> cuts that back
/// once a managed method is known - but a **key function** never becomes one, so a boxing call keeps all
/// eight, and every register that happened to hold something keeps its load alive:
/// </para>
/// <code>
/// Move v89, [action + 0x28]                                  // the delegate's own plumbing, dead
/// Call "il2cpp_vm_object_box", v92, class, s, v89, 0, …      // and read here, so never collected
/// </code>
/// <para>
/// which comes out as <c>_ = "Unmanaged memory load: [v154 (Action`1&lt;EGameState&gt;)+28]"</c> beside a
/// call that had already been recovered perfectly. The generator reads two arguments from that call and no
/// more; the rest are registers the convention happened to leave in place.
/// </para>
/// <para>
/// This is the same reasoning as the class pointer that boxing resolves from its operand's type rather than
/// by reading it - what is never read has no business keeping a load alive.
/// </para>
/// </remarks>
public static class KeyFunctionArguments
{
    /// <summary>How many arguments each helper the generator writes actually reads.</summary>
    private static readonly Dictionary<string, int> Takes = new()
    {
        ["il2cpp_codegen_initialize_runtime_metadata"] = 1,
        ["il2cpp_codegen_initialize_method"] = 1,
        ["SzArrayNew"] = 2,
        ["il2cpp_codegen_box"] = 2,
        ["il2cpp_vm_object_box"] = 2,
        ["il2cpp_vm_object_is_inst"] = 2,
        [UnboxRecovery.ObjectUnbox] = 2,
    };

    /// <summary>
    /// How many arguments an imported C function reads. Its name says so exactly, and an import never
    /// becomes a managed method either, so it keeps every register the same way a key function does.
    /// </summary>
    private static readonly Dictionary<string, int> Imported = new()
    {
        ["memcpy"] = 3, ["memset"] = 3, ["memmove"] = 3, ["memcmp"] = 3,
        ["modf"] = 2, ["modff"] = 2, ["sincos"] = 3, ["sincosf"] = 3,
        ["exp2"] = 1, ["exp2f"] = 1, ["ldexp"] = 2, ["ldexpf"] = 2, ["fmod"] = 2, ["fmodf"] = 2,
    };

    public static bool Run(MethodAnalysisContext method)
    {
        if (method.ControlFlowGraph is not { } graph)
            return false;

        var changed = false;

        foreach (var instruction in graph.Instructions)
        {
            if (!instruction.IsCall || instruction.Operands.Count == 0)
                continue;

            if (instruction.Operands[0] is ulong address)
            {
                if (method.AppContext.Binary is not LibCpp2IL.Elf.ElfFile binary
                    || binary.ImportedFunctionAt(address) is not { } name
                    || !Imported.TryGetValue(name, out var reads))
                    continue;

                changed |= Trim(instruction, reads);
                continue;
            }

            if (instruction.Operands[0] is not string helper || !Takes.TryGetValue(helper, out var takes))
                continue;

            changed |= Trim(instruction, takes);
        }

        return changed;
    }

    /// <summary>Cuts a call back to the arguments it reads.</summary>
    private static bool Trim(Instruction instruction, int takes)
    {
        //Operands are the target, the result where there is one, and then the argument registers.
        var wanted = (instruction.OpCode == OpCode.Call ? 2 : 1) + takes;

        if (instruction.Operands.Count <= wanted)
            return false;

        instruction.Operands.RemoveRange(wanted, instruction.Operands.Count - wanted);
        return true;
    }
}
