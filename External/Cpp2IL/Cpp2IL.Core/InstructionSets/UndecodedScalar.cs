using System;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Disarm;
using Disarm.InternalDisassembly;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>
/// The one-source floating point instructions the disassembler refuses, which are not vector at all.
/// </summary>
/// <remarks>
/// <para>
/// <c>fcvt s0, d0</c> is the single commonest instruction in this game that reaches the lifter as nothing:
/// 387 of them, and the switch has handled <c>FCVT</c> all along - it never sees one, because the mnemonic
/// arrives as <c>INVALID</c>. Everything downstream of a converted value was therefore lost, in twenty-four
/// game methods and three hundred more in the libraries.
/// </para>
/// <para>
/// They are scalar, so they have nothing to do with <see cref="VectorLanes"/> beyond being read out of the
/// binary the same way. What they do have is a width on each side, which is exactly what
/// <see cref="ConversionTarget"/> was built to carry - so a precision change becomes the conversion it is
/// rather than a move that quietly reinterprets.
/// </para>
/// </remarks>
internal static class UndecodedScalar
{
    /// <summary>
    /// Lifts one of them, and says whether it did.
    /// </summary>
    public static bool TryConvert(MethodAnalysisContext context, Arm64Instruction instruction,
        Action<OpCode, object[]> emit, ulong address)
    {
        //Only where the disassembler gave up: anything it did decode is the switch's business, and lifting it
        //here as well would emit the instruction twice.
        if (instruction.Op0Reg != Arm64Register.INVALID)
            return false;

        if (VectorLanes.Word(context, address) is not { } word)
            return false;

        //Floating point data-processing, one source: 00011110 type 1 0000 opcode 10000 Rn Rd.
        if (word >> 24 != 0b00011110 || (word >> 21 & 1) != 1 || (word >> 10 & 0x1F) != 0b10000)
            return false;

        if (Width(context, (int)(word >> 22 & 3)) is not { } from)
            return false;

        var destination = new Register(null, $"V{word & 0x1F}");
        var source = new Register(null, $"V{word >> 5 & 0x1F}");
        var opcode = (int)(word >> 15 & 0x3F);

        //A change of precision, which is a conversion and has to say so or the value carries the width it had.
        if (opcode is 0b000100 or 0b000101 or 0b000111)
        {
            if (Width(context, opcode switch { 0b000100 => 0, 0b000101 => 1, _ => 3 }) is not { } to
                || ReferenceEquals(to, from))
                return false;

            emit(OpCode.Move, [destination, source, new ConversionTarget(to)]);
            return true;
        }

        //A move between two registers of the same width changes nothing but where the value is.
        if (opcode == 0b000000)
        {
            emit(OpCode.Move, [destination, source]);
            return true;
        }

        if (opcode == 0b000010)
        {
            emit(OpCode.Negate, [destination, source]);
            return true;
        }

        //The rest are library methods the compiler inlined to one instruction, named back the same way the
        //decoded forms of them are. `frintz`, `frinta` and the other rounding modes are absent because the
        //managed libraries have no method that rounds that way, and inventing one would be a wrong answer.
        var names = opcode switch
        {
            0b000001 => new[] { "Abs" },
            0b000011 => ["Sqrt"],
            0b001010 => ["Floor"],
            0b001001 => ["Ceil", "Ceiling"],
            _ => null,
        };

        if (names is null || MathIntrinsics.Resolve(context.AppContext, names, 1, IsDouble(context, from)) is not { } method)
            return false;

        emit(OpCode.Call, [method, destination, source]);
        return true;
    }

    /// <summary>The type the two bit width field of a scalar floating point instruction names.</summary>
    /// <remarks>Half precision has no managed type to convert to, so an instruction using it is left alone.</remarks>
    private static TypeAnalysisContext? Width(MethodAnalysisContext context, int type) => type switch
    {
        0 => context.AppContext.SystemTypes.SystemSingleType,
        1 => context.AppContext.SystemTypes.SystemDoubleType,
        _ => null,
    };

    private static bool IsDouble(MethodAnalysisContext context, TypeAnalysisContext type)
        => ReferenceEquals(type, context.AppContext.SystemTypes.SystemDoubleType);
}
