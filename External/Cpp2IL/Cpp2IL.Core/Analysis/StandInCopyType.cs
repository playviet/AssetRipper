using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Overrules a runtime stand-in on a local that is copied to or from something the program can name.
/// </summary>
/// <remarks>
/// <para>
/// A register that once held a class pointer keeps that stand-in type for the rest of the method, and where
/// the same register later carries a managed value across a block edge the copies say two things at once:
/// </para>
/// <code>
/// Move v157 @ X19_v4 (CF.CellData[]),           board @ X0 (CF.CellData[])
/// Move v158 @ X19_v5 (Il2CppClass&lt;CF.BoardLogic&gt;), v157 @ X19_v4 (CF.CellData[])
/// Move v157 @ X19_v4 (CF.CellData[]),           v158 @ X19_v5 (Il2CppClass&lt;CF.BoardLogic&gt;)
/// </code>
/// <para>
/// The generator has to write a conversion between the two, and what comes out is
/// <c>array = (CellData[])num17;</c> - which is not C#, so the statement is commented and every later one
/// that read the array goes with it. `BoardLogic::CheckAndMergeAll` lost its merge loop to exactly that.
/// </para>
/// <para>
/// A stand-in is not a type the program has, so where a copy puts a managed reference into one - or takes one
/// out of it - the managed side is what the register holds. The class pointer that named it belongs to the
/// initialisation guard, which is unreachable by the time this code runs.
/// </para>
/// <para>
/// <b>Not inside a generic body</b>, for the reason
/// <see cref="LocalVariables.SetTypeFromParameter"/> gives: there the method's own <c>MethodInfo*</c> is how
/// every runtime generic context entry is reached, so a local holding one is load-bearing however it is also
/// copied. Last of all, so that nothing which resolves against a stand-in has yet to run.
/// </para>
/// </remarks>
public static class StandInCopyType
{
    public static void Run(MethodAnalysisContext method)
    {
        if (method.ControlFlowGraph is not { } graph || method.GenericParameters.Any())
            return;

        //Only a local that never received a class pointer of its own. Overruling every stand-in a copy
        //touches was measured: it took the six bad casts out of `CheckAndMergeAll` and put **forty** commented
        //statements into the same file, because the reads that legitimately go through a class pointer stopped
        //resolving. A stand-in whose every definition is a copy was never a class pointer at all - the type
        //came from a register the guard used earlier - and that one is free to correct.
        var carriedOnly = new Dictionary<LocalVariable, bool>();

        foreach (var instruction in graph.Instructions)
        {
            if (instruction.Destination is not LocalVariable written)
                continue;

            var copy = instruction is { OpCode: OpCode.Move, Operands: [_, LocalVariable] };
            carriedOnly[written] = copy && carriedOnly.GetValueOrDefault(written, true);
        }

        for (var settling = true; settling;)
        {
            settling = false;

            foreach (var instruction in graph.Instructions)
            {
                if (instruction is not { OpCode: OpCode.Move, Operands: [LocalVariable into, LocalVariable from] })
                    continue;

                if (LocalVariables.IsRuntimeStandIn(into.Type) && Nameable(from.Type) && carriedOnly.GetValueOrDefault(into))
                {
                    into.Type = from.Type;
                    settling = true;
                }
                else if (LocalVariables.IsRuntimeStandIn(from.Type) && Nameable(into.Type) && carriedOnly.GetValueOrDefault(from))
                {
                    from.Type = into.Type;
                    settling = true;
                }
            }
        }
    }

    /// <summary>A reference type the program itself has - which a stand-in never is.</summary>
    private static bool Nameable(TypeAnalysisContext? type)
        => type is { IsValueType: false } and not (ByRefTypeAnalysisContext or PointerTypeAnalysisContext)
            && !LocalVariables.IsRuntimeStandIn(type);
}
