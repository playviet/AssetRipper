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
                    callees.Add(instruction.BranchTarget);
                    break;

                default:
                    //Anything else that writes the register invalidates the page it was holding.
                    if (instruction.Op0Kind == Arm64OperandKind.Register)
                        pages.Remove(instruction.Op0Reg);
                    break;
            }
        }

        return Follow(appContext, callees, depth);
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
}
