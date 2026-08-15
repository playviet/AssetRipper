using System;
using System.Collections.Generic;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// A float literal that was materialised in a general register keeps the bits, not the number - and nothing
/// downstream of it is right until it is read back as the float it is.
/// </summary>
/// <remarks>
/// <para>
/// A floating point constant does not survive compilation as a constant. Where it is small the compiler has
/// <c>FMOV Sd, #imm</c> for it and recovery gets a float; where it is not, the word is built in a
/// <b>general</b> register and moved across:
/// </para>
/// <code>
/// mov  w8, #0x477FFF00       ; the bits of 65535.0f
/// fmov s1, w8
/// fdiv s0, s0, s1
/// </code>
/// <para>
/// The <c>FMOV</c> is lifted as a plain move, so the constant arrives as
/// <c>Move v11 @ X8 (System.Int64), 1199570688</c>, copy propagation folds the move away, and the integer
/// type survives into the divide and poisons every type after it.
/// <c>ProceduralImage::EncodeFloats_0_1_16_16</c> is the whole of it: <c>long num = 1199570688L</c>, two
/// integer divisions of a float by it, and a <c>float</c> method returning the sum of two longs. It compiles,
/// it carries no marker, every scorer here calls it recovered, and it answers with nonsense - which is the one
/// class of defect none of them can see.
/// </para>
/// <para>
/// <b>The machine settles it, so this is a reading rather than a guess.</b> An <c>FDIV</c> cannot take an
/// integer divisor and an <c>FMOV</c> into a vector register moves nothing else: the only way an integer
/// constant reaches either is as the bits of a float. So the evidence asked for is not a type - types are what
/// this is repairing - but <b>where the value lands</b>. A value that goes into a vector register, or that an
/// instruction computing into one reads, is floating point, because that is what those registers are for.
/// </para>
/// <para>
/// <b>Not <c>BitConverter</c>.</b> The opposite direction - <c>FMOV Wd, Sn</c>, a float's bits read as an
/// integer - is an operation the program performs and is written as the call that names it
/// (<see cref="FloatBitsInAnInteger"/>). This direction is not an operation at all: it is how the constant
/// was spelled, and the source said <c>65535f</c>.
/// </para>
/// <para>
/// <b>Seeded, not applied late.</b> It runs among the seeds of
/// <see cref="LocalVariables.ResolveTypesAndFields"/> for two reasons. The <c>FMOV</c> is still there at that
/// point - copy propagation is four steps further on - so the move into the vector register is visible as
/// evidence in its own right; and once the constant is a float the existing fixpoint does the rest for
/// nothing, because <c>SharpenAVectorRegister</c> then finds a divide that reads only floating point values
/// and corrects the destination it had left as a bare width. Applied after the fixpoint the constant would be
/// right and every type computed from it still wrong.
/// </para>
/// </remarks>
public static class FloatConstantInAnInteger
{
    /// <summary>
    /// Rewrites every integer constant that is really a float literal's bits, and types the local it was
    /// moved into.
    /// </summary>
    public static void Seed(MethodAnalysisContext method)
    {
        if (method.ControlFlowGraph is not { } graph)
            return;

        //A constant is only a candidate where exactly one instruction defines the local holding it: two
        //definitions means the register was reused, and the uses below cannot be attributed to this value.
        var definitions = new Dictionary<LocalVariable, Instruction?>();

        foreach (var instruction in graph.Instructions)
        {
            if (instruction.Operands.Count > 0 && instruction.Operands[0] is LocalVariable written)
                definitions[written] = definitions.ContainsKey(written) ? null : instruction;
        }

        //Every use of a local, and whether that use says the value is floating point. A use that says nothing
        //either way - a move to another general register, a comparison - is not evidence and is not a refusal
        //either; a use that would read the constant as a number is what refuses.
        var landsInAVectorRegister = new HashSet<LocalVariable>();
        var readAsANumber = new HashSet<LocalVariable>();

        foreach (var instruction in graph.Instructions)
        {
            var floating = instruction.Operands.Count > 0
                && instruction.Operands[0] is LocalVariable destination
                && InAVectorRegister(destination)
                && instruction.OpCode is OpCode.Move or OpCode.Add or OpCode.Subtract
                    or OpCode.Multiply or OpCode.Divide;

            for (var operand = 1; operand < instruction.Operands.Count; operand++)
                if (instruction.Operands[operand] is LocalVariable read)
                    (floating ? landsInAVectorRegister : readAsANumber).Add(read);
        }

        foreach (var (local, definition) in definitions)
        {
            if (definition is null || !landsInAVectorRegister.Contains(local) || readAsANumber.Contains(local))
                continue;

            if (definition.OpCode != OpCode.Move || definition.Operands.Count != 2)
                continue;

            if (!TryDecode(definition.Operands[1], out var single, out var value))
                continue;

            definition.Operands[1] = value;
            local.Type = single
                ? method.AppContext.SystemTypes.SystemSingleType
                : method.AppContext.SystemTypes.SystemDoubleType;
        }
    }

    /// <summary>
    /// The float an integer constant's bits denote, and whether it is a single or a double.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The width comes from the constant, exactly as it does from the instruction that moved it:
    /// <c>FMOV Sd, Wn</c> moves thirty-two bits and <c>FMOV Dd, Xn</c> moves sixty-four, so a value that fits
    /// in a word was moved as a word. The register cannot answer this - the lifter gives single and double one
    /// name - and a double whose pattern fits in thirty-two bits is a denormal, which the check below refuses
    /// anyway.
    /// </para>
    /// <para>
    /// A subnormal decode is a mislabelled integer rather than a float, which is the same rule
    /// <see cref="FloatLiteralRecovery"/> applies and for the same reason: real source constants are never
    /// subnormal, so <c>4</c> decoding to <c>5.6e-45</c> is a small integer that happened to be handed to a
    /// vector register. Nought is refused with them - a cleared register is not a literal, and
    /// <c>ClearedStruct</c> and <c>FloatStructBroadcast</c> own that shape.
    /// </para>
    /// </remarks>
    private static bool TryDecode(object operand, out bool single, out object value)
    {
        single = true;
        value = operand;

        if (!TryGetIntegerBits(operand, out var bits) || bits == 0)
            return false;

        if (bits <= uint.MaxValue)
        {
            if ((bits & 0x7F800000u) == 0)
                return false;

            value = BitConverter.ToSingle(BitConverter.GetBytes((uint)bits), 0);
            return true;
        }

        if ((bits & 0x7FF0000000000000UL) == 0)
            return false;

        single = false;
        value = BitConverter.ToDouble(BitConverter.GetBytes(bits), 0);
        return true;
    }

    private static bool TryGetIntegerBits(object operand, out ulong bits)
    {
        switch (operand)
        {
            case ulong v: bits = v; return true;
            case long v: bits = unchecked((ulong)v); return true;
            case uint v: bits = v; return true;
            case int v: bits = unchecked((uint)v); return true;
            default: bits = 0; return false;
        }
    }

    /// <summary>
    /// Whether the value lives in one of the vector registers, which on this architecture is where floating
    /// point is kept.
    /// </summary>
    private static bool InAVectorRegister(LocalVariable local)
        => local.Register.Name is { Length: > 1 } name && name[0] is 'V' or 'S' or 'D' && char.IsDigit(name[1]);
}
