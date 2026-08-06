using System.Collections.Concurrent;
using System.Collections.Generic;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Disarm;
using Disarm.InternalDisassembly;
using LibCpp2IL.BinaryStructures;
using LibCpp2IL.Elf;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Gives back the field initialisers a constructor copies in as one block of constant data.
/// </summary>
/// <remarks>
/// <para>
/// Four <c>float</c> fields initialised to four different values are not four instructions. The compiler puts
/// the sixteen bytes in the constant pool and moves them in one go:
/// </para>
/// <code>
/// adrp x8, 0xD60000
/// ldr  q0, [x8, #0x930]     ; 0.2f, 0.2f, 0.15f, -110f
/// stur q0, [x19, #0x48]     ; four fields at once
/// </code>
/// <para>
/// The read is from the binary rather than from anything managed, so it resolved to nothing; the store then
/// resolved to the <i>first</i> of the four fields and assigned it the nothing. <c>CF.BoardController</c> lost
/// ten field defaults that way, and what it said instead - <c>HammerWindUpDuration = 0f</c> - looks like a
/// value rather than a failure.
/// </para>
/// <para>
/// Everything needed is present: the address is a constant by the time this runs, the width is the register
/// the load names, and which fields are covered is in the type's own layout. The one thing that has to be
/// checked is that the data cannot change - see <see cref="ElfFile.IsReadOnlyAddress"/>. A read from
/// <c>.data</c> would be an initial value rather than a constant, and stating it as a constant would state it
/// wrongly, which is worse than the placeholder it replaces.
/// </para>
/// <para>
/// The fields must tile the load exactly. Where they do not - a load wider than the fields below it, a gap, a
/// field of a width this does not read - nothing is written and the placeholder stands, because a blob spread
/// over the wrong fields is the failure this exists to remove.
/// </para>
/// </remarks>
public static class ConstantBlobStore
{
    /// <summary>
    /// How wide the load at each constant address was, by the register it landed in.
    /// </summary>
    /// <remarks>
    /// The width does not survive into ISIL - a Move says nothing about how many bytes moved - and it is the
    /// one thing that cannot be recovered from the fields, since the fields are what it decides. So it is
    /// taken at the point the address becomes a constant, which is the only place both are known. Keyed by
    /// address rather than by local because the local does not exist yet; where one address is read at two
    /// widths the narrower is kept, which can only write fewer fields than were loaded.
    /// </remarks>
    private static readonly ConcurrentDictionary<long, int> Widths = new();

    /// <summary>
    /// Records the width of a load whose address has just been folded to a constant, and gives back the
    /// operand that fold was going to produce anyway.
    /// </summary>
    public static MemoryOperand Note(Arm64Instruction instruction, long address)
    {
        if (WidthOf(instruction.Op0Reg) is var width and > 0)
            Widths.AddOrUpdate(address, width, (_, existing) => existing < width ? existing : width);

        return new MemoryOperand(addend: address);
    }

    public static void Run(MethodAnalysisContext method)
    {
        if (method.ControlFlowGraph is not { } graph || method.AppContext.Binary is not ElfFile elf)
            return;

        foreach (var block in graph.Blocks)
        {
            for (var i = 0; i < block.Instructions.Count; i++)
            {
                var instruction = block.Instructions[i];

                //The load and the store are one instruction by the time this runs - field recovery resolves
                //the store's address to the field and keeps the read as its source - so there is no local in
                //between to follow, and the constant address is right there on the store.
                if (instruction.OpCode != OpCode.Move || instruction.Operands.Count != 2
                    || instruction.Operands[0] is not FieldReference stored)
                    continue;

                var written = instruction.Operands[1] switch
                {
                    MemoryOperand { IsConstant: true, Addend: var address and > 0 } =>
                        Fields(elf, instruction, stored, address) ?? Structure(method, elf, instruction, stored, address),
                    double read => Pair(instruction, stored, read),
                    _ => null,
                };

                if (written is null)
                    continue;

                block.Instructions.RemoveAt(i);
                block.Instructions.InsertRange(i, written);
                i += written.Count - 1;
            }
        }
    }

    /// <summary>
    /// The stores the blob really was, or nothing where the fields below the one written do not account for
    /// exactly as many bytes as were loaded.
    /// </summary>
    private static List<Instruction>? Fields(ElfFile elf, Instruction store, FieldReference stored, long address)
    {
        if (!Widths.TryGetValue(address, out var width) || !elf.IsReadOnlyAddress((ulong)address, width))
            return null;

        //A load no wider than the field it lands in is one field, which is what already happens.
        if (width <= WidthOf(stored.Field.FieldType))
            return null;

        if (!elf.TryMapVirtualAddressToRaw((ulong)address, out var raw) || raw <= 0)
            return null;

        var content = elf.GetRawBinaryContent();

        if ((long)raw + width > content.Length)
            return null;

        var blob = content.Slice((int)raw, width);
        var declaring = stored.Field.DeclaringType;
        var written = new List<Instruction>(4);

        for (var covered = 0; covered < width;)
        {
            if (Field(declaring, stored.Field.Offset + covered) is not { } field)
                return null;

            var size = WidthOf(field.FieldType);

            if (size == 0 || covered + size > width)
                return null;

            written.Add(new Instruction(store.Index, OpCode.Move,
                new FieldReference(field, stored.Local, stored.Offset + covered),
                Value(field.FieldType, blob.Slice(covered, size))));

            covered += size;
        }

        return written;
    }

    /// <summary>
    /// The construction a blob assigned to a field of struct type really was.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A <c>Color</c> is four floats, which is the same sixteen bytes and the same one instruction:
    /// <c>CFramework.ColorExtension</c> declares a hundred and forty of them and the compiler folded every
    /// <c>new Color32(240, 248, 255, 255)</c> into the four floats it comes out as. The store resolves to the
    /// field, the read resolves to nothing, and what is left says <c>AliceBlue = (Color)0</c>.
    /// </para>
    /// <para>
    /// Unlike the plain fields above, the value cannot simply be written - a struct is not a number, and ISIL
    /// has no way to say "these bytes are that struct". What it does have is an allocation followed by a call
    /// to a constructor, which <see cref="IlGenerator"/> fuses back into a single <c>newobj</c>. So the blob is
    /// given back as the constructor call the source wrote, which for a value type is exactly what the
    /// compiler had to have started from.
    /// </para>
    /// <para>
    /// Only where the struct is entirely <c>float</c>, its fields tile the load from zero with no padding, and
    /// it declares a constructor taking one <c>float</c> per field. A struct with a gap, a struct of mixed
    /// widths, or one whose constructor takes something else is left as it was: a blob spread over the wrong
    /// arguments would be a wrong colour stated confidently, which is worse than the placeholder.
    /// </para>
    /// </remarks>
    private static List<Instruction>? Structure(MethodAnalysisContext method, ElfFile elf, Instruction store,
        FieldReference stored, long address)
    {
        if (!Widths.TryGetValue(address, out var width) || !elf.IsReadOnlyAddress((ulong)address, width))
            return null;

        var type = stored.Field.FieldType;
        var parts = new List<FieldAnalysisContext>();

        foreach (var candidate in type.Fields)
        {
            if (candidate.IsStatic)
                continue;

            //Anything that is not a float, and the struct is not one this can state.
            if (candidate.FieldType.Type != Il2CppTypeEnum.IL2CPP_TYPE_R4)
                return null;

            parts.Add(candidate);
        }

        //Tiling the load exactly, from its start, in the order the fields are declared in. A struct that does
        //not is either padded or not the whole of what was loaded.
        if (parts.Count == 0 || parts.Count * 4 != width)
            return null;

        for (var part = 0; part < parts.Count; part++)
        {
            if (parts[part].Offset != part * 4)
                return null;
        }

        if (Constructor(type, parts.Count) is not { } constructor
            || !elf.TryMapVirtualAddressToRaw((ulong)address, out var raw) || raw <= 0)
            return null;

        var content = elf.GetRawBinaryContent();

        if ((long)raw + width > content.Length)
            return null;

        var blob = content.Slice((int)raw, width);
        var arguments = new object[parts.Count + 2];
        var built = new LocalVariable($"blob{method.Locals.Count}", new Register(null, "BLOB"), type);

        method.Locals.Add(built);
        arguments[0] = constructor;
        arguments[1] = built;

        for (var part = 0; part < parts.Count; part++)
            arguments[part + 2] = System.BitConverter.ToSingle(blob.Slice(part * 4, 4));

        return
        [
            //Two operands, because an allocation names what it allocated: upstream builds this from an
            //`object_new` whose second operand is the runtime class, and reads a type back out of it. Here the
            //type is already on the local, so the type itself stands in - it says the same thing, and the
            //reader that would derive one from a class pointer declines it and leaves the local's own alone.
            new Instruction(store.Index, OpCode.Newobj, built, type),
            new Instruction(store.Index, OpCode.CallVoid, arguments),
            new Instruction(store.Index, OpCode.Move, stored, built),
        ];
    }

    /// <summary>The constructor taking one <c>float</c> per field, if the struct declares one.</summary>
    private static MethodAnalysisContext? Constructor(TypeAnalysisContext type, int parts)
    {
        foreach (var candidate in type.Methods)
        {
            if (candidate.IsStatic || candidate.Name != ".ctor" || candidate.Parameters.Count != parts)
                continue;

            var takesFloats = true;

            foreach (var parameter in candidate.Parameters)
                takesFloats &= parameter.ParameterType.Type == Il2CppTypeEnum.IL2CPP_TYPE_R4;

            if (takesFloats)
                return candidate;
        }

        return null;
    }

    /// <summary>
    /// The two <c>float</c> fields a <c>double</c> constant really was, where it is being assigned to one of
    /// them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two adjacent <c>float</c> fields are eight bytes, and eight bytes come out of the constant pool in a
    /// <c>d</c> register: <c>ldr d2, [x8, #0x668]</c> then <c>str d2, [x19, #0x68]</c>. Read as one number
    /// that is a <c>double</c>, and the lifter says so, because at the point it reads the pool it knows the
    /// width of the register and nothing about where the value is going. So <c>HammerShakeAngle</c>, whose
    /// value is <c>4</c>, was recovered as <c>3.1664965E-10</c> - which is <c>4f</c> and the <c>0.12f</c>
    /// beside it, read together as a double.
    /// </para>
    /// <para>
    /// This needs no address and no section check, because the constant carries the evidence itself, exactly
    /// as in <see cref="WideFieldStore"/>: <b>a four byte field cannot be assigned an eight byte number.</b>
    /// Where the field is a <c>float</c> and the value arrived as a <c>double</c>, the two halves of its bits
    /// are the two fields, and requiring a <c>float</c> field to exist at the next offset makes that the only
    /// consistent reading.
    /// </para>
    /// </remarks>
    private static List<Instruction>? Pair(Instruction store, FieldReference stored, double read)
    {
        if (WidthOf(stored.Field.FieldType) != 4 || stored.Field.FieldType.Type != Il2CppTypeEnum.IL2CPP_TYPE_R4)
            return null;

        if (Field(stored.Field.DeclaringType, stored.Field.Offset + 4) is not { } above
            || above.FieldType.Type != Il2CppTypeEnum.IL2CPP_TYPE_R4)
            return null;

        var bits = System.BitConverter.DoubleToInt64Bits(read);

        return
        [
            new Instruction(store.Index, OpCode.Move, stored, System.BitConverter.Int32BitsToSingle((int)bits)),
            new Instruction(store.Index, OpCode.Move,
                new FieldReference(above, stored.Local, stored.Offset + 4),
                System.BitConverter.Int32BitsToSingle((int)(bits >> 32))),
        ];
    }

    /// <summary>The instance field beginning at an offset, if the type has one there.</summary>
    private static FieldAnalysisContext? Field(TypeAnalysisContext declaring, int offset)
    {
        foreach (var candidate in declaring.Fields)
        {
            if (!candidate.IsStatic && candidate.Offset == offset)
                return candidate;
        }

        return null;
    }

    /// <summary>
    /// The constant those bytes are, as the type of the field they are going into.
    /// </summary>
    /// <remarks>
    /// Boxed as the exact type rather than as a common one: a <c>float</c> handed over as a <c>double</c> is
    /// not the same constant, and an integer field assigned a float is a cast the source never wrote.
    /// </remarks>
    private static object Value(TypeAnalysisContext type, System.ReadOnlySpan<byte> bytes) => type.Type switch
    {
        Il2CppTypeEnum.IL2CPP_TYPE_R4 => System.BitConverter.ToSingle(bytes),
        Il2CppTypeEnum.IL2CPP_TYPE_R8 => System.BitConverter.ToDouble(bytes),
        Il2CppTypeEnum.IL2CPP_TYPE_U4 => System.BitConverter.ToUInt32(bytes),
        Il2CppTypeEnum.IL2CPP_TYPE_I8 => System.BitConverter.ToInt64(bytes),
        Il2CppTypeEnum.IL2CPP_TYPE_U8 => System.BitConverter.ToUInt64(bytes),
        _ => System.BitConverter.ToInt32(bytes),
    };

    /// <summary>
    /// How many bytes a field of this type occupies, for the widths a blob can be made of.
    /// </summary>
    /// <remarks>
    /// Anything else - a reference, a struct, an enum whose underlying type is not known here - returns zero,
    /// which stops the walk and leaves the store alone. That is the right answer: a blob whose contents are
    /// not plain numbers is not one this can state.
    /// </remarks>
    private static int WidthOf(TypeAnalysisContext type) => type.Type switch
    {
        Il2CppTypeEnum.IL2CPP_TYPE_I4 or Il2CppTypeEnum.IL2CPP_TYPE_U4 or Il2CppTypeEnum.IL2CPP_TYPE_R4 => 4,
        Il2CppTypeEnum.IL2CPP_TYPE_I8 or Il2CppTypeEnum.IL2CPP_TYPE_U8 or Il2CppTypeEnum.IL2CPP_TYPE_R8 => 8,
        _ => 0,
    };

    /// <summary>How many bytes a load into this register moves.</summary>
    private static int WidthOf(Arm64Register destination) => destination switch
    {
        >= Arm64Register.V0 and <= Arm64Register.V31 => 16,
        >= Arm64Register.D0 and <= Arm64Register.D31 => 8,
        >= Arm64Register.X0 and <= Arm64Register.X31 => 8,
        _ => 0,
    };
}
