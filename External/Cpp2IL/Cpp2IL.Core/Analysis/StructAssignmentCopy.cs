using System.Collections.Generic;
using Cpp2IL.Core.Extensions;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// A block copy of exactly one struct into exactly one field is that field being assigned.
/// </summary>
/// <remarks>
/// <para>
/// Assigning a struct too big to travel in registers is a <c>memcpy</c>, and <c>memcpy</c> is an import no
/// method table names - so the call stayed an address and the field address in front of it stayed arithmetic:
/// </para>
/// <code>
/// CallVoid SeparatedMinMaxCurve..ctor, v583 @ stackaddr_-118 (SeparatedMinMaxCurve), curve, 0, 1
/// Add      v598, v580 (ParticleImage), 696
/// Call     4AFBB20, v600, v598, v583 (SeparatedMinMaxCurve), 136
/// </code>
/// <code>
/// _ = particleImage3 + 696L;                 // commented out
/// _ = "Method not found @4AFBB20";
/// </code>
/// <para>
/// which the source wrote as <c>mergeParticle.sizeOverLifetime = new SeparatedMinMaxCurve(SizeCurve);</c>.
/// </para>
/// <para>
/// <b>Recognised by shape, not by address</b>, so nothing here is pinned to this binary. Three arguments -
/// a destination, a source and a constant length - where the source is a struct of exactly that many bytes
/// and the destination is that same struct's field on an object. Nothing but a copy of one struct has all
/// three at once, and the call has to be one that never resolved to a managed method: a call that did
/// resolve says what it is already.
/// </para>
/// </remarks>
public static class StructAssignmentCopy
{
    public static void Run(MethodAnalysisContext method)
    {
        if (method.ControlFlowGraph is not { } graph)
            return;

        //Where each value was worked out, so a destination that is a field's address can be read back.
        var definitions = new Dictionary<LocalVariable, Instruction>();

        foreach (var instruction in graph.Instructions)
            if (instruction.Operands.Count > 0 && instruction.Operands[0] is LocalVariable written)
                definitions.TryAdd(written, instruction);

        foreach (var instruction in graph.Instructions)
        {
            if (!instruction.IsCall || instruction.Operands.Count == 0 || !instruction.Operands[0].IsNumeric())
                continue;

            //A `Call` writes its answer to operand one; a `CallVoid` has none. `memcpy` answers with the
            //destination it was given, which nothing here reads.
            var first = instruction.OpCode == OpCode.Call ? 2 : 1;

            if (instruction.Operands.Count != first + 3)
                continue;

            if (instruction.Operands[first] is not LocalVariable destination
                || instruction.Operands[first + 1] is not LocalVariable source
                || Constant(instruction.Operands[first + 2]) is not { } length)
            {
                continue;
            }

            if (source.Type is not { IsValueType: true } copied || Aapcs64.SizeOf(copied) != length)
                continue;

            if (definitions.GetValueOrDefault(destination) is not
                { OpCode: OpCode.Add, Operands: [_, LocalVariable held, { } distance] })
            {
                continue;
            }

            if (Constant(distance) is not { } offset || held.Type is not { } holder)
                continue;

            if (FieldAt(holder, offset) is not { } field || field.FieldType.FullName != copied.FullName)
                continue;

            instruction.OpCode = OpCode.Move;
            instruction.Operands = [new FieldReference(field, held, (int)offset), source];
        }
    }

    /// <summary>The instance field of a type - or of something it derives from - that begins at an offset.</summary>
    private static FieldAnalysisContext? FieldAt(TypeAnalysisContext type, long offset)
    {
        for (var step = type; step is not null; step = step.BaseType)
        {
            var declaring = (step as GenericInstanceTypeAnalysisContext)?.GenericType ?? step;

            foreach (var candidate in declaring.Fields)
                if (!candidate.IsStatic && candidate.BackingData?.FieldOffset == offset)
                    return candidate;
        }

        return null;
    }

    private static long? Constant(object operand)
    {
        try
        {
            return operand is string or LocalVariable or Register or MemoryOperand or FieldReference
                ? null
                : System.Convert.ToInt64(operand);
        }
        catch
        {
            return null;
        }
    }
}
