using System.Collections.Generic;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Gives a value the array type it is being used as.
///
/// Indexing an array compiles to the array plus the object header plus the index scaled by the width of an
/// element, and none of that says what is being indexed - so the array itself often reaches the read with no
/// type at all, or with the type of the offset it was added to. Everything downstream then reads as unmanaged
/// memory: the loop's condition, every element, and whatever the elements were passed to.
///
/// What the element turned out to be is the answer. A value reached through the header offset of an array,
/// with an index scaled by how wide that element is, is an array of it - there is no other shape that reads
/// like this, so the type is a conclusion rather than a guess.
/// </summary>
public static class ArrayTypeInference
{
    public static bool Run(MethodAnalysisContext method)
    {
        var elements = method.AppContext.Binary.is32Bit ? 0x10 : 0x20;
        var pointerSize = method.AppContext.Binary.is32Bit ? 4 : 8;
        var definitions = new Dictionary<LocalVariable, Instruction>();

        foreach (var instruction in method.ControlFlowGraph!.Instructions)
            if (instruction.Destination is LocalVariable destination)
                definitions[destination] = instruction;

        var changed = false;

        foreach (var instruction in method.ControlFlowGraph.Instructions)
        {
            //An element read produces its type; an element store consumes it. Either says what the array is.
            if (instruction.OpCode != OpCode.Move || instruction.Operands.Count != 2)
                continue;

            var read = instruction.Operands[1] is MemoryOperand;
            var elementType = TypeOf(instruction.Operands[read ? 0 : 1]);

            if (instruction.Operands[read ? 1 : 0] is not MemoryOperand { Index: null, Scale: 0, Base: LocalVariable address } memory
                || memory.Addend != elements)
                continue;

            if (definitions.GetValueOrDefault(address) is not { OpCode: OpCode.Add, Operands.Count: 3 } sum)
                continue;

            if (ArrayAndWidth(sum, definitions) is not { } indexed)
                continue;

            if (indexed.Array.Type != null)
            {
                changed |= TypeTheElement(instruction, read, indexed, pointerSize);
                continue;
            }

            //The width the index was scaled by has to be the width of the element, or these two things are
            //not the same access.
            if (elementType == null || Width(elementType, pointerSize) != indexed.Width)
                continue;

            indexed.Array.Type = elementType.MakeSzArrayType();
            changed = true;
        }

        return changed;
    }

    /// <summary>
    /// Gives an element read off a known array the type that array holds.
    /// </summary>
    /// <remarks>
    /// The inference above reads an array's type off the elements taken out of it. Where the array is already
    /// known it stops there, and the reverse - which is the far more common direction - was never made: an
    /// element load names no field and no method, so the value it produces arrives with no type at all. Every
    /// field read off that value is then a read of unmanaged memory, the calls it is passed to are unresolved,
    /// and the loop around it goes with them. Code that walks a grid is almost entirely this shape, which is
    /// why the two largest files in this game were also the two worst recovered.
    /// </remarks>
    private static bool TypeTheElement(Instruction instruction, bool read, (LocalVariable Array, int Width) indexed, int pointerSize)
    {
        //Only a read produces a value to name. A store consumes one, and what it stores is typed already or
        //is what the inference above learns the array from.
        if (!read || instruction.Operands[0] is not LocalVariable { Type: null } loaded)
            return false;

        if (indexed.Array.Type is not SzArrayTypeAnalysisContext { ElementType: { } element })
            return false;

        //The same check the other direction makes: an index scaled by something other than the width of the
        //element means this is not an access to that array.
        if (Width(element, pointerSize) != indexed.Width)
            return false;

        loaded.Type = element;
        return true;
    }

    /// <summary>What a value is, where that is known.</summary>
    private static TypeAnalysisContext? TypeOf(object operand) => operand switch
    {
        LocalVariable local => local.Type,
        FieldReference field => field.Field.FieldType,
        _ => null,
    };

    /// <summary>The value being indexed and the width it was scaled by, out of <c>array + index &lt;&lt; n</c>.</summary>
    private static (LocalVariable Array, int Width)? ArrayAndWidth(Instruction sum, Dictionary<LocalVariable, Instruction> definitions)
    {
        for (var side = 1; side <= 2; side++)
        {
            if (sum.Operands[side] is not LocalVariable candidate || sum.Operands[side == 1 ? 2 : 1] is not LocalVariable offset)
                continue;

            if (definitions.GetValueOrDefault(offset) is not { OpCode: OpCode.ShiftLeft, Operands.Count: 3 } shift)
                continue;

            if (Shift(shift.Operands[2]) is not { } by)
                continue;

            return (candidate, 1 << by);
        }

        return null;
    }

    /// <summary>How wide a value of this type is where an array holds one.</summary>
    internal static int? Width(TypeAnalysisContext type, int pointerSize)
    {
        if (!type.IsValueType)
            return pointerSize;

        //An enum is as wide as the integer behind it, which it keeps in the one instance field it has. Without
        //this an array of them matched nothing, so its elements were never named - and a board of cell colours
        //is exactly that, with every read off an element going the same way.
        if (type.BaseType?.FullName == "System.Enum")
        {
            foreach (var field in type.Fields)
            {
                if (!field.IsStatic)
                    return Width(field.FieldType, pointerSize);
            }

            return null;
        }

        return type.FullName switch
        {
            "System.Boolean" or "System.SByte" or "System.Byte" => 1,
            "System.Int16" or "System.UInt16" or "System.Char" => 2,
            "System.Int32" or "System.UInt32" or "System.Single" => 4,
            "System.Int64" or "System.UInt64" or "System.Double" or "System.IntPtr" or "System.UIntPtr" => 8,
            _ => null,
        };
    }

    private static int? Shift(object operand)
        => operand switch
        {
            int i and >= 0 and <= 4 => i,
            long l and >= 0 and <= 4 => (int)l,
            uint u and <= 4 => (int)u,
            ulong ul and <= 4 => (int)ul,
            _ => null,
        };
}
