using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// What the placeholder for a call to an address nothing names should say.
/// </summary>
/// <remarks>
/// <para>
/// A call whose target resolves to no method is written out as <c>_ = "Method not found @83D8280";</c>, and
/// that address tells a reader nothing at all. Where the target is an imported C function the ELF names it
/// outright - <c>ElfFile.ImportedFunctionAt</c> follows the <c>.plt</c> stub to its jump slot and the slot's
/// relocation to the symbol, which is what the loader itself does - so the placeholder can say which function
/// it was, and the name goes in brackets after the address.
/// </para>
/// <para>
/// <b>This changes what the marker says and nothing else.</b> The instruction, its operands and every
/// statement recovered around it are untouched; only the string carried by a statement that recovered
/// <i>nothing</i> is different. It is a diagnostic change, made because <c>Method not found @83D8280</c>
/// cannot be told from <c>Method not found @83D8240</c> without a disassembler, and because most of what is
/// left in that marker is one family.
/// </para>
/// <para>
/// That family is <c>memcpy</c>, <c>memmove</c> and <c>memset</c> - <b>183 sites, 11% of the marker</b>, and
/// the same family on both binaries (34697, 3121 and 9593 call sites in the Snacky Dash image against 22132,
/// 2221 and 3169 in Fluffy Field's). They are a struct assignment rather than a call and want the types on
/// both sides, so they are deliberately not mapped - <c>StructAssignmentCopy</c>, <c>ClearedStruct</c> and
/// <c>ClearingASizedByT</c> take the ones they can, and what reaches here is a copy those passes declined
/// rather than a method that could not be found. Now the site says so.
/// </para>
/// <para>
/// <b>The <c>Method not found @</c> prefix is kept</b>, for every marker and not only for these.
/// <c>InvalidSourceRepair.TracePrefixes</c> matches on it to turn the trace from a <c>Console.WriteLine</c>
/// into a discard, and a marker that does not match it stays a live call: a recovered loop over a struct copy
/// would then write a line per iteration. The name is added after the address for that reason and no other.
/// </para>
/// <para>
/// Nothing here is keyed on an address - the name comes from the dynamic symbol table, so it is as correct on
/// one binary as on another.
/// </para>
/// </remarks>
public static class UnresolvedCallMarker
{
    public static string Describe(MethodAnalysisContext method, ulong address)
        => method.AppContext.Binary is LibCpp2IL.Elf.ElfFile binary
            && binary.ImportedFunctionAt(address) is { } imported
            ? $"Method not found @{address:X} ({imported})"
            : $"Method not found @{address:X}";
}
