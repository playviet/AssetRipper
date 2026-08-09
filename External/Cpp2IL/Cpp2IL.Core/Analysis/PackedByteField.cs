using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// A byte masked out of a field that overlaps it is that byte's own field.
/// </summary>
/// <remarks>
/// <para>
/// <c>Color32</c> declares five fields at four bytes: <c>rgba</c> covering all of it, and <c>r</c>,
/// <c>g</c>, <c>b</c>, <c>a</c> one byte each. A read at offset nought matches whichever the metadata lists
/// first, which is <c>rgba</c> - so <c>c.r</c> came back as <c>c.rgba &amp; 0xFF</c>, an <c>int</c> where a
/// <c>byte</c> belongs, and the statement around it would not compile.
/// </para>
/// <para>
/// The mask says which byte: <c>&amp; 0xFF</c> is the one at the field's own offset, and a right shift of
/// eight, sixteen or twenty-four moves along by one, two or three. Both numbers are in the instructions and
/// the overlapping field is in the metadata, so nothing here is inferred - and the answer is only taken when
/// a one-byte field really is declared at that distance.
/// </para>
/// </remarks>
public static class PackedByteField
{
    public static void Run(MethodAnalysisContext method)
    {
        if (method.ControlFlowGraph is not { } graph)
            return;

        var definitions = new Dictionary<LocalVariable, Instruction>();

        foreach (var instruction in graph.Instructions)
            if (instruction.Destination is LocalVariable destination)
                definitions[destination] = instruction;

        foreach (var instruction in graph.Instructions)
        {
            //The topmost byte needs no mask: shifting a four-byte field right by twenty-four leaves it alone
            //in the low eight bits, so `c.a` is a bare `rgba >> 24` with nothing else to say it is a byte.
            if (instruction is { OpCode: OpCode.ShiftRight, Operands: [{ } high, FieldReference top, { } by] }
                && Places(by) == 24
                && top.Field.DeclaringType is { } topOwner
                && ByteAt(topOwner, top.Offset + 3) is { } topByte
                && Width(top.Field) == 4)
            {
                instruction.OpCode = OpCode.Move;
                instruction.Operands = [high, new FieldReference(topByte, top.Local, top.Offset + 3)];
                continue;
            }

            if (instruction is not { OpCode: OpCode.And, Operands: [{ } result, { } masked, { } mask] }
                || !IsByteMask(mask))
            {
                continue;
            }

            //Either the read itself, or a read shifted along to bring another byte into the low eight bits.
            var shift = 0;
            Instruction? shifting = null;

            if (masked is LocalVariable shifted
                && definitions.GetValueOrDefault(shifted) is { OpCode: OpCode.ShiftRight, Operands: [_, { } from, { } places] } moving
                && Places(places) is { } bits and (8 or 16 or 24))
            {
                shift = bits / 8;
                masked = from;
                shifting = moving;
            }

            if (masked is not FieldReference { Field: { } packed, Local: { } holder } read
                || packed.DeclaringType is not { } owner
                || ByteAt(owner, read.Offset + shift) is not { } member)
            {
                continue;
            }

            instruction.OpCode = OpCode.Move;
            instruction.Operands = [result, new FieldReference(member, holder, read.Offset + shift)];

            //The shift that brought the byte down is now the only thing still reading the packed field, and
            //nothing reads *it* - but it survives as a statement of its own, `_ = (uint)c.rgba >> 8`, which
            //is the one thing left between three of the 96 originals and whole. Recovering an access orphans
            //the arithmetic that reached it, exactly as recovering an indexed one orphans its length read.
            if (shifting is { Operands: [LocalVariable carried, ..] } && !ReadElsewhere(graph, carried, instruction))
            {
                shifting.OpCode = OpCode.Nop;
                shifting.Operands = [];
            }
        }
    }

    /// <summary>Whether anything other than the instruction just rewritten still reads this value.</summary>
    private static bool ReadElsewhere(Graphs.ISILControlFlowGraph graph, LocalVariable value, Instruction rewritten)
    {
        foreach (var instruction in graph.Instructions)
        {
            if (ReferenceEquals(instruction, rewritten) || instruction.OpCode == OpCode.Nop)
                continue;

            //Operand zero is where a result goes; everything after it is read.
            for (var i = 1; i < instruction.Operands.Count; i++)
            {
                if (ReferenceEquals(instruction.Operands[i], value))
                    return true;
            }
        }

        return false;
    }

    /// <summary>How many bytes a field holds, for the primitives this needs to know about.</summary>
    private static int? Width(FieldAnalysisContext field) => field.FieldType.FullName switch
    {
        "System.Int32" or "System.UInt32" or "System.Single" => 4,
        "System.Int16" or "System.UInt16" or "System.Char" => 2,
        "System.Byte" or "System.SByte" or "System.Boolean" => 1,
        "System.Int64" or "System.UInt64" or "System.Double" => 8,
        _ => null,
    };

    /// <summary>The one-byte field declared at that distance into the type, if one is.</summary>
    private static FieldAnalysisContext? ByteAt(TypeAnalysisContext owner, int offset)
        => owner.Fields.FirstOrDefault(f => !f.IsStatic
            && f.FieldType.FullName is "System.Byte" or "System.SByte"
            && f.BackingData?.FieldOffset == offset);

    private static bool IsByteMask(object operand)
    {
        try
        {
            return operand is not string && System.Convert.ToInt64(operand) == 0xFF;
        }
        catch (System.Exception)
        {
            return false;
        }
    }

    private static int? Places(object operand)
    {
        try
        {
            return operand is string ? null : (int)System.Convert.ToInt64(operand);
        }
        catch (System.Exception)
        {
            return null;
        }
    }
}
