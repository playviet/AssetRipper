using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// The second half of a dispatch pair, once the first half has become a named call.
/// </summary>
/// <remarks>
/// <para>
/// Every virtual and interface call is reached through two adjacent words: <c>LDP X8, X1, [X0]</c> reads the
/// method pointer at the slot and the <c>MethodInfo*</c> eight bytes after it.
/// <see cref="VirtualCallRecovery"/> and <see cref="InterfaceCallRecovery"/> both match the first word and
/// rewrite the call around it - and neither touches the second, which survives all the way to IL generation
/// as an <c>Unmanaged memory load</c>.
/// </para>
/// <para>
/// That is not only noise. The read still writes <b>X1</b>, an argument register, so the next call in the
/// method picks it up as an argument: <c>TrackingManager::TrackGameStart</c> hands the leftover
/// <c>MethodInfo</c> to <c>TrackingEvents.GameStart</c> where <c>gameMode</c> belongs. 59 sites in 31 bodies,
/// 7 of which have nothing else wrong with them.
/// </para>
/// <para>
/// The signal that a call resolved is decisive and needs no coordination between the passes: the method
/// pointer's own local <b>becomes unread</b>, because the rewrite dropped the operand that was reading it.
/// Where the call did not resolve the pointer is still read by the <c>IndirectCall</c>, and both halves are
/// left exactly as they were - which matters, because an unresolved call's <c>MethodInfo</c> is what
/// <see cref="SharedCallByItsOwnMethodInfo"/> names it by.
/// </para>
/// <para>
/// Typed rather than removed. Saying what the value is lets the passes that already know what to do with a
/// <c>MethodInfo</c> - the rgctx readers, the dead-code eliminator, the argument conventions - do it, and
/// none of them can be surprised by a register that stops being defined.
/// </para>
/// </remarks>
public static class ResolvedCallMethodInfo
{
    public static void Run(MethodAnalysisContext method)
    {
        if (method.ControlFlowGraph is not { } graph)
            return;

        var pointerSize = method.AppContext.Binary.is32Bit ? 4 : 8;
        var methodInfoType = MethodInfoType(method);

        if (methodInfoType is null)
            return;

        var definitions = new Dictionary<LocalVariable, Instruction>();
        var read = new HashSet<LocalVariable>();

        foreach (var instruction in graph.Instructions)
        {
            if (instruction.Destination is LocalVariable destination)
                definitions[destination] = instruction;

            for (var at = instruction.Destination is null ? 0 : 1; at < instruction.Operands.Count; at++)
                foreach (var named in Mentioned(instruction.Operands[at]))
                    read.Add(named);
        }

        //Keyed by the word the read is at, so the two halves of a pair can find each other.
        var byPlace = new Dictionary<(LocalVariable Base, long Addend), Instruction>();

        foreach (var instruction in graph.Instructions)
        {
            if (instruction.OpCode == OpCode.Move
                && instruction.Operands is [LocalVariable, MemoryOperand { Index: null, Scale: 0, Base: LocalVariable owner } at])
            {
                byPlace[(owner, at.Addend)] = instruction;
            }
        }

        foreach (var ((owner, addend), pointer) in byPlace)
        {
            //Only a real vtable slot, decided the same way the call recovery decides it, or the interface
            //invoker's own entry point.
            if (MiscUtils.GetSlotNum((int)addend, method.AppContext.MetadataVersion, method.AppContext.Binary.is32Bit) < 0
                && addend != Il2CppMethodInfoLayout.InvokerMethod)
            {
                continue;
            }

            if (pointer.Destination is not LocalVariable held || read.Contains(held))
                continue;

            if (!byPlace.TryGetValue((owner, addend + pointerSize), out var sibling)
                || sibling.Destination is not LocalVariable info)
            {
                continue;
            }

            info.Type = methodInfoType;
            info.IsMethodInfo = true;
        }
    }

    private static TypeAnalysisContext? MethodInfoType(MethodAnalysisContext method)
        => method.ParameterLocals.FirstOrDefault(local => local.IsMethodInfo)?.Type
           ?? method.AppContext.AllTypes.FirstOrDefault(type => type.Name == "Il2CppMethodInfo");

    private static IEnumerable<LocalVariable> Mentioned(object operand)
    {
        switch (operand)
        {
            case LocalVariable local:
                yield return local;
                break;
            case MemoryOperand memory:
                if (memory.Base is LocalVariable owner)
                    yield return owner;
                if (memory.Index is LocalVariable index)
                    yield return index;
                break;
        }
    }
}
