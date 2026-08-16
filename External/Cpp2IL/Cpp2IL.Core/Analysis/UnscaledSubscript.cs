using System.Collections.Generic;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// An index the addressing mode does not scale is a byte offset, not a subscript.
/// </summary>
/// <remarks>
/// <para>
/// An element access normally reaches the addressing mode already scaled - <c>[array + 0x20 + i*4]</c> - and
/// the recovery reads <c>i</c> straight off it. A loop that counts an index <b>down</b> does not: it keeps
/// the index in the high half of a register and lets one shift do the scaling as well.
/// </para>
/// <code>
/// mov x9,  #-4294967296        ; -1 &lt;&lt; 32
/// add x10, x9, x8, lsl #32     ; (len - 1) &lt;&lt; 32
/// add x12, x19, x10, asr #30   ; values + (index * 4)     <- asr 30, not 32
/// add x10, x10, x9             ; index--
/// </code>
/// <para>
/// <c>asr #30</c> is <c>32 - log2(stride)</c>, so what lands in the addressing mode is a <b>byte offset</b>
/// with no scale on it - and reading that as a subscript is four times too far.
/// <c>Corpus::Reversed</c> threw <c>IndexOutOfRangeException</c> on every input.
/// </para>
/// <para>
/// Written by moving the scaling out of the shift rather than by dividing: <c>x &gt;&gt; 30</c> used as a byte
/// offset with stride 4 is <c>x &gt;&gt; 32</c> used as a subscript with scale 4, exactly, and it costs no
/// instruction. That is also what keeps this narrow - it applies only where the index's <b>one</b> definition
/// is a right shift and the index has <b>one</b> use, so nothing that merely looks like a byte offset is
/// touched. A division would have to be emitted for the general case, and the general case is where widening
/// this path cost twelve game methods before (<c>il2cpp-a-slot-inside-a-struct-is-its-field</c>).
/// </para>
/// <para>Set <c>UNSCALED_OFF=1</c> to measure the same build without it.</para>
/// </remarks>
public static class UnscaledSubscript
{
    private static readonly bool Off = System.Environment.GetEnvironmentVariable("UNSCALED_OFF") == "1";

    public static void Run(MethodAnalysisContext method)
    {
        if (Off || method.ControlFlowGraph is not { } graph)
            return;

        var pointerSize = method.AppContext.Binary.is32Bit ? 4 : 8;

        //Where an array's elements begin, the same number ArrayAccessRecovery uses.
        var elements = method.AppContext.Binary.is32Bit ? 0x10 : 0x20;

        var definitions = new Dictionary<LocalVariable, Instruction>();
        var uses = new Dictionary<LocalVariable, int>();

        foreach (var instruction in graph.Instructions)
        {
            if (instruction.Destination is LocalVariable written)
                definitions[written] = definitions.ContainsKey(written) ? null! : instruction;

            for (var operand = instruction.Destination is null ? 0 : 1; operand < instruction.Operands.Count; operand++)
                Count(instruction.Operands[operand], uses);
        }

        foreach (var instruction in graph.Instructions)
        {
            for (var operand = 0; operand < instruction.Operands.Count; operand++)
            {
                //Scale nought and scale one both mean "not scaled"; the lifter writes whichever the
                //addressing mode had.
                if (instruction.Operands[operand] is not MemoryOperand { Index: LocalVariable index } access
                    || access.Scale > 1
                    || access.Addend != elements
                    || access.Base is not LocalVariable { Type: { } held }
                    || Stride(held, pointerSize) is not { } stride || stride < 2
                    || Log2(stride) is not { } bits)
                {
                    continue;
                }

                //One definition, one use, and that definition a right shift. Anything else and the value is
                //something the method also computes with, which this is not entitled to rewrite.
                if (uses.GetValueOrDefault(index) != 1
                    || !definitions.TryGetValue(index, out var shift) || shift is null
                    || shift is not { OpCode: OpCode.ShiftRight, Operands: [_, _, long by] }
                    || by + bits >= 64)
                {
                    continue;
                }

                shift.Operands[2] = by + bits;
                instruction.Operands[operand] = new MemoryOperand(access.Base, index, access.Addend, stride);
            }
        }
    }

    private static void Count(object operand, Dictionary<LocalVariable, int> uses)
    {
        switch (operand)
        {
            case LocalVariable local:
                uses[local] = uses.GetValueOrDefault(local) + 1;
                break;

            case MemoryOperand memory:
                if (memory.Base is LocalVariable had)
                    uses[had] = uses.GetValueOrDefault(had) + 1;
                if (memory.Index is LocalVariable indexed)
                    uses[indexed] = uses.GetValueOrDefault(indexed) + 1;
                break;

            case FieldReference field:
                uses[field.Local] = uses.GetValueOrDefault(field.Local) + 1;
                break;
        }
    }

    /// <summary>How far apart the elements of this array are, where it is one and the width is known.</summary>
    private static int? Stride(TypeAnalysisContext held, int pointerSize)
    {
        var element = held switch
        {
            SzArrayTypeAnalysisContext szArray => szArray.ElementType,
            ArrayTypeAnalysisContext { Rank: 1 } single => single.ElementType,
            _ => null,
        };

        return element is null ? null : (int?)ArrayTypeInference.Width(element, pointerSize);
    }

    private static int? Log2(int stride)
    {
        for (var bits = 1; bits < 32; bits++)
            if (1 << bits == stride)
                return bits;

        return null;
    }
}
