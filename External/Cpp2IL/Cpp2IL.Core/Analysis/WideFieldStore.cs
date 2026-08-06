using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using LibCpp2IL.BinaryStructures;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Takes apart a store that wrote two fields at once.
/// </summary>
/// <remarks>
/// <para>
/// Adjacent fields are initialised together. <c>CFramework.Spinner</c> sets an <c>int</c> and the
/// <c>float</c> four bytes after it, and the compiler does both in one instruction:
/// </para>
/// <code>
/// movz x8, #0xC             ; 12
/// movk x8, #0xCCCD          ; and 0.8f in the half above it
/// movk x8, #0x3F4C0000
/// str  x8, [x0, #0x20]      ; eight bytes, two fields
/// </code>
/// <para>
/// Field recovery resolves the offset to the first of the two and stores the whole sixty-four bit value into
/// it, so a four byte <c>int</c> was assigned <c>1061997773</c> - which is the <i>other</i> field's <c>0.8f</c>
/// read as an integer - and the field that was really being set does not appear at all. It compiles, and the
/// decompiler says only <c>Expected I4, but got I8</c>, so nothing about it looks like a failure.
/// </para>
/// <para>
/// The store's width is not carried this far, but it does not need to be: <b>the constant is the evidence.</b>
/// A four byte store cannot carry bits above the thirty-second, and a compiler does not build a half it is not
/// going to write. Requiring a field to exist at the next offset as well makes the reading the only consistent
/// one - and where any of that does not hold, the store is left exactly as it was.
/// </para>
/// </remarks>
public static class WideFieldStore
{
    private const int Half = 4;

    public static void Run(MethodAnalysisContext method)
    {
        if (method.ControlFlowGraph is not { } graph)
            return;

        foreach (var block in graph.Blocks)
        {
            for (var i = 0; i < block.Instructions.Count; i++)
            {
                if (Split(block.Instructions[i], method) is not { } second)
                    continue;

                block.Instructions.Insert(i + 1, second);
                i++;
            }
        }
    }

    /// <summary>
    /// The store of the second field, where this instruction turns out to have been writing both - and the
    /// first is narrowed to what actually reached it.
    /// </summary>
    private static Instruction? Split(Instruction instruction, MethodAnalysisContext method)
    {
        if (instruction.OpCode != OpCode.Move || instruction.Operands.Count < 2
            || instruction.Operands[0] is not FieldReference stored
            || Bits(instruction.Operands[1]) is not { } value)
            return null;

        //Only where the field is half of what was written. A field the full width of the store is one field,
        //and there is nothing to take apart.
        if (WidthOf(stored.Field.FieldType) != Half || value >> (Half * 8) == 0)
            return null;

        if (Neighbour(stored.Field) is not { } above)
            return null;

        instruction.Operands[1] = unchecked((int)(uint)value);

        return new Instruction(instruction.Index, OpCode.Move,
            new FieldReference(above, stored.Local, stored.Offset + Half),
            unchecked((int)(uint)(value >> (Half * 8))));
    }

    /// <summary>The field that begins where this one ends, if the type has one.</summary>
    private static FieldAnalysisContext? Neighbour(FieldAnalysisContext field)
    {
        foreach (var candidate in field.DeclaringType.Fields)
        {
            if (!candidate.IsStatic && candidate.Offset == field.Offset + Half && WidthOf(candidate.FieldType) == Half)
                return candidate;
        }

        return null;
    }

    private static ulong? Bits(object operand) => operand switch
    {
        ulong v => v,
        long v => unchecked((ulong)v),
        uint v => v,
        int v => unchecked((uint)v),
        _ => null,
    };

    /// <summary>
    /// How many bytes a field of this type occupies, for the widths a single store can cover more than one of.
    /// </summary>
    /// <remarks>
    /// Anything else - a reference, a struct, an enum whose underlying type is not known here - returns zero
    /// and is left alone, which is the right answer for a field that is not half of a pair.
    /// </remarks>
    private static int WidthOf(TypeAnalysisContext type) => type.Type switch
    {
        Il2CppTypeEnum.IL2CPP_TYPE_I4 or Il2CppTypeEnum.IL2CPP_TYPE_U4 or Il2CppTypeEnum.IL2CPP_TYPE_R4 => 4,
        _ => 0,
    };
}
