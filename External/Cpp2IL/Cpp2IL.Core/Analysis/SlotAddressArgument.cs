using System.Collections.Generic;
using System.Globalization;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// An address worked out from the frame pointer and handed to a call is the slot at that distance.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="SlotAddressRead"/> already joins <c>[stackaddr_0 - 0x10]</c> to the slot it names, because both
/// numbers are frame offsets and adding them settles it. The same address is just as often not read at all
/// but <em>passed</em> - which is what a <c>ref</c> or <c>out</c> argument is:
/// </para>
/// <code>
/// Subtract v62 @ X0_v4 (System.Int64), v19 @ stackaddr_0, 16
/// CallVoid Array.Resize, v62 @ X0_v4 (System.Int64), v65 (System.Int32)
/// </code>
/// <para>
/// The slot sixteen back is <c>stack_-10</c>, which holds the method's own <c>values</c> - so
/// <c>Array.Resize(ref values, values.Length + 1)</c> came out as
/// <c>T[] array = default(T[]); Array.Resize(ref array, newSize);</c>, resizing nothing and losing the array.
/// </para>
/// <para>
/// Naming the slot in the argument is all there is to it: taking a slot's address makes the address and the
/// slot one variable, so the argument <b>is</b> the slot - which is the same rule
/// <see cref="Cpp2IL.Core.InstructionSets.NewArmV8InstructionSet"/> follows for a libm out-pointer. Only at a
/// call, and only where the frame really has a variable at that distance: an address landing between two of
/// them is the middle of a struct and is left as it was.
/// </para>
/// </remarks>
public static class SlotAddressArgument
{
    public static void Run(MethodAnalysisContext method)
    {
        if (method.ControlFlowGraph is not { } graph)
            return;

        var slots = new Dictionary<string, LocalVariable>();

        foreach (var local in method.Locals)
            if (local.Register.Name is { } named && named.StartsWith(StackSlots.ValuePrefix))
                slots.TryAdd(named, local);

        if (slots.Count == 0)
            return;

        var definitions = new Dictionary<LocalVariable, Instruction>();
        var assigned = new HashSet<LocalVariable>();

        foreach (var instruction in graph.Instructions)
            if (instruction.Destination is LocalVariable destination && !assigned.Add(destination))
                definitions.Remove(destination);
            else if (instruction.Destination is LocalVariable once)
                definitions[once] = instruction;

        foreach (var instruction in graph.Instructions)
        {
            if (instruction.OpCode is not (OpCode.Call or OpCode.CallVoid or OpCode.IndirectCall))
                continue;

            //Operand zero is what is being called; a `Call` also answers into operand one.
            var first = instruction.OpCode == OpCode.Call ? 2 : 1;

            for (var i = first; i < instruction.Operands.Count; i++)
            {
                if (instruction.Operands[i] is not LocalVariable handed || !definitions.TryGetValue(handed, out var made))
                    continue;

                if (Distance(made) is not { } away || away.Base.Register.Name is not { } anchor
                    || !anchor.StartsWith(StackSlots.AddressPrefix)
                    || Offset(anchor[StackSlots.AddressPrefix.Length..]) is not { } start)
                {
                    continue;
                }

                if (slots.TryGetValue(StackSlots.ValuePrefix + Format(start + away.Offset), out var slot))
                    instruction.Operands[i] = slot;
            }
        }
    }

    /// <summary>How far from a frame anchor an address is, where that is how it was worked out.</summary>
    private static (LocalVariable Base, long Offset)? Distance(Instruction made) => made switch
    {
        { OpCode: OpCode.Add, Operands: [_, LocalVariable from, { } by] } when Constant(by) is { } forward
            => (from, forward),
        { OpCode: OpCode.Subtract, Operands: [_, LocalVariable from, { } by] } when Constant(by) is { } back
            => (from, -back),
        _ => null,
    };

    private static long? Constant(object operand)
        => operand switch
        {
            int i => i,
            uint u => u,
            long l => l,
            ulong ul => (long)ul,
            _ => null,
        };

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
