using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// The struct of floats a call had to copy to the stack, taken from the stores that put it there.
/// </summary>
/// <remarks>
/// <para>
/// Aapcs64 has eight vector registers for arguments and an aggregate that does not fit in what is left of them
/// is copied to the stack <b>whole</b>. The lifter's walk keeps counting past v7 regardless, so
/// <c>Debug.DrawRay(Vector3, Vector3, Color)</c> - ten floats - has its colour named v6..v9, which hold two of
/// the caller's own parameters and two callee-saved registers. <see cref="HomogeneousFloatArguments"/> refuses
/// to build a struct out of those, quite rightly, and the argument is written out as whatever single float
/// happened to be in v6: sixty sites in this game, <c>GizmosDrawer</c> alone holding a third of them.
/// </para>
/// <para>
/// The value is not lost, though - it is in the caller's own frame, put there by a store a few instructions
/// above the branch. <b>Here and nowhere later</b>: nothing in the caller reads those slots, so the first run
/// of <c>DeadCodeEliminator</c> removes the stores before <see cref="MetadataResolver"/> has even named the
/// callee. This hook is the only one upstream of it, and a direct call's callee is already resolvable here
/// because the lifter looked it up to lay the operands out in the first place.
/// </para>
/// <para>
/// What the pass does is replace the registers the walk named with the registers the stores read from, so the
/// value reaches the assembly at the end of the pipeline as an ordinary chain that single assignment form and
/// dead code elimination both understand. It runs before either, which is what makes that work: a register
/// substituted here is versioned with everything else, and its definition is kept alive by the use.
/// </para>
/// <para>
/// <b>Three earlier attempts named the slot instead of following it</b> (1.1.22, 1.1.41, 1.1.42) and each
/// measured worse in the same way - an outgoing <c>stack_0</c> is the same name as the caller's own first
/// incoming argument, so sixty stand-ins became confident wrong values. Nothing here names a slot: the slot is
/// only ever used to find which register was stored into it.
/// </para>
/// </remarks>
public static class StackedFloatArgument
{
    /// <summary>How many single-predecessor blocks back a store is looked for.</summary>
    private const int Depth = 2;

    /// <summary>
    /// Diagnostics, read once: <c>STACKARG_TRACE=1</c> names every argument taken from the stores, and
    /// <c>=2</c> also names the ones it could not take and shows the slots it had to work from.
    /// </summary>
    private static readonly string? Trace = System.Environment.GetEnvironmentVariable("STACKARG_TRACE");

    /// <summary>
    /// Which parameters of which calls had their registers taken from the stores, so that the passes at the
    /// end of the pipeline can tell an argument whose operands are now right from one whose operands are the
    /// run of registers the lifter's walk invented past v7.
    /// </summary>
    /// <remarks>
    /// Recorded rather than recognised. Reading it off the operands - "these are no longer v(start)..." - was
    /// tried first and is wrong: half a dozen passes between here and there replace an operand for reasons of
    /// their own, and every one of them read as a correction that had not happened.
    /// <c>MathUtils::DistanceBetweenPointAndLine</c> was assembled out of three registers holding nothing of
    /// the sort, and one of its lanes came out as a member of a different argument.
    /// </remarks>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Instruction, HashSet<int>>
        Corrected = new();

    /// <summary>Whether this argument's operands are the ones the stores named rather than the walk's.</summary>
    public static bool WasCorrected(Instruction instruction, int parameter)
        => Corrected.TryGetValue(instruction, out var parameters) && parameters.Contains(parameter);

    public static void Run(MethodAnalysisContext method)
    {
        if (method.ControlFlowGraph is not { } graph)
            return;

        foreach (var block in graph.Blocks)
        {
            for (var at = 0; at < block.Instructions.Count; at++)
            {
                var instruction = block.Instructions[at];

                if (instruction.OpCode is not (OpCode.Call or OpCode.CallVoid)
                    || instruction.Operands.Count == 0 || instruction.Operands[0] is not ulong target)
                {
                    continue;
                }

                //One method at the address, so that a generic definition sharing it with an instantiation
                //cannot make this the wrong signature.
                if (!method.AppContext.MethodsByAddress.TryGetValue(target, out var called) || called.Count != 1)
                    continue;

                Substitute(method, block, at, instruction, called[0]);
            }
        }
    }

    private static void Substitute(MethodAnalysisContext caller, Block block, int at, Instruction instruction,
        MethodAnalysisContext callee)
    {
        if (ParametersOnTheStack.Placement(callee) is not { Count: > 0 } placed)
            return;

        //Which of the stacked parameters are structs of floats, and where each of their fields sits in the
        //outgoing argument area.
        var wanted = new Dictionary<int, (long At, int Floats)>();

        foreach (var (index, offset) in placed)
        {
            var type = callee.Parameters[index].ParameterType;

            if (type.Namespace != nameof(System) && HomogeneousFloatStruct.Count(type) is { } floats and > 1)
                wanted[index] = (offset, floats);
        }

        if (wanted.Count == 0)
            return;

        var stored = Stores(block, at);
        var outgoingBase = OutgoingBase(stored, wanted);

        if (Trace == "2")
        {
            System.Console.Error.WriteLine(
                $"STACKARG? {caller.DeclaringType?.Name}::{caller.Name} -> {callee.DeclaringType?.Name}::{callee.Name} wants "
                + string.Join(", ", wanted.Select(w => $"p{w.Key}@{w.Value.At:X}x{w.Value.Floats}"))
                + " stores " + string.Join(", ", stored.Select(s => $"{s.Key:X}<-{s.Value}"))
                + " base " + (outgoingBase?.ToString("X") ?? "none"));
        }

        if (stored.Count == 0 || outgoingBase is not { } outgoing)
            return;

        //The same walk the lifter made when it laid the operands out - see GetArgumentOperandsForCall.
        var first = instruction.OpCode == OpCode.Call
            ? (callee.IsStatic ? 2 : 3)
            : (callee.IsStatic ? 1 : 2);

        var beyond = first + callee.Parameters.Count;

        for (var i = 0; i < callee.Parameters.Count; i++)
        {
            var type = callee.Parameters[i].ParameterType;
            var handed = beyond;

            if (type.Namespace != nameof(System) && HomogeneousFloatStruct.Count(type) is { } occupies)
                beyond += occupies - 1;

            if (!wanted.TryGetValue(i, out var argument) || first + i >= instruction.Operands.Count)
                continue;

            //Every field, or none: half a struct assembled out of two places is the confident wrong answer
            //this exists to remove.
            var fields = new List<object>();

            for (var field = 0; field < argument.Floats; field++)
            {
                if (!stored.TryGetValue(outgoing + argument.At + field * 4, out var source))
                    break;

                fields.Add(source);
            }

            if (fields.Count != argument.Floats)
                continue;

            instruction.Operands[first + i] = fields[0];

            for (var field = 1; field < argument.Floats && handed + field - 1 < instruction.Operands.Count; field++)
                instruction.Operands[handed + field - 1] = fields[field];

            Corrected.GetOrCreateValue(instruction).Add(i);

            if (Trace is not null)
            {
                System.Console.Error.WriteLine(
                    $"STACKARG {caller.DeclaringType?.Name}::{caller.Name} -> {callee.DeclaringType?.Name}::{callee.Name}"
                    + $" param {i} @{outgoing:X}+{argument.At:X} <- {string.Join(", ", fields)}");
            }
        }
    }

    /// <summary>
    /// The register last stored into each stack slot before this call, nearest store winning.
    /// </summary>
    /// <remarks>
    /// Nearest rather than "since the last call", because a compiler that has already put the right value in
    /// an outgoing slot for one call does not store it again for the next - and the outgoing area is the same
    /// area for every call in the method.
    /// </remarks>
    private static Dictionary<long, object> Stores(Block block, int at)
    {
        var stored = new Dictionary<long, object>();

        //Every slot already written between here and the call, whatever was written into it. A nearer store
        //of something that is not a float has to shadow a further one that is, or the walk reaches past the
        //value actually being passed and takes an older one.
        var shadowed = new HashSet<long>();

        for (var depth = 0; depth < Depth; depth++)
        {
            for (var i = at - 1; i >= 0; i--)
            {
                var instruction = block.Instructions[i];

                if (instruction.OpCode != OpCode.Move || instruction.Operands.Count != 2
                    || instruction.Operands[0] is not Register slot
                    || OffsetOfSlot(slot.Name) is not { } offset || !shadowed.Add(offset))
                {
                    continue;
                }

                //A vector register, which is the only thing a field of a struct of floats travels in.
                if (instruction.Operands[1] is Register { Name: { Length: > 1 } name } source
                    && name[0] is 'V' or 'S' or 'D')
                {
                    stored[offset] = source;
                }
            }

            if (block.Predecessors.Count != 1)
                break;

            block = block.Predecessors[0];
            at = block.Instructions.Count;
        }

        return stored;
    }

    /// <summary>
    /// Where the outgoing argument area begins, in the slot numbering the stack analysis settled on.
    /// </summary>
    /// <remarks>
    /// It cannot simply be read off: the slot names are relative to the frame, the argument area is relative
    /// to the stack pointer, and the distance between the two is whatever the prologue subtracted - which a
    /// tail call, whose outgoing area <i>is</i> the incoming one, does not subtract at all. So it is derived
    /// from the stores themselves: the base that lines the most fields up with the slots actually written is
    /// the one the compiler used, and a base that lines up none of them means the evidence is not here.
    /// </remarks>
    private static long? OutgoingBase(Dictionary<long, object> stored, Dictionary<int, (long At, int Floats)> wanted)
    {
        long? best = null;
        var matched = 0;

        foreach (var slot in stored.Keys)
        {
            foreach (var (_, argument) in wanted)
            {
                var candidate = slot - argument.At;

                //The outgoing argument area is below the frame, so its slots are the negative ones. A
                //non-negative base is this method's own incoming argument area, which is only the outgoing
                //area for a tail call - and the three sites in this game where that is what the search found
                //assemble a struct out of registers nothing defines, so it is not evidence of anything.
                if (candidate >= 0)
                    continue;

                var lined = 0;

                foreach (var (_, other) in wanted)
                    for (var field = 0; field < other.Floats; field++)
                        if (stored.ContainsKey(candidate + other.At + field * 4))
                            lined++;

                if (lined <= matched)
                    continue;

                matched = lined;
                best = candidate;
            }
        }

        return best;
    }

    /// <summary>The offset a slot's name spells, which is hexadecimal and may be negative.</summary>
    private static long? OffsetOfSlot(string name)
    {
        if (!name.StartsWith(StackSlots.ValuePrefix))
            return null;

        var suffix = name[StackSlots.ValuePrefix.Length..];
        var negative = suffix.StartsWith('-');

        return long.TryParse(negative ? suffix[1..] : suffix,
            System.Globalization.NumberStyles.HexNumber, null, out var offset)
            ? negative ? -offset : offset
            : null;
    }
}
