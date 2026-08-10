using System;
using System.Collections.Generic;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Reads the fields back out of a small struct a call answered with in one general register.
/// </summary>
/// <remarks>
/// <para>
/// AAPCS64 returns a struct of four bytes in <c>w0</c>, so <c>Color32</c> comes back as a number and every
/// field of it is a shift and a mask off that number. <see cref="PackedPairField"/> is this question the
/// other way round - building a struct out of two words - and this is the destructuring half:
/// </para>
/// <code>
/// Call       BoardSettingSO.GetColor, v71, this._settings, color   ; v71 is a Color32 in X0
/// ShiftRight v147, v71, 24                                          ; .a
/// ShiftRight v153, v71, 16 ; And v154, v153, 255                    ; .b
/// ShiftRight v157, v71, 8  ; And v158, v157, 255                    ; .g
/// And        v161, v71, 255                                         ; .r
/// </code>
/// <para>
/// Every one of those is invalid as written - the register carries a struct, and C# will not shift one -
/// so <c>SubCell::SetColor</c> lost its whole body to the first statement, which was the assignment of the
/// call's answer into a <c>long</c>. What the source said is <c>bgImage.color = _settings.GetColor(color)</c>,
/// and the arithmetic under it is <c>Color32</c>'s conversion to <c>Color</c>, inlined: four bytes, each over
/// 255.
/// </para>
/// <para>
/// The result is retyped as what the callee returns and each extraction becomes the field it takes out. A
/// mask decides the width, and the top field needs none - there is nothing above it to mask off - so a shift
/// that reaches exactly the end of the struct is accepted on its own.
/// </para>
/// <para>
/// Only where the answer is used this way. A struct that is stored whole, or passed on, is already right, and
/// retyping it there would say the same thing twice.
/// </para>
/// </remarks>
public static class PackedStructFieldRead
{
    public static void Run(MethodAnalysisContext method)
    {
        if (method.ControlFlowGraph is not { } graph)
            return;

        var pointerSize = method.AppContext.Binary.is32Bit ? 4 : 8;

        //What each call of the right shape answered into, and the struct it answered with.
        Dictionary<LocalVariable, (TypeAnalysisContext Type, int Width)> packed = new();

        foreach (var instruction in graph.Instructions)
        {
            if (instruction is not { OpCode: OpCode.Call, Operands: [MethodAnalysisContext callee, LocalVariable answer, ..] })
                continue;

            //The declared size, not the sum of the fields: `Color32` overlaps an `int` with four bytes, so
            //adding them up says eight and every question asked of it afterwards is asked of the wrong struct.
            if (callee.ReturnType is not { IsValueType: true, IsEnumType: false } returned
                || returned.Namespace == nameof(System)
                || Aapcs64.SizeOf(returned) is not { } width
                || width is < 2 or > 8)
            {
                continue;
            }

            packed[answer] = (returned, (int)width);
        }

        Trace(method, $"{packed.Count} packed answers");

        if (packed.Count == 0)
            return;

        //Which instruction reads each shift's answer, so a mask can be told apart from a use of the shift.
        Dictionary<LocalVariable, Instruction?> readers = new();

        foreach (var instruction in graph.Instructions)
        {
            for (var i = 1; i < instruction.Operands.Count; i++)
                if (instruction.Operands[i] is LocalVariable read)
                    readers[read] = readers.ContainsKey(read) ? null : instruction;
        }

        var taken = new HashSet<LocalVariable>();

        foreach (var instruction in graph.Instructions)
        {
            switch (instruction)
            {
                //The field at the front needs no shift at all, only the mask that cuts the rest away.
                case { OpCode: OpCode.And, Operands: [LocalVariable, LocalVariable whole, { } mask] }
                    when packed.ContainsKey(whole) && Width(mask) is { } front:
                    Take(instruction, whole, 0, front, packed[whole].Type, method, taken);
                    break;

                case { OpCode: OpCode.ShiftRight, Operands: [LocalVariable shifted, LocalVariable whole, { } by] }
                    when packed.TryGetValue(whole, out var held) && Constant(by) is { } places && places % 8 == 0:
                {
                    var offset = places / 8;

                    if (readers.GetValueOrDefault(shifted) is { OpCode: OpCode.And, Operands: [LocalVariable, _, { } mask] } masked
                        && Width(mask) is { } cut)
                    {
                        if (Take(masked, whole, offset, cut, held.Type, method, taken))
                        {
                            instruction.OpCode = OpCode.Nop;
                            instruction.Operands = [];
                        }

                        break;
                    }

                    //Nothing above the top field to mask off, so the shift alone is the read of it.
                    Take(instruction, whole, offset, held.Width - (int)offset, held.Type, method, taken);
                    break;
                }
            }
        }

        //Only once every extraction off it has been rewritten, or the ones left behind would be shifts of a
        //struct - which is what this is here to stop saying.
        foreach (var whole in taken)
            whole.Type = packed[whole].Type;
    }

    private static bool Take(Instruction reading, LocalVariable whole, long offset, int width,
        TypeAnalysisContext type, MethodAnalysisContext method, HashSet<LocalVariable> taken)
    {
        //By the width as well as the offset. `Color32` keeps an `int rgba` at nought overlapping `byte r`, and
        //the first field at an offset is not always the one the mask says was read.
        if (FieldOfWidth(type, offset, width, method) is not { } field)
        {
            var definition = (type as GenericInstanceTypeAnalysisContext)?.GenericType ?? type;
            var has = string.Join(", ", System.Linq.Enumerable.Select(definition.Fields,
                f => $"{f.Name}@{f.BackingData?.FieldOffset:X}:{f.FieldType.Name}{(f.IsStatic ? " static" : "")}"));

            Trace(method, $"no field of {type.Name} at +{offset} of {width} bytes; it has {has}");
            return false;
        }

        reading.OpCode = OpCode.Move;
        reading.Operands = [reading.Operands[0], new FieldReference(field, whole, (int)offset)];
        taken.Add(whole);
        return true;
    }

    /// <summary>Says what the pass saw, for one named method. Set PACKED_TRACE=1 and PACKED_METHOD.</summary>
    private static void Trace(MethodAnalysisContext method, string what)
    {
        if (System.Environment.GetEnvironmentVariable("PACKED_TRACE") != "1")
            return;

        var wanted = System.Environment.GetEnvironmentVariable("PACKED_METHOD");

        if (wanted == null || method.Name.Contains(wanted))
            System.Console.WriteLine($"PACKED {method.Name}: {what}");
    }

    /// <summary>The field of <paramref name="type"/> at that unboxed offset and of exactly that width.</summary>
    private static FieldAnalysisContext? FieldOfWidth(TypeAnalysisContext type, long offset, int width,
        MethodAnalysisContext method)
    {
        var header = method.AppContext.Binary.is32Bit ? 8 : 0x10;
        var pointerSize = method.AppContext.Binary.is32Bit ? 4 : 8;
        var definition = (type as GenericInstanceTypeAnalysisContext)?.GenericType ?? type;

        //Both conventions occur. il2cpp records a struct's fields at the offsets they sit at in a *boxed* one,
        //which is why `FieldOfStructValue` adds the header - but a type laid out explicitly keeps the offsets
        //the attributes gave it, and `Color32` is one: `rgba@0, r@0, g@1, b@2, a@3`. Asking only the boxed way
        //found no field at any of the four bytes the conversion reads.
        foreach (var at in (long[])[offset, offset + header])
        {
            foreach (var field in definition.Fields)
            {
                if (field.IsStatic || field.BackingData?.FieldOffset != at)
                    continue;

                //And by the width, which is what tells `r` from the `rgba` it overlaps.
                if (ArrayTypeInference.Width(field.FieldType, pointerSize) != width)
                    continue;

                return type is GenericInstanceTypeAnalysisContext instance
                    ? new ConcreteGenericFieldAnalysisContext(field, instance)
                    : field;
            }
        }

        return null;
    }

    /// <summary>How many bytes a mask keeps, where it keeps a whole number of them off the bottom.</summary>
    private static int? Width(object mask) => Constant(mask) switch
    {
        0xFF => 1,
        0xFFFF => 2,
        0xFFFFFFFF => 4,
        _ => null,
    };

    private static long? Constant(object operand)
    {
        try
        {
            return operand is string or LocalVariable or Register or MemoryOperand or FieldReference ? null : Convert.ToInt64(operand);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
