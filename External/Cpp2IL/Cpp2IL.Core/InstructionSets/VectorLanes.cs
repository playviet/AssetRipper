using System;
using System.Collections.Generic;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Disarm;
using Disarm.InternalDisassembly;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>
/// Takes apart the work the compiler packed several values into one register to do.
/// </summary>
/// <remarks>
/// <para>
/// Every instruction the arm64 disassembler cannot decode at all in this game is Advanced SIMD - 2257 of them,
/// in 464 methods - and none of it is code anyone wrote as SIMD. It is ordinary scalar arithmetic that clang
/// noticed it could do two or four at a time:
/// </para>
/// <code>
/// fmov s1, w9                 ; 360f
/// dup  v1.2s, v1.s[0]         ; in both lanes
/// dup  v0.2s, v0.s[0]         ; and the divisor in both lanes of another
/// ld1  { v1.s }[0], [x8]      ; overwrite the low lane with a field
/// fdiv v0.2s, v1.2s, v0.2s    ; two divisions in one instruction
/// stur d0, [x19, #0x34]       ; and the pair is a Vector2
/// </code>
/// <para>
/// Lifting a register as one value gets the low lane right and loses the rest, so what came out was one
/// component of a vector and a placeholder where the other belonged. Since a placeholder takes the statement
/// holding it, the assignment went too.
/// </para>
/// <para>
/// <b>A lane is a register</b>, named by where it starts rather than by its index, because one instruction's
/// lane 1 is four bytes along and the next one's is eight. Lane zero keeps the register's own name: the low
/// part of <c>v0</c> is exactly what <c>s0</c> and <c>d0</c> mean and everything already lifted reads it that
/// way, so nothing that worked before changes. Every other lane gets a name of its own, the instruction is
/// emitted once per lane, and what was one packed register becomes what the source had - several independent
/// values.
/// </para>
/// <para>
/// A lane above the first is only read once something has written it. Arm64 zeroes the high part of a vector
/// register whenever the low part is written on its own, so any instruction this does not model discards what
/// it knew about that register rather than guessing. Half a vectorised condition is not half an answer, it is
/// the wrong answer, so an instruction that cannot be followed all the way across leaves its placeholder
/// standing.
/// </para>
/// </remarks>
internal sealed class VectorLanes
{
    /// <summary>How wide one lane of each register was last taken to be, in bytes.</summary>
    private readonly Dictionary<int, int> width = new();

    /// <summary>Which lanes above the first have been written, by register and by where the lane starts.</summary>
    private readonly HashSet<(int Register, int Offset)> written = new();

    /// <summary>
    /// The registers whose lanes hold the answer to a question rather than a number.
    /// </summary>
    /// <remarks>
    /// A vectorised <c>||</c> is a chain of steps that only make sense over a lane the architecture set to all
    /// ones or to none: narrowing it, shifting its lowest bit into the sign and reading the sign back are the
    /// same value three times over, and reducing the lanes with an unsigned maximum asks whether any of them
    /// held. As answers they are moves and one <c>or</c>; as numbers they are none of those things - so the
    /// steps are only taken where what they read is known to be an answer.
    /// </remarks>
    private readonly HashSet<int> masks = new();

    /// <summary>
    /// A whole-register load that has not been split into lanes yet, because nothing has said how wide they are.
    /// </summary>
    /// <remarks>
    /// <c>ldr q3, [x8, #0x380]</c> is sixteen bytes of constant and says nothing about whether they are four
    /// floats or two doubles - only the instruction that reads them does. So the load is kept and split at the
    /// first use, at the width that use asks for.
    /// </remarks>
    private readonly Dictionary<int, (int Base, long Offset, int Bytes)> pending = new();

    /// <summary>
    /// Where a register stops holding anything: every byte at or above this offset is zero.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Writing <c>s2</c> puts zero into the twelve bytes above it, writing <c>d2</c> into the eight above, and
    /// an instruction over a 64 bit arrangement does the same to the top half - the architecture leaves nothing
    /// of what was there. So a register is very often not unknown above what last wrote it: it is zero, and a
    /// wider instruction reading it is entitled to that.
    /// </para>
    /// <para>
    /// This is the difference between following a chain and giving up on it. A pair computed in <c>.2s</c> and
    /// then compared in <c>.4s</c> - which is what a vectorised pair of range checks does - reads two lanes
    /// that were computed and two that are zero, and without the second pair the comparison cannot be emitted
    /// at all. The zeros are written out only where something asks for one, so a method full of ordinary
    /// scalar float arithmetic gains no instructions.
    /// </para>
    /// </remarks>
    private readonly Dictionary<int, int> zeroAbove = new();

    /// <summary>
    /// Where the next instruction is, for the ones that do not say.
    /// </summary>
    /// <remarks>
    /// A word the disassembler cannot place in the encoding space at all comes back as <c>INVALID</c> with
    /// <b>no address</b>, unlike the ones it recognises and has no decoder for. Since every arm64 instruction
    /// is four bytes and they arrive in order, the address is the last one plus four - and without this the
    /// larger half of the family this class exists for could not be read back out of the binary.
    /// </remarks>
    private ulong next;

    public void Reset()
    {
        width.Clear();
        written.Clear();
        masks.Clear();
        pending.Clear();
        zeroAbove.Clear();
        next = 0;
    }

    /// <summary>
    /// Lifts what the lane model understands, and says whether it accounted for the whole instruction.
    /// </summary>
    /// <remarks>
    /// Three answers, not two. An instruction the model decodes itself is handled here outright. One that does
    /// the same arithmetic to every lane and that the disassembler did decode is handled here for the lanes
    /// above the first and left to the ordinary lifting for the first, so this returns false and both run.
    /// Anything else discards what was known about the register it writes.
    /// </remarks>
    /// <summary>
    /// Every word this refused, with what was known about the registers at the time - for finding out whether
    /// a refusal is an encoding that is not decoded or a lane that was never written. Off unless asked for.
    /// </summary>
    internal static readonly System.Collections.Concurrent.ConcurrentQueue<string> Refused = new();

    internal static bool Reporting;

    public bool TryConvert(MethodAnalysisContext context, Arm64Instruction instruction, Action<OpCode, object[]> emit)
    {
        var address = instruction.Address != 0 ? instruction.Address : next;
        next = address + 4;

        //A scalar the disassembler also refused. Dispatched from here because this is where the address of an
        //instruction that carries none has been worked out - see UndecodedScalar.cs.
        if (UndecodedScalar.TryConvert(context, instruction, emit, address))
            return true;

        if (Store(instruction, emit))
            return true;

        var word = Word(context, address);

        if (word is { } decodable && Decode(context, decodable, emit))
            return true;

        if (Reporting && word is { } refused && (refused >> 25 & 0x7) == 0b111 && instruction.Op0Reg == Arm64Register.INVALID)
        {
            Refused.Enqueue($"{refused:X8}\t{context.DeclaringType?.Name}::{context.Name}\t" +
                $"w=[{string.Join(",", width)}]\tlanes=[{string.Join(",", written)}]\tmask=[{string.Join(",", masks)}]\t" +
                $"zero=[{string.Join(",", zeroAbove)}]\tpending=[{string.Join(",", pending.Keys)}]");
        }

        if (word is { } loaded && (Load(loaded, instruction) || LoadPair(loaded, instruction)))
            return false;

        if (Replicate(instruction, emit))
            return false;

        Invalidate(instruction, word);
        return false;
    }

    private bool Decode(MethodAnalysisContext context, uint word, Action<OpCode, object[]> emit)
        => Copy(word, emit) || Immediate(word, emit) || Arithmetic(context, word, emit) || Integer(word, emit)
           || Scaled(word, emit) || Permute(word, emit) || Reduce(word, emit) || Shifted(word, emit);

    /// <summary>
    /// The same constant put into every lane, which is how a vector of a literal is made.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>movi v0.2s, #0x3f, lsl #24</c> is <c>[0.5f, 0.5f]</c>. The disassembler decodes it, so it never
    /// appeared among the instructions it refuses - and the ordinary lifting takes it for a single value,
    /// which left the model no choice but to forget the register. Almost every chain that ends in a refused
    /// <c>fmul</c> begins at one of these: a vector multiplied by a constant is the commonest vectorised
    /// expression there is, and the constant end of it was never established.
    /// </para>
    /// <para>
    /// The immediate is eight bits with a shift, so what it names is exact. The forms that build a value out
    /// of the individual bits of the immediate - the sixty-four bit <c>movi</c> and the two floating point
    /// ones - are left alone, because what they produce is not what the eight bits say.
    /// </para>
    /// </remarks>
    private bool Immediate(uint word, Action<OpCode, object[]> emit)
    {
        //0 Q op 0111100000 abc cmode o2 1 defgh Rd, and o2 must be zero or this is not one of them.
        if (word >> 31 != 0 || (word >> 19 & 0x3FF) != 0b0111100000
            || (word >> 10 & 1) != 1 || (word >> 11 & 1) != 0)
            return false;

        var inverted = (word >> 29 & 1) == 1;
        var cmode = (int)(word >> 12 & 0xF);
        var immediate = (long)((word >> 16 & 7) << 5 | (word >> 5 & 0x1F));

        var q = (int)(word >> 30 & 1);
        var destination = (int)(word & 0x1F);

        //The floating point form, whose eight bits expand into a number rather than being one - the same
        //expansion the scalar `fmov` uses, and the same reason it cannot be taken as written.
        if (cmode == 0b1111)
        {
            var wide = inverted && q == 1;

            if (inverted && q == 0)
                return false;

            var lanesOfIt = wide ? 2 : LanesIn(4, q);
            object number = wide
                ? NewArmV8InstructionSet.ExpandedWide((int)immediate)
                : NewArmV8InstructionSet.Expanded((int)immediate);

            Forget(destination);

            for (var lane = 0; lane < lanesOfIt; lane++)
                emit(OpCode.Move, [Lane(destination, lane * (wide ? 8 : 4)), number]);

            Note(destination, wide ? 8 : 4, lanesOfIt, false);
            return true;
        }

        var (elementWidth, value) = (cmode & 0b1001, cmode & 0b1101) switch
        {
            //Thirty-two bit lanes, the immediate shifted by whole bytes.
            (0b0000, _) => (4, immediate << ((cmode >> 1 & 3) * 8)),
            //Sixteen bit lanes, shifted by none or one byte.
            (_, 0b1000) => (2, immediate << ((cmode & 2) == 0 ? 0 : 8)),
            //One byte lanes, which no managed value is made of but which cost nothing to say.
            _ when cmode == 0b1110 && !inverted => (1, immediate),
            _ => (0, 0L),
        };

        if (elementWidth == 0 || (inverted && elementWidth == 1))
            return false;

        if (inverted)
            value = ~value & (elementWidth == 4 ? 0xFFFFFFFFL : 0xFFFFL);

        var lanes = LanesIn(elementWidth, q);

        Forget(destination);

        for (var lane = 0; lane < lanes; lane++)
            emit(OpCode.Move, [Lane(destination, lane * elementWidth), value]);

        Note(destination, elementWidth, lanes, false, emit);
        return true;
    }

    /// <summary>
    /// A whole vector multiplied by one lane of another, which is how a vector is scaled by a number.
    /// </summary>
    /// <remarks>
    /// <c>fmul v4.2s, v5.2s, v4.s[0]</c> is a <c>Vector2</c> times a <c>float</c>, and it sits in the middle
    /// of every vectorised expression this game has. Left undecoded it takes the register it writes with it,
    /// and every lane downstream of that register is refused - which is why the range check two instructions
    /// later could not be followed until this was here.
    /// </remarks>
    private bool Scaled(uint word, Action<OpCode, object[]> emit)
    {
        if (word >> 31 != 0 || (word >> 24 & 0x1F) != 0b01111 || (word >> 10 & 1) != 0
            || (word >> 29 & 1) != 0 || (word >> 12 & 0xF) != 0b1001)
            return false;

        var size = (int)(word >> 22 & 3);

        if (size is not (2 or 3))
            return false;

        var elementWidth = size == 3 ? 8 : 4;
        var high = (int)(word >> 11 & 1);
        var low = (int)(word >> 21 & 1);

        //The register holding the scale is four bits wide for single precision, the fifth being part of the
        //lane number.
        var scale = size == 2 ? (int)(word >> 20 & 1) << 4 | (int)(word >> 16 & 0xF) : (int)(word >> 16 & 0x1F);
        var index = size == 3 ? high : high << 1 | low;

        return Emit((int)(word & 0x1F), elementWidth, LanesIn(elementWidth, (int)(word >> 30 & 1)),
            OpCode.Multiply, false, emit, [((int)(word >> 5 & 0x1F), null), (scale, index)]);
    }

    /// <summary>The instruction as the architecture wrote it, which is the only place the lane is recorded.</summary>
    /// <remarks>
    /// Read from the binary rather than from <c>MethodAnalysisContext.RawBytes</c>, which for a managed method
    /// is sliced at the method's <i>virtual</i> address as though it were a file offset - so its length is the
    /// method's and its contents are whatever happens to lie at that offset in the file. Everything else in the
    /// lifter only asks it for its length, which is why that has never shown.
    /// </remarks>
    internal static uint? Word(MethodAnalysisContext context, ulong address)
    {
        var binary = context.AppContext.Binary;

        if (address == 0 || !binary.TryMapVirtualAddressToRaw(address, out var raw) || raw <= 0)
            return null;

        var content = binary.GetRawBinaryContent();

        return raw + 4 > content.Length ? null : BitConverter.ToUInt32(content.Slice((int)raw, 4));
    }

    /// <summary>
    /// The instructions that only move a value: between a lane and a general register, from one lane into
    /// every lane, from one lane to another, or between a lane and memory.
    /// </summary>
    /// <remarks>
    /// These are decoded from the word rather than taken from the disassembler, which reports every one of
    /// them as undecodable. Every field layout in this class was checked against the toolchain's own
    /// disassembler over all 845 distinct words this game contains, and agrees on all 754 it claims.
    /// </remarks>
    private bool Copy(uint word, Action<OpCode, object[]> emit)
    {
        var q = (int)(word >> 30 & 1);
        var destination = (int)(word & 0x1F);
        var source = (int)(word >> 5 & 0x1F);

        //Advanced SIMD copy: 0 Q op 01110000 imm5 0 imm4 1 Rn Rd.
        if (word >> 31 == 0 && (word >> 21 & 0x1FF) is 0b001110000 or 0b101110000
            && (word >> 10 & 1) == 1 && (word >> 15 & 1) == 0)
        {
            var operation = (int)(word >> 29 & 1);
            var selector = (int)(word >> 11 & 0xF);

            if (Element(word >> 16 & 0x1F) is not var (elementWidth, index))
                return false;

            switch (operation, selector)
            {
                //dup: one lane, or one general register, into every lane.
                case (0, 0b0000):
                case (0, 0b0001):
                    var fromLane = selector == 0;

                    if (fromLane && !Ensure(source, index * elementWidth, elementWidth, emit))
                        return false;

                    var from = fromLane ? Lane(source, index * elementWidth) : General(source);
                    var lanes = LanesIn(elementWidth, q);
                    var copiedAnswers = fromLane && masks.Contains(source);

                    Forget(destination);

                    for (var lane = 0; lane < lanes; lane++)
                        emit(OpCode.Move, [Lane(destination, lane * elementWidth), from]);

                    Note(destination, elementWidth, lanes, copiedAnswers, emit);
                    return true;

                //umov/smov: one lane back out into a general register. ISIL carries no width, so the
                //sign extension the two differ by is nothing it can say.
                case (0, 0b0101):
                case (0, 0b0111):
                    if (!Ensure(source, index * elementWidth, elementWidth, emit))
                        return false;

                    emit(OpCode.Move, [General(destination), Lane(source, index * elementWidth)]);
                    return true;

                //ins: a general register into one lane.
                case (0, 0b0011):
                    emit(OpCode.Move, [Lane(destination, index * elementWidth), General(source)]);
                    Write(destination, index * elementWidth, elementWidth, false);
                    return true;

                //ins: one lane into another. The source index is in the selector, scaled by the width.
                case (1, _):
                    var sourceIndex = selector >> Shift(elementWidth);

                    //`mov v1.d[1], v0.d[0]` moves eight bytes, and where both registers are being followed
                    //four bytes at a time those eight bytes are two lanes apiece. Moving them as one would
                    //leave the second of them holding what it held before, which is not what was written.
                    var moved = width.TryGetValue(destination, out var tracked) && tracked < elementWidth
                        && width.TryGetValue(source, out var trackedSource) && trackedSource == tracked
                            ? tracked
                            : elementWidth;

                    for (var part = 0; part * moved < elementWidth; part++)
                    {
                        if (!Ensure(source, sourceIndex * elementWidth + part * moved, moved, emit))
                            return false;
                    }

                    for (var part = 0; part * moved < elementWidth; part++)
                    {
                        emit(OpCode.Move,
                        [
                            Lane(destination, index * elementWidth + part * moved),
                            Lane(source, sourceIndex * elementWidth + part * moved),
                        ]);

                        Write(destination, index * elementWidth + part * moved, moved, masks.Contains(source));
                    }

                    return true;
            }

            return false;
        }

        //`mov vd.16b, vn.16b`, which the architecture writes as an `orr` of a register with itself: the whole
        //register, so every lane of it - and it is what puts a computed pair somewhere a comparison can reach.
        if (word >> 31 == 0 && (word >> 21 & 0x1FF) == 0b001110101 && (word >> 10 & 0x3F) == 0b000111
            && (int)(word >> 16 & 0x1F) == source)
        {
            Split(source, 4, LanesIn(4, q), emit);

            if (!width.TryGetValue(source, out var copiedWidth))
                return false;

            var copiedLanes = LanesIn(copiedWidth, q);

            for (var lane = 0; lane < copiedLanes; lane++)
            {
                if (!Ensure(source, lane * copiedWidth, copiedWidth, emit))
                    return false;
            }

            var copiedMask = masks.Contains(source);
            Forget(destination);

            for (var lane = 0; lane < copiedLanes; lane++)
                emit(OpCode.Move, [Lane(destination, lane * copiedWidth), Lane(source, lane * copiedWidth)]);

            Note(destination, copiedWidth, copiedLanes, copiedMask, emit);
            return true;
        }

        //Load or store one lane: 0 Q 0011010 L 0 00000 opcode S size Rn Rt, no offset. Q is bit 30 and part
        //of the lane number, so it cannot be tested as though it belonged to the opcode.
        if (word >> 31 == 0 && (word >> 23 & 0x7F) == 0b0011010 && (word >> 21 & 1) == 0
            && (word >> 16 & 0x1F) == 0)
        {
            var opcode = (int)(word >> 13 & 0x7);
            var s = (int)(word >> 12 & 1);
            var size = (int)(word >> 10 & 3);

            var (elementWidth, index) = (opcode, size, s) switch
            {
                (0b000, _, _) => (1, q << 3 | s << 2 | size),
                (0b010, 0 or 2, _) => (2, q << 2 | s << 1 | size >> 1),
                (0b100, 0, _) => (4, q << 1 | s),
                (0b100, 1, 0) => (8, q),
                _ => (0, 0),
            };

            if (elementWidth == 0)
                return false;

            var address = Place(source, 0);

            if ((word >> 22 & 1) == 1)
            {
                emit(OpCode.Move, [Lane(destination, index * elementWidth), address]);
                Write(destination, index * elementWidth, elementWidth, false);
            }
            else
            {
                if (!Ensure(destination, index * elementWidth, elementWidth, emit))
                    return false;

                emit(OpCode.Move, [address, Lane(destination, index * elementWidth)]);
            }

            return true;
        }

        return false;
    }

    /// <summary>
    /// The arithmetic and the comparisons the disassembler does not decode either, done to each lane in turn.
    /// </summary>
    /// <remarks>
    /// <c>fdiv</c> over a pair of lanes is the one that showed why this cannot be left out: without it the
    /// register kept whatever was in it before and the store that followed wrote that - a wrong number where a
    /// placeholder used to stand, which is the trade this whole project refuses. An instruction that writes a
    /// modelled register has to be either understood or seen to fail.
    /// </remarks>
    private bool Arithmetic(MethodAnalysisContext context, uint word, Action<OpCode, object[]> emit)
    {
        if (word >> 31 != 0 || (word >> 24 & 0x1F) != 0b01110)
            return false;

        var q = (int)(word >> 30 & 1);
        var destination = (int)(word & 0x1F);
        var left = (int)(word >> 5 & 0x1F);
        var right = (int)(word >> 16 & 0x1F);

        var unsigned = (int)(word >> 29 & 1);
        var alternate = (int)(word >> 23 & 1);
        var size = (int)(word >> 22 & 3);
        var floatWidth = (word >> 22 & 1) == 1 ? 8 : 4;

        //Three registers, same arrangement, floating point.
        if ((word >> 21 & 1) == 1 && (word >> 10 & 1) == 1)
        {
            var opcode = (int)(word >> 11 & 0x1F);

            var operation = (unsigned, alternate, opcode) switch
            {
                (0, 0, 0b11010) => OpCode.Add,
                (0, 1, 0b11010) => OpCode.Subtract,
                (1, 0, 0b11011) => OpCode.Multiply,
                (1, 0, 0b11111) => OpCode.Divide,
                _ => (OpCode?)null,
            };

            if (operation is { } binary)
                return Emit(destination, floatWidth, LanesIn(floatWidth, q), binary, false, emit, [(left, null), (right, null)]);

            //A comparison between two vectors, which is where a vectorised range check begins.
            var comparison = (unsigned, alternate, opcode) switch
            {
                (0, 0, 0b11100) => OpCode.CheckEqual,
                (1, 0, 0b11100) => OpCode.CheckGreaterOrEqual,
                (1, 1, 0b11100) => OpCode.CheckGreater,
                _ => (OpCode?)null,
            };

            if (comparison is { } relational)
                return Emit(destination, floatWidth, LanesIn(floatWidth, q), relational, true, emit, [(left, null), (right, null)]);
        }

        //Two registers, floating point or integer.
        if ((word >> 17 & 0x1F) == 0b10000 && (word >> 10 & 3) == 0b10)
        {
            var opcode = (int)(word >> 12 & 0x1F);

            //`fabs` is absent because ISIL has no word for it - the ordinary lifting turns the scalar form into
            //the library call it was inlined from, and a lane cannot be given a call of its own.
            var operation = (unsigned, alternate, opcode) switch
            {
                (1, 1, 0b01111) => OpCode.Negate,
                //A conversion between integer and floating point is a move: ISIL carries no width, and the
                //managed types either side decide the cast, exactly as for the scalar forms.
                (_, 0, 0b11101) or (_, 1, 0b11011) => OpCode.Move,
                _ => (OpCode?)null,
            };

            if (operation is { } unary)
                return Emit(destination, floatWidth, LanesIn(floatWidth, q), unary, false, emit, [(left, null)]);

            //A library method the compiler inlined, done to every lane. ISIL has no word for rounding or for
            //an absolute value, but it has a call - and a call per lane is exactly what the source said, since
            //rounding a vector is rounding each of its numbers. Single precision only, for the reason
            //`MathCallOperands` gives: a `Math.Floor(double)` pins a type the conversions around it have not
            //carried, and the body then does not compile at all.
            var library = (unsigned, alternate, opcode) switch
            {
                (0, 0, 0b11001) => new[] { "Floor" },
                (0, 1, 0b11000) => new[] { "Ceil", "Ceiling" },
                (0, 1, 0b01111) => new[] { "Abs" },
                (1, 1, 0b11111) => new[] { "Sqrt" },
                _ => null,
            };

            if (library is not null && floatWidth == 4
                && MathIntrinsics.Resolve(context.AppContext, library, 1, false) is { } method)
            {
                return EmitCall(destination, left, 4, LanesIn(4, q), method, emit);
            }

            //A comparison against zero, floating point then integer.
            var floatZero = (unsigned, alternate, opcode) switch
            {
                (0, 1, 0b01100) => OpCode.CheckGreater,
                (0, 1, 0b01101) => OpCode.CheckEqual,
                (0, 1, 0b01110) => OpCode.CheckLess,
                (1, 1, 0b01100) => OpCode.CheckGreaterOrEqual,
                (1, 1, 0b01101) => OpCode.CheckLessOrEqual,
                _ => (OpCode?)null,
            };

            if (floatZero is { } againstZero)
                return EmitAgainstZero(destination, left, floatWidth, LanesIn(floatWidth, q), againstZero, emit);

            var integerWidth = 1 << size;

            var integerZero = (unsigned, opcode) switch
            {
                (0, 0b01000) => OpCode.CheckGreater,
                (0, 0b01001) => OpCode.CheckEqual,
                (0, 0b01010) => OpCode.CheckLess,
                (1, 0b01000) => OpCode.CheckGreaterOrEqual,
                (1, 0b01001) => OpCode.CheckLessOrEqual,
                _ => (OpCode?)null,
            };

            //`cmlt v, v, #0` over lanes that already hold an answer is the second half of the sign trick, and
            //asking whether an answer is less than zero is not what it means - it restates it.
            if (integerZero is { } compareZero)
            {
                return masks.Contains(left)
                    ? Emit(destination, integerWidth, LanesIn(integerWidth, q), OpCode.Move, true, emit, [(left, null)])
                    : EmitAgainstZero(destination, left, integerWidth, LanesIn(integerWidth, q), compareZero, emit);
            }

            //`mvn` over answers negates each of them, and `xtn` only makes them narrower, which they are not.
            //Over anything else both are bit patterns this model has nothing to say about.
            if (!masks.Contains(left))
                return false;

            if ((unsigned, opcode) == (1, 0b00101) && size == 0 && width.TryGetValue(left, out var negatedWidth))
                return Emit(destination, negatedWidth, LanesIn(negatedWidth, q), OpCode.Not, true, emit, [(left, null)]);

            if ((unsigned, opcode) == (0, 0b10010))
            {
                //Narrowing reads lanes twice the width it writes, so the two sides step differently - and
                //`xtn2` writes the top half of its destination and leaves the bottom alone, so the lanes it
                //produces start where the ones already there end.
                var narrowed = 1 << size;
                var half = LanesIn(narrowed, 0);

                return Narrow(destination, left, narrowed, half, q == 1, emit);
            }
        }

        return false;
    }

    /// <summary>
    /// The integer arithmetic, comparisons and bitwise operations over three registers of the same shape.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The bitwise ones are written over byte arrangements whatever they are really operating on, because the
    /// operation is the same whatever the lanes are - so the width has to come from what the model already
    /// knows the register holds, and where it knows nothing there is nothing to say.
    /// </para>
    /// <para>
    /// <c>bsl</c>, <c>bit</c> and <c>bif</c> are a select: one register chooses, lane by lane, between the
    /// other two. That is only a select if the register choosing holds answers rather than numbers - as bits
    /// it is a blend of two values, which is not something a lane of a managed program ever holds. The three
    /// differ only in which register chooses and which way round the arms go.
    /// </para>
    /// </remarks>
    private bool Integer(uint word, Action<OpCode, object[]> emit)
    {
        if (word >> 31 != 0 || (word >> 24 & 0x1F) != 0b01110
            || (word >> 21 & 1) != 1 || (word >> 10 & 1) != 1)
            return false;

        var q = (int)(word >> 30 & 1);
        var unsigned = (int)(word >> 29 & 1);
        var size = (int)(word >> 22 & 3);
        var opcode = (int)(word >> 11 & 0x1F);

        var destination = (int)(word & 0x1F);
        var left = (int)(word >> 5 & 0x1F);
        var right = (int)(word >> 16 & 0x1F);

        //The bitwise family, which the size field names rather than measures.
        if (opcode == 0b00011)
        {
            if (!width.TryGetValue(left, out var elementWidth) && !width.TryGetValue(right, out elementWidth))
                return false;

            var lanes = LanesIn(elementWidth, q);

            //A select, and which register chooses is the whole difference between the three of them.
            var chooser = (unsigned, size) switch
            {
                (1, 1) => destination,  //bsl: the destination chose before it was written
                (1, 2) => right,        //bit: insert where the second register says
                (1, 3) => right,        //bif: insert where it does not
                _ => -1,
            };

            if (chooser >= 0)
            {
                if (!masks.Contains(chooser))
                    return false;

                var (whenSet, whenClear) = (unsigned, size) switch
                {
                    (1, 1) => (left, right),
                    (1, 2) => (left, destination),
                    _ => (destination, left),
                };

                return Choose(destination, chooser, whenSet, whenClear, elementWidth, lanes, emit);
            }

            var bitwise = (unsigned, size) switch
            {
                (0, 0) => OpCode.And,
                (0, 2) => OpCode.Or,
                (1, 0) => OpCode.Xor,
                _ => (OpCode?)null,
            };

            return bitwise is { } logical
                && Emit(destination, elementWidth, lanes, logical, masks.Contains(left) && masks.Contains(right),
                    emit, [(left, null), (right, null)]);
        }

        var operation = (unsigned, opcode) switch
        {
            (0, 0b10000) => OpCode.Add,
            (1, 0b10000) => OpCode.Subtract,
            (0, 0b10011) => OpCode.Multiply,
            _ => (OpCode?)null,
        };

        var width8 = 1 << size;
        var count = LanesIn(width8, q);

        if (operation is { } arithmetic)
            return Emit(destination, width8, count, arithmetic, false, emit, [(left, null), (right, null)]);

        //The unsigned comparisons are mapped onto the signed ones, matching how the rest of the lifting
        //treats a condition code: ISIL has one set of relational opcodes.
        var comparison = (unsigned, opcode) switch
        {
            (0, 0b00110) or (1, 0b00110) => OpCode.CheckGreater,
            (0, 0b00111) or (1, 0b00111) => OpCode.CheckGreaterOrEqual,
            (1, 0b10001) => OpCode.CheckEqual,
            _ => (OpCode?)null,
        };

        return comparison is { } relational
            && Emit(destination, width8, count, relational, true, emit, [(left, null), (right, null)]);
    }

    /// <summary>Chooses between two registers lane by lane, according to a third.</summary>
    private bool Choose(int destination, int chooser, int whenSet, int whenClear, int elementWidth, int lanes,
        Action<OpCode, object[]> emit)
    {
        foreach (var register in new[] { chooser, whenSet, whenClear })
        {
            for (var lane = 0; lane < lanes; lane++)
            {
                if (!Ensure(register, lane * elementWidth, elementWidth, emit))
                    return false;
            }
        }

        var lifted = new List<object[]>();

        for (var lane = 0; lane < lanes; lane++)
        {
            lifted.Add([
                Lane(destination, lane * elementWidth),
                Lane(chooser, lane * elementWidth),
                Lane(whenSet, lane * elementWidth),
                Lane(whenClear, lane * elementWidth),
            ]);
        }

        Forget(destination);

        foreach (var operands in lifted)
            emit(OpCode.Select, operands);

        Note(destination, elementWidth, lanes, false);
        return true;
    }

    /// <summary>
    /// The instructions that only move lanes about: interleaving two registers, taking every other lane of
    /// them, or sliding a window across the pair.
    /// </summary>
    /// <remarks>
    /// Nothing is computed, so nothing has to be understood about what the lanes hold - only which lane ends
    /// up where. That makes these the safest of the family, and the ones that keep a chain alive across the
    /// shuffling a compiler does between two vectorised expressions.
    /// </remarks>
    private bool Permute(uint word, Action<OpCode, object[]> emit)
    {
        var q = (int)(word >> 30 & 1);
        var destination = (int)(word & 0x1F);
        var left = (int)(word >> 5 & 0x1F);
        var right = (int)(word >> 16 & 0x1F);

        //0 Q 001110 size 0 Rm 0 opcode 10 Rn Rd
        if ((word >> 24 & 0x3F) == 0b001110 && (word >> 21 & 1) == 0 && (word >> 15 & 1) == 0
            && (word >> 10 & 3) == 0b10 && word >> 31 == 0)
        {
            var elementWidth = 1 << (int)(word >> 22 & 3);
            var lanes = LanesIn(elementWidth, q);
            var half = lanes / 2;

            //Which lane of the pair, counted across both registers, each lane of the result comes from.
            var pick = (int)(word >> 12 & 0x7) switch
            {
                0b001 => Enumerate(lanes, i => 2 * i),                                            //uzp1
                0b101 => Enumerate(lanes, i => 2 * i + 1),                                        //uzp2
                0b010 => Enumerate(lanes, i => i % 2 == 0 ? i : lanes + i - 1),                   //trn1
                0b110 => Enumerate(lanes, i => i % 2 == 0 ? i + 1 : lanes + i),                   //trn2
                0b011 => Enumerate(lanes, i => i % 2 == 0 ? i / 2 : lanes + i / 2),               //zip1
                0b111 => Enumerate(lanes, i => i % 2 == 0 ? half + i / 2 : lanes + half + i / 2), //zip2
                _ => null,
            };

            return pick is not null && Shuffle(destination, left, right, elementWidth, lanes, pick, emit);
        }

        //ext: 0 Q 101110 00 0 Rm 0 imm4 0 Rn Rd - a window over the two registers, starting imm4 bytes in.
        if ((word >> 24 & 0x3F) == 0b101110 && (word >> 22 & 3) == 0 && (word >> 21 & 1) == 0
            && (word >> 15 & 1) == 0 && (word >> 10 & 1) == 0 && word >> 31 == 0)
        {
            if (!width.TryGetValue(left, out var elementWidth))
                return false;

            var start = (int)(word >> 11 & 0xF);

            if (start % elementWidth != 0)
                return false;

            var lanes = LanesIn(elementWidth, q);

            return Shuffle(destination, left, right, elementWidth, lanes,
                Enumerate(lanes, i => i + start / elementWidth), emit);
        }

        return false;
    }

    /// <summary>
    /// The across-lane reduction a vectorised <c>||</c> or <c>&amp;&amp;</c> ends with.
    /// </summary>
    /// <remarks>
    /// An unsigned maximum over lanes that hold answers is "did any of them hold", and an unsigned minimum is
    /// "did all of them". Over numbers they are a maximum and a minimum, which ISIL has no word for, so only
    /// the answers are taken.
    /// </remarks>
    private bool Reduce(uint word, Action<OpCode, object[]> emit)
    {
        if (word >> 31 != 0 || (word >> 24 & 0x1F) != 0b01110
            || (word >> 17 & 0x1F) != 0b11000 || (word >> 10 & 3) != 0b10)
            return false;

        var destination = (int)(word & 0x1F);
        var source = (int)(word >> 5 & 0x1F);

        if (!masks.Contains(source) || !width.TryGetValue(source, out var elementWidth))
            return false;

        var combine = ((int)(word >> 29 & 1), (int)(word >> 12 & 0x1F)) switch
        {
            (1, 0b01010) => OpCode.Or,
            (1, 0b11010) => OpCode.And,
            _ => (OpCode?)null,
        };

        if (combine is not { } across)
            return false;

        var lanes = LanesIn(elementWidth, (int)(word >> 30 & 1));

        for (var lane = 0; lane < lanes; lane++)
        {
            if (!Ensure(source, lane * elementWidth, elementWidth, emit))
                return false;
        }

        //Into a scalar, so the answer lives in the low part alone and the rest of the register is gone.
        var answer = Lane(destination, 0);
        var read = new List<object>();

        for (var lane = 0; lane < lanes; lane++)
            read.Add(Lane(source, lane * elementWidth));

        Forget(destination);
        emit(OpCode.Move, [answer, read[0]]);

        for (var lane = 1; lane < lanes; lane++)
            emit(across, [answer, answer, read[lane]]);

        return true;
    }

    /// <summary>
    /// A shift by a constant, which over lanes holding an answer is the other half of the sign trick.
    /// </summary>
    /// <remarks>
    /// <c>shl #15</c> then <c>cmlt #0</c> moves the lowest bit of each lane into its sign and reads it back:
    /// two instructions that together say what the lane already said. Over numbers a shift is a shift.
    /// </remarks>
    private bool Shifted(uint word, Action<OpCode, object[]> emit)
    {
        if (word >> 31 != 0 || (word >> 23 & 0x3F) != 0b011110 || (word >> 10 & 1) != 1)
            return false;

        var high = (int)(word >> 19 & 0xF);

        if (high == 0)
            return false;

        var destination = (int)(word & 0x1F);
        var source = (int)(word >> 5 & 0x1F);
        var unsigned = (int)(word >> 29 & 1);
        var opcode = (int)(word >> 11 & 0x1F);

        var highest = high >= 8 ? 3 : high >= 4 ? 2 : high >= 2 ? 1 : 0;
        var elementWidth = 1 << highest;
        var bits = 8 << highest;
        var places = high << 3 | (int)(word >> 16 & 0x7);

        var (operation, by) = (unsigned, opcode) switch
        {
            (0, 0b01010) => (OpCode.ShiftLeft, places - bits),
            (_, 0b00000) => (OpCode.ShiftRight, 2 * bits - places),
            _ => (OpCode.Invalid, 0),
        };

        if (operation == OpCode.Invalid)
            return false;

        var lanes = LanesIn(elementWidth, (int)(word >> 30 & 1));

        //Shifting an answer's lowest bit into the sign says nothing new about it.
        if (masks.Contains(source))
            return Emit(destination, elementWidth, lanes, OpCode.Move, true, emit, [(source, null)]);

        var read = new List<object>();

        for (var lane = 0; lane < lanes; lane++)
        {
            if (!Ensure(source, lane * elementWidth, elementWidth, emit))
                return false;

            read.Add(Lane(source, lane * elementWidth));
        }

        Forget(destination);

        for (var lane = 0; lane < lanes; lane++)
            emit(operation, [Lane(destination, lane * elementWidth), read[lane], by]);

        Note(destination, elementWidth, lanes, false, emit);
        return true;
    }

    /// <summary>
    /// Writes each lane of a packed register to its own place, where a single store would write only the first.
    /// </summary>
    /// <remarks>
    /// <c>stur d0, [x19, #0x34]</c> is eight bytes, and when the model knows the register holds two four-byte
    /// lanes those eight bytes are the two fields of a vector, four bytes apart - which is exactly the shape
    /// the field recovery already reads a <c>stp s0, s1</c> as. Without this the lanes above the first are
    /// computed and then dropped, and the assignment keeps only one of its components.
    /// </remarks>
    private bool Store(Arm64Instruction instruction, Action<OpCode, object[]> emit)
    {
        if (instruction.Mnemonic is not (Arm64Mnemonic.STR or Arm64Mnemonic.STUR)
            || instruction.Op1Kind != Arm64OperandKind.Memory
            || instruction.MemBase == Arm64Register.INVALID
            || instruction.MemAddendReg != Arm64Register.INVALID
            || instruction.MemIsPreIndexed)
            return false;

        if (VectorNumber(instruction.Op0Reg) is not { } stored || !width.TryGetValue(stored, out var elementWidth))
            return false;

        if (GeneralNumber(instruction.MemBase) is not { } baseRegister)
            return false;

        var lanes = BytesOf(instruction.Op0Reg) / elementWidth;

        if (lanes < 2 || !Ensure(stored, (lanes - 1) * elementWidth, elementWidth, emit))
            return false;

        for (var lane = 0; lane < lanes; lane++)
        {
            //Through `Place`, so that register 31 is the frame rather than an address: a `Vector4` built up
            //in the frame and then passed is four lane stores, and writing them as memory nothing can name
            //left the slot the call reads with no writes reaching it at all.
            emit(OpCode.Move,
            [
                Place(baseRegister, instruction.MemOffset + (long)lane * elementWidth),
                Lane(stored, lane * elementWidth),
            ]);
        }

        return true;
    }

    /// <summary>
    /// Remembers a whole-register load instead of forgetting the register, so it can be split at the width
    /// whatever reads it turns out to want.
    /// </summary>
    private bool Load(uint word, Arm64Instruction instruction)
    {
        if (instruction.Mnemonic is not (Arm64Mnemonic.LDR or Arm64Mnemonic.LDUR)
            || (word >> 27 & 0x7) != 0b111 || (word >> 26 & 1) != 1)
            return false;

        //Both forms of the offset, because a `Vector2` spilled to the stack and read back is the unscaled one
        //and it is everywhere. Scaled: size 111 1 01 opc imm12 Rn Rt, the offset in units of the width it
        //loads. Unscaled: size 111 1 00 opc 0 imm9 00 Rn Rt, the offset in bytes and signed.
        var bytes = ((int)(word >> 30 & 3), (int)(word >> 22 & 3)) switch
        {
            (0b00, 0b11) => 16,
            (0b11, 0b01) => 8,
            _ => 0,
        };

        if (bytes == 0)
            return false;

        long offset;

        switch (word >> 24 & 3)
        {
            case 0b01:
                offset = (long)(word >> 10 & 0xFFF) * bytes;
                break;
            case 0b00 when (word >> 21 & 1) == 0 && (word >> 10 & 3) == 0:
                var unscaled = (int)(word >> 12 & 0x1FF);
                offset = unscaled >= 0x100 ? unscaled - 0x200 : unscaled;
                break;
            default:
                return false;
        }

        var loaded = (int)(word & 0x1F);

        Forget(loaded);
        pending[loaded] = ((int)(word >> 5 & 0x1F), offset, bytes);
        return true;
    }

    /// <summary>
    /// The same for a load pair, which fills two whole registers from adjacent addresses.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A hundred and twenty-eight bit load pair is how two adjacent statics or two adjacent fields reach the
    /// vector registers at once - <c>Color.Lerp(a, b, t)</c> opens with <c>ldp q2, q3, [x8, #0x10]</c> - and
    /// recording nothing for it left every lane of both registers unknown. Everything downstream then refused:
    /// <b>1550 refusals across 235 methods</b> are instructions this file can already decode and declines only
    /// because no lane of a source was ever written. Worse than a refusal in places, since the first lane is
    /// still lifted the ordinary way and reads a stale register.
    /// </para>
    /// <para>
    /// Only the plain signed-offset form. Pre- and post-index write the address back into the base register,
    /// which this would have to model, and there is no need to guess at it.
    /// </para>
    /// </remarks>
    private bool LoadPair(uint word, Arm64Instruction instruction)
    {
        if (instruction.Mnemonic != Arm64Mnemonic.LDP)
            return false;

        //opc 101 V 010 L imm7 Rt2 Rn Rt - V says the registers are vector ones, 010 the signed-offset form.
        if ((word >> 27 & 0x7) != 0b101 || (word >> 26 & 1) != 1
            || (word >> 23 & 0x7) != 0b010 || (word >> 22 & 1) != 1)
        {
            return false;
        }

        //The width each register takes, which is also what the immediate counts in.
        var bytes = (word >> 30 & 3) switch
        {
            0b00u => 4,
            0b01u => 8,
            0b10u => 16,
            _ => 0,
        };

        if (bytes == 0)
            return false;

        var imm7 = (int)(word >> 15 & 0x7F);
        var offset = (imm7 >= 0x40 ? imm7 - 0x80 : imm7) * (long)bytes;

        var baseRegister = (int)(word >> 5 & 0x1F);
        var first = (int)(word & 0x1F);
        var second = (int)(word >> 10 & 0x1F);

        Forget(first);
        pending[first] = (baseRegister, offset, bytes);

        Forget(second);
        pending[second] = (baseRegister, offset + bytes, bytes);

        return true;
    }

    /// <summary>
    /// Does what an instruction with a vector arrangement does to every lane above the first, leaving the
    /// first to the ordinary lifting.
    /// </summary>
    private bool Replicate(Arm64Instruction instruction, Action<OpCode, object[]> emit)
    {
        if (OperationOf(instruction.Mnemonic) is not { } operation)
            return false;

        if (LanesIn(instruction.Op0Arrangement) is not { } lanes || lanes < 2)
            return false;

        if (VectorNumber(instruction.Op0Reg) is not { } destination)
            return false;

        var elementWidth = WidthIn(instruction.Op0Arrangement);
        var sources = new List<(int Register, int? Fixed)>();

        foreach (var (register, arrangement, element) in new[]
                 {
                     (instruction.Op1Reg, instruction.Op1Arrangement, instruction.Op1VectorElement),
                     (instruction.Op2Reg, instruction.Op2Arrangement, instruction.Op2VectorElement),
                 })
        {
            if (register == Arm64Register.INVALID)
                break;

            if (VectorNumber(register) is not { } number)
                return false;

            //`fmul v0.2s, v1.2s, v2.s[0]` scales a whole vector by one lane of another, so that operand is
            //the same value in every lane rather than one lane apiece.
            sources.Add(arrangement == Arm64ArrangementSpecifier.None ? (number, (int)element.Index) : (number, null));
        }

        if (sources.Count == 0)
            return false;

        //Lane zero is left to the ordinary lifting, which already gets it right, so only what is above it is
        //emitted here - and the model still says it followed the instruction, or the register it writes would
        //be forgotten while its lanes were being kept.
        var readable = Readable(1, lanes, elementWidth, sources.ToArray(), emit);
        var lifted = new List<object[]>();

        for (var lane = 1; lane < readable; lane++)
        {
            var operands = new List<object> { Lane(destination, lane * elementWidth) };

            foreach (var (register, fixedLane) in sources)
                operands.Add(Lane(register, (fixedLane ?? lane) * elementWidth));

            lifted.Add(operands.ToArray());
        }

        Forget(destination);

        foreach (var operands in lifted)
            emit(operation, operands);

        //Only where every lane was followed. The instruction wrote all of them whatever this managed to
        //emit, so saying the rest are zero would be saying something false about lanes that hold a result -
        //and a lane wrongly believed to be zero is read as one by the next instruction along.
        if (readable == lanes)
            Note(destination, elementWidth, lanes, false);

        return true;
    }

    /// <summary>
    /// Forgets the lanes of a register something outside this model has written.
    /// </summary>
    /// <remarks>
    /// Writing the low part of a vector register zeroes the rest of it, so after any instruction this does not
    /// follow, what was known about that register's upper lanes is no longer true.
    /// </remarks>
    private void Invalidate(Arm64Instruction instruction, uint? word)
    {
        //A call leaves the first eight vector registers holding whatever the callee left in them. The rest are
        //preserved, which is why an accumulator is kept in one - and the model has to know the difference, or
        //it either believes a clobbered register or throws away a kept one.
        if (instruction.Mnemonic is Arm64Mnemonic.BL or Arm64Mnemonic.BLR)
        {
            for (var argument = 0; argument < 8; argument++)
                Forget(argument);

            return;
        }

        //An instruction the disassembler could not place carries no operands at all, so there is nothing to
        //say which register it wrote - and keeping what was known about a register something has since
        //overwritten is how a stale lane becomes a plausible wrong number. Every instruction that writes a
        //vector register names it in the lowest five bits, so for those that is the one to forget. Only for
        //those: an instruction it *did* decode says which register it writes, and a branch says none at all -
        //taking the low bits of one of those forgets a register chosen out of a branch offset.
        if (instruction.Mnemonic is Arm64Mnemonic.INVALID or Arm64Mnemonic.UNIMPLEMENTED && word is { } undecoded)
            Forget((int)(undecoded & 0x1F));

        if (VectorNumber(instruction.Op0Reg) is not { } destination)
            return;

        Forget(destination);

        //Named one by one rather than by exclusion: getting this wrong the other way says a register holds
        //zeros when it holds a value, which reads as a plausible number and compiles.
        if (WritesScalar(instruction.Mnemonic) && BytesOf(instruction.Op0Reg) is > 0 and < 16)
            zeroAbove[destination] = BytesOf(instruction.Op0Reg);
    }

    private static bool WritesScalar(Arm64Mnemonic mnemonic) => mnemonic
        is Arm64Mnemonic.FMOV or Arm64Mnemonic.FADD or Arm64Mnemonic.FSUB or Arm64Mnemonic.FMUL
        or Arm64Mnemonic.FDIV or Arm64Mnemonic.FNEG or Arm64Mnemonic.FABS or Arm64Mnemonic.FSQRT
        or Arm64Mnemonic.FMAXNM or Arm64Mnemonic.FMINNM or Arm64Mnemonic.FNMUL or Arm64Mnemonic.FABD
        or Arm64Mnemonic.FCSEL or Arm64Mnemonic.FCVT or Arm64Mnemonic.SCVTF or Arm64Mnemonic.UCVTF
        or Arm64Mnemonic.FCVTZS or Arm64Mnemonic.FCVTZU or Arm64Mnemonic.LDR or Arm64Mnemonic.LDUR
        or Arm64Mnemonic.MOVI;

    /// <summary>
    /// Emits one operation per lane, and only if every lane it reads is known - half a vectorised expression
    /// is not half an answer.
    /// </summary>
    private bool Emit(int destination, int elementWidth, int lanes, OpCode operation, bool answers,
        Action<OpCode, object[]> emit, (int Register, int? Fixed)[] sources)
    {
        if (Readable(0, lanes, elementWidth, sources, emit) < lanes)
            return false;

        //Read before the destination is forgotten, because it is very often a source as well.
        var lifted = new List<object[]>();

        for (var lane = 0; lane < lanes; lane++)
        {
            var operands = new List<object> { Lane(destination, lane * elementWidth) };

            foreach (var (register, fixedLane) in sources)
                operands.Add(Lane(register, (fixedLane ?? lane) * elementWidth));

            lifted.Add(operands.ToArray());
        }

        Forget(destination);

        foreach (var operands in lifted)
            emit(operation, operands);

        Note(destination, elementWidth, lanes, answers, emit);
        return true;
    }

    /// <summary>
    /// Calls a library method once per lane, which is what a rounding or an absolute value over a vector is.
    /// </summary>
    private bool EmitCall(int destination, int source, int elementWidth, int lanes, MethodAnalysisContext method,
        Action<OpCode, object[]> emit)
    {
        if (Readable(0, lanes, elementWidth, [(source, null)], emit) < lanes)
            return false;

        var read = new List<object>();

        for (var lane = 0; lane < lanes; lane++)
            read.Add(Lane(source, lane * elementWidth));

        Forget(destination);

        for (var lane = 0; lane < lanes; lane++)
            emit(OpCode.Call, [method, Lane(destination, lane * elementWidth), read[lane]]);

        Note(destination, elementWidth, lanes, false, emit);
        return true;
    }

    private bool EmitAgainstZero(int destination, int source, int elementWidth, int lanes, OpCode operation,
        Action<OpCode, object[]> emit)
    {
        if (Readable(0, lanes, elementWidth, [(source, null)], emit) < lanes)
            return false;

        var read = new List<object>();

        for (var lane = 0; lane < lanes; lane++)
            read.Add(Lane(source, lane * elementWidth));

        Forget(destination);

        for (var lane = 0; lane < lanes; lane++)
            emit(operation, [Lane(destination, lane * elementWidth), read[lane], 0]);

        Note(destination, elementWidth, lanes, true, emit);
        return true;
    }

    /// <summary>
    /// Halves the width of each lane, which over lanes holding an answer changes nothing but where they sit.
    /// </summary>
    /// <remarks>
    /// A narrowing reads at twice the width it writes, so the two sides step differently - and it is very
    /// often <c>xtn v3.4h, v3.4s</c>, one register doing both, so what it reads has to be taken before what it
    /// knows is discarded.
    /// </remarks>
    private bool Narrow(int destination, int source, int elementWidth, int lanes, bool upperHalf,
        Action<OpCode, object[]> emit)
    {
        var read = new List<object>();

        for (var lane = 0; lane < lanes; lane++)
        {
            if (!Ensure(source, lane * elementWidth * 2, elementWidth * 2, emit))
                return false;

            read.Add(Lane(source, lane * elementWidth * 2));
        }

        var first = upperHalf ? lanes : 0;

        if (!upperHalf)
            Forget(destination);

        for (var lane = 0; lane < lanes; lane++)
        {
            emit(OpCode.Move, [Lane(destination, (first + lane) * elementWidth), read[lane]]);
            Write(destination, (first + lane) * elementWidth, elementWidth, true);
        }

        masks.Add(destination);
        return true;
    }

    /// <summary>Moves a run of lanes into the top half of a register, leaving the bottom half where it is.</summary>
    private bool EmitFrom(int destination, int firstDestinationLane, int source, int firstSourceLane,
        int destinationWidth, int sourceWidth, int count, Action<OpCode, object[]> emit)
    {
        for (var lane = 0; lane < count; lane++)
        {
            if (!Ensure(source, (firstSourceLane + lane) * sourceWidth, sourceWidth, emit))
                return false;
        }

        for (var lane = 0; lane < count; lane++)
        {
            emit(OpCode.Move,
            [
                Lane(destination, (firstDestinationLane + lane) * destinationWidth),
                Lane(source, (firstSourceLane + lane) * sourceWidth),
            ]);

            Write(destination, (firstDestinationLane + lane) * destinationWidth, destinationWidth, true);
        }

        return true;
    }

    /// <summary>Moves lanes about between two registers according to where each one comes from.</summary>
    private bool Shuffle(int destination, int left, int right, int elementWidth, int lanes, int[] pick,
        Action<OpCode, object[]> emit)
    {
        var read = new List<object>();

        foreach (var from in pick)
        {
            var register = from < lanes ? left : right;
            var lane = (from < lanes ? from : from - lanes) * elementWidth;

            if (!Ensure(register, lane, elementWidth, emit))
                return false;

            read.Add(Lane(register, lane));
        }

        var answers = masks.Contains(left) && masks.Contains(right);
        Forget(destination);

        for (var lane = 0; lane < lanes; lane++)
            emit(OpCode.Move, [Lane(destination, lane * elementWidth), read[lane]]);

        Note(destination, elementWidth, lanes, answers, emit);
        return true;
    }

    /// <summary>
    /// How far up the lanes every source is known, splitting a whole-register load on the way if one is
    /// waiting to find out how wide its lanes are.
    /// </summary>
    private int Readable(int from, int lanes, int elementWidth, (int Register, int? Fixed)[] sources,
        Action<OpCode, object[]> emit)
    {
        var usable = from;

        while (usable < lanes)
        {
            var readable = true;

            foreach (var (register, fixedLane) in sources)
                readable &= Ensure(register, (fixedLane ?? usable) * elementWidth, elementWidth, emit);

            if (!readable)
                break;

            usable++;
        }

        return usable;
    }

    /// <summary>
    /// Makes a lane readable if it can be, and says whether it now is.
    /// </summary>
    /// <remarks>
    /// Three ways a lane nothing has written is still known: it is part of a whole-register load waiting to
    /// find out how wide its lanes are, it is above where the register stops holding anything and so is zero,
    /// or it is the low lane, which is the register itself. Anything else is genuinely unknown, and the
    /// instruction reading it has to be left alone.
    /// </remarks>
    private bool Ensure(int register, int offset, int elementWidth, Action<OpCode, object[]> emit)
    {
        if (Has(register, offset))
            return true;

        if (pending.ContainsKey(register))
            Split(register, elementWidth, offset / elementWidth + 1, emit);

        if (Has(register, offset))
            return true;

        if (!zeroAbove.TryGetValue(register, out var above) || offset < above)
            return false;

        emit(OpCode.Move, [Lane(register, offset), 0]);
        written.Add((register, offset));
        return true;
    }

    /// <summary>Splits a whole-register load into lanes, now that something has said how wide they are.</summary>
    private void Split(int register, int elementWidth, int lanes, Action<OpCode, object[]> emit)
    {
        if (!pending.TryGetValue(register, out var load) || lanes * elementWidth > load.Bytes)
            return;

        pending.Remove(register);

        for (var lane = 0; lane < lanes; lane++)
            emit(OpCode.Move, [Lane(register, lane * elementWidth), Place(load.Base, load.Offset + (long)lane * elementWidth)]);

        Note(register, elementWidth, lanes, false);
    }

    /// <summary>
    /// Where a value lives, as the rest of the lifting names it.
    /// </summary>
    /// <remarks>
    /// Register 31 as a base is the stack pointer, and a stack slot is modelled as a variable rather than as
    /// an address - so a lane of a spilled vector has to be named the same way, or it is a load from somewhere
    /// nothing can say anything about and the statement reading it is lost.
    /// </remarks>
    private static object Place(int baseRegister, long offset)
        => baseRegister == 31 ? new StackOffset((int)offset) : new MemoryOperand(General(baseRegister), null, offset);

    private void Note(int register, int elementWidth, int lanes, bool answers, Action<OpCode, object[]>? emit = null)
    {
        width[register] = elementWidth;

        for (var lane = 1; lane < lanes; lane++)
            written.Add((register, lane * elementWidth));

        //Everything the instruction did not write is zero, which is what makes a pair computed in `.2s`
        //readable by the `.4s` that compares it.
        zeroAbove[register] = elementWidth * lanes;

        if (answers)
            masks.Add(register);
        else
            masks.Remove(register);
    }

    /// <summary>Writes one lane, leaving the rest of the register as it was.</summary>
    private void Write(int register, int offset, int elementWidth, bool answers)
    {
        width[register] = elementWidth;

        if (offset != 0)
            written.Add((register, offset));

        //A single lane displaces the zeros only as far as it reaches.
        if (zeroAbove.TryGetValue(register, out var above) && above < offset + elementWidth)
            zeroAbove[register] = offset + elementWidth;

        if (!answers)
            masks.Remove(register);
    }

    private void Forget(int register)
    {
        width.Remove(register);
        masks.Remove(register);
        pending.Remove(register);
        zeroAbove.Remove(register);

        for (var offset = 1; offset < 16; offset++)
            written.Remove((register, offset));
    }

    private bool Has(int register, int offset) => offset == 0 || written.Contains((register, offset));

    /// <summary>
    /// A lane, named by where it starts rather than by its index: the same eight bytes are lane 1 of a pair of
    /// doubles and lanes 2 and 3 of four floats, and one instruction writes them under one name while the next
    /// reads them under the others.
    /// </summary>
    private static Register Lane(int register, int offset)
        => new(null, offset == 0 ? $"V{register}" : $"V{register}#{offset}");

    /// <summary>
    /// The general register a lane is copied to or from, under the one name per physical register the rest of
    /// the lifting uses.
    /// </summary>
    private static Register General(int register) => new(null, $"X{register}");

    private static int[] Enumerate(int count, Func<int, int> of)
    {
        var picked = new int[count];

        for (var i = 0; i < count; i++)
            picked[i] = of(i);

        return picked;
    }

    /// <summary>The width an <c>imm5</c> names, and the lane it selects - the lowest set bit is the width.</summary>
    private static (int Width, int Index)? Element(uint imm5)
    {
        for (var bit = 0; bit < 4; bit++)
        {
            if ((imm5 & 1 << bit) != 0)
                return (1 << bit, (int)(imm5 >> (bit + 1)));
        }

        return null;
    }

    private static int Shift(int elementWidth) => elementWidth switch { 1 => 0, 2 => 1, 4 => 2, _ => 3 };

    private static int LanesIn(int elementWidth, int q) => (q == 1 ? 16 : 8) / elementWidth;

    private static int? LanesIn(Arm64ArrangementSpecifier arrangement) => arrangement switch
    {
        //Only the arrangements whose lanes are numbers a managed program computes with. The byte
        //arrangements are how the architecture writes an operation on the whole register, not eight or
        //sixteen values, and splitting them would invent lanes nothing reads.
        Arm64ArrangementSpecifier.TwoS => 2,
        Arm64ArrangementSpecifier.FourS => 4,
        Arm64ArrangementSpecifier.TwoD => 2,
        Arm64ArrangementSpecifier.FourH => 4,
        Arm64ArrangementSpecifier.EightH => 8,
        _ => null,
    };

    private static int WidthIn(Arm64ArrangementSpecifier arrangement) => arrangement switch
    {
        Arm64ArrangementSpecifier.TwoD => 8,
        Arm64ArrangementSpecifier.FourH or Arm64ArrangementSpecifier.EightH => 2,
        _ => 4,
    };

    private static int BytesOf(Arm64Register register) => register switch
    {
        >= Arm64Register.V0 and <= Arm64Register.V31 => 16,
        >= Arm64Register.D0 and <= Arm64Register.D31 => 8,
        >= Arm64Register.S0 and <= Arm64Register.S31 => 4,
        _ => 0,
    };

    private static int? VectorNumber(Arm64Register register) => register switch
    {
        >= Arm64Register.V0 and <= Arm64Register.V31 => register - Arm64Register.V0,
        >= Arm64Register.D0 and <= Arm64Register.D31 => register - Arm64Register.D0,
        >= Arm64Register.S0 and <= Arm64Register.S31 => register - Arm64Register.S0,
        >= Arm64Register.H0 and <= Arm64Register.H31 => register - Arm64Register.H0,
        >= Arm64Register.B0 and <= Arm64Register.B31 => register - Arm64Register.B0,
        _ => null,
    };

    private static int? GeneralNumber(Arm64Register register) => register switch
    {
        >= Arm64Register.X0 and <= Arm64Register.X31 => register - Arm64Register.X0,
        >= Arm64Register.W0 and <= Arm64Register.W31 => register - Arm64Register.W0,
        _ => null,
    };

    /// <summary>
    /// What one lane of an arrangement-carrying instruction the disassembler did decode does, where ISIL has a
    /// word for it.
    /// </summary>
    private static OpCode? OperationOf(Arm64Mnemonic mnemonic) => mnemonic switch
    {
        Arm64Mnemonic.FADD or Arm64Mnemonic.ADD => OpCode.Add,
        Arm64Mnemonic.FSUB or Arm64Mnemonic.SUB => OpCode.Subtract,
        Arm64Mnemonic.FMUL or Arm64Mnemonic.MUL => OpCode.Multiply,
        Arm64Mnemonic.FDIV => OpCode.Divide,
        Arm64Mnemonic.FNEG or Arm64Mnemonic.NEG => OpCode.Negate,
        Arm64Mnemonic.SCVTF or Arm64Mnemonic.UCVTF or Arm64Mnemonic.FCVTZS or Arm64Mnemonic.FCVTZU
            or Arm64Mnemonic.FCVT => OpCode.Move,
        _ => null,
    };
}
