using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.ISIL;
using LibCpp2IL.BinaryStructures;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// What this fork adds: carrying a type through the arithmetic and the choices that pass a value on, so
/// that a value keeps the type it started with rather than losing it at the first operation.
///
/// Kept apart from the file it belongs to so that the file stays as close to upstream as it can,
/// and a later version of Cpp2IL can be merged without the two sets of changes meeting.
/// </summary>
public static partial class LocalVariables
{
    /// <summary>
    /// Gives an untyped value the type of whatever it is being compared against.
    /// </summary>
    /// <remarks>
    /// Two values are only compared for order when they are the same kind of thing, so a comparison says as
    /// much about a value as an assignment does - and nothing was reading it. The value that suffers most is
    /// a loop counter: it is written by arithmetic on itself, which says nothing, and its only other mention
    /// is the test against the length it runs to. Left untyped it is written out as an <c>object</c> or a
    /// native integer, so the test becomes a comparison between unrelated things, the increment becomes
    /// pointer arithmetic, and the whole loop - with everything inside it - cannot be written down.
    ///
    /// Only a number is carried across. Comparing a reference against zero is a null check rather than a
    /// statement that the two are alike, and taking a type from it would say a reference is an integer.
    /// </remarks>
    private static bool PropagateComparison(Instruction comparison)
    {
        //Operand 0 is where the answer goes; the two being compared follow it.
        if (comparison.Operands.Count < 3)
            return false;

        return Carry(comparison.Operands[1], comparison.Operands[2])
            || Carry(comparison.Operands[2], comparison.Operands[1]);
    }

    /// <summary>Types an untyped local from the other side of a comparison, where that side is a number.</summary>
    private static bool Carry(object from, object to)
        => to is LocalVariable { Type: null } untyped && TypeOfOperand(from) is { IsValueType: true, Namespace: nameof(System) } number
            && number.Name is "SByte" or "Byte" or "Int16" or "UInt16" or "Int32" or "UInt32" or "Int64" or "UInt64"
                or "Single" or "Double" or "Char" or "IntPtr" or "UIntPtr"
            && SetTypeIfUnknown(untyped, number);

    /// <summary>What a value is, where that is known.</summary>
    private static TypeAnalysisContext? TypeOfOperand(object operand) => operand switch
    {
        LocalVariable local => local.Type,
        FieldReference field => field.Field.FieldType,
        _ => null,
    };

    /// <summary>
    /// Replaces a value's type where it says <c>object</c> and the call that produced it says something else.
    ///
    /// One body is shared between every instantiation of a generic whose arguments are references, so a call
    /// resolved by address says its element is an <c>object</c> - and once a value is called that, nothing
    /// else will change it, because a type that is already known is left alone. Every field read through it
    /// then fails, since <c>object</c> has no fields. Where the call has since been given back the
    /// instantiation it was written with, what it returns is the better answer.
    /// </summary>
    private static bool SharpenFromReturn(LocalVariable value, TypeAnalysisContext? produced)
    {
        if (produced == null)
            return false;

        if (value.Type is { FullName: "System.Object" } && produced.FullName != "System.Object")
        {
            value.Type = produced;
            return true;
        }

        //A struct of floats travels in the vector registers, and where the compiler read a whole one out of
        //static storage - `ldur v1, [x8, #0xc]` for `Vector3.one` - the register was typed by the *width* of
        //that load and came out `System.Int64`. The read is then resolved into the property call it stands
        //for, and the callee says outright what it produces, so the guess from the width has nothing to
        //recommend it: `Vector3.one * 0.85f` came out as `(long)(Vector3.one * 1062836634L)`, which C# will
        //quietly compile because a `long` converts to a `float`.
        //A struct of floats only. Widening this to any value type the callee declares - so that
        //`_settings.GetColor(c)` returning a four-byte `Color32` in w0 stops being called `System.Int64` -
        //was built and **cost five branches**, `decisions` 1304 -> 1299, while closing no method at all. A
        //declaration does beat a width, but somewhere downstream a small struct arriving where an integer
        //was expected loses a comparison, and finding that is a round of its own.
        if (IsOnlyAWidth(value.Type) && HomogeneousFloatStruct.Count(produced) is > 1)
        {
            value.Type = produced;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Whether a type is no more than the width of the register a value was loaded into.
    /// </summary>
    /// <remarks>
    /// The integers are what an unrecognised load of eight bytes leaves behind. <c>Single</c> and
    /// <c>Double</c> belong here too, for the same reason and only in this question: <c>Vector2.zero</c> is
    /// <c>ldr d2, [x8, #...]</c>, eight bytes, and the register was called a <c>Double</c> because that is
    /// what a d register usually holds - while a <c>Vector2</c> is eight bytes and fits it exactly. Where a
    /// call declares it returns a struct of floats, that declaration is the better answer than the width.
    /// </remarks>
    private static bool IsOnlyAWidth(TypeAnalysisContext? type)
        => IsABareInteger(type) || type?.FullName is "System.Single" or "System.Double";

    /// <summary>
    /// Whether a type is what a value of unknown kind ends up as - the width of the register it was in, and
    /// nothing more. Only these are overruled, so a type that says something is never thrown away.
    /// </summary>
    private static bool IsABareInteger(TypeAnalysisContext? type)
        => type?.FullName is "System.Int64" or "System.UInt64" or "System.IntPtr" or "System.UIntPtr";

    /// <summary>
    /// Types the method's own runtime method parameter as what it is.
    ///
    /// A body shared between the instantiations of a generic is handed the <c>MethodInfo*</c> of the one it
    /// is running as, because that is the only thing that tells them apart - the type arguments, the runtime
    /// class, and the generic context all hang off it. The parameter was named but never typed, so every
    /// chain that starts there read as unmanaged memory: the base constructor of a generic type, and every
    /// static field and type such a body reaches.
    /// </summary>
    private static void SeedOwnMethodInfoParameter(MethodAnalysisContext method)
    {
        if (method.DeclaringType?.DeclaringAssembly is not { } assembly)
            return;

        foreach (var local in method.ParameterLocals)
        {
            if (local.IsMethodInfo && local.Type == null)
                local.Type = new RuntimeMethodInfoAnalysisContext(method, assembly);
        }
    }

    /// <summary>
    /// Arithmetic on a number produces a number of the same kind, so the result takes its operands' type and
    /// they take the result's. Only the primitive integer types travel this edge: <c>Add</c> also computes
    /// addresses, and letting a reference type across it would call the address the object it points into.
    ///
    /// Without this a computed value is left with no type at all, and an untyped value lowers to a native
    /// integer - which is why <c>x &amp; ~(x &gt;&gt; 31)</c>, all of it plainly ints, came out as operations
    /// on <c>object</c> that would not compile.
    /// </summary>
    /// <summary>
    /// Every value the method dereferences through. A pointer is not a number, however it was arrived at: a
    /// value used as the base of a memory operand holds an address, and giving it the type of the offset it
    /// was added to - which is what indexing an array looks like - calls the array an <c>int</c> and loses
    /// every access through it.
    /// </summary>
    private static HashSet<LocalVariable> AddressesUsedAsBases(MethodAnalysisContext method)
    {
        var bases = new HashSet<LocalVariable>();

        foreach (var instruction in method.ControlFlowGraph!.Instructions)
        {
            foreach (var operand in instruction.Operands)
            {
                if (operand is MemoryOperand { Base: LocalVariable address })
                    bases.Add(address);
            }
        }

        return bases;
    }

    private static bool PropagateArithmetic(MethodAnalysisContext method, Instruction arithmetic, HashSet<LocalVariable> addresses)
    {
        if (arithmetic.Operands.Count < 2 || arithmetic.Operands[0] is not LocalVariable destination)
            return false;

        //A shift's second operand counts places rather than sharing the result's type, so it is left alone.
        var last = arithmetic.OpCode is OpCode.ShiftLeft or OpCode.ShiftRight ? 1 : arithmetic.Operands.Count - 1;

        var logical = arithmetic.OpCode is OpCode.Not or OpCode.And or OpCode.Or or OpCode.Xor;

        //A struct of floats is handed about in the same registers a float is, so dividing a `Vector2` by a
        //`float` is two divisions and reaches here as one `Divide` over a float and something else. The result
        //is the vector, not the float - and calling it a float is worse than leaving it untyped, because the
        //cast the decompiler then writes does not compile. So a float only speaks for the result where nothing
        //else in the expression is known to be anything but one.
        var floatsMaySpeak = !logical && OnlyFloatingPointIsKnown(arithmetic, last);
        var changed = false;

        //The destination's own type is what needs correcting here, so it cannot also be the reason not to:
        //`OnlyFloatingPointIsKnown` counts operand zero, and a result left `System.Int64` by a load whose
        //width was all anyone knew therefore blocks its own correction. Judged on what the expression *reads*
        //instead - and only where every one of those is a known floating point value, which is what keeps the
        //`Vector2 / float` shape out: that one reads a vector on one side, so it never qualifies.
        if (!logical && ReadsOnlyFloatingPoint(arithmetic, last, out var reads) && !addresses.Contains(destination))
            changed |= SharpenAVectorRegister(destination, reads);

        for (var i = 1; i <= last && i < arithmetic.Operands.Count; i++)
        {
            //An immediate with a fraction can only have come out of a floating point register, so it says what
            //the result is even where nothing else in the expression is typed yet.
            if (floatsMaySpeak && !addresses.Contains(destination) && FloatingPointConstant(arithmetic.Operands[i], method) is { } constant)
            {
                changed |= SetTypeIfUnknown(destination, constant);
                changed |= SharpenAVectorRegister(destination, constant);
                continue;
            }

            //A field says what it holds as plainly as a typed local does, and one side of an expression being
            //a field is the usual shape once a struct handed over a register at a time has been taken apart.
            if (arithmetic.Operands[i] is FieldReference read)
            {
                if (Travels(read.Field.FieldType, arithmetic.OpCode, floatsMaySpeak) && !addresses.Contains(destination))
                {
                    changed |= SetTypeIfUnknown(destination, read.Field.FieldType);
                    changed |= SharpenAVectorRegister(destination, read.Field.FieldType);
                }

                continue;
            }

            if (arithmetic.Operands[i] is not LocalVariable operand)
                continue;

            if (Travels(operand.Type, arithmetic.OpCode, floatsMaySpeak) && !addresses.Contains(destination))
            {
                changed |= SetTypeIfUnknown(destination, operand.Type);
                changed |= SharpenAVectorRegister(destination, operand.Type);
            }

            if (Travels(destination.Type, arithmetic.OpCode, floatsMaySpeak) && !addresses.Contains(operand))
                changed |= SetTypeIfUnknown(operand, destination.Type);
        }

        return changed;
    }

    /// <summary>
    /// Gives a vector register holding the result of floating point arithmetic its floating point type, even
    /// where it already has one that says nothing.
    /// </summary>
    /// <remarks>
    /// The type a value takes here is settled by a fixpoint, so a destination can be given the type of an
    /// operand that is *later* sharpened and never revisited: `Vector3.one * 0.85f` typed its result
    /// `System.Int64` from a whole-vector load, and kept it after the load became a `Vector3`. A bare integer
    /// is what a value of unknown kind ends up as, and a value in v0..v31 is a float - so between the two
    /// there is nothing to weigh. Only in a vector register, and only overruling a bare integer, so a type
    /// that says something is never thrown away.
    /// </remarks>
    private static bool SharpenAVectorRegister(LocalVariable local, TypeAnalysisContext? type)
    {
        if (type?.FullName is not ("System.Single" or "System.Double")
            || !IsABareInteger(local.Type)
            || local.Register.Name is not { Length: > 1 } name || name[0] is not ('V' or 'S' or 'D'))
        {
            return false;
        }

        local.Type = type;
        return true;
    }

    /// <summary>
    /// Whether every type known about this expression is a floating point one - so that a value found to be a
    /// float is the whole value rather than one lane of something wider.
    /// </summary>
    private static bool OnlyFloatingPointIsKnown(Instruction arithmetic, int last)
    {
        for (var i = 0; i <= last && i < arithmetic.Operands.Count; i++)
        {
            //A field counts as much as a local. Leaving it out let `eventData.position / pixelsPerUnit` -
            //a `Vector2` over a `float` - be read as a float, which is the same mistake the rule exists to
            //stop and cost five methods their whole bodies the first time round.
            var type = arithmetic.Operands[i] switch
            {
                LocalVariable local => local.Type,
                FieldReference field => field.Field.FieldType,
                _ => null,
            };

            if (type is not null && !IsFloatingPoint(type))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Whether every value the expression reads is a known floating point one, and what kind.
    /// </summary>
    /// <remarks>
    /// Operand zero - the destination - is deliberately not counted, and a constant with a fraction counts as
    /// much as a typed value: <c>Vector3.one.x * 0.85f</c> reads a <c>Single</c> field and a <c>float</c>, and
    /// nothing else. Requiring *every* read to be known is what keeps the shape this rule must not touch out:
    /// dividing a <c>Vector2</c> by a <c>float</c> reads a vector on one side, so it never qualifies.
    /// </remarks>
    private static bool ReadsOnlyFloatingPoint(Instruction arithmetic, int last, out TypeAnalysisContext? kind)
    {
        kind = null;

        for (var i = 1; i <= last && i < arithmetic.Operands.Count; i++)
        {
            switch (arithmetic.Operands[i])
            {
                //A constant with a fraction can only have come out of a floating point register, and says so
                //without saying which width - so it is allowed through without settling the kind.
                case float or double:
                    continue;

                case LocalVariable { Type: { } local } when IsFloatingPoint(local):
                    kind ??= local;
                    continue;

                case FieldReference { Field.FieldType: { } field } when IsFloatingPoint(field):
                    kind ??= field;
                    continue;

                default:
                    return false;
            }
        }

        return kind is not null;
    }

    private static bool IsFloatingPoint(TypeAnalysisContext? type) => type?.FullName is "System.Single" or "System.Double";

    private static TypeAnalysisContext? FloatingPointConstant(object operand, MethodAnalysisContext method) => operand switch
    {
        float => method.AppContext.SystemTypes.SystemSingleType,
        double => method.AppContext.SystemTypes.SystemDoubleType,
        _ => null,
    };

    /// <summary>
    /// Whether a value of this type comes back out of this operation still being one.
    /// </summary>
    /// <remarks>
    /// An integer does, of all of them. A boolean survives the logical ones - negated or combined, a condition
    /// is still a condition - but not the numeric ones, where it is being counted rather than tested. A float
    /// is the other way round: the numeric operations are what it is for, while a logical one on a float is
    /// working on the bits of the register, and calling the result a float would be a statement about the
    /// value that is not true.
    /// </remarks>
    private static bool Travels(TypeAnalysisContext? type, OpCode opCode, bool floatsMaySpeak)
    {
        if (IsPrimitiveInteger(type))
            return true;

        if (IsFloatingPoint(type))
            return floatsMaySpeak;

        return type?.FullName == "System.Boolean" && opCode is OpCode.Not or OpCode.And or OpCode.Or or OpCode.Xor;
    }

    private static bool IsPrimitiveInteger(TypeAnalysisContext? type) => type?.FullName is
        "System.SByte" or "System.Byte" or "System.Int16" or "System.UInt16"
        or "System.Int32" or "System.UInt32" or "System.Int64" or "System.UInt64";

    // A select's two arms and its result all hold the same value, so types flow between them exactly as
    // they do across a phi. Its condition, operand 1, is a boolean and takes no part in that.
    private static bool PropagateSelect(Instruction select)
    {
        if (select.Operands[0] is not LocalVariable destination)
            return false;

        var changed = false;

        for (var i = 2; i < select.Operands.Count; i++)
        {
            if (select.Operands[i] is not LocalVariable arm)
                continue;

            changed |= SetTypeIfUnknown(destination, arm.Type);
            changed |= SetTypeIfUnknown(arm, destination.Type);
        }

        return changed;
    }

    /// <summary>
    /// The class a metadata slot points at, which is one read past the slot itself.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A type constant names the <b>slot</b> the runtime keeps the class in, not the class - so a body that
    /// wants the class reads through it once. That read produced a local with no type of its own, and
    /// everything past it went the same way: the class-initialised flag, the static field storage at
    /// <c>0xB8</c>, the runtime generic context at <c>0xC0</c> are all read at offsets that mean nothing
    /// unless what they are read through is known to be a class.
    /// </para>
    /// <para>
    /// Seeded before <see cref="SeedNewobjResults"/> deliberately, so that where a register holds the class
    /// only until an allocation puts the new object in it, the allocation still wins - the object is what the
    /// rest of the body does something with, and it is the later of the two.
    /// </para>
    /// </remarks>
    private static void SeedDereferencedClasses(MethodAnalysisContext method)
    {
        foreach (var instruction in method.ControlFlowGraph!.Instructions)
        {
            if (instruction.OpCode != OpCode.Move || instruction.Operands.Count < 2
                || instruction.Operands[0] is not LocalVariable destination
                || instruction.Operands[1] is not MemoryOperand { Index: null, Scale: 0, Addend: 0, Base: LocalVariable slot }
                || slot.Type is not RuntimeClassTypeAnalysisContext held)
                continue;

            destination.Type = held;
        }
    }

    /// <summary>The array-allocating helpers, which say what they are allocating.</summary>
    private static readonly string[] ArrayAllocators =
    [
        "SzArrayNew", "il2cpp_array_new_specific", "il2cpp_vm_array_new_specific",
    ];

    /// <summary>
    /// The array an allocation produced, which the allocation names.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>Call "SzArrayNew", v70, typeof(System.Int32[]), 3</c> - the type is the call's own first argument and
    /// is not in any doubt, but the local it wrote had none, so everything read through the new array went
    /// unresolved: its length at <c>0x18</c>, its elements at <c>0x20</c>. A static constructor that fills a
    /// jagged array literal does that once per row.
    /// </para>
    /// <para>
    /// Seeded beside the other allocations rather than left to inference, which cannot reach it: nothing about
    /// a length read through an untyped base says the base was an array, so the fact has to come from the one
    /// place that states it.
    /// </para>
    /// </remarks>
    internal static void SeedArrayAllocations(MethodAnalysisContext method)
    {
        foreach (var instruction in method.ControlFlowGraph!.Instructions)
        {
            if (instruction.OpCode != OpCode.Call || instruction.Operands.Count < 3
                || instruction.Operands[0] is not string name
                || System.Array.IndexOf(ArrayAllocators, name) < 0
                || instruction.Operands[1] is not LocalVariable allocated
                || Allocated(instruction.Operands[2]) is not { } array)
                continue;

            allocated.Type = array;
        }
    }

    /// <summary>
    /// Types a local from the fact that a bitwise operation is done to it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// C# has no <c>&amp;</c>, <c>|</c> or <c>^</c> on a reference, so a local that is an operand of one is not
    /// a reference, whatever the inference concluded. Left as <c>object</c> the statement cannot be written -
    /// <c>if ((int)(obj8 &amp; 1L) != 0)</c> - and it is a condition, so the branch goes with it.
    /// </para>
    /// <para>
    /// This is the one operator family where the argument is airtight. Addition is <b>not</b> included on
    /// purpose: <c>obj + 8L</c> really can be il2cpp walking a pointer, and calling that a number makes it
    /// compile while saying something the program never did - the trap recorded in
    /// <c>the loop that kept no subscript</c>.
    /// </para>
    /// <para>
    /// The width comes from whatever else is in the expression rather than being guessed, so nothing is
    /// invented: the destination if it is a number, otherwise the other operand.
    /// </para>
    /// </remarks>
    internal static void SeedBitwiseOperands(MethodAnalysisContext method)
    {
        CurrentTypes = method.AppContext.SystemTypes;

        foreach (var instruction in method.ControlFlowGraph!.Instructions)
        {
            if (instruction.OpCode is not (OpCode.And or OpCode.Or or OpCode.Xor) || instruction.Operands.Count != 3)
                continue;

            var width = Numeric(instruction.Operands[0]) ?? Numeric(instruction.Operands[1]) ?? Numeric(instruction.Operands[2]);

            if (width is null)
                continue;

            for (var side = 1; side <= 2; side++)
            {
                if (instruction.Operands[side] is LocalVariable local
                    && local.Type is null or { IsValueType: false })
                    local.Type = width;
            }
        }
    }

    /// <summary>
    /// The type of an operand where that is a number, and nothing where it is not.
    /// </summary>
    /// <remarks>
    /// A literal counts. <c>obj8 &amp; 1L</c> is the commonest shape of all and there is no other local in it
    /// to take a width from - reading only locals here made the whole seed inert.
    /// </remarks>
    private static TypeAnalysisContext? Numeric(object operand)
    {
        var types = CurrentTypes;

        return operand switch
        {
            LocalVariable { Type: { IsValueType: true } type } when IsInteger(type) => type,
            long or ulong => types?.SystemInt64Type,
            int or uint or short or ushort or sbyte or byte => types?.SystemInt32Type,
            _ => null,
        };
    }

    private static bool IsInteger(TypeAnalysisContext type) => type.Type is
        Il2CppTypeEnum.IL2CPP_TYPE_I1 or Il2CppTypeEnum.IL2CPP_TYPE_U1
        or Il2CppTypeEnum.IL2CPP_TYPE_I2 or Il2CppTypeEnum.IL2CPP_TYPE_U2
        or Il2CppTypeEnum.IL2CPP_TYPE_I4 or Il2CppTypeEnum.IL2CPP_TYPE_U4
        or Il2CppTypeEnum.IL2CPP_TYPE_I8 or Il2CppTypeEnum.IL2CPP_TYPE_U8;

    private static SystemTypesContext? CurrentTypes;

    /// <summary>
    /// Types what an array element was read into, from the array it was read out of.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>Move v191, [shapes (Shape[]) + 0x20 + i*8]</c> says everything needed - the array's type is known and
    /// the read is a proper element access - and yet the local it lands in had no type at all. Everything done
    /// with the element afterwards then had nothing to go on, and the one that costs most is a virtual call:
    /// <see cref="VirtualCallRecovery"/> needs the receiver's type to know which vtable slot means which
    /// method, so <c>foreach (Shape s in shapes) s.Area()</c> stayed an indirect call and the loop was lost.
    /// </para>
    /// <para>
    /// Seeded rather than inferred for the same reason the allocations are: nothing downstream of the read
    /// states what the element is, so it has to come from the read itself.
    /// </para>
    /// </remarks>
    internal static void SeedArrayElements(MethodAnalysisContext method)
    {
        var elements = method.AppContext.Binary.is32Bit ? 0x10 : 0x20;

        foreach (var instruction in method.ControlFlowGraph!.Instructions)
        {
            if (instruction.OpCode != OpCode.Move || instruction.Operands.Count != 2
                || instruction.Operands[0] is not LocalVariable { Type: null } read
                || instruction.Operands[1] is not MemoryOperand { Base: { } from, Index: not null, Scale: > 0, Addend: var at }
                || at != elements)
                continue;

            //Only a reference element. A struct read out of an array is the value, not a reference to it, and
            //saying otherwise would put a type on a register holding half a struct - which is its own defect.
            if (ElementOf(from) is { IsValueType: false } element)
                read.Type = element;
        }
    }

    /// <summary>What one element of the array a read is based on is, where the base says it is an array.</summary>
    private static TypeAnalysisContext? ElementOf(object held) => held switch
    {
        LocalVariable { Type: SzArrayTypeAnalysisContext array } => array.ElementType,
        LocalVariable { Type: ArrayTypeAnalysisContext { Rank: 1 } array } => array.ElementType,
        FieldReference { Field.FieldType: SzArrayTypeAnalysisContext array } => array.ElementType,
        FieldReference { Field.FieldType: ArrayTypeAnalysisContext { Rank: 1 } array } => array.ElementType,
        _ => null,
    };

    /// <summary>The array type an allocation was asked for, however the type reached the call.</summary>
    private static TypeAnalysisContext? Allocated(object operand)
    {
        var type = operand switch
        {
            LocalVariable { Type: RuntimeClassTypeAnalysisContext runtimeClass } => runtimeClass.RepresentedType,
            RuntimeClassTypeAnalysisContext runtimeClass => runtimeClass.RepresentedType,
            TypeAnalysisContext named => named,
            _ => null,
        };

        return type is SzArrayTypeAnalysisContext or ArrayTypeAnalysisContext ? type : null;
    }

    /// <summary>
    /// Types a local from the parameter it is handed to, overruling a runtime stand-in that is standing where
    /// a managed type belongs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A register reused for two unrelated things across a join is typed by whichever edge the inference
    /// reached first, and once it has any type at all the parameter it is passed as cannot correct it. In
    /// <c>GenerateSmartBlock</c> one local is written from six places - three <c>ECellColor[]</c> and three
    /// <c>List&lt;ECellColor&gt;</c> - and came out as <c>Il2CppMethodInfo</c>, so every one of those writes
    /// was a reference assigned to a runtime structure and none of them could be written down:
    /// </para>
    /// <code>
    /// //num51 = (nint)_junkPoolA;
    /// </code>
    /// <para>
    /// **Managed code never holds one of these.** A method's own <c>MethodInfo*</c> travels past the end of
    /// the declared parameters, which the caller already skips, so within the declared ones a stand-in is
    /// simply wrong and the parameter's own type is the answer.
    /// </para>
    /// </remarks>
    internal static bool SetTypeFromParameter(MethodAnalysisContext inside, LocalVariable local, TypeAnalysisContext? parameterType)
    {
        //Not inside a generic body. There the method's own `MethodInfo*` is what every runtime generic
        //context entry is reached through, so a local holding one is load-bearing however it is also passed -
        //overruling it took `RemoteConfigManager::Get` from twenty-one of thirty-six instructions to one, and
        //emptied three other generic extension methods outright. `GenerateSmartBlock`, which this is for, is
        //not generic.
        if (parameterType != null && !inside.GenericParameters.Any() && IsRuntimeStandIn(local.Type))
        {
            local.Type = parameterType;
            return true;
        }

        return SetTypeIfUnknown(local, parameterType);
    }

    /// <summary>Whether the type is one of the runtime's own structures rather than anything a program names.</summary>
    private static bool IsRuntimeStandIn(TypeAnalysisContext? type)
        => type is RuntimeMethodInfoAnalysisContext or RuntimeClassTypeAnalysisContext
            or StaticFieldStorageTypeAnalysisContext or RgctxTableTypeAnalysisContext
            or MethodRgctxTableTypeAnalysisContext;

    /// <summary>
    /// Whether this is the only place its destination is given a value.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Taking single assignment form apart turns every phi into a copy on each incoming edge, so a local
    /// written once in the source is written several times here - and a seed that stamps a type on a
    /// destination cannot tell those apart from a real definition. <c>SeedMethodInfoTypes</c> did not, and
    /// stamped <c>Il2CppMethodInfo</c> on a phi that merged a <c>methodof</c> edge with an ordinary
    /// <c>int</c>:
    /// </para>
    /// <code>
    /// -1 Move v143 (Il2CppMethodInfo), rows @ X1 (System.Int32)
    /// -1 Move v143 (Il2CppMethodInfo), methodof(HashSet`1&lt;Int32&gt;::Clear)
    /// </code>
    /// <para>
    /// A runtime <c>MethodInfo</c> lowers to a native int, so every local the propagation then reached was
    /// declared <c>nint</c> and every real assignment to it became an invalid cast: <b>111</b> statements of
    /// the form <c>num = (nint)array</c> across the export, and one of them made a whole array come out as
    /// <c>Il2CppMethodInfo[]</c> because the element type was read off the poisoned local.
    /// </para>
    /// </remarks>
    internal static bool StandsForOneValue(MethodAnalysisContext method, Instruction instruction)
    {
        if (instruction.Destination is not LocalVariable destination || method.ControlFlowGraph is not { } graph)
            return true;

        foreach (var other in graph.Instructions)
            if (!ReferenceEquals(other, instruction) && ReferenceEquals(other.Destination, destination))
                return false;

        return true;
    }

    /// <summary>
    /// Whether some call in this method puts its result in this local.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A receiver position says the value is *at least* the callee's declaring type, which is true and weak.
    /// A return type says what it is. Both reach <see cref="SetTypeIfUnknown"/>, which is monotonic, so
    /// whichever arrives first wins - and where the callee's own address carries two names the resolution is
    /// late, so the receiver position wins a race it should always lose.
    /// </para>
    /// <para>
    /// <c>SaveManager::SetLevel</c> is the case. <c>SaveBaseSingleton&lt;CommonSaveData&gt;.get_I</c> answers
    /// into <c>x0</c>, and clang leaves it there for <c>SetFinishLevel</c>, which never reads <c>this</c>. So
    /// the local was typed <c>SaveManager</c>, <c>CommonSaveData.level</c> at 0x50 resolved to
    /// <c>SaveManager.abilitySaveData</c> - also at 0x50 - and the method wrote a level number into a save
    /// object.
    /// </para>
    /// </remarks>
    private static bool AnsweredByACall(MethodAnalysisContext method, LocalVariable local)
    {
        if (method.ControlFlowGraph is not { } graph)
            return false;

        foreach (var instruction in graph.Instructions)
            if (instruction.OpCode is OpCode.Call or OpCode.IndirectCall
                && instruction.Operands.Count > 1
                && ReferenceEquals(instruction.Operands[1], local))
            {
                return true;
            }

        return false;
    }
}
