using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// What this fork adds: recognising a runtime-metadata guard by which of the runtime structures it reads
/// rather than by a fixed offset, which moves between il2cpp versions.
///
/// Kept apart from the file it belongs to so that the file stays as close to upstream as it can,
/// and a later version of Cpp2IL can be merged without the two sets of changes meeting.
/// </summary>
public static partial class MetadataInitGuardRemover
{
    /// <summary>
    /// Whether the block is one of the guards il2cpp opens a method with, by what it reads rather than by
    /// the shape of the reading.
    /// </summary>
    private static bool IsInitialisationGuard(Block guard, long initialisedFlagOffset, float metadataVersion, bool is32Bit)
        => guard.Instructions.Any(instruction => ReadsClassBookkeeping(instruction, metadataVersion, is32Bit))
            || TestsTheInitialisedFlag(guard, initialisedFlagOffset, metadataVersion, is32Bit);

    /// <summary>
    /// Whether the block tests the bit a class carries to say it has been initialised.
    /// </summary>
    /// <remarks>
    /// The test above reads the byte holding that bit as part of the masking, which is what a compiler does
    /// when it can reach the byte in one instruction. This build cannot: the flag sits far enough into
    /// Il2CppClass that its address is worked out first and then read from, so the read names no offset at
    /// all and the masking is of a value rather than of a field. Nothing about the guard is different - only
    /// where the offset is written - and matched on the shape alone, not one guard in this game is found.
    /// </remarks>
    private static bool TestsTheInitialisedFlag(Block guard, long initialisedFlagOffset, float metadataVersion, bool is32Bit)
    {
        foreach (var instruction in guard.Instructions)
        {
            //The flag is one bit of a byte, so whatever else it looks like, the test masks one against a read.
            if (instruction.OpCode != OpCode.And || instruction.Operands is not [_, { } masked, { } mask] || !IsOne(mask))
                continue;

            if (ReadThrough(masked, guard) is not { } read)
                continue;

            if (read.Offset == initialisedFlagOffset)
                return true;

            //A build that keeps the flag somewhere else is still reading a field of the class that holds no
            //data, and managed code has no way of reaching one of those.
            if (read.Held.Type is RuntimeClassTypeAnalysisContext && !CarriesData((uint)read.Offset, metadataVersion, is32Bit))
                return true;
        }

        return false;
    }

    /// <summary>
    /// What a value was read from and at what offset, following the address arithmetic in front of the read.
    /// Every step is a single assignment in the same block, so what is found is what the read reached.
    /// </summary>
    private static (LocalVariable Held, long Offset)? ReadThrough(object operand, Block block)
    {
        //A value that was loaded stands for the load, which is where the address it used can be seen.
        if (operand is LocalVariable loaded)
        {
            if (DefinitionIn(block, loaded) is not { OpCode: OpCode.Move, Operands: [_, MemoryOperand source] })
                return null;

            operand = source;
        }

        if (operand is not MemoryOperand { Index: null, Scale: 0, Base: LocalVariable held } memory)
            return null;

        var offset = memory.Addend;

        //An offset too large to ride along with the read is added on beforehand, possibly more than once.
        for (var steps = 0; steps < 4; steps++)
        {
            if (DefinitionIn(block, held) is not { OpCode: OpCode.Add, Operands: [_, LocalVariable behind, { } added] }
                || Constant(added) is not { } step)
                break;

            offset += step;
            held = behind;
        }

        return (held, offset);
    }

    /// <summary>The instruction a local is assigned by within the block, of which in SSA there is one.</summary>
    private static Instruction? DefinitionIn(Block block, LocalVariable local)
        => block.Instructions.FirstOrDefault(instruction => ReferenceEquals(instruction.Destination, local));

    private static long? Constant(object operand)
        => operand switch
        {
            int i => i,
            uint u => u,
            long l => l,
            ulong ul => (long)ul,
            _ => null,
        };

    /// <summary>
    /// Whether the instruction reads one of Il2CppClass's bookkeeping fields out of a runtime class
    /// pointer. Which byte holds the initialised flag moves between il2cpp versions - the constant
    /// above is 0x135, while a metadata 31 arm64 build tests 0xE4 - so a guard is recognised by what
    /// it reads rather than by a fixed offset. Managed code never reads the runtime class struct, so
    /// any read of it that is not one of the fields carrying real data is codegen, not program logic.
    /// </summary>
    private static bool ReadsClassBookkeeping(Instruction instruction, float metadataVersion, bool is32Bit)
    {
        foreach (var operand in instruction.Operands)
        {
            //A generic method's own context is filled in the first time the method runs, behind the same
            //shape of test - and managed code never reads the runtime method struct at all, so any read of
            //one is codegen rather than program logic, whichever field it names.
            if (operand is MemoryOperand { Index: null, Scale: 0, Base: LocalVariable { Type: RuntimeMethodInfoAnalysisContext } })
                return true;

            if (operand is not MemoryOperand { Index: null, Scale: 0, Base: LocalVariable { Type: RuntimeClassTypeAnalysisContext } } memory)
                continue;

            if (!CarriesData((uint)memory.Addend, metadataVersion, is32Bit))
                return true;
        }

        return false;
    }

    // The fields of Il2CppClass a method body can legitimately reach through: its statics, its rgctx
    // table, its element type, its interface offsets, and its vtable.
    private static bool CarriesData(uint offset, float metadataVersion, bool is32Bit) =>
        Il2CppClassUsefulOffsets.IsStaticFieldsPtr(offset, is32Bit)
        || Il2CppClassUsefulOffsets.IsRGCTXDataPtr(offset, is32Bit)
        || Il2CppClassUsefulOffsets.IsElementTypePtr(offset, is32Bit)
        || Il2CppClassUsefulOffsets.IsInterfaceOffsetsPtr(offset, is32Bit)
        || Il2CppClassUsefulOffsets.IsInterfaceOffsetsCount(offset, is32Bit)
        || Il2CppClassUsefulOffsets.IsPointerIntoVtable(offset, metadataVersion, is32Bit);
}
