using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using LibCpp2IL.BinaryStructures;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Follows a struct field down to the member the arithmetic was actually reading.
/// </summary>
/// <remarks>
/// <para>
/// A struct in a field lies where it is rather than being pointed at, so reading one of its members is a read
/// at a fixed distance from the outer field. Where that distance is not nought the offset matches nothing at
/// the outer level and the resolver walks into the struct to find it - which is how <c>n.A.Y</c> comes back
/// correctly. Where it <i>is</i> nought the offset matches the outer field exactly, the resolver stops there,
/// and <c>n.A.X</c> comes back as <c>n.A</c>:
/// </para>
/// <code>
/// Add v9, n.A (Pair), n.A.Y (System.Single)     ; a Pair plus a float
/// </code>
/// <para>
/// The disambiguation is in the instruction. <b>Arithmetic is done on numbers</b> - ISIL has no addition of one
/// struct to another, and no such thing was compiled - so an operand that has resolved to a struct is a read of
/// what lies at the front of it. Following that down, repeatedly, gives the member that was read, and the
/// expression becomes writable again: <c>NestedSum</c> was four reads of which two were wrong, and the whole
/// method was commented out for it.
/// </para>
/// <para>
/// Only operands that are read, never the one being written. A struct field on the left of a store is a whole
/// struct being assigned, which is an ordinary thing to do and means what it says.
/// </para>
/// </remarks>
public static class StructInArithmetic
{
    public static void Run(MethodAnalysisContext method)
    {
        if (method.ControlFlowGraph is not { } graph)
            return;

        //Round again whenever something is learnt: adding two floats gives a float, and that answer is what
        //tells the *next* addition it is arithmetic rather than an address. `n.A.X + n.A.Y + n.B.X + n.B.Y`
        //is three additions of which only the first has a number on both sides to begin with.
        var learnt = true;

        while (learnt)
        {
            learnt = false;
            learnt |= Once(graph, method);
        }
    }

    private static bool Once(Graphs.ISILControlFlowGraph graph, MethodAnalysisContext method)
    {
        var changed = false;

        foreach (var instruction in graph.Instructions)
        {
            if (!ReadsNumbers(instruction.OpCode))
                continue;

            //Only where something beside it is a plain number. `Add` is also how an address is worked out,
            //and a struct is an ordinary thing to compute an address from - narrowing to the shape that has
            //a number on the other side is what tells the two apart. Left broad, this cost three whole
            //methods in the game to gain one in the corpus.
            if (!BesideANumber(instruction) && !ProducesANumber(instruction))
                continue;

            //From one, because operand nought is what the instruction writes.
            for (var operand = 1; operand < instruction.Operands.Count; operand++)
            {
                //Two shapes. A struct that reached a field of its own is a field reference and is followed
                //from there; one that never did is a plain local - which is what a struct arriving in
                //registers looks like once `HomogeneousFloatParameters` has named them, and it is the shape
                //the generator was giving up on with "Part of a struct used as a value", 52 times over.
                var member = instruction.Operands[operand] switch
                {
                    FieldReference read => Inside(read),
                    LocalVariable local => FrontOf(local, method),
                    _ => null,
                };

                if (member is null)
                    continue;

                instruction.Operands[operand] = member;
                changed = true;

                //What an addition of numbers produces is a number, and saying so is what lets the addition
                //after it be recognised at all. A destination already *called* a struct is the register's
                //inherited name rather than the answer's type - see the loop below - and is replaced too.
                if (instruction.Operands[0] is LocalVariable produced
                    && (produced.Type is null || IsAStructOfNumbers(produced.Type)))
                {
                    produced.Type = member.Path[^1].FieldType;
                }
            }
        }

        //And the same mistake with nothing to rewrite: both operands are already numbers and only the
        //destination is called a struct. The first register of a homogeneous float struct is named with the
        //*whole* struct so that a call reading it can be written, and every later instruction writing that
        //register inherited the name - so `num = position.x * size` came out
        //`Vector3 v = (Vector3)(position.x * size)`, and the statement went. Nothing writes a struct with an
        //`Add`; whatever the register is called, the answer is the number its operands are.
        foreach (var instruction in graph.Instructions)
        {
            //Floating point arithmetic only. A **bitwise** operation writing a struct-named register is how a
            //small struct is *packed* - `num19 | (num18 << 32)` really is a `SubCellRef`, and calling that
            //answer an `Int64` cost `BoardLogic` a whole body when this was written for every operator.
            if (instruction.OpCode is not (OpCode.Add or OpCode.Subtract or OpCode.Multiply or OpCode.Divide or OpCode.Negate)
                || instruction.Operands.Count < 2
                || instruction.Operands[0] is not LocalVariable answer
                || answer.Type is not { } called || !IsAStructOfNumbers(called))
            {
                continue;
            }

            TypeAnalysisContext? number = null;

            for (var operand = 1; operand < instruction.Operands.Count; operand++)
            {
                if (Numeric(instruction.Operands[operand]) is not { } source)
                {
                    number = null;
                    break;
                }

                number ??= source;
            }

            if (number is null)
                continue;

            answer.Type = number;
            changed = true;
        }

        return changed;
    }

    /// <summary>A value type that is not itself a number - which is what a register wrongly keeps.</summary>
    private static bool IsAStructOfNumbers(TypeAnalysisContext type)
        => type is { IsValueType: true, IsEnumType: false } && type.Namespace != nameof(System)
            && type.Fields.Any(f => !f.IsStatic);

    /// <summary>The floating point number an operand is, where it is one.</summary>
    private static TypeAnalysisContext? Numeric(object operand)
    {
        var type = operand switch
        {
            LocalVariable local => local.Type,
            NestedFieldReference nested => nested.Path[^1].FieldType,
            FieldReference field => field.Field.FieldType,
            _ => null,
        };

        return type is { Namespace: nameof(System), Name: "Single" or "Double" } ? type : null;
    }

    /// <summary>Whether one of the values this instruction reads is a plain number.</summary>
    /// <summary>
    /// Whether what the instruction writes is a number, which says as much as a number beside it.
    /// </summary>
    /// <remarks>
    /// `Subtract v143 (Single), v144 (Vector2), this._lastOwnerSize (Vector2)` has a struct on **both**
    /// sides, so there is no number beside either of them - and it is still arithmetic, because a `Single`
    /// is what it produces and no subtraction of one struct from another was ever compiled. That is the
    /// shape the generator gives up on with "Part of a struct used as a value".
    /// </remarks>
    private static bool ProducesANumber(Instruction instruction)
        => instruction.Operands.Count > 0
            && instruction.Operands[0] is LocalVariable { Type: { } written }
            && IsNumber(written);

    private static bool BesideANumber(Instruction instruction)
    {
        for (var operand = 1; operand < instruction.Operands.Count; operand++)
        {
            //A literal with a fraction in it. No address is ever a float, so this is arithmetic and the
            //struct beside it is one of its members - `MergeEffect::Play` multiplies a `Vector3` by `2.5d`
            //and the generator gave up on the whole statement. An *integer* literal says nothing of the
            //kind: a constant distance from a struct is how a field of one is reached.
            if (instruction.Operands[operand] is float or double)
                return true;

            var type = instruction.Operands[operand] switch
            {
                FieldReference field => field.Field.FieldType,
                LocalVariable local => local.Type,
                _ => null,
            };

            if (type is not null && IsNumber(type))
                return true;
        }

        return false;
    }

    /// <summary>The number at the front of a struct a local holds, however many structs deep it goes.</summary>
    /// <remarks>
    /// The local <i>is</i> the struct, so the member is named on it at distance nought. Only where the walk
    /// ends on a number: a struct whose front member is another struct is followed, and one that never
    /// arrives at a number is left alone rather than half-named.
    /// </remarks>
    private static NestedFieldReference? FrontOf(LocalVariable local, MethodAnalysisContext method)
    {
        //Through a by-reference, which is what a struct an `out` parameter was written into looks like where
        //the caller reads it back: `TryGetBoardWorldRect(out var rect)` leaves the slot's *address* typed
        //`UnityEngine.Rect&`, and `RaycastBoardCell` then adds it to a float. A reference to a struct standing
        //where a number belongs is the member at the front of that struct, exactly as the struct itself is.
        var pointee = local.Type is ByRefTypeAnalysisContext { ElementType: { } referred } ? referred : local.Type;

        if (pointee is not { IsValueType: true } held || IsNumber(held))
            return null;

        //Never a parameter the analysis has since retyped. `IlGenerator.LoadLocal` matches a local to a
        //parameter **by name** and emits `ldarg`, so what lands on the stack is the parameter's declared
        //type, not the analysis type - and naming a `Color` member on a `TMP_MeshInfo[]` parameter is written
        //back as `((Color*)(&meshInfos))->r`, which Roslyn refuses with CS0208. Six of those got past every
        //fast measure and were caught only by the Unity gate. The signature is the authority: where it
        //disagrees, the analysis type is the wrong one and following it down compounds the mistake.
        if (method.Parameters.FirstOrDefault(p => p.Name == local.Name) is { } declared
            && declared.ParameterType.FullName != held.FullName)
            return null;

        var path = new List<FieldAnalysisContext>();

        for (var type = held; path.Count < 8; )
        {
            if (Front(type) is not { } front)
                break;

            path.Add(front);
            type = front.FieldType;
        }

        return path.Count > 0 && IsNumber(path[^1].FieldType)
            ? new NestedFieldReference([.. path], local, 0)
            : null;
    }

    /// <summary>The member at the front of a struct-typed field, however many structs deep it goes.</summary>
    internal static NestedFieldReference? Inside(FieldReference reference)
    {
        List<FieldAnalysisContext> path = reference is NestedFieldReference nested
            ? [.. nested.Path]
            : [reference.Field];

        var went = false;

        while (Front(path[^1].FieldType) is { } inner)
        {
            path.Add(inner);
            went = true;
        }

        return went ? new NestedFieldReference([.. path], reference.Local, reference.Offset) : null;
    }

    /// <summary>
    /// The instance field a struct keeps at its own offset nought, where the type is a struct at all.
    /// </summary>
    /// <remarks>
    /// A number is not descended into: an <c>int</c> has a single field standing for its value, and following
    /// that would turn every read of one into a read of <c>Int32.m_value</c>, which is the same value said
    /// worse. An enum is a number for this purpose too.
    /// </remarks>
    internal static FieldAnalysisContext? Front(TypeAnalysisContext type)
    {
        if (!type.IsValueType || IsNumber(type) || type.BaseType?.FullName == "System.Enum")
            return null;

        //The one that lies lowest, not the one recorded at nought. `FieldAnalysisContext.Offset` is
        //`BackingData.FieldOffset`, which il2cpp records **as the type is boxed** - so the first field of a
        //value type sits at the object header, 0x10 on 64-bit, and never at nought. Asking for nought found
        //nothing for any struct whose offsets are recorded at all, which is every ordinary one:
        //`Joystick.Horizontal` reads `input.x` and this refused to see it.
        FieldAnalysisContext? front = null;

        foreach (var candidate in type.Fields)
        {
            if (candidate.IsStatic || candidate.Offset < 0)
                continue;

            if (front is null || candidate.Offset < front.Offset)
                front = candidate;
        }

        return front;
    }

    internal static bool IsNumber(TypeAnalysisContext type) => type.Type is
        Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN or Il2CppTypeEnum.IL2CPP_TYPE_CHAR
        or Il2CppTypeEnum.IL2CPP_TYPE_I1 or Il2CppTypeEnum.IL2CPP_TYPE_U1
        or Il2CppTypeEnum.IL2CPP_TYPE_I2 or Il2CppTypeEnum.IL2CPP_TYPE_U2
        or Il2CppTypeEnum.IL2CPP_TYPE_I4 or Il2CppTypeEnum.IL2CPP_TYPE_U4
        or Il2CppTypeEnum.IL2CPP_TYPE_I8 or Il2CppTypeEnum.IL2CPP_TYPE_U8
        or Il2CppTypeEnum.IL2CPP_TYPE_R4 or Il2CppTypeEnum.IL2CPP_TYPE_R8
        or Il2CppTypeEnum.IL2CPP_TYPE_I or Il2CppTypeEnum.IL2CPP_TYPE_U;

    private static bool ReadsNumbers(OpCode opCode) => opCode is
        OpCode.Add or OpCode.Subtract or OpCode.Multiply or OpCode.Divide
        or OpCode.ShiftLeft or OpCode.ShiftRight or OpCode.And or OpCode.Or or OpCode.Xor
        or OpCode.Not or OpCode.Negate
        or (>= OpCode.CheckEqual and <= OpCode.CheckLessOrEqual);
}
