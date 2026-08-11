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
    private static readonly bool Trace = System.Environment.GetEnvironmentVariable("ARRAYTYPE_TRACE") is not null;

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

            //Something the array is *assigned* beats anything worked out from what came out of it, and beats
            //it whether or not the array already has a type. The element's own type is only as good as
            //whatever last touched it - `op_Equality` types its argument `UnityEngine.Object`, and from that
            //this concluded `UnityEngine.Object[]` for a local a field read three instructions earlier had
            //said was `FTUE[]`. Nothing improves a type once it is set, so every field read off an element
            //became unmanaged memory: `TutorialMenu::OnTutorialStepCompleted` is that, twice.
            //
            //Only ever *downwards*, to something the current answer is a base of. Replacing a type with an
            //unrelated one is how a pass that means well empties a method, and there is no case for it here:
            //an array is assigned an array of the same thing or of something more exact, never of something
            //else entirely.
            var assigned = Assigned(method.ControlFlowGraph, indexed.Array);

            if (Trace)
                System.Console.Error.WriteLine($"ARRAYTYPE {method.Name}: array={indexed.Array} "
                    + $"held={indexed.Array.Type?.FullName ?? "?"} assigned={assigned?.FullName ?? "?"} "
                    + $"element={elementType?.FullName ?? "?"} width={indexed.Width} "
                    + $"better={(assigned is { } a && MoreExactThan(a, indexed.Array.Type))}");

            if (assigned is { } declared && MoreExactThan(declared, indexed.Array.Type))
            {
                indexed.Array.Type = declared;
                changed = true;
            }

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
        if (!read || instruction.Operands[0] is not LocalVariable loaded)
            return false;

        if (indexed.Array.Type is not SzArrayTypeAnalysisContext { ElementType: { } element })
            return false;

        //The same check the other direction makes: an index scaled by something other than the width of the
        //element means this is not an access to that array.
        if (Width(element, pointerSize) != indexed.Width)
            return false;

        //An untyped element, or one wearing a base class of what the array holds. The second happens because
        //something got to it first with less to go on: `op_Equality(Object, Object)` types its argument, so
        //an `FTUE` taken out of an `FTUE[]` is called a `UnityEngine.Object` and the two field reads off it
        //become unmanaged memory. What the array holds is the better answer and cannot contradict this one -
        //every element of an `FTUE[]` is a `UnityEngine.Object`, which is why the vaguer name was allowed in
        //the first place.
        if (loaded.Type != null && (loaded.Type.FullName == element.FullName || !DerivesFrom(element, loaded.Type)))
            return false;

        loaded.Type = element;
        return true;
    }

    /// <summary>The most exact array type anything flowing into this local already says it has.</summary>
    /// <remarks>
    /// <para>
    /// The array a loop reads through is a <b>phi</b>, not a local anything moves into - the copies on the
    /// incoming edges are made later, when single assignment form is taken apart, and this runs long before
    /// that. So the thing to read is the phi's own inputs, which are right there as its operands.
    /// </para>
    /// <para>
    /// <c>TutorialMenu::OnTutorialStepCompleted</c> merges two reads of the same field: one input kept
    /// <c>CF.FTUE[]</c>, the other had already been flattened to <c>UnityEngine.Object[]</c> by the element
    /// inference below. Either says more than an element does, and the exact one says most.
    /// </para>
    /// <para>
    /// Inputs that cannot be the same array - two unrelated element types - mean the register is being
    /// reused and the phi says nothing, which is the same judgement <c>SsaForm.InputsDisagree</c> makes.
    /// </para>
    /// </remarks>
    private static TypeAnalysisContext? Assigned(Graphs.ISILControlFlowGraph graph, LocalVariable array)
    {
        TypeAnalysisContext? best = null;

        foreach (var instruction in graph.Instructions)
        {
            if (instruction.OpCode is not (OpCode.Move or OpCode.Phi) || instruction.Operands.Count < 2
                || !ReferenceEquals(instruction.Operands[0], array))
                continue;

            for (var i = 1; i < instruction.Operands.Count; i++)
            {
                if (TypeOf(instruction.Operands[i]) is not { } type || ElementOf(type) is null)
                    continue;

                if (best is null || MoreExactThan(type, best))
                    best = type;
                else if (best.FullName != type.FullName && !MoreExactThan(best, type))
                    return null;
            }
        }

        return best;
    }

    /// <summary>Whether one array type says more than another: nothing at all, or the same array of a subclass.</summary>
    private static bool MoreExactThan(TypeAnalysisContext candidate, TypeAnalysisContext? held)
    {
        if (held is null)
            return true;

        return ElementOf(candidate) is { } exact && ElementOf(held) is { } vague
            && exact.FullName != vague.FullName && DerivesFrom(exact, vague);
    }

    /// <summary>Whether one type is the other, further down.</summary>
    /// <remarks>
    /// Walked up the derived type's own hierarchy, because that is the direction a reference is allowed to
    /// travel: an <c>FTUE</c> may be called the <c>UnityEngine.Object</c> it also is, never the other way
    /// round.
    /// </remarks>
    private static bool DerivesFrom(TypeAnalysisContext derived, TypeAnalysisContext ancestor)
    {
        for (var walk = derived.BaseType; walk != null; walk = walk.BaseType)
            if (walk.FullName == ancestor.FullName)
                return true;

        return false;
    }

    private static TypeAnalysisContext? ElementOf(TypeAnalysisContext type) => type switch
    {
        SzArrayTypeAnalysisContext array => array.ElementType,
        ArrayTypeAnalysisContext { Rank: 1 } array => array.ElementType,
        _ => null,
    };

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
            _ => Contents(type, pointerSize),
        };
    }

    /// <summary>How much of itself a struct takes up, which is what one element of an array of them is.</summary>
    /// <remarks>
    /// The runtime records what an instance occupies <i>boxed</i>, so the object header comes off:
    /// <c>UnityEngine.Vector3</c> is <c>0x1C</c> boxed and twelve bytes of floats. Without this, an array of
    /// any struct the list above does not name had no width at all, so nothing about an access into one could
    /// be worked out and every one of them stayed a read of unmanaged memory - and twelve is exactly the width
    /// that cannot be a shift, so those arrays never had an index register to be recognised by either.
    /// </remarks>
    private static int? Contents(TypeAnalysisContext type, int pointerSize)
    {
        var header = 2 * pointerSize;

        try
        {
            if (type.Definition?.RawSizes.instance_size is { } boxed && boxed > header)
                return (int)boxed - header;
        }
        catch
        {
            //A generic parameter, or anything else with no definition of its own to be sized.
        }

        return null;
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
