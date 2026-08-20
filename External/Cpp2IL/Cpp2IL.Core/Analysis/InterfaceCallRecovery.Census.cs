using System.Collections.Generic;
using System.Linq;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Why <see cref="InterfaceCallRecovery"/> refused an interface dispatch it recognised the shape of.
/// </summary>
/// <remarks>
/// <para>
/// The interface walk is <b>48% of every <c>Unmanaged memory</c> occurrence in Snacky Dash's gameplay
/// code</b> - 2869 of 5935, against sixteen on the game this fork was calibrated on. <c>IFACE_TRACE</c>
/// shows the walk fully intact where the pass runs, so the evidence is not being removed before it: the
/// pass sees the shape and declines it.
/// </para>
/// <para>
/// <c>Dispatch</c> has six <c>continue</c>s and one final <c>return null</c>, and until now it traced the
/// body but never the reason. Two and a half thousand occurrences were therefore attributed to "the
/// interface walk" as a whole, which is not something a fix can be aimed at. This names the step that
/// declined, so the next round is pointed at one of seven conditions rather than at a family.
/// </para>
/// <para>
/// Measurement only - it changes no decision and costs one static string read per refusal when unset.
/// Turn it on with <c>IFACE_CENSUS=1</c>; the totals go to stderr every 500 attempts and again at the end.
/// </para>
/// </remarks>
internal static class InterfaceCallCensus
{
    private static readonly Dictionary<string, int> Counts = new();

    internal static void Counted(string why)
    {
        if (System.Environment.GetEnvironmentVariable("IFACE_CENSUS") != "1")
            return;

        lock (Counts)
        {
            Counts[why] = Counts.GetValueOrDefault(why) + 1;

            if (Counts.TryGetValue("attempted", out var total) && total % 500 == 0)
                Dump();
        }
    }

    /// <summary>Every reason, most frequent first. Safe to call from anywhere; silent when unset.</summary>
    internal static void Dump()
    {
        if (System.Environment.GetEnvironmentVariable("IFACE_CENSUS") != "1")
            return;

        System.Console.Error.WriteLine("IFACE CENSUS " + string.Join("  ",
            Counts.OrderByDescending(k => k.Value).Select(k => $"{k.Key}={k.Value}")));
    }
}
