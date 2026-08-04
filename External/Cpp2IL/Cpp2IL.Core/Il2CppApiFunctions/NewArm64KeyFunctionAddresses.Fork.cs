using System.Collections.Generic;
using System.Linq;
using Disarm;
using Disarm.InternalDisassembly;
using Cpp2IL.Core.Logging;
using Cpp2IL.Core.Utils;

namespace Cpp2IL.Core.Il2CppApiFunctions;

/// <summary>
/// What this fork adds: recognising the padding between arm64 functions, filling in the key function
/// addresses that name matching does not reach, and finding the write barrier by the store it guards.
///
/// Kept apart from the file it belongs to so that the file stays as close to upstream as it can,
/// and a later version of Cpp2IL can be merged without the two sets of changes meeting.
/// </summary>
public partial class NewArm64KeyFunctionAddresses
{
    // Corlib methods which assign a reference to a field and so are followed by a write barrier. Several,
    // because any one of them can be stripped, and because agreement between them rules out a false match.
    private static readonly (string Namespace, string Type, string Method)[] WriteBarrierAnchors =
    [
        ("System.Threading.Tasks", "Task`1", "GetAwaiter"),
        ("System.Threading.Tasks", "Task", "GetAwaiter"),
        ("System.Threading", "ExecutionContext", "get_LogicalCallContext"),
        ("System.Threading", "CancellationTokenSource", "get_Token"),
        ("System", "BadImageFormatException", "get_Message"),
    ];

    /// <summary>
    /// Whether an instruction is one of the all zero words a compiler pads between functions with.
    /// </summary>
    private bool IsPadding(Arm64Instruction instruction)
    {
        var binary = _appContext.Binary;
        var raw = binary.MapVirtualAddressToRaw(instruction.Address);

        if (raw <= 0 || raw + 4 > binary.RawLength)
            return false;

        for (var offset = 0ul; offset < 4; offset++)
        {
            if (binary.GetByteAtRawAddress((ulong)raw + offset) != 0)
                return false;
        }

        return true;
    }

    /// <summary>
    /// Finds the runtime function every method calls to initialize its metadata, which nothing else looks for on
    /// arm64.
    /// </summary>
    /// <remarks>
    /// The search on x86 disassembles one known corlib method and takes the first call in it, which cannot work here
    /// because it is written against a different disassembler. This instead uses the one property the function has
    /// that no other has: il2cpp emits a guarded call to it at the top of nearly every method body, so among the
    /// branches near the start of a method that do not land on a managed method, it is overwhelmingly the most common.
    /// <para>
    /// The other half of what the x86 search fills in, il2cpp_object_new, is already found from the exports.
    /// </para>
    /// </remarks>
    protected override void AttemptInstructionAnalysisToFillGaps()
    {
        if (il2cpp_codegen_initialize_method != 0 || il2cpp_codegen_initialize_runtime_metadata != 0)
            return;

        const int methodsToSample = 2000;
        const int instructionsFromStart = 12;

        Logger.Verbose("\tLooking for the metadata initialization function among the calls methods start with...");

        var counts = new Dictionary<ulong, int>();
        var sampled = 0;

        foreach (var method in _appContext.AllTypes.SelectMany(type => type.Methods))
        {
            if (method.UnderlyingPointer == 0)
                continue;

            if (sampled++ >= methodsToSample)
                break;

            List<Arm64Instruction> body;
            try
            {
                body = NewArm64Utils.GetArm64MethodBodyAtVirtualAddress(_appContext.Binary, method.UnderlyingPointer).ToList();
            }
            catch
            {
                continue;
            }

            foreach (var instruction in body.Take(instructionsFromStart))
            {
                if (instruction.Mnemonic is not Arm64Mnemonic.BL)
                    continue;

                //A branch to a managed method is the method doing its own work, not the runtime preamble.
                if (_appContext.MethodsByAddress.ContainsKey(instruction.BranchTarget))
                    continue;

                counts[instruction.BranchTarget] = counts.GetValueOrDefault(instruction.BranchTarget) + 1;
            }
        }

        if (counts.Count == 0)
        {
            Logger.VerboseNewline("No candidates found, aborting.");
            return;
        }

        var best = counts.OrderByDescending(pair => pair.Value).First();

        //A function only a handful of methods call is not the one every method calls.
        if (best.Value < sampled / 10)
        {
            Logger.VerboseNewline($"Best candidate 0x{best.Key:X} only appears {best.Value} times in {sampled} methods, which is too few. Aborting.");
            return;
        }

        if (_appContext.MetadataVersion < 27)
            il2cpp_codegen_initialize_method = best.Key;
        else
            il2cpp_codegen_initialize_runtime_metadata = best.Key;

        Logger.VerboseNewline($"Found at 0x{best.Key:X}, called by {best.Value} of the first {sampled} methods.");
    }

    /// <summary>
    /// Finds Il2CppCodeGenWriteBarrier, which il2cpp calls after every store of a reference into a heap object.
    /// </summary>
    /// <remarks>
    /// Recovery turns the call into a nop and drops the arguments it computed, so without this every such store is
    /// followed by a call to an unnamed function in the recovered body. This is the arm64 counterpart of the x86
    /// search: look through corlib methods that are known to store a reference into a field for a call preceded by
    /// both the store itself and the address of the slot being handed over as the first argument.
    /// </remarks>
    protected override ulong GetWriteBarrier()
    {
        Logger.Verbose("\tLooking for Il2CppCodeGenWriteBarrier via corlib reference-field stores...");

        var votes = new Dictionary<ulong, int>();

        foreach (var (@namespace, typeName, methodName) in WriteBarrierAnchors)
        {
            var method = ReflectionCache.GetType(typeName, @namespace)?.Methods?.FirstOrDefault(m => m.Name == methodName);

            if (method == null || method.MethodPointer == 0)
                continue;

            List<Arm64Instruction> body;
            try
            {
                body = NewArm64Utils.GetArm64MethodBodyAtVirtualAddress(_appContext.Binary, method.MethodPointer).ToList();
            }
            catch
            {
                continue;
            }

            foreach (var target in FindWriteBarrierCalls(body).Distinct())
                votes[target] = votes.GetValueOrDefault(target) + 1;
        }

        var best = 0ul;
        var bestVotes = 0;

        foreach (var vote in votes)
        {
            if (vote.Value <= bestVotes)
                continue;

            best = vote.Key;
            bestVotes = vote.Value;
        }

        if (best == 0)
        {
            Logger.VerboseNewline("Not found. Write barriers disabled?");
            return 0;
        }

        Logger.VerboseNewline($"Found at 0x{best:X} (found in {bestVotes} of {WriteBarrierAnchors.Length} checked methods)");

        return best;
    }

    /// <summary>
    /// The target of every branch in <paramref name="body"/> that is preceded both by a store of a 64 bit register
    /// into a slot and by that slot's address being put into x0, which under aapcs64 is the first argument. That is
    /// Il2CppCodeGenWriteBarrier(&amp;slot, value) sitting directly after the store it guards.
    /// </summary>
    private static IEnumerable<ulong> FindWriteBarrierCalls(List<Arm64Instruction> body)
    {
        const int window = 6;

        for (var index = 0; index < body.Count; index++)
        {
            if (body[index].Mnemonic is not Arm64Mnemonic.BL)
                continue;

            if (IsPrecededByGuardedStore(body, index, window))
                yield return body[index].BranchTarget;
        }
    }

    private static bool IsPrecededByGuardedStore(List<Arm64Instruction> body, int callIndex, int window)
    {
        for (var storeIndex = callIndex - 1; storeIndex >= 0 && callIndex - storeIndex <= window; storeIndex--)
        {
            var store = body[storeIndex];

            //A reference is pointer sized, so a store of a w register is a value and not what a barrier guards.
            if (store.Mnemonic is not (Arm64Mnemonic.STR or Arm64Mnemonic.STUR)
                || store.Op1Kind != Arm64OperandKind.Memory
                || store.Op0Reg is < Arm64Register.X0 or > Arm64Register.X31)
                continue;

            for (var addressIndex = callIndex - 1; addressIndex >= 0 && callIndex - addressIndex <= window; addressIndex--)
            {
                var takeAddress = body[addressIndex];

                if (takeAddress.Op0Kind != Arm64OperandKind.Register || takeAddress.Op0Reg != Arm64Register.X0)
                    continue;

                //The slot is either at an offset from the object, or is the object itself when the field is first.
                var matchesOffsetForm = takeAddress.Mnemonic == Arm64Mnemonic.ADD
                    && takeAddress.Op1Reg == store.MemBase
                    && takeAddress.Op2Kind == Arm64OperandKind.Immediate
                    && takeAddress.Op2Imm == store.MemOffset;

                var matchesDirectForm = takeAddress.Mnemonic == Arm64Mnemonic.MOV
                    && store.MemOffset == 0
                    && takeAddress.Op1Reg == store.MemBase;

                if (matchesOffsetForm || matchesDirectForm)
                    return true;
            }
        }

        return false;
    }
}
