using System;
using System.Collections.Generic;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// A ternary between two fields is a choice of offsets, not a branch.
/// </summary>
/// <remarks>
/// <para>
/// <c>starImage.sprite = show ? highLightStarSprite : hideStarSprite</c> has no branch left in it: clang
/// selects between the two fields' <b>offsets</b> and then does one load at <c>this + &lt;register&gt;</c>.
/// </para>
/// <code>
/// CheckNotEqual v17, show, 0
/// Select        v18, v17, 40, 48
/// Move          v21, [this + v18]
/// </code>
/// <para>
/// Nothing can name a field whose offset is only known at run time, so both arms disappeared and the
/// assignment came out as <c>default(Sprite)</c> - a wrong value carrying nothing but an <c>unmanaged</c>
/// marker to say so. It is not an unrecovered read; it is a recovered read of the wrong thing.
/// </para>
/// <para>
/// Both constants are in the select, so the load is done once per arm at a constant offset and the choice
/// moves on to the values. Resolution then names each of them as the field it is, exactly as it would have
/// had the source been written with an <c>if</c>. This is <see cref="FieldAddressSinking"/>'s question one
/// level down: there the addresses were chosen between, here the offsets were.
/// </para>
/// <para>
/// Reading a field of an object has no side effect, so doing both and choosing afterwards computes the same
/// answer - the same argument that lets the sinking above move a load. What it may not do is read where no
/// field is, so both offsets have to name one on the type the base is known to hold, and a write through the
/// chosen address is refused outright: that would be a store to two fields rather than one.
/// </para>
/// </remarks>
public static class SelectedFieldOffset
{
    public static void Run(MethodAnalysisContext method)
    {
        if (method.ControlFlowGraph is not { } graph)
            return;

        var chosen = new Dictionary<LocalVariable, (object Condition, long IfTrue, long IfFalse)>();

        foreach (var instruction in graph.Instructions)
        {
            if (instruction is { OpCode: OpCode.Select, Operands: [LocalVariable picked, { } condition, { } whenTrue, { } whenFalse] }
                && Constant(whenTrue) is { } ifTrue
                && Constant(whenFalse) is { } ifFalse
                && ifTrue != ifFalse
                && ifTrue >= 0 && ifFalse >= 0)
            {
                chosen[picked] = (condition, ifTrue, ifFalse);
            }
        }

        if (chosen.Count == 0)
            return;

        foreach (var block in graph.Blocks)
        {
            for (var at = 0; at < block.Instructions.Count; at++)
            {
                var instruction = block.Instructions[at];

                //Only a plain load. A write through the chosen address would be a store to two fields at
                //once, which no rewriting of it can mean.
                if (instruction is not { OpCode: OpCode.Move, Operands: [LocalVariable read, MemoryOperand memory] })
                    continue;

                if (memory is not { Base: LocalVariable { Type: { } holder } through, Index: LocalVariable index, Addend: 0 }
                    || memory.Scale > 1
                    || !chosen.TryGetValue(index, out var choice))
                {
                    continue;
                }

                if (MemberAt(holder, choice.IfTrue) is not { } whenTrue || MemberAt(holder, choice.IfFalse) is not { } whenFalse)
                    continue;

                //The two arms must agree on what they hold, or the choice has no type to be written as.
                if (whenTrue.FullName != whenFalse.FullName)
                    continue;

                //Left as plain reads at a constant distance, for the resolution that follows to name - which
                //is what reaches `minMaxIconScale.y`, a member *inside* a field, that no single field
                //reference could have been built for here.
                var first = new LocalVariable($"arm{method.Locals.Count}", new Register(null, "ARM"), whenTrue);
                method.Locals.Add(first);

                var second = new LocalVariable($"arm{method.Locals.Count}", new Register(null, "ARM"), whenFalse);
                method.Locals.Add(second);

                block.Instructions.Insert(at, new Instruction(instruction.Index, OpCode.Move, first,
                    new MemoryOperand(through, addend: choice.IfTrue)));

                block.Instructions.Insert(at + 1, new Instruction(instruction.Index, OpCode.Move, second,
                    new MemoryOperand(through, addend: choice.IfFalse)));

                instruction.OpCode = OpCode.Select;
                instruction.Operands = [read, choice.Condition, first, second];

                at += 2;
            }
        }
    }

    /// <summary>
    /// What lies at that distance into the type: a field of its own, or one step into a value-type field.
    /// </summary>
    /// <remarks>
    /// One step is enough and is where it stops. A <c>Vector2</c> field holds the pair of offsets that
    /// <c>on ? minMaxIconScale.y : minMaxIconScale.x</c> chooses between, and going deeper would start
    /// naming members of members on offsets that agree by accident.
    /// </remarks>
    private static TypeAnalysisContext? MemberAt(TypeAnalysisContext owner, long offset, int depth = 0)
    {
        if (owner.IsEnumType || (owner.IsValueType && owner.Namespace == nameof(System)))
            return null;

        foreach (var field in owner.Fields)
        {
            if (field.IsStatic || field.BackingData?.FieldOffset is not { } at)
                continue;

            if (at == offset)
                return field.FieldType;

            if (depth == 0 && field.FieldType is { IsValueType: true } held && offset > at
                && MetadataResolver.LaidOutSize(held, 8) is { } size && offset < at + size)
            {
                return MemberAt(held, offset - at, depth + 1);
            }
        }

        return null;
    }

    private static long? Constant(object operand)
    {
        try
        {
            return operand is string or LocalVariable or Register ? null : Convert.ToInt64(operand);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
