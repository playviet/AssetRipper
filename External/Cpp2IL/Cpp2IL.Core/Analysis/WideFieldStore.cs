using System.Linq;
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
/// <para>
/// The same holds one and two bytes wide, which is where most of them are: adjacent flags and small enums are
/// what a struct packs. <c>SegmentRuleEvaluator.RuleValue</c> declares <c>bool _bool</c> and
/// <c>VKind _kind</c> next to each other and <c>RuleValue.Number</c> sets both with <c>strh 0x0100</c>, so
/// <c>_kind</c> - the field that says what kind of value it is - was never assigned and every number, string
/// and raw value the rule evaluator built read back as <c>VKind.Null</c>. Narrower pairs need one condition
/// more than the four-byte one: nothing above the two fields, since eight bytes could hold four one-byte
/// fields, and a negative number reaches this sign-extended across the whole register.
/// </para>
/// </remarks>
public static class WideFieldStore
{
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

        var half = WidthOf(stored.Field.FieldType);

        //Only where the field is half of what was written. A field the full width of the store is one field,
        //and there is nothing to take apart.
        if (half is not (1 or 2 or 4) || value >> (half * 8) == 0)
            return null;

        //And nothing above the two of them. A wide store cannot carry more than eight bytes, so for a pair of
        //four this is already true; for the narrower pairs it is what keeps a store covering four one-byte
        //fields - or a negative number sign-extended into the register - from being read as two.
        if (half * 2 < 8 && value >> (half * 16) != 0)
            return null;

        if (Neighbour(stored.Field, half) is not { } above)
            return null;

        instruction.Operands[1] = Narrow(value, half);

        return new Instruction(instruction.Index, OpCode.Move,
            new FieldReference(above, stored.Local, stored.Offset + half),
            Narrow(value >> (half * 8), half));
    }

    /// <summary>What actually reached a field this wide.</summary>
    private static int Narrow(ulong value, int width) => width switch
    {
        1 => (int)(value & 0xFF),
        2 => (int)(value & 0xFFFF),
        _ => unchecked((int)(uint)value),
    };

    /// <summary>The field that begins where this one ends, if the type has one of the same width.</summary>
    private static FieldAnalysisContext? Neighbour(FieldAnalysisContext field, int half)
    {
        foreach (var candidate in field.DeclaringType.Fields)
        {
            if (!candidate.IsStatic && candidate.Offset == field.Offset + half && WidthOf(candidate.FieldType) == half)
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
    /// <para>
    /// Anything else - a reference, a struct, a type whose width is not fixed here - returns zero and is left
    /// alone, which is the right answer for a field that is not half of a pair.
    /// </para>
    /// <para>
    /// An enum has no width of its own: it is as wide as the thing it is stored as, and the instance field
    /// every enum declaration carries is what says which. <c>SegmentRuleEvaluator.RuleValue</c> keeps a
    /// <c>bool</c> and a <c>VKind : byte</c> side by side and the compiler sets both with one sixteen-bit
    /// store, so without the byte widths - and without the enum behind one of them - <c>_kind</c> was never
    /// written at all and every rule value came back as <c>Null</c>.
    /// </para>
    /// </remarks>
    private static int WidthOf(TypeAnalysisContext type)
    {
        if (type.IsEnumType)
        {
            var underlying = type.Fields.FirstOrDefault(f => !f.IsStatic)?.FieldType;

            return underlying is null || underlying.IsEnumType ? 0 : WidthOf(underlying);
        }

        return type.Type switch
        {
            Il2CppTypeEnum.IL2CPP_TYPE_I4 or Il2CppTypeEnum.IL2CPP_TYPE_U4 or Il2CppTypeEnum.IL2CPP_TYPE_R4 => 4,
            Il2CppTypeEnum.IL2CPP_TYPE_I2 or Il2CppTypeEnum.IL2CPP_TYPE_U2 or Il2CppTypeEnum.IL2CPP_TYPE_CHAR => 2,
            Il2CppTypeEnum.IL2CPP_TYPE_I1 or Il2CppTypeEnum.IL2CPP_TYPE_U1 or Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN => 1,
            _ => 0,
        };
    }
}
