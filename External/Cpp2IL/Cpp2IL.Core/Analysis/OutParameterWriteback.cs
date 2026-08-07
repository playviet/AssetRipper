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
        if (slots.Count == 0)
            return;

        //A struct is cleared a slot at a time, and only the one whose address was taken is found above - so
        //the rest keep the zero, and a field read after the call folds to it. `foreach (int x in xs)` came
        //out calling `keep(0)` and adding `0`, because `List<T>.Enumerator._current` lives two slots above
        //the address that was handed over.
        foreach (var alongside in ClearedAlongsideAnAddressTakenSlot(graph, slots))
            slots.Add(alongside);

        foreach (var instruction in graph.Instructions)
        {
            for (var i = 0; i < instruction.Operands.Count; i++)
            {
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
        foreach (var instruction in graph.Instructions)
        {
            if (instruction.OpCode == OpCode.Move
                && instruction.Operands[0] is Register destination
                && destination.Name.StartsWith(StackSlots.AddressPrefix)
                && IsZero(instruction.Operands[1]))
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
    private static IEnumerable<string> ClearedAlongsideAnAddressTakenSlot(Graphs.ISILControlFlowGraph graph, HashSet<string> taken)
    {
        var cleared = new SortedSet<int>();

        foreach (var instruction in graph.Instructions)
        {
            if (instruction.OpCode == OpCode.Move && instruction.Operands.Count == 2
                && instruction.Operands[0] is Register slot && IsZero(instruction.Operands[1])
                && OffsetOfSlotNamed(slot.Name) is { } offset)
                cleared.Add(offset);
        }

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
            if (run.Count > 0 && offset - run[^1] is not (4 or 8))
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
