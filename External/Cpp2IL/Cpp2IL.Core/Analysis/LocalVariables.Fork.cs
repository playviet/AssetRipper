using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.ISIL;
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
        if (produced == null || value.Type is not { FullName: "System.Object" } || produced.FullName == "System.Object")
            return false;

        value.Type = produced;
        return true;
    }

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

        for (var i = 1; i <= last && i < arithmetic.Operands.Count; i++)
        {
            //An immediate with a fraction can only have come out of a floating point register, so it says what
            //the result is even where nothing else in the expression is typed yet.
            if (floatsMaySpeak && !addresses.Contains(destination) && FloatingPointConstant(arithmetic.Operands[i], method) is { } constant)
            {
                changed |= SetTypeIfUnknown(destination, constant);
                continue;
            }

            //A field says what it holds as plainly as a typed local does, and one side of an expression being
            //a field is the usual shape once a struct handed over a register at a time has been taken apart.
            if (arithmetic.Operands[i] is FieldReference read)
            {
                if (Travels(read.Field.FieldType, arithmetic.OpCode, floatsMaySpeak) && !addresses.Contains(destination))
                    changed |= SetTypeIfUnknown(destination, read.Field.FieldType);

                continue;
            }

            if (arithmetic.Operands[i] is not LocalVariable operand)
                continue;

            if (Travels(operand.Type, arithmetic.OpCode, floatsMaySpeak) && !addresses.Contains(destination))
                changed |= SetTypeIfUnknown(destination, operand.Type);

            if (Travels(destination.Type, arithmetic.OpCode, floatsMaySpeak) && !addresses.Contains(operand))
                changed |= SetTypeIfUnknown(operand, destination.Type);
        }

        return changed;
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
}
