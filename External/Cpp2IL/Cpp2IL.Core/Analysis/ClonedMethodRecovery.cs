using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Model.Contexts;
using LibCpp2IL;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Names a method the compiler emitted twice, where the copy every call goes to is in no method table.
/// </summary>
/// <remarks>
/// <para>
/// <c>CFramework.Logger::Log</c> is registered at <c>0x23146B0</c>. Nothing branches there - the whole
/// binary contains exactly one reference to the address, in the method pointer table itself. What the
/// program calls, 146 times, is <c>0x22D6C2C</c>, which appears in no table at all, and it sits in a gap
/// between two registered methods. Every one of those 146 statements came out as
/// <c>_ = "Method not found @22D6C2C";</c> and took the statement around it with it.
/// </para>
/// <para>
/// The two are the same function. Their prologues differ by one saved register pair, so they are not
/// byte-identical and folding them by content would not find it - but they call the same nine things in
/// the same order, ending with the same tail branch, and no other method in the binary does:
/// </para>
/// <code>
/// 0x23146B0   bl 21844D8 x4, bl 3D85B50, bl 2184658, bl 47EA730, bl 3BCAD3C, bl 2184658, b 47A9CC0
/// 0x22D6C2C   bl 21844D8 x4, bl 3D85B50, bl 2184658, bl 47EA730, bl 3BCAD3C, bl 2184658, b 47A9CC0
/// </code>
/// <para>
/// So the rule is: <b>an address no table names, whose outgoing calls are exactly one registered method's,
/// is that method</b> - and only where that method has no callers of its own, which is the other half of
/// what makes this a clone rather than a coincidence. A registered body nothing calls and an unregistered
/// body everything calls are the same function seen from its two ends.
/// </para>
/// <para>
/// Nothing here disassembles. A <c>bl</c> is <c>100101</c> and a signed word offset, a <c>b</c> is
/// <c>000101</c> and the same, so the whole thing is one arithmetic pass over the managed code - which
/// matters, because it runs once for the application rather than once for a method.
/// </para>
/// <para>
/// The answer goes into <see cref="ApplicationAnalysisContext.MethodsByAddress"/>, so nothing downstream
/// needs to know this happened: the lifter names the call the ordinary way, and with it come the argument
/// registers the convention gives it - which for <c>Log(Object, string, Object, Color)</c> means the four
/// vector registers its <c>Color</c> travels in, and those are past recovering by the time the ISIL exists.
/// </para>
/// </remarks>
public static class ClonedMethodRecovery
{
    /// <summary>How many calls a body must make before its sequence of them can identify it.</summary>
    /// <remarks>
    /// Two accessors that both call one helper have the same one-call signature and are not the same
    /// method. Three is where a sequence stops being something a short body might have by chance.
    /// </remarks>
    private const int MinimumCalls = 3;

    /// <summary>As long a function as is worth reading, in instructions.</summary>
    private const int MaximumLength = 8192;

    /// <summary>How much longer one of two copies of a function may be than the other.</summary>
    /// <remarks>
    /// They are not the same bytes - one copy of <c>Log</c> saves an extra register pair - but they are the
    /// same code, so a difference of more than a quarter means it is something else that happens to call the
    /// same things.
    /// </remarks>
    private const double LengthTolerance = 0.25;

    private static readonly bool Trace = System.Environment.GetEnvironmentVariable("CLONE_TRACE") is not null;

    public static void Register(ApplicationAnalysisContext app)
    {
        if (app.Binary.is32Bit)
            return;

        var content = app.Binary.GetRawBinaryContent();

        var starts = app.MethodsByAddress.Keys.Where(address => address != 0).ToList();
        starts.Sort();

        if (starts.Count < 2)
            return;

        var lowest = starts[0];
        var highest = starts[^1];

        //One pass over the managed code: what each registered method calls, and how often each address is
        //called. Both answers come out of the same words, so the scan is done once.
        var called = new Dictionary<ulong, int>();
        var signatures = new Dictionary<ulong, string>();
        var lengths = new Dictionary<ulong, int>();

        for (var i = 0; i + 1 < starts.Count; i++)
        {
            if (Read(app, content, starts[i], starts[i + 1]) is not { } read)
                continue;

            foreach (var target in read.Targets)
                if (!called.ContainsKey(target))
                    called[target] = 1;
                else
                    called[target]++;

            signatures[starts[i]] = read.Signature;
            lengths[starts[i]] = read.Length;
        }

        //A signature only names a method where it names one method. Two bodies the compiler generated from
        //one template - an accessor pair, a switch of `throw`s - have the same one, and so does a clone,
        //which is exactly why the count has to be checked rather than the first match taken.
        var byCall = new Dictionary<string, ulong?>();

        foreach (var (start, signature) in signatures)
            byCall[signature] = byCall.ContainsKey(signature) ? null : start;

        //Every address something calls that no table names, in order, so each one's extent ends where the
        //next begins - two of them can sit in the same gap, and reading past the first into the second
        //would give the first the second's calls.
        var candidates = called.Keys
            .Where(address => address > lowest && address < highest && !app.MethodsByAddress.ContainsKey(address))
            .ToList();

        candidates.Sort();

        var boundaries = starts.Concat(candidates).ToList();
        boundaries.Sort();

        var named = 0;

        foreach (var candidate in candidates)
        {
            var after = boundaries.BinarySearch(candidate) + 1;

            if (after <= 0 || after >= boundaries.Count
                || Read(app, content, candidate, boundaries[after]) is not { } read
                || !byCall.TryGetValue(read.Signature, out var original) || original is not { } start
                || !app.MethodsByAddress.TryGetValue(start, out var methods) || methods.Count != 1)
                continue;

            //The other half of the fingerprint: the registered copy is the one nothing calls. Without this a
            //signature match is a similarity; with it, the two are one function seen from each end.
            if (called.GetValueOrDefault(start) != 0)
                continue;

            if (!lengths.TryGetValue(start, out var length)
                || System.Math.Abs(length - read.Length) > length * LengthTolerance)
                continue;

            if (Trace)
                System.Console.Error.WriteLine($"CLONE {candidate:X} = {methods[0].DeclaringType?.Name}::{methods[0].Name}"
                    + $" (registered {start:X}, {called.GetValueOrDefault(candidate)} callers, "
                    + $"{read.Length} vs {length} instructions, {read.Signature.Count(c => c == ',') + 1} calls)");

            app.MethodsByAddress[candidate] = [methods[0]];
            named++;
        }

        if (named > 0)
            Logging.Logger.VerboseNewline($"\tNamed {named} cloned method bodies no method table reaches.");
    }

    /// <summary>What one function calls, in order, and how long it is.</summary>
    /// <remarks>
    /// Only branches that leave the function count. One that stays inside it is the shape of a decision the
    /// function is making rather than something it uses, and where the two copies differ - a register saved
    /// in one and not the other - the offsets of those branches differ with them.
    /// </remarks>
    private static (string Signature, List<ulong> Targets, int Length)? Read(ApplicationAnalysisContext app,
        System.ReadOnlySpan<byte> content, ulong start, ulong end)
    {
        if (end <= start || end - start > MaximumLength * 4
            || !app.Binary.TryMapVirtualAddressToRaw(start, out var raw) || raw <= 0
            || raw + (long)(end - start) > content.Length)
            return null;

        var targets = new List<ulong>();
        var outside = new List<string>();

        for (var at = 0UL; at < end - start; at += 4)
        {
            var word = (uint)(content[(int)((ulong)raw + at)]
                | content[(int)((ulong)raw + at + 1)] << 8
                | content[(int)((ulong)raw + at + 2)] << 16
                | content[(int)((ulong)raw + at + 3)] << 24);

            //bl is 100101 and b is 000101, both followed by a signed twenty-six bit word offset.
            var linked = word >> 26 == 0b100101;

            if (!linked && word >> 26 != 0b000101)
                continue;

            var offset = (long)(word & 0x3FFFFFF);

            if (offset >= 1 << 25)
                offset -= 1 << 26;

            var target = (ulong)((long)(start + at) + offset * 4);

            if (linked)
                targets.Add(target);

            if (target < start || target >= end)
                outside.Add(target.ToString("X"));
        }

        return outside.Count < MinimumCalls
            ? null
            : (string.Join(",", outside), targets, (int)((end - start) / 4));
    }
}
