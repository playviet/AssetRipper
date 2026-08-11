using System.Collections.Generic;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Gives back the value a callee wrote through an out or ref parameter.
/// </summary>
/// <remarks>
/// <para>
/// A by-reference argument is a stack slot the caller hands the address of:
/// </para>
/// <code>
/// ADD  X1, X31, 0xC      ; the address of the slot
/// STRB W31, [X31 + 0xC]  ; the compiler clears it first
/// BL   Boolean.TryParse  ; the callee writes it
/// LDRB W8,  [X31 + 0xC]  ; and the caller reads it back
/// </code>
/// <para>
/// Nothing in that says the call wrote anything, so the zero flows across it and the read back folds to a
/// constant: <c>bool.TryParse(raw, out var b)</c> came out as <c>result = 0L != 0L</c>, always false. The
/// method still compiles, still names every call the binary names, and still branches - so none of the
/// scorers noticed. Only running it against the original did.
/// </para>
/// <para>
/// The slot and its address are the same variable, so the two names are folded into one; and the clearing
/// store is dropped, because with it gone the variable has no definition before the call and nothing can be
/// propagated across it. What the callee wrote is then read from the one local the address was taken of,
/// which is exactly the local <c>IlGenerator</c> passes by reference.
/// </para>
/// <para>
/// Runs before single assignment form is built, on unversioned registers - after it, the constant has
/// already been propagated into the read and the two are no longer telling apart.
/// </para>
/// </remarks>
public static class OutParameterWriteback
{
    public static void Run(MethodAnalysisContext method)
    {
        var graph = method.ControlFlowGraph;
        if (graph == null)
            return;

        var slots = SlotsWhoseAddressIsTaken(graph.Instructions);

        if (System.Environment.GetEnvironmentVariable("SLOT_TRACE") is not null)
            System.Console.Error.WriteLine($"SLOTS {method.Name}: taken=[{string.Join(",", slots)}] "
                + $"cleared=[{string.Join(",", System.Linq.Enumerable.Select(ZeroStores(graph),
                    i => i.Operands[0] is Register r ? r.Name : i.Operands[0].GetType().Name))}]");

        if (slots.Count == 0)
            return;

        //Worked out once, before the renaming below moves the destinations from one prefix to the other: the
        //instructions are the same objects either way, and only which of them clears a slot matters here.
        var clearing = ZeroStores(graph);

        //A struct is cleared a slot at a time, and only the one whose address was taken is found above - so
        //the rest keep the zero, and a field read after the call folds to it. `foreach (int x in xs)` came
        //out calling `keep(0)` and adding `0`, because `List<T>.Enumerator._current` lives two slots above
        //the address that was handed over.
        foreach (var alongside in ClearedAlongsideAnAddressTakenSlot(clearing, slots))
            slots.Add(alongside);

        foreach (var instruction in graph.Instructions)
        {
            //Except the one operand `OutPointerSlotResult` has just written: a libm out-pointer's answer sent
            //to the slot it names. See IsTheAnswer there for why the exception is that narrow.
            var answered = OutPointerSlotResult.IsTheAnswer(instruction) ? 1 : -1;

            for (var i = 0; i < instruction.Operands.Count; i++)
            {
                if (i == answered)
                    continue;

                if (instruction.Operands[i] is Register register
                    && register.Name.StartsWith(StackSlots.ValuePrefix)
                    && slots.Contains(Suffix(register.Name, StackSlots.ValuePrefix)))
                {
                    instruction.Operands[i] = new Register(null, StackSlots.AddressPrefix + Suffix(register.Name, StackSlots.ValuePrefix));
                }
            }
        }

        //Taking the address is not reading the slot, but it is written as one - `add x1, sp, #0xc` becomes
        //`Move x1, slot`. Where the value is put in the slot *after* the address is taken, which is what
        //boxing does, single assignment form then correctly says x1 holds what was there before the store,
        //and the store itself is dead:
        //
        //  ldr w8, [x19, #0x14]   ; the value
        //  add x1, sp, #0xc       ; Move x1, slot      <- reads the slot here
        //  str w8, [sp, #0xc]     ; Move slot, w8      <- so this is dead
        //  bl  il2cpp_codegen_box ; and the box gets nothing
        //
        //The register does not hold a copy of the slot, it *is* the slot, so the move is an alias rather than
        //a read. Taking it out and letting the uses name the slot puts them after the store, where they are.
        AliasAddressRegisters(graph);

        //Only the compiler's own clearing store goes, and it is a store of zero. Anything else put in the slot
        //is a value that is meant to be there - the caller filling in a `ref`, or the value about to be boxed,
        //which reaches the helper through the same kind of address. Dropping those as well took the store out
        //of `IEnumerator.Current`, so the box read a slot nothing had written and the property returned null.
        //A slot wider than a register is cleared from one - `movi v0.2d, #0` then `stp v0, v0, [sp, #0x10]`
        //clears thirty-two bytes - so the value stored is a **register** that holds zero rather than the
        //number nought, and the store looked like a value somebody meant to put there. Left standing it is
        //an assignment to the slot, which is the same variable as the address after the renaming above, so
        //everything that wanted the address read the cleared struct instead: `Corpus::Tally` handed
        //`MoveNext` a zero and threw. Which registers hold zero is carried along the block, like
        //ConstantFolding does, so nothing is assumed about how control reached it.
        foreach (var instruction in clearing)
        {
            if (instruction.Operands.Count > 0 && instruction.Operands[0] is Register destination
                && destination.Name.StartsWith(StackSlots.AddressPrefix))
            {
                instruction.OpCode = OpCode.Nop;
                instruction.Operands = [];
            }
        }
    }

    /// <summary>
    /// Replaces a register that was handed a slot's address with the slot itself, wherever it is read.
    /// </summary>
    /// <remarks>
    /// Done a block at a time and only as far as the register keeps that meaning: a register is reused, and
    /// the one that carried an address here may carry something else three instructions later.
    /// </remarks>
    private static void AliasAddressRegisters(Graphs.ISILControlFlowGraph graph)
    {
        foreach (var block in graph.Blocks)
        {
            for (var i = 0; i < block.Instructions.Count; i++)
            {
                var move = block.Instructions[i];

                if (move.OpCode != OpCode.Move || move.Operands.Count < 2
                    || move.Operands[0] is not Register held
                    || move.Operands[1] is not Register slot
                    || !slot.Name.StartsWith(StackSlots.AddressPrefix)
                    || held.Name.StartsWith(StackSlots.AddressPrefix))
                    continue;

                //Only where every use of it is an argument to a call. That is the shape this is about - the
                //address is handed over for the callee to write or read - and it is narrow on purpose: the
                //first version aliased every use, which fixed the boxing and cost fifty whole bodies
                //elsewhere, because an address that is used for anything else is not the slot.
                var uses = new List<(Instruction Instruction, int Operand)>();
                var onlyCalls = true;

                for (var j = i + 1; j < block.Instructions.Count && onlyCalls; j++)
                {
                    var later = block.Instructions[j];

                    for (var operand = 0; operand < later.Operands.Count; operand++)
                    {
                        if (later.Operands[operand] is not Register register || register.Number != held.Number)
                            continue;

                        //Written to rather than read: the register has stopped being the address, and
                        //everything after this point is about something else.
                        if (operand == 0 && later.IsAssignment)
                        {
                            j = block.Instructions.Count;
                            break;
                        }

                        if (later.OpCode is OpCode.Call or OpCode.CallVoid && operand > 1)
                            uses.Add((later, operand));
                        else
                            onlyCalls = false;
                    }
                }

                if (!onlyCalls || uses.Count == 0)
                    continue;

                foreach (var (instruction, operand) in uses)
                    instruction.Operands[operand] = slot;

                move.OpCode = OpCode.Nop;
                move.Operands = [];
            }
        }
    }

    private static bool IsZero(object operand, HashSet<string> zeroed) =>
        (operand is Register register && zeroed.Contains(register.Name)) || IsZero(operand);

    private static bool IsZero(object operand) => operand switch
    {
        int i => i == 0,
        uint u => u == 0,
        long l => l == 0,
        ulong ul => ul == 0,
        short s => s == 0,
        ushort us => us == 0,
        byte b => b == 0,
        sbyte sb => sb == 0,

        //A struct is cleared through a *vector* register - `movi v0.2d, #0` then `stp v0, v0, [sp, #0x50]` -
        //and the lane model writes that zero as a float, because that is what a lane of a vector register
        //holds. Reading only the integer widths meant the commonest clear in the whole binary was not one:
        //`BoardController::TryGetPressedPointerScreenPosition` clears 68 bytes of `Touch` that way, so the
        //slot kept the zero, and the slot is the same variable as its address - which is what `memcpy` and
        //`get_phase` were then handed. Both came out as `0f`.
        float f => f == 0,
        double d => d == 0,

        _ => false,
    };

    private static HashSet<string> SlotsWhoseAddressIsTaken(IEnumerable<Instruction> instructions)
    {
        var slots = new HashSet<string>();

        foreach (var instruction in instructions)
        {
            foreach (var operand in instruction.Operands)
            {
                if (operand is Register register && register.Name.StartsWith(StackSlots.AddressPrefix))
                    slots.Add(Suffix(register.Name, StackSlots.AddressPrefix));
            }
        }

        return slots;
    }

    /// <summary>
    /// The slots cleared in the same run as one whose address is taken - the rest of the struct.
    /// </summary>
    /// <remarks>
    /// The compiler zeroes a struct before it is filled, one slot at a time and in order, so the clearing
    /// stores form a run of adjacent offsets. Where any slot in such a run has had its address handed to a
    /// call, the whole run is that struct being cleared, and every slot in it is written by the callee rather
    /// than holding the zero afterwards. Adjacent means the next four or eight bytes: those are the widths a
    /// store uses, and anything further apart is a different variable.
    /// </remarks>
    /// <summary>
    /// Every store of a zero into a stack slot, whether the zero is the number or a register holding one.
    /// </summary>
    /// <remarks>
    /// A slot wider than a register is cleared from one - <c>movi v0.2d, #0</c> then
    /// <c>stp v0, v0, [sp, #0x10]</c> clears thirty-two bytes - so the value stored is a register, and a test
    /// that only knows the number nought sees none of it. Which registers hold zero is carried along the
    /// block, as <c>ConstantFolding</c> does, so nothing is assumed about how control reached it.
    /// </remarks>
    private static HashSet<Instruction> ZeroStores(Graphs.ISILControlFlowGraph graph)
    {
        var stores = new HashSet<Instruction>();

        foreach (var block in graph.Blocks)
        {
            var zeroed = new HashSet<string>();

            foreach (var instruction in block.Instructions)
            {
                if (instruction.OpCode == OpCode.Move && instruction.Operands.Count == 2
                    && instruction.Operands[0] is Register slot && OffsetOfSlotNamed(slot.Name) != null
                    && IsZero(instruction.Operands[1], zeroed))
                    stores.Add(instruction);

                if (instruction.Operands.Count > 0 && instruction.Operands[0] is Register written)
                {
                    if (instruction.OpCode == OpCode.Move && instruction.Operands.Count > 1
                        && IsZero(instruction.Operands[1], zeroed))
                        zeroed.Add(written.Name);
                    else
                        zeroed.Remove(written.Name);
                }
            }
        }

        return stores;
    }

    private static IEnumerable<string> ClearedAlongsideAnAddressTakenSlot(HashSet<Instruction> clearing, HashSet<string> taken)
    {
        var cleared = new SortedSet<int>();

        foreach (var instruction in clearing)
            if (instruction.Operands[0] is Register slot && OffsetOfSlotNamed(slot.Name) is { } offset)
                cleared.Add(offset);

        var takenOffsets = new HashSet<int>();
        foreach (var name in taken)
            if (OffsetOfSuffix(name) is { } offset)
                takenOffsets.Add(offset);

        var run = new List<int>();
        var found = new List<string>();

        void Flush()
        {
            if (run.Exists(takenOffsets.Contains))
                foreach (var offset in run)
                    found.Add(StackSlotAddress.Format(offset));

            run.Clear();
        }

        foreach (var offset in cleared)
        {
            //4 and 8 are a word and a doubleword; 16 is a vector, which is how a struct wider than that is
            //cleared - `stp v0, v0, [sp, #0x10]` writes two of them and leaves a gap of 0x10 between the two
            //slots it names. Without 16 the run breaks there and the second half keeps the zero, which is
            //where `Corpus::Tally` read its key.
            if (run.Count > 0 && offset - run[^1] is not (4 or 8 or 16))
                Flush();

            run.Add(offset);
        }

        Flush();

        return found;
    }

    private static int? OffsetOfSlotNamed(string name)
    {
        if (name.StartsWith(StackSlots.ValuePrefix))
            return OffsetOfSuffix(Suffix(name, StackSlots.ValuePrefix));

        if (name.StartsWith(StackSlots.AddressPrefix))
            return OffsetOfSuffix(Suffix(name, StackSlots.AddressPrefix));

        return null;
    }

    /// <summary>The number a slot's name spells, which is hexadecimal and may be negative.</summary>
    private static int? OffsetOfSuffix(string suffix)
    {
        var negative = suffix.StartsWith('-');
        var digits = negative ? suffix[1..] : suffix;

        if (!int.TryParse(digits, System.Globalization.NumberStyles.HexNumber, null, out var value))
            return null;

        return negative ? -value : value;
    }

    private static string Suffix(string name, string prefix) => name.Substring(prefix.Length);
}
