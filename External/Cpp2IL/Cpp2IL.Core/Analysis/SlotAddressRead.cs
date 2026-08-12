using System.Collections.Generic;
using System.Globalization;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// A read at a distance from a slot's address is a read of the slot that distance away.
/// </summary>
/// <remarks>
/// <para>
/// The compiler takes the address of one place in the frame and reaches the rest from there, so a local
/// holding <c>&amp;stack[0]</c> is read at <c>-0x18</c> to get <c>stack[-0x18]</c>. Both numbers are frame
/// offsets and adding them names the slot exactly - there is nothing to infer. Left alone each one is a load
/// of unmanaged memory and a statement saying so: <b>119 of the 1021</b>, the largest shape the marker has
/// that the binary still answers.
/// </para>
/// <para>
/// Only where the frame has a variable there. <c>StackAnalyzer</c> names the slots something touched, and a
/// distance landing between two of them - the middle of a struct - is a question this cannot answer and
/// leaves as it was.
/// </para>
/// </remarks>
public static class SlotAddressRead
{
    public static bool Run(MethodAnalysisContext method)
    {
        if (method.ControlFlowGraph is not { } graph)
            return false;

        var slots = new Dictionary<string, LocalVariable>();

        foreach (var local in method.Locals)
        {
            if (local.Register.Name is { } named && named.StartsWith(StackSlots.ValuePrefix))
                slots.TryAdd(named, local);
        }

        //No early return on an empty set. `StackAnalyzer` only names a slot something touched **as a slot**,
        //and a method that anchors its locals on the frame pointer touches none of them that way: every
        //access is `[x29 - n]`, an address, so `slots` is empty and this used to give up before the fallback
        //below - which exists precisely to make the slot that is missing.
        //
        //It is the commonest shape there is. **468 of the 590** untyped bases that root at the frame are
        //`stackaddr_0` from `add x29, sp, #0`, concentrated in shared-generic bodies and async state
        //machines, where il2cpp emits an alloca for a `T`-sized buffer so the stack pointer moves and x29
        //has to anchor the locals. **381 of the 439 direct sites returned here**, and a hundred of them have
        //a type at the site and convert on the spot.
        //
        //And 183 of the stores among them are worse than a marker: the generator has nowhere to put a write
        //through an address it cannot name, so it `Pop`s the value and says nothing. `IListExtension::Swap`
        //spills its `j` parameter to `[x29 - 0x34]`, the store is dropped, and the reload comes back zero.

        var changed = false;

        foreach (var instruction in graph.Instructions)
        {
            for (var operand = 0; operand < instruction.Operands.Count; operand++)
            {
                if (instruction.Operands[operand] is not MemoryOperand { Index: null, Scale: 0, Base: LocalVariable address } read
                    || address.Register.Name is not { } name
                    || !name.StartsWith(StackSlots.AddressPrefix)
                    || Offset(name[StackSlots.AddressPrefix.Length..]) is not { } start)
                    continue;

                var wanted = StackSlots.ValuePrefix + Format(start + read.Addend);

                if (!slots.TryGetValue(wanted, out var slot))
                {
                    //`StackAnalyzer` names the slots something touched **as a slot**. Where the only thing
                    //touching this one is the address arithmetic, there is no variable to point at - and one
                    //can be made, because the generator registers any local it meets in the instructions.
                    //It needs a type, and what was read out of the slot is what says it: a load with no type
                    //for its destination would give an `object` local that a number cannot be stored into,
                    //which is a worse statement than the marker.
                    //A write is different from a read. Reading a slot into a destination that has no type
                    //would give an `object` local a number cannot be stored into, which is a worse statement
                    //than the marker - so a read still needs an answer. **Writing** one does not: the slot
                    //holds whatever is put into it, and where that is itself untyped the slot is untyped too
                    //and the two are declared alike, which is a statement that compiles and is true. Without
                    //this the store had nowhere to go and the generator dropped it in silence; 55 of them.
                    var writing = operand == 0 && instruction is { OpCode: OpCode.Move, Operands: [_, LocalVariable] };
                    var held = Held(instruction, operand);

                    if (held is null && !writing)
                        continue;

                    slot = new LocalVariable(wanted, new Register(null, wanted), held);
                    slots[wanted] = slot;
                    method.Locals.Add(slot);
                }

                instruction.Operands[operand] = slot;
                changed = true;
            }
        }

        return changed;
    }

    /// <summary>
    /// What the slot holds, taken from the place the value is going to or coming from.
    /// </summary>
    private static TypeAnalysisContext? Held(Instruction instruction, int operand)
        => instruction.OpCode == OpCode.Move && instruction.Operands.Count == 2
            ? (operand == 1 ? instruction.Operands[0] : instruction.Operands[1]) switch
            {
                LocalVariable { Type: { } type } => type,
                FieldReference field => field.Field.FieldType,
                _ => null,
            }
            : null;

    /// <summary>A frame offset as the slot names write it: hexadecimal, with a leading minus.</summary>
    private static long? Offset(string written)
    {
        var negative = written.StartsWith('-');
        var digits = negative ? written[1..] : written;

        return long.TryParse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value)
            ? negative ? -value : value
            : null;
    }

    private static string Format(long offset)
        => offset < 0 ? "-" + (-offset).ToString("X") : offset.ToString("X");
}
