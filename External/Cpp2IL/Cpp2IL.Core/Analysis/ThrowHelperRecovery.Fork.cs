using System;
using System.Collections.Generic;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Api;
using Cpp2IL.Core.Utils;
using Disarm;
using Disarm.InternalDisassembly;
using Iced.Intel;
using LibCpp2IL;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// What this fork adds: reading the name of the exception a throw helper raises out of the helper itself,
/// on both architectures, and following the call chain that leads to it.
///
/// Kept apart from the file it belongs to so that the file stays as close to upstream as it can,
/// and a later version of Cpp2IL can be merged without the two sets of changes meeting.
/// </summary>
public static partial class ThrowHelperRecovery
{
    private static string? ResolveNameX86(ApplicationAnalysisContext appContext, ulong address, int depth)
    {
        InstructionList body;

        try
        {
            body = X86Utils.GetMethodBodyAtVirtAddressNew(address, true, appContext.Binary);
        }
        catch
        {
            return null;
        }

        var name = FindExceptionName(appContext, body);

        if (name != null)
            return name;

        foreach (var instruction in body)
        {
            if (instruction.Mnemonic != Mnemonic.Call || instruction.Op0Kind != OpKind.NearBranch64)
                continue;

            name = ResolveName(appContext, instruction.NearBranchTarget, depth + 1);

            if (name != null)
                return name;
        }

        return null;
    }

    /// <summary>
    /// The same search on arm64. A string constant is not reachable in one instruction there: the page
    /// is loaded with adrp and the offset within it added afterwards, so the pair has to be followed to
    /// get the address the x86 side reads straight off a single lea. Every helper here is also reached
    /// through a one-instruction branch veneer, so an unconditional branch out of the function is
    /// followed as if it were a call.
    /// </summary>
    private static string? ResolveNameArm64(ApplicationAnalysisContext appContext, ulong address, int depth)
    {
        List<Arm64Instruction> body;

        try
        {
            body = NewArm64Utils.GetArm64MethodBodyAtVirtualAddress(appContext.Binary, address, false, MaxInstructions);
        }
        catch
        {
            return null;
        }

        var pages = new Dictionary<Arm64Register, ulong>();
        var callees = new List<ulong>();
        var returned = false;

        foreach (var instruction in body)
        {
            switch (instruction.Mnemonic)
            {
                case Arm64Mnemonic.ADRP:
                    pages[instruction.Op0Reg] = (ulong)((long)(instruction.Address & ~0xFFFUL) + instruction.Op1Imm);
                    break;

                case Arm64Mnemonic.ADD when instruction.Op2Kind == Arm64OperandKind.Immediate
                    && pages.TryGetValue(instruction.Op1Reg, out var page):
                    //Only a name reached before the function returns can be this helper's own. There is no
                    //way to know where the function ends without disassembling it, so the read runs on past
                    //that point to collect the branches out - but a name found there would belong to
                    //whatever the next function throws, which is how a helper ended up named after the
                    //exception of its neighbour.
                    if (!returned && ReadCStringAtVirtualAddress(appContext, page + (ulong)instruction.Op2Imm) is { } text
                        && text.EndsWith("Exception", StringComparison.Ordinal))
                        return text;

                    pages.Remove(instruction.Op0Reg);
                    break;

                case Arm64Mnemonic.RET:
                    returned = true;
                    break;

                case Arm64Mnemonic.BL:
                case Arm64Mnemonic.B:
                    //Only branches taken before the function returns, for the same reason a name is: past that
                    //point the read is into whatever function comes next, and its callees are not this one's.
                    if (!returned)
                        callees.Add(instruction.BranchTarget);

                    break;

                default:
                    //Anything else that writes the register invalidates the page it was holding.
                    if (instruction.Op0Kind == Arm64OperandKind.Register)
                        pages.Remove(instruction.Op0Reg);
                    break;
            }
        }

        //A helper that comes back is not a throw helper. Nothing else in this search says so: the name is
        //looked for in the body and then, failing that, in everything the body calls, five deep - and five
        //hops below `IsInst`, the check il2cpp makes before storing a reference into an array, something
        //raises `OutOfMemoryException`. All 17995 of its call sites were rewritten into an unconditional
        //throw, which ends the block, so the store the check guards and everything after it became
        //unreachable. `ColorExtension.ToHex` and the whole of `InterstitialGate` threw on entry.
        //
        //A real throw helper does not return - it ends in a raise - so where a name was not found in the body
        //itself and the body returns, the search stops rather than borrowing a name from something it called.
        //A veneer is unaffected: it is one branch and no return, so it is still followed to what it stands for.
        return returned ? null : Follow(appContext, callees, depth);
    }

    private static string? Follow(ApplicationAnalysisContext appContext, List<ulong> callees, int depth)
    {
        foreach (var callee in callees)
        {
            if (ResolveName(appContext, callee, depth + 1) is { } name)
                return name;
        }

        return null;
    }

    /// <summary>The addresses this thread is part-way through, which is how a cycle ends.</summary>
    /// <remarks>
    /// Per thread, because methods are analysed in parallel: one thread being part-way through an address says
    /// nothing to another. The <i>answer</i> is shared; being busy is not.
    /// </remarks>
    [ThreadStatic]
    private static HashSet<ulong>? resolving;

    /// <summary>Whether this search gave up anywhere - past the depth it will go, or back on itself.</summary>
    [ThreadStatic]
    private static bool gaveUp;

    /// <summary>Whether a search of this address may go ahead, and marks it as under way if so.</summary>
    private static bool BeginResolving(ulong address, int depth)
    {
        if (depth == 0)
            gaveUp = false;

        if (depth >= MaxDepth)
        {
            gaveUp = true;
            return false;
        }

        if ((resolving ??= []).Add(address))
            return true;

        gaveUp = true;
        return false;
    }

    private static void EndResolving(ulong address) => resolving?.Remove(address);

    /// <summary>
    /// Writes down what an address turned out to be, where that is actually known.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The answer is shared across the whole application, and it used to depend on <b>who asked first</b>. A
    /// <c>null</c> went into the cache before recursing, to stop a cycle - and nothing told that placeholder,
    /// or a search that stopped at <see cref="MaxDepth"/>, apart from a settled "this is not a throw helper".
    /// So whichever call site reached an address first, with whatever depth it had left, fixed the answer for
    /// every other one.
    /// </para>
    /// <para>
    /// It showed as two hosts disagreeing about the same binary. Analysing one method, an address resolved to
    /// nothing; analysing the whole assembly it resolved to <c>OutOfMemoryException</c> - nine names found
    /// against twenty-five. The calls that did not resolve stayed calls to an address, and a later pass, seeing
    /// an unresolved call carrying a type, read them as casts: five <c>isinst</c> that were really
    /// <c>throw</c>.
    /// </para>
    /// <para>
    /// A name is a name however it was reached. A nothing is only worth keeping when the search that produced
    /// it ran to the end - otherwise it is this address's turn to be remembered wrongly.
    /// </para>
    /// </remarks>
    private static void Remember(ApplicationAnalysisContext appContext, ulong address, string? name)
    {
        if (name != null || !gaveUp)
            appContext.ThrowHelperNamesByAddress[address] = name;
    }
}
