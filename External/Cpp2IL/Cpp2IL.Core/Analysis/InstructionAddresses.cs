using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Which machine instruction each ISIL instruction came from.
/// </summary>
/// <remarks>
/// <para>
/// The lifter builds this map and throws it away: <c>GetIsilFromMethod</c> keeps a local list of addresses
/// only long enough to turn a branch's target address into a target instruction, and then returns without it.
/// <see cref="ISIL.Instruction.Index"/> is the ISIL ordinal, not an address - one machine instruction becomes
/// several ISIL ones, so the two do not even count at the same rate.
/// </para>
/// <para>
/// Nothing in the recovery needs the map. <see cref="ExceptionTable"/> does, because it answers in machine
/// addresses - that is what the compiler wrote down - and without this there is no way to say which block a
/// landing pad is. The two together are an <b>instrument</b>, not a pass: they are what
/// <c>probe2 padcheck</c> uses to show that of the 22 catch pads the table names for
/// <c>CFramework.SaveIO::Load</c>, 22 are in the raw lift and 2 survive the analysis. Attaching the other 20
/// so they would survive was built, measured and reverted - see <c>ROUND-LOG.md</c> round 6.
/// </para>
/// <para>
/// Only instructions the lifter itself made have an address. A later pass that synthesises one gives it
/// index <c>-1</c> (a phi copy, a split return), and those answer zero - correctly, because they came from
/// no machine instruction at all.
/// </para>
/// </remarks>
public static class InstructionAddresses
{
    private static readonly ConditionalWeakTable<MethodAnalysisContext, ulong[]> Recorded = new();

    /// <summary>
    /// Called once by the lifter, with the two lists it built side by side.
    /// </summary>
    public static void Record(MethodAnalysisContext method, List<Instruction> instructions, List<ulong> addresses)
    {
        if (instructions.Count != addresses.Count)
            return;

        Recorded.AddOrUpdate(method, addresses.ToArray());
    }

    /// <summary>The address the instruction was lifted from, or zero where it was not lifted from one.</summary>
    public static ulong Of(MethodAnalysisContext method, Instruction instruction)
    {
        if (instruction.Index < 0 || !Recorded.TryGetValue(method, out var addresses) || instruction.Index >= addresses.Length)
            return 0;

        return addresses[instruction.Index];
    }

    /// <summary>The first address any instruction in the block was lifted from.</summary>
    public static ulong Of(MethodAnalysisContext method, Graphs.Block block)
    {
        foreach (var instruction in block.Instructions)
        {
            if (Of(method, instruction) is var address and not 0)
                return address;
        }

        return 0;
    }
}
