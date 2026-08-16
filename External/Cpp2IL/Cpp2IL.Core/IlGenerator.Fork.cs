using System.Collections.Generic;
using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using AsmResolver.PE.DotNet.Metadata.Tables;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils.AsmResolver;
using LibCpp2IL.BinaryStructures;

namespace Cpp2IL.Core;

/// <summary>
/// The half of <see cref="IlGenerator"/> this fork adds: laying the blocks out in the order control takes,
/// keeping values on the evaluation stack instead of naming every one of them, folding the runtime helper
/// calls the code generator expanded back into the CIL instruction each one stands for, and filling in the
/// operands where a value's type and the place it is going do not agree.
///
/// It is kept apart from the file it belongs to so that the file stays as close to upstream as it can, and
/// a later version of Cpp2IL can be merged without the two sets of changes meeting.
/// </summary>
public static partial class IlGenerator
{
    /// <summary>
    /// Whether the operand is a value that only holds part of a struct - the first of the several registers
    /// a struct of floats travels in. Loading it as the struct would be a lie, and every use of it after
    /// that reads as one.
    /// </summary>
    private static bool SpansSeveralRegisters(object? operand)
        => operand is LocalVariable { Type: { } type } && Analysis.HomogeneousFloatStruct.SpansSeveralRegisters(type);

    /// <summary>
    /// The element an operand names, if it names one: the array, the index into it, and what an element is.
    ///
    /// Indexing an array compiles to the array's address plus the header plus the index scaled by the width
    /// of an element, and the analysis puts that back together as one operand. Here it becomes the one CIL
    /// instruction it was: nothing else can be written down, because the fields of an array header have no
    /// names a C# file may use.
    /// </summary>
    private static (object Array, object? Index, TypeAnalysisContext Element, FieldAnalysisContext? Inside)? ArrayElement(MemoryOperand memory, MethodAnalysisContext context)
    {
        var pointerSize = context.AppContext.Binary.is32Bit ? 4 : 8;
        var elements = context.AppContext.Binary.is32Bit ? 0x10 : 0x20;

        //An array reached straight out of a field is the same array; nothing about the access changes
        //because the reference was not first copied into a register the compiler happened to keep.
        object? array = memory.Base as LocalVariable;
        array ??= memory.Base as FieldReference;

        var arrayType = array switch
        {
            LocalVariable local => local.Type,
            FieldReference field => field.Field.FieldType,
            _ => null,
        };

        //A register whose type arrived through a by-reference still holds the array itself: the local declared
        //for it has the reference stripped already, so `ldloc` pushes a `bool[]` whatever the analysis called
        //it. `ComputeHighlights` had both `arr.Length` and `arr[ns] = true` refused - the store silently, as a
        //`pop`, so the highlight was never written and no marker said so.
        while (arrayType is ByRefTypeAnalysisContext { ElementType: { } behind })
            arrayType = behind;

        var element = arrayType switch
        {
            SzArrayTypeAnalysisContext szArray => szArray.ElementType,
            ArrayTypeAnalysisContext { Rank: 1 } singleDimension => singleDimension.ElementType,
            _ => null,
        };

        if (array == null || element == null)
            return null;

        if (memory.Addend == elements)
            return (array, memory.Index, element, FrontMember(memory, element, context));

        //A register subscript *and* a distance into the element. The scale already carried the element, so
        //what is left over is the member - `_cornersCache[c].y` is `[array + c*12 + 0x24]`, which is what a
        //loop that walks a `Vector3[]` by a stepped byte offset comes to once the offset is paired with its
        //counter. The constant-subscript path below has a subscript to divide out first; here there is none.
        if (memory.Index != null && memory.Scale > 0 && element.IsValueType
            && memory.Addend > elements && memory.Addend - elements < memory.Scale
            && FieldAt(element, (int)(memory.Addend - elements)) is { } member)
        {
            return (array, memory.Index, element, member);
        }

        //A constant subscript is folded into the one offset the load already had, so there is no index
        //register left to read it from - the distance past the first element is the subscript.
        if (memory.Index != null || memory.Addend < elements
            || ArrayTypeInference.Width(element, pointerSize) is not { } width || width == 0)
            return null;

        var past = memory.Addend - elements;
        var subscript = (int)(past / width);
        var within = (int)(past % width);

        if (within == 0)
            return (array, subscript, element, FrontMember(memory, element, context));

        //Not a whole number of elements past the start, which for an array of a struct is not a mistake: it
        //is one field of one element. `Vector3` is twelve bytes, so `positions[2].y` is thirty-one bytes past
        //the header and nothing about that is expressible as an element on its own. Reading the element and
        //then the field is, and it is the same two steps the compiler folded into one offset.
        if (!element.IsValueType || FieldAt(element, within) is not { } inside)
            return null;

        return (array, subscript, element, inside);
    }

    /// <summary>
    /// The member at the front of a struct element, where the read is of that member rather than of the
    /// whole element.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A struct's first field begins where the struct does, so a read at distance nought into an element of
    /// <c>Vector3[]</c> is <c>positions[0]</c> and <c>positions[0].x</c> written the same way. The offset
    /// cannot tell them apart and neither can ISIL, which carries no width - but the place the value lands
    /// can: nothing copies a <c>Vector3</c> into a <c>float</c>.
    /// </para>
    /// <para>
    /// Without this <c>_cornersBuf[0].x</c> came out as <c>(float)cornersBuf[0]</c> and was commented away
    /// while <c>.y</c>, four bytes further in and unambiguous, was kept.
    /// </para>
    /// </remarks>
    private static FieldAnalysisContext? FrontMember(MemoryOperand memory, TypeAnalysisContext element, MethodAnalysisContext context)
    {
        //An enum has one instance field, `value__`, holding the whole of it - so a number is exactly what a
        //read of the element produces and there is nothing in front of anything. Naming it is also not
        //something C# will accept: `primaryTargets[(int)blockCat].value__` is what
        //`BoardLogic::CheckAndMergeAll` came out as, commented, twice. The guard below catches a struct whose
        //first field is of its own type; an enum's is of the *underlying* type, so it went straight past.
        if (element.IsEnumType)
            return null;

        if (!element.IsValueType || context.ControlFlowGraph is not { } graph
            || FieldAt(element, 0) is not { } front || ReferenceEquals(front.FieldType, element))
            return null;

        foreach (var instruction in graph.Instructions)
        {
            //A copy is the plain case, and arithmetic is the same evidence: what an instruction *writes*
            //discriminates as well as what sits beside it. `Rect.MinMaxRect(c[0].x, .., c[2].x, ..)` inlines
            //to a copy for one corner and a subtraction for the next, and only the copy was believed - so the
            //element the subtraction reads was loaded whole and the whole statement went.
            //Not a call: an argument may be a struct passed entire to something that answers with a number,
            //and Vector3.Distance would then have said its first argument was an `x`.
            //A *store* is the same evidence the other way round: there the memory operand is `Operands[0]`
            //and the value is beside it. Looking only at what an instruction reads meant a store never
            //matched at all, so an element assigned one float was reported as the whole element:
            //
            //    //array[0] = (Vector3)num7;     <- the store at +0x20, commented out
            //    array[0].y = y;                 <-        +0x24, kept
            //    array[0].z = z;                 <-        +0x28, kept
            //
            //`GizmosDrawer::DrawPoint` is that, and so are 140 element-base stores across the game.
            if (instruction.OpCode == OpCode.Move && instruction.Operands.Count == 2
                && instruction.Operands[0] is MemoryOperand into && Same(into, memory)
                && instruction.Operands[1] is LocalVariable { Type: { } stored }
                && !ReferenceEquals(stored, element) && Analysis.StructInArithmetic.IsNumber(stored))
                return front;

            if (instruction.OpCode is not (OpCode.Move or OpCode.Add or OpCode.Subtract or OpCode.Multiply or OpCode.Divide)
                || instruction.Operands.Count < 2
                || instruction.Operands[0] is not LocalVariable { Type: { } wanted })
                continue;

            var reads = false;

            for (var i = 1; i < instruction.Operands.Count && !reads; i++)
                reads = instruction.Operands[i] is MemoryOperand read && Same(read, memory);

            //A number where a struct would go, and the number the front member is.
            if (reads && !ReferenceEquals(wanted, element) && Analysis.StructInArithmetic.IsNumber(wanted))
                return front;
        }

        return null;
    }

    /// <summary>Whether two memory operands name the same place.</summary>
    private static bool Same(MemoryOperand one, MemoryOperand other)
        => Equals(one.Base, other.Base) && Equals(one.Index, other.Index)
            && one.Addend == other.Addend && one.Scale == other.Scale;

    /// <summary>The instance field of a struct that begins at an offset, if one does.</summary>
    private static FieldAnalysisContext? FieldAt(TypeAnalysisContext type, int offset)
    {
        foreach (var candidate in type.Fields)
        {
            if (!candidate.IsStatic && candidate.Offset == offset)
                return candidate;
        }

        return null;
    }

    /// <summary>
    /// Reads one element of an array of more than one dimension, by the indices rather than by a distance.
    /// </summary>
    /// <remarks>
    /// Such an array is indexed through methods the runtime gives it rather than by <c>ldelem</c> - there is no
    /// instruction that takes more than one index. They are not declared anywhere to be found, so the reference
    /// is built here from the array's own signature, which is what the language does too.
    /// </remarks>
    private static void LoadMultiDimensionalElement(MultiDimensionalElement element, MethodDefinition method,
        Dictionary<LocalVariable, CilLocalVariable> locals)
    {
        var module = method.DeclaringModule!;
        var instructions = method.CilMethodBody!.Instructions;

        LoadArray(element.Array, method, locals);

        foreach (var index in element.Indices)
            LoadIndex(index, method, locals);

        instructions.Add(CilOpCodes.Call, Accessor(element, module, "Get"));
    }

    /// <summary>The <c>Get</c> or <c>Set</c> an array of this shape is indexed through.</summary>
    private static IMethodDescriptor Accessor(MultiDimensionalElement element, ModuleDefinition module, string name)
    {
        var array = element.ArrayType.ToTypeSignature(module);
        var value = element.ArrayType.ElementType.ToTypeSignature(module);
        var integer = module.CorLibTypeFactory.Int32;
        var indices = new TypeSignature[element.Indices.Length];

        for (var index = 0; index < indices.Length; index++)
            indices[index] = integer;

        var signature = name == "Get"
            ? MethodSignature.CreateInstance(value, indices)
            : MethodSignature.CreateInstance(module.CorLibTypeFactory.Void, [.. indices, value]);

        return array.ToTypeDefOrRef().CreateMemberReference(name, signature).ImportWith(module.DefaultImporter);
    }

    private static bool TryLoadArrayElement(MemoryOperand memory, MethodDefinition method,
        Dictionary<LocalVariable, CilLocalVariable> locals)
    {
        if (CurrentContext is not { } context || ArrayElement(memory, context) is not { } element)
            return false;

        var instructions = method.CilMethodBody!.Instructions;

        LoadArray(element.Array, method, locals);
        LoadIndex(element.Index, method, locals);

        //A field of an element is reached through the element's address, which is what `ldelema` is for.
        if (element.Inside is { } inside)
        {
            instructions.Add(CilOpCodes.Ldelema, method.DeclaringModule!.DefaultImporter.ImportType(element.Element.ToTypeSignature(method.DeclaringModule!).ToTypeDefOrRef()));
            instructions.Add(CilOpCodes.Ldfld, inside.ToFieldDescriptor(method.DeclaringModule!));
            return true;
        }

        instructions.Add(CilOpCodes.Ldelem, method.DeclaringModule!.DefaultImporter.ImportType(element.Element.ToTypeSignature(method.DeclaringModule!).ToTypeDefOrRef()));
        return true;
    }

    /// <summary>
    /// Writes a value through a by-reference parameter of the method being generated, rather than into a
    /// local that shares its name. Answers whether it did.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every ISIL local gets a <see cref="CilLocalVariable"/> of its own, parameters included.
    /// <c>LoadLocal</c> notices that a local names a parameter and emits <c>ldarg</c>; nothing on the storing
    /// side ever did, so <c>Move [pos @ X1 (UnityEngine.Vector3&amp;)], v71.x</c> came out as a store into a
    /// shadow local - and where the addend was not nought, as a <c>pop</c>.
    /// </para>
    /// <para>
    /// So <b>no recovered method has ever written its own <c>out</c> or <c>ref</c> parameter</b>. Twenty-nine
    /// of the thirty-eight in this game hand back the zero the caller cleared the slot with:
    /// <c>TryGetLookAtWorldPosition</c>, <c>TryGetBoardWorldRect</c>, every <c>InterstitialGate</c> method.
    /// All five compilability scorers rate those bodies whole, which is why this survived so long - only an
    /// execution oracle can see it.
    /// </para>
    /// <para>
    /// A store through a by-reference is one machine word, so an addend inside a struct names the member at
    /// that distance and an addend of nought names the member at the front - the same reading
    /// <see cref="FrontMember"/> makes of the load. A whole struct does not travel this way; the compiler
    /// copies it, which is a different shape entirely.
    /// </para>
    /// </remarks>
    private static bool TryStoreThroughByRef(MemoryOperand memory, MethodDefinition method,
        Dictionary<LocalVariable, CilLocalVariable> locals)
    {
        if (memory is not { Index: null, Scale: 0, Base: LocalVariable slot }
            || slot.Type is not ByRefTypeAnalysisContext { ElementType: { } pointee }
            || CurrentContext is not { } context)
        {
            return false;
        }

        if (method.Parameters.FirstOrDefault(p => p.Name == slot.Name) is not { } parameter
            || parameter.ParameterType is not ByReferenceTypeSignature)
        {
            return false;
        }

        var module = method.DeclaringModule!;
        var instructions = method.CilMethodBody!.Instructions;

        //A value type with members of its own is written one member at a time - unless what is being stored
        //*is* one of those structs, which `SubCellVisual::TryGetLookPosition` does: `pos = _lookPos` in one
        //move. Taking the front member there wrote a `Vector3` into a `float`.
        if (pointee.IsValueType && !IsAPrimitive(pointee) && !StoringTheWholeOf(pointee))
        {
            if (MemberAt(pointee, memory.Addend, context) is not { } member)
                return false;

            var into = member.ToFieldDescriptor(module);
            var held = new CilLocalVariable(into.Signature!.FieldType);
            method.CilMethodBody!.LocalVariables.Add(held);

            //`stfld` wants the pointer underneath the value, and the value is already on the stack.
            instructions.Add(CilOpCodes.Stloc, held);
            instructions.Add(CilOpCodes.Ldarg, parameter);
            instructions.Add(CilOpCodes.Ldloc, held);
            instructions.Add(CilOpCodes.Stfld, into);
            return true;
        }

        if (memory.Addend != 0)
            return false;

        var signature = pointee.ToTypeSignature(module);
        var value = new CilLocalVariable(signature);
        method.CilMethodBody!.LocalVariables.Add(value);

        instructions.Add(CilOpCodes.Stloc, value);
        instructions.Add(CilOpCodes.Ldarg, parameter);
        instructions.Add(CilOpCodes.Ldloc, value);
        //`stind.ref` takes no operand at all; `stobj` names the type it writes.
        if (pointee.IsValueType)
            instructions.Add(CilOpCodes.Stobj, module.DefaultImporter.ImportType(signature.ToTypeDefOrRef()));
        else
            instructions.Add(CilOpCodes.Stind_Ref);

        return true;
    }

    /// <summary>
    /// Reads through this method's own by-reference parameter, rather than loading the address itself.
    /// Answers whether it did.
    /// </summary>
    /// <remarks>
    /// The read-side twin of <see cref="TryStoreThroughByRef"/>, and it was simply missing: the store knew
    /// that <c>[reason @ X3 (System.String&amp;)]</c> means "the string reason points at", and the load did not,
    /// so <c>LoadOperand</c> fell into the branch that loads the local - which for a by-ref parameter puts the
    /// <i>address</i> on the stack. The decompiler wrote that back as
    /// <c>Unsafe.AsPointer(ref reason)</c> and commented the statement out.
    /// <para>
    /// Seven bodies are blocked by nothing else, four of them the whole of
    /// <c>InterstitialGate</c> - whose condition chains all survive and which lose only their one call to
    /// <c>Log(reason, …)</c>.
    /// </para>
    /// <para>
    /// The by-ref test has to come first, and has to require a real parameter of this method: the branch it
    /// precedes legitimately handles a slot-address local, where loading the local <i>is</i> the value.
    /// </para>
    /// </remarks>
    private static bool TryLoadThroughByRef(MemoryOperand memory, MethodDefinition method,
        Dictionary<LocalVariable, CilLocalVariable> locals)
    {
        if (memory is not { Index: null, Scale: 0, Base: LocalVariable slot }
            || slot.Type is not ByRefTypeAnalysisContext { ElementType: { } pointee }
            || CurrentContext is not { } context)
        {
            return false;
        }

        if (method.Parameters.FirstOrDefault(p => p.Name == slot.Name) is not { } parameter
            || parameter.ParameterType is not ByReferenceTypeSignature)
        {
            return false;
        }

        var module = method.DeclaringModule!;
        var instructions = method.CilMethodBody!.Instructions;

        //A member of the struct it points at, rather than the whole of it.
        if (memory.Addend != 0)
        {
            if (!pointee.IsValueType || IsAPrimitive(pointee)
                || MemberAt(pointee, memory.Addend, context) is not { } member)
            {
                return false;
            }

            instructions.Add(CilOpCodes.Ldarg, parameter);
            instructions.Add(CilOpCodes.Ldfld, member.ToFieldDescriptor(module));
            return true;
        }

        var signature = pointee.ToTypeSignature(module);

        instructions.Add(CilOpCodes.Ldarg, parameter);

        //`ldind.ref` takes no operand at all; `ldobj` names the type it reads.
        if (pointee.IsValueType)
            instructions.Add(CilOpCodes.Ldobj, module.DefaultImporter!.ImportType(signature.ToTypeDefOrRef()));
        else
            instructions.Add(CilOpCodes.Ldind_Ref);

        return true;
    }

    /// <summary>
    /// Hands a callee this method's own by-reference parameter, which is already the address it wants.
    /// Answers whether it did.
    /// </summary>
    /// <remarks>
    /// The scratch local below is the right answer where nothing produces an address, but a by-ref parameter
    /// *is* one - and passing a copy of what it refers to means the callee fills in a local nobody reads.
    /// `TryGetLookAtWorldPosition` ends in `return TryGetPressedPointerWorldPosition(out pos)`, which handed
    /// the inner call a fresh `Vector3` and returned `pos` untouched.
    /// </remarks>
    private static bool TryLoadByRefParameter(object? argument, MethodDefinition method)
    {
        if (argument is not LocalVariable { Type: ByRefTypeAnalysisContext } slot
            || method.Parameters.FirstOrDefault(p => p.Name == slot.Name) is not { } parameter
            || parameter.ParameterType is not ByReferenceTypeSignature)
        {
            return false;
        }

        method.CilMethodBody!.Instructions.Add(CilOpCodes.Ldarg, parameter);
        return true;
    }

    /// <summary>Whether the value being stored is a whole one of these rather than a member of one.</summary>
    private static bool StoringTheWholeOf(TypeAnalysisContext pointee)
        => CurrentInstruction is { Operands: [_, { } source, ..] }
            && TypeOfOperand(source)?.FullName == pointee.FullName;

    /// <summary>The member of a struct at that distance into it, at whichever convention it was recorded.</summary>
    /// <remarks>
    /// <see cref="MetadataResolver.FieldOfStructValue"/> adds the object header, because il2cpp records a
    /// struct's fields at the offsets they sit at in a boxed one. This build does not, for these types:
    /// <c>probe fields Vector3</c> says <c>x</c> at 0, <c>y</c> at 4, <c>z</c> at 8, with the header showing
    /// only in <c>instance=1C</c>. Asking the boxed way alone found no member at any of the three, which is
    /// why the first attempt at this wrote nothing for a <c>Vector3</c> out parameter. Both are tried, raw
    /// first, the same way <see cref="Analysis.PackedStructFieldRead"/> reads a packed struct's fields.
    /// </remarks>
    private static FieldAnalysisContext? MemberAt(TypeAnalysisContext type, long offset, MethodAnalysisContext context)
    {
        var definition = (type as GenericInstanceTypeAnalysisContext)?.GenericType ?? type;

        foreach (var field in definition.Fields)
            if (!field.IsStatic && field.BackingData?.FieldOffset == offset)
                return type is GenericInstanceTypeAnalysisContext instance
                    ? new ConcreteGenericFieldAnalysisContext(field, instance)
                    : field;

        return MetadataResolver.FieldOfStructValue(type, offset, context);
    }

    /// <summary>A value that is one number rather than a thing with members.</summary>
    private static bool IsAPrimitive(TypeAnalysisContext type)
        => type.IsEnumType || type.FullName is "System.Boolean" or "System.Char"
            or "System.SByte" or "System.Byte" or "System.Int16" or "System.UInt16"
            or "System.Int32" or "System.UInt32" or "System.Int64" or "System.UInt64"
            or "System.Single" or "System.Double" or "System.IntPtr" or "System.UIntPtr";

    private static bool TryStoreArrayElement(MemoryOperand memory, MethodDefinition method,
        Dictionary<LocalVariable, CilLocalVariable> locals)
    {
        if (CurrentContext is not { } context || ArrayElement(memory, context) is not { } element)
            return false;

        var module = method.DeclaringModule!;
        var instructions = method.CilMethodBody!.Instructions;

        //Both forms want the array and the index underneath the value, and the value is already on the stack.
        var scratch = new CilLocalVariable(element.Inside is { } stored
            ? stored.FieldType.ToTypeSignature(module)
            : element.Element.ToTypeSignature(module));

        method.CilMethodBody!.LocalVariables.Add(scratch);

        instructions.Add(CilOpCodes.Stloc, scratch);
        LoadArray(element.Array, method, locals);
        LoadIndex(element.Index, method, locals);

        if (element.Inside is { } inside)
        {
            instructions.Add(CilOpCodes.Ldelema, module.DefaultImporter.ImportType(element.Element.ToTypeSignature(module).ToTypeDefOrRef()));
            instructions.Add(CilOpCodes.Ldloc, scratch);
            instructions.Add(CilOpCodes.Stfld, inside.ToFieldDescriptor(module));
            return true;
        }

        instructions.Add(CilOpCodes.Ldloc, scratch);
        instructions.Add(CilOpCodes.Stelem, module.DefaultImporter.ImportType(element.Element.ToTypeSignature(module).ToTypeDefOrRef()));
        return true;
    }

    /// <summary>An operand with no index is element zero, which is how a pointer to the first one reads.</summary>
    private static void LoadIndex(object? index, MethodDefinition method, Dictionary<LocalVariable, CilLocalVariable> locals)
    {
        switch (index)
        {
            case LocalVariable local:
                LoadLocal(local, method, locals);
                break;
            case int constant:
                method.CilMethodBody!.Instructions.Add(CilOpCodes.Ldc_I4, constant);
                break;
            case FieldReference:
                LoadArray(index, method, locals);
                break;
            default:
                method.CilMethodBody!.Instructions.Add(CilOpCodes.Ldc_I4_0);
                break;
        }
    }

    /// <summary>The array itself, wherever it was being held.</summary>
    private static void LoadArray(object array, MethodDefinition method, Dictionary<LocalVariable, CilLocalVariable> locals)
    {
        switch (array)
        {
            case LocalVariable local:
                LoadLocal(local, method, locals);
                break;
            case NestedFieldReference nested:
                LoadNestedField(nested, method, locals, method.DeclaringModule!);
                break;
            case FieldReference field when field.Field.IsStatic:
                method.CilMethodBody!.Instructions.Add(CilOpCodes.Ldsfld, field.Field.ToFieldDescriptor(method.DeclaringModule!));
                break;
            case FieldReference field:
                LoadLocal(field.Local, method, locals);
                method.CilMethodBody!.Instructions.Add(CilOpCodes.Ldfld, field.Field.ToFieldDescriptor(method.DeclaringModule!));
                break;
        }
    }

    /// <summary>
    /// The method being generated, so that the pieces that only know about operands can still ask what the
    /// binary looks like. Generation is one method at a time on one thread, so a value per thread is enough.
    /// </summary>
    [System.ThreadStatic]
    private static MethodAnalysisContext? CurrentContext;

    /// <summary>
    /// The instruction being generated, for the one question a destination cannot answer on its own: what is
    /// on the evaluation stack. A store through a by-reference is a member of the struct when what it stores
    /// is one number, and the whole of it when it is the struct - and only the source operand says which.
    /// </summary>
    [System.ThreadStatic]
    private static Instruction? CurrentInstruction;

    /// <summary>
    /// Two CIL instructions that say the same thing are equal, and before offsets are worked out a body is
    /// full of instructions that say the same thing - every <c>nop</c>, every <c>ldc.i4 0</c>. Position is
    /// what these passes are about, so they have to tell one from another by which object it is.
    /// </summary>
    private sealed class ByIdentity : IEqualityComparer<CilInstruction>
    {
        public static readonly ByIdentity Instance = new();

        public bool Equals(CilInstruction? left, CilInstruction? right) => ReferenceEquals(left, right);

        public int GetHashCode(CilInstruction instruction) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(instruction);
    }

    /// <summary>
    /// The order to write the blocks out in: each one followed, where it can be, by the block control reaches
    /// next. The graph numbers its blocks in the order they were discovered, which has little to do with the
    /// order they run in, and writing them out that way separates a value from its use by the whole method.
    ///
    /// Every block ends with a jump to its successor written out in full - nothing here relies on one block
    /// falling into the next - so the order is free to be chosen, and choosing the order control takes lets
    /// the jump between neighbours be dropped and the value travel on the stack.
    /// </summary>
    private static List<Block> LayoutOrder(ISILControlFlowGraph graph)
    {
        var order = new List<Block>();
        var seen = new HashSet<Block>();
        var pending = new Stack<Block>();

        pending.Push(graph.EntryBlock);

        while (pending.Count > 0)
        {
            var block = pending.Pop();

            if (!seen.Add(block))
                continue;

            order.Add(block);

            //Pushed in reverse so that the first successor is the one taken off next, and so ends up adjacent.
            var successors = SuccessorsInLayoutOrder(block, graph).ToList();

            for (var i = successors.Count - 1; i >= 0; i--)
                pending.Push(successors[i]);
        }

        //A block the walk never reached is still written out, so that nothing is silently dropped.
        foreach (var block in graph.Blocks)
        {
            if (seen.Add(block))
                order.Add(block);
        }

        return order;
    }

    /// <summary>
    /// A block's successors, with the one reached by falling past the end first. For a two-way branch that is
    /// the one the condition does not name, which is the block the generated jump would otherwise have to
    /// reach around.
    /// </summary>
    private static IEnumerable<Block> SuccessorsInLayoutOrder(Block block, ISILControlFlowGraph graph)
    {
        if (block.Instructions.Count == 0 || block.Instructions[^1].OpCode != OpCode.ConditionalJump)
            return block.Successors;

        var taken = TryResolveJumpTargetBlock(block.Instructions[^1], graph);

        return taken == null
            ? block.Successors
            : block.Successors.Where(s => s != taken).Concat(block.Successors.Where(s => s == taken));
    }

    /// <summary>
    /// Removes a jump whose destination is the instruction after it.
    ///
    /// Each block of the graph is emitted with the jump to its successor spelled out, even where that
    /// successor comes next and nothing else reaches it. The jump changes nothing, but it stands between the
    /// instruction that produced a value and the one that consumes it, and it makes the instruction after it
    /// a place that is jumped to - so the value has to be parked in a local to get across, which is what
    /// filled the recovered methods with variables that are read once and named after a register.
    /// </summary>
    private static void RemoveBranchesToTheFollowingInstruction(CilMethodBody body, Dictionary<Block, CilInstruction> blockEntryMap)
    {
        var instructions = body.Instructions;

        for (var i = instructions.Count - 2; i >= 0; i--)
        {
            var branch = instructions[i];

            if (branch.OpCode.Code is not (CilCode.Br or CilCode.Br_S))
                continue;

            if (branch.Operand is not CilInstructionLabel { Instruction: { } target } || target != instructions[i + 1])
                continue;

            //Anything that named the jump has to be told where it went, or it would name nothing.
            Retarget(body, blockEntryMap, branch, instructions[i + 1]);
            instructions.RemoveAt(i);
        }
    }

    /// <summary>Points everything that referred to one instruction at another.</summary>
    private static void Retarget(CilMethodBody body, Dictionary<Block, CilInstruction> blockEntryMap, CilInstruction from, CilInstruction to)
    {
        foreach (var instruction in body.Instructions)
        {
            switch (instruction.Operand)
            {
                case CilInstructionLabel { Instruction: { } single } when single == from:
                    instruction.Operand = new CilInstructionLabel(to);
                    break;

                case IList<ICilLabel> labels:
                    for (var i = 0; i < labels.Count; i++)
                    {
                        if (labels[i] is CilInstructionLabel { Instruction: { } branchTarget } && branchTarget == from)
                            labels[i] = new CilInstructionLabel(to);
                    }
                    break;
            }
        }

        foreach (var block in blockEntryMap.Keys.ToList())
        {
            if (blockEntryMap[block] == from)
                blockEntryMap[block] = to;
        }
    }

    /// <summary>
    /// Moves the plain loads that open an operation ahead of the value written immediately before them, so
    /// that the value meets its use with nothing in between.
    ///
    /// <see cref="KeepSingleUseValuesOnStack"/> can only drop a store whose load comes next, and the load
    /// rarely does: an operation pushes its operands left to right, so a value computed last - the last
    /// argument of a call, the right side of a comparison - is loaded after the ones before it, and the
    /// store and the load end up a receiver apart. That is enough to keep the local, and a local in the
    /// middle of an argument list stops the decompiler folding anything around it, so
    /// <c>TutorialMenu.I.Show()</c> came back as three statements and two named variables.
    ///
    /// What sits in between is only ever loads of locals, arguments and literals. None of those can be
    /// changed by the value's own computation, and none of them can fail, so running them first computes
    /// the same operands in the same order the source wrote them in - a receiver is evaluated before its
    /// arguments. Only a run that a branch cannot land in the middle of is moved, and only when the value
    /// is written and read once, so the local it leaves behind is the one about to be dropped.
    /// </summary>
    private static void MoveArgumentLoadsAheadOfTheValueTheyFollow(CilMethodBody body,
        Dictionary<Instruction, List<CilInstruction>> instructionMap)
    {
        //Which ISIL instruction wrote each piece of CIL, named by the first piece it wrote. Only a whole
        //one of these is moved past: half of an operation is not a value.
        var producedBy = new Dictionary<CilInstruction, CilInstruction>(ByIdentity.Instance);

        foreach (var generated in instructionMap.Values)
        {
            if (generated.Count == 0)
                continue;

            foreach (var instruction in generated)
                producedBy[instruction] = generated[0];
        }

        var instructions = body.Instructions.ToList();
        var jumpedTo = BranchTargets(instructions);
        var stores = new Dictionary<CilLocalVariable, int>();
        var loads = new Dictionary<CilLocalVariable, int>();

        foreach (var instruction in instructions)
        {
            if (LocalOf(instruction, CilOpCodes.Stloc) is { } stored)
                stores[stored] = stores.GetValueOrDefault(stored) + 1;
            else if (LocalOf(instruction, CilOpCodes.Ldloc) is { } loaded)
                loads[loaded] = loads.GetValueOrDefault(loaded) + 1;
            else if (instruction.OpCode == CilOpCodes.Ldloca && instruction.Operand is CilLocalVariable addressed)
                stores[addressed] = int.MaxValue;
        }

        var moved = false;

        for (var store = 0; store < instructions.Count; store++)
        {
            if (LocalOf(instructions[store], CilOpCodes.Stloc) is not { } local)
                continue;

            if (stores.GetValueOrDefault(local) != 1 || loads.GetValueOrDefault(local) != 1)
                continue;

            var load = store + 1;

            //The load being waited for is itself a plain one, so the run has to stop at it rather than
            //take it in.
            while (load < instructions.Count && LocalOf(instructions[load], CilOpCodes.Ldloc) != local
                && IsPlainLoad(instructions[load]))
                load++;

            //Nothing to move, or what follows the run is not the load this store is waiting for.
            if (load == store + 1 || load >= instructions.Count || LocalOf(instructions[load], CilOpCodes.Ldloc) != local)
                continue;

            if (!TryFindProducer(instructions, producedBy, store, out var producer))
                continue;

            //A value handed straight from one operation to the next belongs to both: inserting between them
            //would separate a store from the load that was about to make it unnecessary, so the pair is taken
            //in and the run goes ahead of the whole chain.
            while (producer > 0
                && LocalOf(instructions[producer - 1], CilOpCodes.Stloc) is { } carried
                && LocalOf(instructions[producer], CilOpCodes.Ldloc) == carried
                && stores.GetValueOrDefault(carried) == 1 && loads.GetValueOrDefault(carried) == 1
                && TryFindProducer(instructions, producedBy, producer - 1, out var earlier))
            {
                producer = earlier;
            }

            if (Enumerable.Range(producer, load - producer + 1).Any(index => jumpedTo.Contains(instructions[index])))
                continue;

            if (LoadsSomethingTheProducerWrites(instructions, producer, store, load))
                continue;

            var run = instructions.GetRange(store + 1, load - store - 1);
            instructions.RemoveRange(store + 1, run.Count);
            instructions.InsertRange(producer, run);
            moved = true;
            store = load - 1;
        }

        if (!moved)
            return;

        body.Instructions.Clear();

        foreach (var instruction in instructions)
            body.Instructions.Add(instruction);
    }

    /// <summary>
    /// Where the ISIL instruction that ends at <paramref name="store"/> begins, if the whole of it lies in
    /// one unbroken run ending there and computes rather than branches.
    /// </summary>
    private static bool TryFindProducer(List<CilInstruction> instructions,
        Dictionary<CilInstruction, CilInstruction> producedBy, int store, out int producer)
    {
        producer = store;

        if (!producedBy.TryGetValue(instructions[store], out var start))
            return false;

        while (producer > 0 && producedBy.TryGetValue(instructions[producer - 1], out var previous) && previous == start)
            producer--;

        if (instructions[producer] != start)
            return false;

        for (var index = producer; index <= store; index++)
        {
            if (!producedBy.TryGetValue(instructions[index], out var owner) || owner != start)
                return false;

            //Control has to reach the end of the run for the value to exist at all, and an argument the run
            //writes is not one the loads may be moved above.
            if (instructions[index].OpCode.FlowControl is not (CilFlowControl.Next or CilFlowControl.Call))
                return false;

            if (instructions[index].OpCode.Code is CilCode.Starg or CilCode.Starg_S)
                return false;
        }

        return true;
    }

    /// <summary>Whether the run being moved reads a local the value's computation writes.</summary>
    private static bool LoadsSomethingTheProducerWrites(List<CilInstruction> instructions, int producer, int store, int load)
    {
        var written = new HashSet<CilLocalVariable>();

        for (var index = producer; index <= store; index++)
        {
            if (LocalOf(instructions[index], CilOpCodes.Stloc) is { } stored)
                written.Add(stored);
        }

        for (var index = store + 1; index < load; index++)
        {
            if (LocalOf(instructions[index], CilOpCodes.Ldloc) is { } loaded && written.Contains(loaded))
                return true;
        }

        return false;
    }

    /// <summary>
    /// A push of something already sitting somewhere: a local, an argument, or a literal. None of these can
    /// be altered by anything a method body does to memory, so where they are read does not matter.
    /// </summary>
    private static bool IsPlainLoad(CilInstruction instruction) =>
        instruction.OpCode.Code is CilCode.Nop
            or CilCode.Ldloc or CilCode.Ldloc_S or CilCode.Ldloc_0 or CilCode.Ldloc_1 or CilCode.Ldloc_2 or CilCode.Ldloc_3
            or CilCode.Ldarg or CilCode.Ldarg_S or CilCode.Ldarg_0 or CilCode.Ldarg_1 or CilCode.Ldarg_2 or CilCode.Ldarg_3
            or CilCode.Ldc_I4 or CilCode.Ldc_I4_S or CilCode.Ldc_I4_M1 or CilCode.Ldc_I4_0 or CilCode.Ldc_I4_1
            or CilCode.Ldc_I4_2 or CilCode.Ldc_I4_3 or CilCode.Ldc_I4_4 or CilCode.Ldc_I4_5 or CilCode.Ldc_I4_6
            or CilCode.Ldc_I4_7 or CilCode.Ldc_I4_8 or CilCode.Ldc_I8 or CilCode.Ldc_R4 or CilCode.Ldc_R8
            or CilCode.Ldnull or CilCode.Ldstr or CilCode.Ldtoken;

    /// <summary>The instructions something branches to, which no run may be moved across.</summary>
    private static HashSet<CilInstruction> BranchTargets(IEnumerable<CilInstruction> instructions)
    {
        var jumpedTo = new HashSet<CilInstruction>(ByIdentity.Instance);

        foreach (var instruction in instructions)
        {
            switch (instruction.Operand)
            {
                case CilInstructionLabel { Instruction: { } target }:
                    jumpedTo.Add(target);
                    break;
                case IEnumerable<ICilLabel> labels:
                    foreach (var label in labels)
                    {
                        if (label is CilInstructionLabel { Instruction: { } switchTarget })
                            jumpedTo.Add(switchTarget);
                    }
                    break;
            }
        }

        return jumpedTo;
    }

    /// <summary>
    /// Removes a local that only carries a value from the instruction that produced it to the one that
    /// consumes it, leaving the value on the evaluation stack instead.
    ///
    /// Every value here is given a local of its own, so a call whose result is used once is written out,
    /// stored, and loaded straight back. The decompiler will fold a value it finds on the stack into the
    /// expression that uses it, but it keeps a local as a local - so what the source wrote as
    /// <c>SaveManager.I.GetLevel()</c> came back as two named variables and two statements, and a method
    /// of a few lines as a dozen.
    ///
    /// A store followed immediately by a load of the same local is exactly the identity on the stack, so
    /// dropping the pair is value-preserving. It is only dropped when the local is written and read once
    /// in the whole body - otherwise the local is still needed - and when neither instruction is jumped
    /// to, since removing an instruction a branch names would leave the branch pointing nowhere.
    /// </summary>
    private static void KeepSingleUseValuesOnStack(CilMethodBody body)
    {
        var instructions = body.Instructions.ToList();
        var stores = new Dictionary<CilLocalVariable, int>();
        var loads = new Dictionary<CilLocalVariable, int>();

        foreach (var instruction in instructions)
        {
            if (LocalOf(instruction, CilOpCodes.Stloc) is { } stored)
                stores[stored] = stores.GetValueOrDefault(stored) + 1;
            else if (LocalOf(instruction, CilOpCodes.Ldloc) is { } loaded)
                loads[loaded] = loads.GetValueOrDefault(loaded) + 1;
            else if (instruction.OpCode == CilOpCodes.Ldloca && instruction.Operand is CilLocalVariable addressed)
            {
                //Its address escapes, so what happens to it cannot be seen from here.
                stores[addressed] = int.MaxValue;
            }
        }

        //Only what something actually branches to counts. Where a block begins is not itself a reason: the
        //blocks were laid out in order and the jumps between neighbours have already been dropped, so a block
        //nothing jumps to is simply the code that follows.
        var jumpedTo = BranchTargets(instructions);
        var removed = new HashSet<int>();

        for (var i = 0; i < instructions.Count - 1; i++)
        {
            if (LocalOf(instructions[i], CilOpCodes.Stloc) is not { } local)
                continue;

            //Instructions that fold into their neighbours leave a nop behind, and a nop between the store and
            //the load says nothing about either.
            var load = i + 1;

            while (load < instructions.Count && instructions[load].OpCode == CilOpCodes.Nop)
                load++;

            if (load >= instructions.Count || LocalOf(instructions[load], CilOpCodes.Ldloc) != local)
                continue;

            if (stores.GetValueOrDefault(local) != 1 || loads.GetValueOrDefault(local) != 1)
                continue;

            if (Enumerable.Range(i, load - i + 1).Any(index => jumpedTo.Contains(instructions[index])))
                continue;

            for (var index = i; index <= load; index++)
                removed.Add(index);

            body.LocalVariables.Remove(local);
            i = load;
        }

        if (removed.Count == 0)
            return;

        //By position, not by value: an instruction is equal to any other that says the same thing, so asking
        //the body to remove one of them removes whichever comes first.
        body.Instructions.Clear();

        for (var index = 0; index < instructions.Count; index++)
        {
            if (!removed.Contains(index))
                body.Instructions.Add(instructions[index]);
        }
    }

    /// <summary>
    /// Throws away a value written into a local nothing ever reads, instead of naming it.
    ///
    /// A method whose result the caller ignores still returns one, and the lifter gives that result a
    /// register like any other, so the body ends up storing it. Nothing reads it, but the decompiler will
    /// not drop a store on its own account, and writes it out as a declaration - <c>Coroutine coroutine =
    /// self.InvokeDelay(...);</c> for a line the source wrote as a call and nothing else.
    ///
    /// Discarding the value says the same thing and needs no name for it. Only a local that no instruction
    /// reads and whose address is never taken is treated this way, so what is thrown away is a value that
    /// had nowhere to go.
    /// </summary>
    private static void DiscardValuesNothingReads(CilMethodBody body)
    {
        var stores = new Dictionary<CilLocalVariable, int>();
        var read = new HashSet<CilLocalVariable>();

        foreach (var instruction in body.Instructions)
        {
            if (LocalOf(instruction, CilOpCodes.Stloc) is { } stored)
                stores[stored] = stores.GetValueOrDefault(stored) + 1;
            else if (LocalOf(instruction, CilOpCodes.Ldloc) is { } loaded)
                read.Add(loaded);
            else if (instruction.OpCode == CilOpCodes.Ldloca && instruction.Operand is CilLocalVariable addressed)
                read.Add(addressed);
        }

        var discarded = new List<CilLocalVariable>();

        foreach (var (local, _) in stores)
        {
            if (!read.Contains(local))
                discarded.Add(local);
        }

        if (discarded.Count == 0)
            return;

        //Rewritten in place: a branch may name the store, and the instruction it names is still there.
        foreach (var instruction in body.Instructions)
        {
            if (LocalOf(instruction, CilOpCodes.Stloc) is { } stored && !read.Contains(stored))
            {
                instruction.OpCode = CilOpCodes.Pop;
                instruction.Operand = null;
            }
        }

        foreach (var local in discarded)
            body.LocalVariables.Remove(local);
    }

    /// <summary>The local an instruction stores to or loads from, once the compact forms are accounted for.</summary>
    private static CilLocalVariable? LocalOf(CilInstruction instruction, CilOpCode opCode)
    {
        var isStore = opCode == CilOpCodes.Stloc;
        var code = instruction.OpCode.Code;

        var matches = isStore
            ? code is CilCode.Stloc or CilCode.Stloc_S or CilCode.Stloc_0 or CilCode.Stloc_1 or CilCode.Stloc_2 or CilCode.Stloc_3
            : code is CilCode.Ldloc or CilCode.Ldloc_S or CilCode.Ldloc_0 or CilCode.Ldloc_1 or CilCode.Ldloc_2 or CilCode.Ldloc_3;

        return matches ? instruction.Operand as CilLocalVariable : null;
    }

    /// <summary>
    /// A call to one of the runtime helpers the key function search identified by name. Each of these is a
    /// CIL instruction the code generator expanded into a call, so it can be folded straight back.
    /// Returns whether the call was handled; anything else falls through to the placeholder.
    /// </summary>
    private static bool TryEmitKeyFunction(string name, Instruction instruction, MethodDefinition method,
        Dictionary<LocalVariable, CilLocalVariable> locals, MemberReference writeLine, MemberReference stringCtor)
    {
        var body = method.CilMethodBody!;
        var instructions = body.Instructions;
        var module = method.DeclaringModule!;
        var importer = module.DefaultImporter!;

        switch (name)
        {
            // The guard around it is removed where it can be recognised; where it could not, the call on its
            // own does nothing a managed reader can see, so it says less than nothing to keep it.
            case "il2cpp_codegen_initialize_runtime_metadata":
            case "il2cpp_codegen_initialize_method":
                //Dropping the call is right; dropping the **value** it was lifted as producing is not. The
                //unknown-callee convention gives every call `x0` as a result, and leaving that local
                //unassigned is what made the generator throw on 691 bodies once these calls started being
                //recognised - and a body AssetRipper fills with the exception scores as a whole one, so
                //nothing measured it. Assigning a default keeps everything after the call readable.
                if (instruction.OpCode == OpCode.Call && instruction.Operands.Count > 1
                    && instruction.Operands[1] is LocalVariable { Type: { } produced })
                {
                    AddDefaultValue(instructions, produced, module, importer);
                    StoreToOperand(instruction.Operands[1], method, locals, writeLine);
                }

                return true;

            // Allocating a vector: the class operand describes the array type, whose element type is what
            // newarr takes, and the operand after it is the length.
            //A one dimensional array is its own kind of type here, not an array type of rank one, so both are
            //asked for the element type newarr takes.
            case "SzArrayNew" when ClassArgument(Argument(instruction, 1)) is SzArrayTypeAnalysisContext or ArrayTypeAnalysisContext:
            {
                var length = Argument(instruction, 2);
                var elementType = ClassArgument(Argument(instruction, 1)) switch
                {
                    SzArrayTypeAnalysisContext single => single.ElementType,
                    ArrayTypeAnalysisContext multiple => multiple.ElementType,
                    _ => null,
                };

                if (length is null || elementType is null)
                    return false;

                //An array's length is an int, but il2cpp hands it over in a 64-bit register, so a constant one
                //arrived as a long and the array was written `new int[8L]`. That compiles, and it is also not
                //the shape ILSpy's array-initialiser transform matches - so `int[] t = { 11, 22, 33 }` could
                //not be folded back and stayed a bare call to InitializeArray with a token it then refused to
                //print. Only a constant is narrowed; a computed length is left exactly as it was.
                if (length is long wide and >= 0 and <= int.MaxValue)
                    length = (int)wide;

                LoadOperand(length, method, locals, writeLine, stringCtor);
                instructions.Add(CilOpCodes.Newarr, importer.ImportType(elementType.ToTypeSignature(module).ToTypeDefOrRef()));
                StoreResult(instruction, method, locals, writeLine);
                return true;
            }

            // A type test: the value, then the class it is being tested against.
            //Boxing puts a copy of a value on the heap, which is the one instruction it is.
            case "il2cpp_codegen_box" when ClassArgument(Argument(instruction, 2)) is { IsValueType: true } boxed:
            {
                var boxedValue = Argument(instruction, 1);
                if (boxedValue is null)
                    return false;

                LoadOperand(boxedValue, method, locals, writeLine, stringCtor, boxed);
                instructions.Add(CilOpCodes.Box, importer.ImportType(boxed.ToTypeSignature(module).ToTypeDefOrRef()));
                StoreResult(instruction, method, locals, writeLine);
                return true;
            }

            //The runtime's own boxing entry point, which is what a thunk-followed call reaches. Same
            //instruction, **arguments the other way round**: `il2cpp_vm_object_box(klass, value)` takes the
            //class first, while the `il2cpp_codegen_box` shape above is built by `CastHelperRecovery` with
            //the value first. Reading one site settled it - the class is at argument one and carries an
            //`Il2CppClass<...>`, the value at argument two.
            //A generic parameter as well as a value type: `box !!T` is what the language and the IL both say
            //for boxing one, and an open parameter is not `IsValueType` because nothing yet knows whether it
            //is. Refusing it left `Il2CppClass<T>` boxes saying only that the call could not be written.
            case "il2cpp_vm_object_box" when ClassArgument(Argument(instruction, 1))
                is { IsValueType: true } or GenericParameterTypeAnalysisContext:
            {
                var vmBoxed = ClassArgument(Argument(instruction, 1))!;
                var vmValue = Argument(instruction, 2);
                if (vmValue is null)
                    return false;

                LoadOperand(vmValue, method, locals, writeLine, stringCtor, vmBoxed);
                instructions.Add(CilOpCodes.Box, importer.ImportType(vmBoxed.ToTypeSignature(module).ToTypeDefOrRef()));
                StoreResult(instruction, method, locals, writeLine);
                return true;
            }

            //The same helper, where what came back was read through rather than kept as an object - see
            //UnboxRecovery. `unbox.any` is the one instruction that checks the class and takes the value out,
            //which is both halves of what the helper did.
            case Analysis.UnboxRecovery.ObjectUnbox when ClassArgument(Argument(instruction, 2)) is { } unboxed:
            {
                var boxedObject = Argument(instruction, 1);
                if (boxedObject is null)
                    return false;

                LoadOperand(boxedObject, method, locals, writeLine, stringCtor);
                instructions.Add(CilOpCodes.Unbox_Any, importer.ImportType(unboxed.ToTypeSignature(module).ToTypeDefOrRef()));
                StoreResult(instruction, method, locals, writeLine);
                return true;
            }

            case "il2cpp_vm_object_is_inst" when ClassArgument(Argument(instruction, 2)) is { } testedAgainst:
            {
                var value = Argument(instruction, 1);
                if (value is null)
                    return false;

                LoadOperand(value, method, locals, writeLine, stringCtor);
                instructions.Add(CilOpCodes.Isinst, importer.ImportType(testedAgainst.ToTypeSignature(module).ToTypeDefOrRef()));
                StoreResult(instruction, method, locals, writeLine);
                return true;
            }

            //The same call with **no class operand**, which is what is left once every pass that could name
            //one has refused: 25 sites, 10 bodies, and each of them a statement saying only that the call
            //could not be written. The class is not the only thing that says what these produce - the type
            //the analysis gave the **result** says it too, and where it does the instruction is exact.
            case "il2cpp_vm_object_is_inst" when ResultType(instruction) is { IsValueType: false } inferred:
            {
                if (Argument(instruction, 1) is not { } tested)
                    return false;

                LoadOperand(tested, method, locals, writeLine, stringCtor);
                instructions.Add(CilOpCodes.Isinst, importer.ImportType(inferred.ToTypeSignature(module).ToTypeDefOrRef()));
                StoreResult(instruction, method, locals, writeLine);
                return true;
            }

            case Analysis.UnboxRecovery.ObjectUnbox when ResultType(instruction) is { IsValueType: true } taken:
            {
                if (Argument(instruction, 1) is not { } boxed)
                    return false;

                LoadOperand(boxed, method, locals, writeLine, stringCtor);
                instructions.Add(CilOpCodes.Unbox_Any, importer.ImportType(taken.ToTypeSignature(module).ToTypeDefOrRef()));
                StoreResult(instruction, method, locals, writeLine);
                return true;
            }

            //Preparing a type, which managed code cannot observe: it cannot run before the type it is in has
            //been prepared. The guarded form is already removed; this is the bare call the guard was lost from.
            case "il2cpp_runtime_class_init_actual":
            case "il2cpp_runtime_class_init":
                if (instruction.OpCode == OpCode.Call && instruction.Operands.Count > 1
                    && instruction.Operands[1] is LocalVariable { Type: { } prepared })
                {
                    AddDefaultValue(instructions, prepared, module, importer);
                    StoreToOperand(instruction.Operands[1], method, locals, writeLine);
                }

                return true;

            default:
                return false;
        }
    }

    /// <summary>The type the analysis decided a call produces, where it decided one.</summary>
    private static TypeAnalysisContext? ResultType(Instruction instruction)
        => instruction.OpCode == OpCode.Call && instruction.Operands.Count > 1
            && instruction.Operands[1] is LocalVariable { Type: { } produced }
            ? produced
            : null;

    /// <summary>
    /// The type a runtime class argument describes. The metadata load is sometimes still in a local and
    /// sometimes has been folded into the call, so both shapes have to be recognised.
    /// </summary>
    private static TypeAnalysisContext? ClassArgument(object? operand) => operand switch
    {
        LocalVariable { Type: RuntimeClassTypeAnalysisContext runtimeClass } => runtimeClass.RepresentedType,
        RuntimeClassTypeAnalysisContext runtimeClass => runtimeClass.RepresentedType,
        TypeAnalysisContext type => type,
        _ => null,
    };

    // The nth argument of a call, counting past the target and, for a call that produces one, the result.
    private static object? Argument(Instruction instruction, int index)
    {
        var start = instruction.OpCode == OpCode.Call ? 2 : 1;
        return instruction.Operands.Count > start + index - 1 ? instruction.Operands[start + index - 1] : null;
    }

    /// <summary>Gives an instruction somewhere for a branch to land, where it generated nothing.</summary>
    /// <remarks>
    /// Resolving a jump takes the **first** IL its target produced - `instructionMap[target][0]` - without
    /// checking there is one, so an ISIL instruction that produces no IL is a crash waiting for a jump to
    /// land on it. Recognising `il2cpp_codegen_initialize_runtime_metadata`, whose entire contribution is to
    /// be deleted, turned that latent fault into **652 lost bodies** - and the loss scored as an improvement
    /// on every scorer, because AssetRipper writes the exception out as the method. A `Nop` costs nothing:
    /// the decompiler drops it, and only a branch ever looks for it.
    /// </remarks>
    private static void EnsureBranchable(List<CilInstruction> generated, MethodDefinition method)
    {
        if (generated.Count == 0)
            generated.Add(method.CilMethodBody!.Instructions.Add(CilOpCodes.Nop));
    }

    private static void StoreResult(Instruction instruction, MethodDefinition method,
        Dictionary<LocalVariable, CilLocalVariable> locals, MemberReference writeLine)
    {
        if (instruction.OpCode == OpCode.Call && instruction.Operands.Count > 1)
            StoreToOperand(instruction.Operands[1], method, locals, writeLine);
        else
            method.CilMethodBody!.Instructions.Add(CilOpCodes.Pop);
    }

    /// <summary>
    /// Loads the value of a move, knowing where it is about to be stored.
    /// </summary>
    /// <remarks>
    /// A move carries no type of its own: arm64 writes a register, and how wide the value is and whether it is a
    /// number at all is only known from the destination. Loading it blind writes an eight-byte constant into whatever
    /// the destination turns out to be, and a struct assigned from one is not C# - so the declaration is commented
    /// out, and with it every later statement that used the local. `BoardController::ComputeHighlights` lost 847 of
    /// its 849 lines to four such declarations.
    /// </remarks>
    /// <summary>Names a field rather than reading it - the handle InitializeArray copies an array through.</summary>
    /// <summary>
    /// Loads a type as the handle it is, where the place it is going wants one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>typeof(X)</c> is two instructions in C# - <c>ldtoken X</c> and a call to
    /// <c>Type.GetTypeFromHandle</c> - and both survive: the analysis resolves the token and the ISIL reads
    /// <c>Call Type.GetTypeFromHandle, v218, typeof(UnityEngine.RectTransform)</c>. The generator was the
    /// only thing missing it, loading a type used as a value as a native-integer zero, which is where
    /// <c>Type.GetTypeFromHandle((RuntimeTypeHandle)0)</c> came from. 43 sites in the game, every one of
    /// them a commented statement.
    /// </para>
    /// <para>
    /// <b>Only where a <c>RuntimeTypeHandle</c> is declared.</b> A type operand is far more often a class
    /// pointer the runtime is being handed - the argument to a cast, an allocation, a boxing helper - and
    /// those are native integers, exactly as the case this narrows says. The parameter's own type is what
    /// tells the two apart, and it is not a guess.
    /// </para>
    /// </remarks>
    private static bool TryLoadTypeToken(TypeAnalysisContext type, TypeAnalysisContext? expected,
        CilInstructionCollection instructions, ModuleDefinition module, ReferenceImporter importer)
    {
        if (expected?.FullName != "System.RuntimeTypeHandle")
            return false;

        instructions.Add(CilOpCodes.Ldtoken, importer.ImportType(type.ToTypeSignature(module).ToTypeDefOrRef()));
        return true;
    }

    private static void LoadFieldToken(FieldToken token, CilInstructionCollection instructions, ModuleDefinition module)
    {
        instructions.Add(CilOpCodes.Ldtoken, token.Field.ToFieldDescriptor(module));
    }

    /// <summary>
    /// The type to load a comparison's operand as, which is never a struct.
    /// </summary>
    /// <remarks>
    /// il2cpp compares small structs by their bits, so a comparison's operands carry the struct's type while
    /// what is being compared is a register. Handing that type down makes a zero operand become
    /// <c>default(Vector4)</c>, and then the comparison is between two structs - which the decompiler writes
    /// as <c>Unsafe.As&lt;Vector4, UIntPtr&gt;(ref a) &gt;= Unsafe.As&lt;Vector4, UIntPtr&gt;(ref b)</c>, and
    /// C# has no <c>&gt;=</c> on <c>UIntPtr</c>. Three methods in the game failed to compile that way, and
    /// only the Unity gate saw it - none of the source-based scorers compile anything.
    /// </remarks>
    private static TypeAnalysisContext? ComparableType(TypeAnalysisContext? type)
    {
        var comparable = type is { IsValueType: true } && !IsNumeric(type) ? null : type;

        //Remembered so that `ConvertToWidthOf` can tell this expectation from a declared one. Here the width
        //wanted is the *other operand's*, which the analysis inferred; everywhere else it is a field's, a
        //parameter's or a return type's, which the metadata states. Narrowing to an inferred width is what
        //took `Corpus::Mix` from 321186724655 to -935822545.
        arithmeticExpectation[expectationsSeen++ & 1] = comparable;

        return comparable;
    }

    /// <summary>The last two widths asked for by an arithmetic or a comparison, which nothing declared.</summary>
    [System.ThreadStatic] private static TypeAnalysisContext?[]? arithmetic;

    [System.ThreadStatic] private static int expectationsSeen;

    private static TypeAnalysisContext?[] arithmeticExpectation => arithmetic ??= new TypeAnalysisContext?[2];

    /// <summary>
    /// The struct an integer zero stands for, where that is what it is standing for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A cleared register is an empty struct, and the compiler moves one that way rather than spending
    /// instructions zeroing fields. Read back literally it is a number where a value type belongs -
    /// <c>(Vector3)0L</c>, <c>(Color32)0L</c> - which is not C#, so the statement holding it was lost. It is
    /// exactly the same situation as a zero standing for <c>null</c>, which is handled a few lines above, and
    /// <c>default(Vector3)</c> is the same value written in a way that compiles.
    /// </para>
    /// <para>
    /// A numeric type is excluded because for those the zero is simply the number, and a native integer
    /// because there the zero is a pointer.
    /// </para>
    /// </remarks>
    /// <summary>
    /// The arguments an allocation carries for the constructor il2cpp inlined, or null if it carries none.
    /// </summary>
    /// <remarks>
    /// <see cref="Analysis.InlinedConstructorRecovery"/> appends one operand per parameter after the two an
    /// allocation already has, having matched each of the inlined body's field writes to the parameter that
    /// stands for it. Only an exact count is taken, so an allocation carrying anything else is left to the
    /// paths that were already there.
    /// </remarks>
    private static List<object>? CarriedConstructorArguments(Instruction allocation, MethodAnalysisContext constructor)
    {
        if (constructor.Parameters.Count == 0 || allocation.Operands.Count != 2 + constructor.Parameters.Count)
            return null;

        return allocation.Operands.Skip(2).ToList();
    }

    private static TypeAnalysisContext? ZeroValueOf(TypeAnalysisContext? expected, object operand)
        => (expected is { IsValueType: true } && !IsNumeric(expected) && !LowersToNativeInt(expected)
                || expected is GenericParameterTypeAnalysisContext)
            && IsZeroConstant(operand)
            ? expected
            : null;

    private static void LoadOperandInto(object destination, object value, MethodDefinition method,
        Dictionary<LocalVariable, CilLocalVariable> locals, MemberReference writeLine, MemberReference stringCtor,
        TypeAnalysisContext? convertTo = null)
    {
        //An arm64 conversion instruction, which until now was lifted as though it were a plain move and so
        //silently reinterpreted the bits instead of converting them. The instruction says exactly what it
        //produces, so the conversion is emitted rather than inferred - see ConversionTarget.cs.
        if (convertTo is not null)
        {
            LoadOperand(value, method, locals, writeLine, stringCtor);
            Convert(method.CilMethodBody!.Instructions, convertTo);
            return;
        }

        var type = TypeOfOperand(destination);

        //Zero is what a cleared register holds, and in a struct it means an empty one - `(Dictionary<int,
        //object>.Enumerator)0L` is not C#, so the declaration is dropped and every statement using the local
        //goes with it.
        //A type parameter belongs here too, and only here: unconstrained, `T` is neither a reference nor a
        //value type to the language, so neither a nought nor `null` can be written where one belongs -
        //`W val4 = (W)0;` and `W val4 = null;` are both refused. `default(T)` is what the cleared register
        //holds under either instantiation, and `initobj` is how the runtime says it.
        if ((type is { IsValueType: true } && !IsNumeric(type) && !LowersToNativeInt(type)
                || type is GenericParameterTypeAnalysisContext)
            && IsZeroConstant(value))
        {
            var module = method.DeclaringModule!;
            AddDefaultValue(method.CilMethodBody!.Instructions, type, module, module.DefaultImporter!);
            return;
        }

        //A zero into a place declared as a reference is `null`. That reading was refused here for a long
        //time - a register the analysis called a reference while in fact holding a number is common - but
        //the alternative is not a number in a reference slot, it is **invalid IL**: `Expected O, but got I8`,
        //849 of them in the game, and ILSpy's own note predicts a lost statement seven times over. In
        //`Corpus::SumSteps` it is the exception slot the compiler cleared, so `if (obj == null)` was false
        //and the recovered method threw where the original returned.
        //A local whose type was never worked out is given `System.Object` in the emitted IL, so a number
        //stored into one is invalid there too - it is the same place, arrived at from the other side.
        var reference = type is { IsValueType: false } && !LowersToNativeInt(type)
            || (destination is LocalVariable { Type: null } untyped && OnlyAskedIfItIsNull(untyped));

        if (reference && IsZeroConstant(value))
        {
            method.CilMethodBody!.Instructions.Add(CilOpCodes.Ldnull);
            return;
        }

        //Only a value type is handed down otherwise, and only so an immediate is loaded at the width it is
        //about to be stored at.
        LoadOperand(value, method, locals, writeLine, stringCtor, type is { IsValueType: true } ? type : null);
    }

    /// <summary>
    /// Whether nothing does anything with the local except ask whether it is zero.
    /// </summary>
    /// <remarks>
    /// A local whose type was never worked out is given <c>System.Object</c> in the emitted IL, so storing a
    /// number into one is invalid IL whatever the number meant - `Expected O, but got I8`, and ILSpy's own
    /// note predicts a lost statement seven times over. But reading every such zero as <c>null</c> outright
    /// turns a genuine integer zero into one, which is a wrong value rather than a lost statement.
    ///
    /// A local that is only ever **compared against zero** cannot be an arithmetic value: whichever it is,
    /// `null` and `0` answer that question the same way, so nothing can be got wrong by choosing the one
    /// that compiles. That is what the compiler's cleared exception slot looks like, and it is why
    /// `Corpus::SumSteps` threw where the original returned.
    /// </remarks>
    private static bool OnlyAskedIfItIsNull(LocalVariable local)
        => CurrentContext?.ControlFlowGraph is { } graph && OnlyAskedIfItIsNull(local, graph, new HashSet<LocalVariable>());

    /// <remarks>
    /// The question is followed through a copy. Destroying SSA leaves the answer a block away from the
    /// register that was cleared - <c>Corpus::Using</c> clears the exception slot in one block, the edge copy
    /// moves it into a second local in another, and only that second local is compared against zero. Asking
    /// only about direct reads therefore said no, the zero stayed a zero, <c>if (obj == null)</c> was false,
    /// and the recovered <c>using</c> threw <c>OutOfMemoryException</c> where the original returned. A copy
    /// carries the value unchanged, so "nothing does anything with it except ask whether it is zero" is a
    /// question about the copy's uses just as much as about this local's.
    /// </remarks>
    private static bool OnlyAskedIfItIsNull(LocalVariable local, ISILControlFlowGraph graph, HashSet<LocalVariable> seen)
    {
        //A cycle of copies answers nothing either way, and following it again would not terminate.
        if (!seen.Add(local))
            return true;

        foreach (var instruction in graph.Instructions)
        {
            for (var operand = 0; operand < instruction.Operands.Count; operand++)
            {
                if (!ReferenceEquals(instruction.Operands[operand], local))
                    continue;

                //Written to, which says nothing about what it is.
                if (operand == 0 && instruction.OpCode != OpCode.Call && instruction.OpCode != OpCode.CallVoid)
                    continue;

                //Copied into another local the analysis never typed either: the copy holds exactly this
                //value, so ask the same question of it. A typed destination is not followed - there the
                //declaration decides, and this rule is only for the slots that have none.
                if (instruction.OpCode == OpCode.Move
                    && operand == 1
                    && instruction.Operands.Count == 2
                    && instruction.Operands[0] is LocalVariable { Type: null } copy)
                {
                    if (!OnlyAskedIfItIsNull(copy, graph, seen))
                        return false;
                    continue;
                }

                if (instruction.OpCode is not (OpCode.CheckEqual or OpCode.CheckNotEqual)
                    || instruction.Operands.Count < 3
                    || !IsZeroConstant(instruction.Operands[operand == 1 ? 2 : 1]))
                    return false;
            }
        }

        return true;
    }

    /// <summary>Turns what is on the stack into the type an arm64 conversion instruction produces.</summary>
    private static void Convert(CilInstructionCollection instructions, TypeAnalysisContext type)
    {
        switch (type.Type)
        {
            case Il2CppTypeEnum.IL2CPP_TYPE_R4:
                instructions.Add(CilOpCodes.Conv_R4);
                break;
            case Il2CppTypeEnum.IL2CPP_TYPE_R8:
                instructions.Add(CilOpCodes.Conv_R8);
                break;
            case Il2CppTypeEnum.IL2CPP_TYPE_I8:
                instructions.Add(CilOpCodes.Conv_I8);
                break;
            case Il2CppTypeEnum.IL2CPP_TYPE_U8:
                instructions.Add(CilOpCodes.Conv_U8);
                break;
            //The narrowing conversions. Without these every extension fell to conv.i4, which for a value
            //already in a 32-bit register does nothing at all - so `(byte)v` kept the whole of v and the
            //answer was quietly wrong. conv.i1 and friends both narrow and leave an int on the stack, which
            //is exactly what sxtb/uxtb/sxth/uxth do.
            case Il2CppTypeEnum.IL2CPP_TYPE_I1:
                instructions.Add(CilOpCodes.Conv_I1);
                break;
            case Il2CppTypeEnum.IL2CPP_TYPE_U1:
                instructions.Add(CilOpCodes.Conv_U1);
                break;
            case Il2CppTypeEnum.IL2CPP_TYPE_I2:
                instructions.Add(CilOpCodes.Conv_I2);
                break;
            case Il2CppTypeEnum.IL2CPP_TYPE_U2:
                instructions.Add(CilOpCodes.Conv_U2);
                break;
            default:
                instructions.Add(CilOpCodes.Conv_I4);
                break;
        }
    }

    // What a value is, where that is known. A field says what it holds just as directly as a typed local.
    private static TypeAnalysisContext? TypeOfOperand(object operand) => operand switch
    {
        LocalVariable local => local.Type,
        FieldReference field => field.Field.FieldType,
        //An element of an array is of the array's element type, and a field of one of the field's. Without
        //this an element has no type at all here, so `words[i] == null` loads the constant zero as the
        //number it literally is and then loads the element at that width: `(long)words[i] == 0L`, which does
        //not compile and takes the whole `foreach` body with it.
        MemoryOperand memory when CurrentContext is { } context && ArrayElement(memory, context) is { } element
            => element.Inside?.FieldType ?? element.Element,
        _ => null,
    };

    // The types a value sits in a 32-bit slot for, which includes an enum with any of them underneath.
    private static bool IsThirtyTwoBitInteger(TypeAnalysisContext? type) =>
        StoredAs(type)?.Type is Il2CppTypeEnum.IL2CPP_TYPE_I4 or Il2CppTypeEnum.IL2CPP_TYPE_U4
            or Il2CppTypeEnum.IL2CPP_TYPE_I2 or Il2CppTypeEnum.IL2CPP_TYPE_U2
            or Il2CppTypeEnum.IL2CPP_TYPE_I1 or Il2CppTypeEnum.IL2CPP_TYPE_U1
            or Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN or Il2CppTypeEnum.IL2CPP_TYPE_CHAR;

    /// <summary>
    /// Converts a value already on the stack to the integer width of the place it is going to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ISIL holds values, not registers, and has no width - so a local whose type was settled as
    /// <c>System.Int64</c> is pushed as one wherever it goes, including into an <c>int</c>. That is not
    /// verifiable IL, and the decompiler writes the mismatch out in place of the statement: after the enum
    /// constants were fixed, <b>822</b> of its notes were still one integer width meeting another.
    /// </para>
    /// <para>
    /// Only where both sides are a known fixed-width integer. A native integer is deliberately left alone -
    /// it is what an untyped value lowers to, so converting to or from one would be acting on the absence of
    /// a type rather than on a type. Constants are left alone too, because the ones above already choose
    /// their own width from the same question.
    /// </para>
    /// <para>
    /// The source is asked through <see cref="TypeOfOperand"/> rather than being required to be a local.
    /// A field and an array element state their width in the metadata just as flatly as a local's settled
    /// type does - <c>Ldfld</c> of an <c>int</c> field leaves an I4 on the stack whatever the place it is
    /// going to expects - and this is the same standard the narrowing rule below already asks of the
    /// <em>destination</em>. Restricting it to locals left every field and element load unconverted, which
    /// is what <b>242</b> of the decompiler's remaining notes are: 172 "Expected I4, but got I8" and 70 the
    /// other way about.
    /// </para>
    /// </remarks>
    private static void ConvertToWidthOf(CilInstructionCollection instructions, object operand,
        TypeAnalysisContext? expectedType)
    {
        if (expectedType is null)
            return;

        //A constant chooses its own width where it is emitted, but only the 64-bit cases ask what the place
        //expects: `int`, `uint`, `short`, `byte`, `bool` and the rest all emit `ldc.i4` whatever they are
        //going into. A 32-bit constant meeting a 64-bit place is `Expected I8, but got I4`, and it is the
        //whole of what was left of that note after a field's width was allowed to speak - four of them take
        //`SegmentRuleEvaluator::Tokenize`'s entire `do` loop with them. Nothing here narrows: the emitter
        //already did that where the value fits, so this only ever widens, which cannot lose anything.
        if (EmittedAsThirtyTwoBits(operand, expectedType) is { } thirtyTwo)
        {
            if (thirtyTwo && IsSixtyFourBitInteger(expectedType))
                instructions.Add(operand is uint ? CilOpCodes.Conv_U8 : CilOpCodes.Conv_I8);

            return;
        }

        if (TypeOfOperand(operand) is not { } declared)
            return;

        var from = StoredAs(declared);
        var to = StoredAs(expectedType);

        //A conversion that cannot lose anything needs no confidence in the width it is going to.
        if (IsSixtyFourBitInteger(to) && IsThirtyTwoBitInteger(from))
        {
            instructions.Add(CilOpCodes.Conv_I8);
            return;
        }

        //One that can lose something needs the width to be **declared**. Narrowing to a width the analysis
        //*inferred* was built and measured: it took `Corpus::Mix` from 321186724655 to -935822545, because
        //the other operand of the arithmetic had been called 32 bits and the value was really 64. A field, a
        //parameter and a return type are all stated by the metadata, and the program itself truncates when it
        //stores 64 bits into one of them - so writing the truncation down is what the program does. It is the
        //largest thing the decompiler's own notes complain about: **293** say "Expected I4, but got I8".
        if (IsThirtyTwoBitInteger(to) && IsSixtyFourBitInteger(from) && !WasInferred(expectedType))
            instructions.Add(CilOpCodes.Conv_I4);
    }

    /// <summary>
    /// Counts, and does not act on, every value crossing the value/reference boundary at a load.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The largest family in the decompiler's own notes is a value meeting a reference - 443 say
    /// "Expected O, but got &lt;a number&gt;" and 177 the other way about. The obvious emission is the one
    /// the language uses, <c>box</c> one way and <c>unbox.any</c> the other, and it is <b>not obviously
    /// right</b>: il2cpp always boxes through a call, so a site with no boxing call in the binary is one
    /// where the analysis has mistyped something rather than one where a box was dropped. Boxing a local
    /// that wears a bare <em>width</em> from a register move - which is most of them - would put a
    /// pointer inside a boxed <c>long</c>, compile, and score whole.
    /// </para>
    /// <para>
    /// So this only reports, under <c>BOXTRACE=1</c>, and the thing to read out of it is how many sites
    /// have a source the metadata <em>states</em> (a field, an array element) against how many have a
    /// local carrying a width. Three attempts at this family have already been spent guessing from the
    /// analysis side; the counts decide whether there is a safe subset before anything is emitted.
    /// </para>
    /// </remarks>
    private static void CountTheBoundaryCrossings(object operand, TypeAnalysisContext? expectedType)
    {
        if (!boxTrace || expectedType is null)
            return;

        var actual = TypeOfOperand(operand);
        if (actual is null || LowersToNativeInt(expectedType) || LowersToNativeInt(actual))
            return;

        if (actual.IsValueType == expectedType.IsValueType)
            return;

        //Where the source type came from is the whole question, so say it: a field and an element are
        //stated by the metadata, a local is whatever the fixpoint settled on.
        var source = operand switch
        {
            FieldReference => "field",
            MemoryOperand => "element",
            LocalVariable => "local",
            _ => operand.GetType().Name,
        };

        var direction = expectedType.IsValueType ? "unbox" : "box";
        System.Console.Error.WriteLine(
            $"BOX {direction} {source} {actual.FullName} -> {expectedType.FullName} in {CurrentContext?.DeclaringType?.Name}::{CurrentContext?.Name}");
    }

    private static readonly bool boxTrace = System.Environment.GetEnvironmentVariable("BOXTRACE") is not null;

    /// <summary>
    /// Whether a constant operand reached the stack as a 32-bit value - <see langword="null"/> if it is not
    /// an integer constant at all, and so has no width of this kind to answer with.
    /// </summary>
    /// <remarks>
    /// This has to mirror what <c>LoadOperand</c> actually emitted rather than what the value nominally is,
    /// or the answer is wrong in the one case that already asks the question: a <c>long</c> whose place
    /// wants 32 bits is emitted as <c>ldc.i4</c>, and calling it 64-bit here would add a narrowing to a
    /// value that is already narrow. A zero going somewhere a reference belongs became <c>ldnull</c>, which
    /// is not an integer either - but its place is not a 64-bit integer, so it falls out of the widening
    /// test on its own.
    /// </remarks>
    private static bool? EmittedAsThirtyTwoBits(object operand, TypeAnalysisContext? expectedType) => operand switch
    {
        long l => IsThirtyTwoBitInteger(expectedType) && l is >= int.MinValue and <= int.MaxValue,
        ulong => false,
        int or uint or short or ushort or byte or sbyte or bool or char => true,
        _ => null,
    };

    private static bool IsSixtyFourBitInteger(TypeAnalysisContext? type) =>
        StoredAs(type)?.Type is Il2CppTypeEnum.IL2CPP_TYPE_I8 or Il2CppTypeEnum.IL2CPP_TYPE_U8;

    /// <summary>Whether this expectation is one <see cref="ComparableType"/> just produced.</summary>
    private static bool WasInferred(TypeAnalysisContext? expected)
        => arithmetic is { } asked && (ReferenceEquals(asked[0], expected) || ReferenceEquals(asked[1], expected));

    /// <summary>
    /// The integer a value is actually held as - which for an enum is what it derives from, not itself.
    /// </summary>
    /// <remarks>
    /// An enum's own <c>Type</c> is <c>VALUETYPE</c>, so the rule above saw none of them and every enum
    /// constant was loaded as a 64-bit number. <c>levelManager.levelEndReason = ELevelEndReason.out_of_time</c>
    /// is an <c>ldc.i8</c> into an <c>int</c>-sized field: not verifiable IL, so the whole statement is
    /// commented out. It is the largest thing the decompiler's own notes complain about - <b>684</b> of them
    /// say "Expected I4, but got I8" - and enum assignment is most of it.
    ///
    /// Read from the metadata rather than assumed to be <c>int</c>: an enum may derive from any integer, and
    /// a <c>long</c>-based one loaded as 32 bits would be the same defect the other way round.
    /// </remarks>
    private static TypeAnalysisContext? StoredAs(TypeAnalysisContext? type)
        => type is { IsEnumType: true }
            ? type.Fields.FirstOrDefault(f => !f.IsStatic)?.FieldType ?? type
            : type;

    /// <summary>
    /// Whether the operand ends up as a native integer in the emitted IL: either a value whose type was
    /// never established, or one of the runtime handles that has no managed type to lower to.
    /// </summary>
    private static bool LowersToNativeInt(object operand) =>
        operand is LocalVariable local && LowersToNativeInt(local.Type);

    /// <summary>
    /// A local whose address can be handed to a by-reference parameter. Where the argument is a value the method
    /// already has a local for, that local is used, so what the callee writes lands where the code reads it.
    /// </summary>
    /// <summary>
    /// Whether a call's result can be stored where the call says, without a cast the language will refuse.
    /// </summary>
    /// <remarks>
    /// A large struct comes back through the pointer in <c>x8</c>, so the destination is the place it was
    /// told to write rather than a register - see <see cref="Analysis.Aapcs64.ReturnsIndirectly"/>. That is
    /// right, and it only helps where the place has the type the callee returns. Inside a **shared generic**
    /// it does not: the enumerator of a <c>Dictionary</c> is typed by the stand-in
    /// <c>Dictionary&lt;int, object&gt;</c> while the call returns the real instantiation, and the store then
    /// reads back as an explicit cast between the two:
    /// <code>
    /// //Dictionary&lt;int, object&gt;.Enumerator e = (Dictionary&lt;int, object&gt;.Enumerator)scratch.GetEnumerator();
    /// </code>
    /// which does not compile, and takes every later statement using the local with it - thirteen
    /// self-assignments in `BoardController::ComputeHighlights`, and all fifty of its branches.
    /// </remarks>
    private static bool ResultFitsItsDestination(object destination, MethodAnalysisContext callee)
    {
        //A field is a place too, and one the buffer's own front member resolves to: `get_startColor` returning
        //a `MinMaxGradient` had its answer stored into that struct's `m_Mode`, which is an enum, so the
        //statement came out as a write through a pointer of a value that is not one.
        var held = destination switch
        {
            MemoryOperand { Index: null, Scale: 0, Addend: 0, Base: LocalVariable place } => place.Type,
            FieldReference field => field.Field.FieldType,
            _ => null,
        };

        if (held is null || callee.ReturnType is not { } returned)
            return true;

        //Compared as the analysis knows them, not as signatures. Round-tripping through `TypeSignature` and
        //comparing the text refuses `List<int>.Enumerator` against itself - the nesting and the generic
        //argument are spelled differently on the two sides - and refusing that is refusing the very case the
        //indirect return exists for.
        return ReferenceEquals(held, returned) || held.FullName == returned.FullName;
    }

    /// <summary>
    /// Loads the address of a field, where that is what the position wants and a field is what is there.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A value type's method is called on the address of one, and a by-reference parameter wants an address
    /// too - and until now both answered with the address of a **local**, because that is all
    /// <c>ReceiverLocal</c> and <c>ScratchLocal</c> can make. Where the thing in that position is a field,
    /// the local is a copy: what the callee writes lands in the copy and the field never changes, and what
    /// the callee reads is whatever the copy happened to be given. <c>ldflda</c> is the place itself.
    /// </para>
    /// <para>
    /// <b>Only where the base can carry the field.</b> <c>LoadLocal</c> matches a local to a parameter by
    /// name and emits <c>ldarg</c>, so what lands on the stack is the signature's type - and <c>ldflda</c> of
    /// a field the stack type does not declare is invalid IL that Roslyn refuses outright. The declaring type
    /// has to agree, checked by name because the two sides are built separately.
    /// </para>
    /// </remarks>
    /// <summary>
    /// Builds a struct of floats from the registers its fields were handed over in.
    /// </summary>
    /// <remarks>
    /// Aapcs64 gives such a struct one vector register per field, so the argument is not a value anywhere -
    /// it only exists once the fields are gathered. <see cref="Analysis.HomogeneousFloatArguments"/> decides
    /// which registers those are and refuses wherever it cannot answer for one, so everything reaching here
    /// is a field of the struct and the constructor takes them in the order the type declares them.
    /// </remarks>
    private static bool TryBuildFloatStruct(object? operand, MethodDefinition method,
        Dictionary<LocalVariable, CilLocalVariable> locals, ModuleDefinition module,
        MemberReference writeLine, MemberReference stringCtor)
    {
        if (operand is not Analysis.FloatStructAssembly assembly)
            return false;

        var single = assembly.Constructor.AppContext.SystemTypes.SystemSingleType;

        foreach (var part in assembly.Parts)
            LoadOperand(part, method, locals, writeLine, stringCtor, single);

        method.CilMethodBody!.Instructions.Add(CilOpCodes.Newobj,
            module.DefaultImporter.ImportMethod(assembly.Constructor.ToMethodDescriptor(module)));

        return true;
    }

    private static bool TryLoadFieldAddress(object? operand, MethodDefinition method,
        Dictionary<LocalVariable, CilLocalVariable> locals, ModuleDefinition module)
    {
        if (operand is not FieldReference reference)
            return false;

        var instructions = method.CilMethodBody!.Instructions;

        if (reference.Field.IsStatic)
        {
            instructions.Add(CilOpCodes.Ldsflda, reference.Field.ToFieldDescriptor(module));
            return true;
        }

        //The outer members of a chain are read as values and only the last one is addressed - `a.b.c` is
        //`ldfld b; ldflda c` - which is what makes a nested field's address a place rather than a copy.
        var path = reference is NestedFieldReference nested ? nested.Path : [reference.Field];

        if (path[0].DeclaringType is not { } owner || reference.Local.Type is not { } held
            || owner.FullName != held.FullName)
            return false;

        //A field of a struct is addressed through the struct's own address. `ldflda` wants an object
        //reference or a managed pointer, and a value type loaded by `ldloc` is neither - the decompiler said
        //so, "Expected native int or pointer, but got O", and commented the statement out. Every async
        //kick-off in the game is this shape: the state machine is a struct in a stack slot and
        //`builder.Start(ref sm)` / `return builder.Task` both address a field of it.
        if (held.IsValueType)
        {
            if (!LoadAddressOfLocal(reference.Local, method, locals))
                return false;
        }
        else
        {
            LoadLocal(reference.Local, method, locals);
        }

        for (var i = 0; i < path.Length - 1; i++)
            instructions.Add(CilOpCodes.Ldfld, path[i].ToFieldDescriptor(module));

        instructions.Add(CilOpCodes.Ldflda, path[^1].ToFieldDescriptor(module));
        return true;
    }

    /// <summary>The address of a local, however it is held. Answers whether there is one to take.</summary>
    private static bool LoadAddressOfLocal(LocalVariable local, MethodDefinition method,
        Dictionary<LocalVariable, CilLocalVariable> locals)
    {
        var instructions = method.CilMethodBody!.Instructions;

        //`this` in a value type's method is already a managed pointer.
        if (local.IsThis)
        {
            instructions.Add(CilOpCodes.Ldarg_0);
            return true;
        }

        if (method.Parameters.FirstOrDefault(p => p.Name == local.Name) is { } parameter)
        {
            instructions.Add(CilOpCodes.Ldarga, parameter);
            return true;
        }

        if (!locals.TryGetValue(local, out var slot))
            return false;

        instructions.Add(CilOpCodes.Ldloca, slot);
        return true;
    }

    /// <summary>
    /// The address of a value that already has a place of its own, for a call that needs one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Calling a method on a value type, or passing one by reference, takes an address, and
    /// <see cref="ScratchLocal"/> answers with a local it has just minted whenever the operand is not among
    /// <c>locals</c>. <b>Nothing ever stores the operand into that local</b>, so the callee is handed a place
    /// holding whatever the type's default is. A <b>parameter</b> is never among <c>locals</c> - it lives in
    /// <c>method.Parameters</c> - so every call on a value-type parameter went this way:
    /// </para>
    /// <code>
    /// CoinButton::OnCoinChanged     source: coinText.text = coin.ToString();
    ///   ISIL (already correct):     Call Int32.ToString, v21 @ X0_v2 (String), coin @ X1 (Int32)
    ///   emitted:                    int num = default(int); textMeshProUGUI.text = num.ToString();
    /// </code>
    /// <para>
    /// <c>ldarga</c> is the address of the argument itself rather than of a copy nothing wrote, which is both
    /// valid and true. 44 sites across 37 methods, every one of them scoring <c>full</c> while calling a
    /// method on a default value - the analysis was right the whole time and only the emission was wrong.
    /// </para>
    /// <para>
    /// The type has to match exactly. An address is typed, and handing a <c>T&amp;</c> where a <c>U&amp;</c>
    /// is expected is unverifiable in a way the value case is not - which is why this answers false rather
    /// than guessing, leaving the existing scratch local to be minted as before.
    /// </para>
    /// </remarks>
    private static bool LoadAddressOfArgument(object? operand, TypeSignature wanted, MethodDefinition method)
    {
        if (operand is not LocalVariable local)
            return false;

        var instructions = method.CilMethodBody!.Instructions;

        //`this` inside a value type's own method is already a managed pointer to the value. Only there: in a
        //reference type's method argument nought is the object, not an address.
        if (local.IsThis && method.DeclaringType is { IsValueType: true } declaring
            && declaring.ToTypeSignature().FullName == wanted.FullName)
        {
            instructions.Add(CilOpCodes.Ldarg_0);
            return true;
        }

        if (method.Parameters.FirstOrDefault(p => p.Name == local.Name) is not { } parameter
            || parameter.ParameterType?.FullName != wanted.FullName)
            return false;

        instructions.Add(CilOpCodes.Ldarga, parameter);
        return true;
    }

    /// <summary>The local a value type's method is called on, where that local is a real place on the stack.</summary>
    /// <remarks>
    /// <para>
    /// Calling a method on a struct takes the address of one, and which one matters. `ScratchLocal` compares
    /// `TypeSignature` by reference, so two descriptions of `List&lt;int&gt;.Enumerator` never match and it
    /// makes a fresh local nothing assigns - the enumerator a `foreach` walks was not the one
    /// `GetEnumerator` filled.
    /// </para>
    /// <para>
    /// **Only for a slot the frame actually has**, which is what an address-taken struct is. Reusing the
    /// caller's local for *every* value-type receiver was measured twice and costs
    /// `AssetLoader::MoveNext` thirteen of its nineteen instructions: a register-allocated local aliases
    /// other uses, and handing the callee its address spreads whatever is wrong with it. A stack slot does
    /// not - it is one place, holding one struct, for as long as the frame lives.
    /// </para>
    /// </remarks>
    private static CilLocalVariable ReceiverLocal(object? receiver, TypeSignature valueType, MethodDefinition method,
        Dictionary<LocalVariable, CilLocalVariable> locals)
    {
        //The slot is named by the register the local sits in, not by the local's own name. A step of an
        //accessor chain counts too: `Analysis.InaccessibleFieldRecovery` writes it once and reads it once,
        //so there is nothing else for a callee's mutation to reach - which is the whole objection to reusing
        //an ordinary local here. Without it `enumerator.Current.Key` calls `get_Key` on a fresh
        //`default(KeyValuePair<,>)` while `get_Current`'s answer is stored into a local nothing reads.
        if (receiver is LocalVariable { Register.Name: { } name } local
            && (name.StartsWith(StackSlots.AddressPrefix) || name == Analysis.InaccessibleFieldRecovery.ChainStep)
            && locals.TryGetValue(local, out var existing)
            && existing.VariableType.FullName == valueType.FullName)
            return existing;

        return ScratchLocal(receiver, valueType, method, locals);
    }

    private static CilLocalVariable ScratchLocal(object? argument, TypeSignature elementType, MethodDefinition method,
        Dictionary<LocalVariable, CilLocalVariable> locals)
    {
                //By name, not by object. Two signatures describing the same `Rect` are never the same instance, so
        //the reference test essentially never matched and a fresh local nothing assigns was minted at every
        //call on a value-type receiver.
        if (argument is LocalVariable local && locals.TryGetValue(local, out var existing)
            && existing.VariableType?.FullName == elementType.FullName)
            return existing;

        var scratch = new CilLocalVariable(elementType);
        method.CilMethodBody!.LocalVariables.Add(scratch);
        return scratch;
    }

    /// <summary>
    /// Whether the value a method was found to return can actually be its return value.
    /// </summary>
    /// <remarks>
    /// Aapcs64 returns a struct in the vector registers when its fields are all floats, in a pair of general purpose
    /// registers when it is small enough, and through memory the caller points x8 at when it is not. Recovery reads
    /// the return from x0 in every case, so for a struct it picks up whatever happened to be in x0, usually the
    /// receiver. Returning that says something plainly untrue and does not compile either, so it is better to return
    /// nothing in particular.
    /// </remarks>
    private static bool ReturnsTheRightThing(object returned, TypeAnalysisContext returnType)
    {
        if (!returnType.IsValueType)
            return true;

        //Nothing is known about the value, so there is nothing to object to.
        if (returned is not LocalVariable { Type: { } valueType })
            return true;

        if (ReferenceEquals(valueType, returnType))
            return true;

        //A method that answers through the buffer its caller passed holds that buffer as a reference to the
        //return type - that is what turns the stores into it into fields - and the local emitter writes a
        //local typed as a managed reference out as the thing it points at, since there is no ldloca in what
        //this generates. So a `T&` here is already a `T` by the time the return loads it.
        if (valueType is ByRefTypeAnalysisContext { ElementType: { } pointee } && pointee.FullName == returnType.FullName)
            return true;

        //A reference where a value type is expected is the receiver left in x0 by a body that was not recovered, and
        //one struct where another is expected is whatever else was in the register. Only a number standing in for
        //another number is worth keeping, since the conversion between them is real.
        return IsNumeric(valueType) && IsNumeric(returnType);
    }

    private static bool IsNumeric(TypeAnalysisContext type) => type.Type is
        Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN or Il2CppTypeEnum.IL2CPP_TYPE_CHAR
        or Il2CppTypeEnum.IL2CPP_TYPE_I1 or Il2CppTypeEnum.IL2CPP_TYPE_U1
        or Il2CppTypeEnum.IL2CPP_TYPE_I2 or Il2CppTypeEnum.IL2CPP_TYPE_U2
        or Il2CppTypeEnum.IL2CPP_TYPE_I4 or Il2CppTypeEnum.IL2CPP_TYPE_U4
        or Il2CppTypeEnum.IL2CPP_TYPE_I8 or Il2CppTypeEnum.IL2CPP_TYPE_U8
        or Il2CppTypeEnum.IL2CPP_TYPE_R4 or Il2CppTypeEnum.IL2CPP_TYPE_R8
        or Il2CppTypeEnum.IL2CPP_TYPE_I or Il2CppTypeEnum.IL2CPP_TYPE_U;

    /// <summary>
    /// Pushes a default value of <paramref name="type"/>, for an argument that the code being recovered no longer
    /// contains because the constructor it belonged to was inlined.
    /// </summary>
    /// <remarks>
    /// The value has to match the signature the argument is passed as, not the analysis type, because a runtime
    /// handle is not a reference even though nothing managed stands behind it: it is a native integer, and pushing
    /// null for one does not compile.
    /// </remarks>
    private static void AddDefaultValue(CilInstructionCollection instructions, TypeAnalysisContext type, ModuleDefinition module, ReferenceImporter importer)
    {
        var signature = type.ToTypeSignature(module).ImportWith(importer);

        switch (signature.ElementType)
        {
            case ElementType.R4:
                instructions.Add(CilOpCodes.Ldc_R4, 0f);
                return;
            case ElementType.R8:
                instructions.Add(CilOpCodes.Ldc_R8, 0d);
                return;
            case ElementType.I8:
            case ElementType.U8:
                instructions.Add(CilOpCodes.Ldc_I4_0);
                instructions.Add(CilOpCodes.Conv_I8);
                return;
            case ElementType.I:
            case ElementType.U:
            case ElementType.Ptr:
            case ElementType.FnPtr:
                instructions.Add(CilOpCodes.Ldc_I4_0);
                instructions.Add(CilOpCodes.Conv_I);
                return;
            case ElementType.Boolean:
            case ElementType.Char:
            case ElementType.I1:
            case ElementType.U1:
            case ElementType.I2:
            case ElementType.U2:
            case ElementType.I4:
            case ElementType.U4:
                instructions.Add(CilOpCodes.Ldc_I4_0);
                return;
            case ElementType.Class:
            case ElementType.Object:
            case ElementType.String:
            case ElementType.Array:
            case ElementType.SzArray:
                instructions.Add(CilOpCodes.Ldnull);
                return;
        }

        //`initobj` for a type parameter as well as for a struct: it is the one form that means the same thing
        //under a reference instantiation and a value one, which is exactly why the language spells
        //`default(T)` that way and refuses both `null` and `0`.
        if (signature.IsValueType || signature.ElementType is ElementType.Var or ElementType.MVar)
        {
            var local = new CilLocalVariable(signature);
            instructions.Owner.LocalVariables.Add(local);
            instructions.Add(CilOpCodes.Ldloca, local);
            instructions.Add(CilOpCodes.Initobj, signature.ToTypeDefOrRef());
            instructions.Add(CilOpCodes.Ldloc, local);
        }
        else
        {
            instructions.Add(CilOpCodes.Ldnull);
        }
    }

    /// <summary>
    /// The constructor of <paramref name="type"/> that takes the fewest arguments, for an allocation whose own
    /// constructor call was inlined away and so cannot be read back out of the code.
    /// </summary>
    private static MethodAnalysisContext? ShortestConstructor(TypeAnalysisContext? type, AssemblyAnalysisContext? assembly)
    {
        if (type is null or RuntimeClassTypeAnalysisContext || type.Name is null)
            return null;

        //Only a type of this assembly can be trusted here. A build strips its class libraries down to what the game
        //used, so a constructor that exists in the build may not exist, or may not be reachable, in the copy the
        //project is compiled against, and a call to it would not compile.
        if (assembly is null || !ReferenceEquals(type.DeclaringAssembly, assembly))
            return null;

        MethodAnalysisContext? shortest = null;

        foreach (var method in type.Methods)
        {
            if (method.IsStatic || method.Name != ".ctor")
                continue;

            if (shortest == null || method.Parameters.Count < shortest.Parameters.Count)
                shortest = method;
        }

        return shortest;
    }

    /// <summary>
    /// The constructor call belonging to an allocation, wherever along the path it is made.
    ///
    /// The language allocates an object and constructs it in one instruction; il2cpp writes the two
    /// separately, and it does not always keep them together - a delegate is allocated, the method it is to
    /// call is worked out, and only then is the constructor called, which puts a block boundary between them.
    /// Looking only inside the allocation's own block therefore misses the delegates, and a delegate that is
    /// not built in one instruction is not one a decompiler will write back as the lambda it was: it sees an
    /// object being handed a method pointer, which C# has no syntax for at all.
    ///
    /// Only along a path that had to be taken: each step is followed only where the block goes one way and
    /// the block it goes to is reached no other way, so what is found is what runs. Anything that reads the
    /// object before its constructor stops the search, since an object already in use was not still being
    /// built.
    /// </summary>
    private static Instruction? FindConstructorCallAlongPath(MethodAnalysisContext context, Instruction newobj)
    {
        var newObject = newobj.Operands[0];
        var block = context.ControlFlowGraph!.Blocks.FirstOrDefault(candidate => candidate.Instructions.Contains(newobj));

        if (block == null)
            return null;

        var index = block.Instructions.IndexOf(newobj) + 1;
        var walked = new HashSet<Block>();

        while (block != null && walked.Add(block))
        {
            for (var i = index; i < block.Instructions.Count; i++)
            {
                var candidate = block.Instructions[i];

                if (candidate is { OpCode: OpCode.CallVoid, Operands: [MethodAnalysisContext { Name: ".ctor" }, _, ..] }
                    && ReferenceEquals(candidate.Operands[1], newObject))
                    return candidate;

                if (Mentions(candidate, newObject))
                    return null;
            }

            block = block.Successors.Count == 1 && block.Successors[0].Predecessors.Count == 1
                ? block.Successors[0]
                : null;

            index = 0;
        }

        return null;
    }

    /// <summary>Whether an instruction names a value, directly or as the place it reads or writes through.</summary>
    private static bool Mentions(Instruction instruction, object value)
    {
        foreach (var operand in instruction.Operands)
            switch (operand)
            {
                case MemoryOperand memory when ReferenceEquals(memory.Base, value) || ReferenceEquals(memory.Index, value):
                case FieldReference field when ReferenceEquals(field.Local, value):
                    return true;
                default:
                    if (ReferenceEquals(operand, value))
                        return true;
                    break;
            }

        return false;
    }

    /// <summary>
    /// Whether a constructor is the one belonging to the type being allocated, rather than the base one
    /// il2cpp leaves behind where it inlined the real constructor into its caller.
    ///
    /// Compared by name: a constructed generic type is worked out fresh wherever it is needed, so the type
    /// a call names and the type the allocation names are two objects saying the same thing - and comparing
    /// the objects would reject every delegate, whose type is always a constructed one.
    /// </summary>
    private static bool ConstructsAllocatedType(MethodAnalysisContext constructor, TypeAnalysisContext? allocatedType, Instruction? call)
        => allocatedType != null
           && (ReferenceEquals(constructor.DeclaringType, allocatedType) || constructor.DeclaringType?.FullName == allocatedType.FullName)
           && DelegateTakesItsTarget(constructor, call)
           && CanBeNamed(constructor.DeclaringType);

    /// <summary>
    /// Whether a delegate is being built around a method it can actually stand for.
    ///
    /// Building the delegate in one instruction is what lets a decompiler write it back as the lambda it came
    /// from, and to do that it reads the lambda's parameter names off the delegate's own <c>Invoke</c>, one
    /// per parameter. A method that does not take what the delegate hands out therefore has no name for one
    /// of its parameters, and the decompiler stops - taking the rest of the assembly with it, since it is
    /// decompiling all of it at once. Where the two do not agree the allocation is left as it was: the
    /// delegate reads as an object handed a method pointer, which is worse to read but costs only itself.
    /// </summary>
    /// <summary>
    /// Whether a type is one the recovered project can write down.
    ///
    /// A generic body is compiled once for all the arguments that share a representation, and il2cpp records
    /// that one body against stand-ins rather than against any of the types it actually serves - an enum
    /// argument becomes <c>System.Int32Enum</c>, which the runtime library keeps to itself. Building the
    /// delegate in one instruction names its type in the source; naming a type the project cannot see does not
    /// compile, and takes the declaration it is part of with it. Left unfused it is a cast among statements
    /// that can be dropped one at a time, which costs far less.
    /// </summary>
    private static bool CanBeNamed(TypeAnalysisContext? type)
    {
        switch (type)
        {
            case null:
                return true;

            case GenericInstanceTypeAnalysisContext generic:
                return CanBeNamed(generic.GenericType) && generic.GenericArguments.All(CanBeNamed);

            default:
                var visibility = type.Attributes & System.Reflection.TypeAttributes.VisibilityMask;
                return !IsSharedEnumStandIn(type)
                       && (visibility is System.Reflection.TypeAttributes.Public or System.Reflection.TypeAttributes.NestedPublic
                           || type.Definition == null);
        }
    }

    /// <summary>
    /// The constructor to build the allocated type with, given the one the compiled code called.
    ///
    /// A generic body is compiled once for every argument that shares a representation, so the constructor the
    /// code calls belongs to whichever instantiation il2cpp recorded that shared body against - for an enum
    /// argument, one written against a stand-in the runtime library keeps to itself. The allocation names the
    /// real type, and it is the real type the object is: the same constructor on it is the one to build it
    /// with, and it is the only one of the two the project can write down.
    /// </summary>
    private static MethodAnalysisContext? ConstructorOfAllocatedType(MethodAnalysisContext? called, TypeAnalysisContext? allocatedType)
    {
        if (called == null || allocatedType == null || CanBeNamed(called.DeclaringType))
            return called;

        return allocatedType.Methods.FirstOrDefault(m => m is { Name: ".ctor", IsStatic: false }
                                                         && m.Parameters.Count == called.Parameters.Count);
    }

    /// <summary>
    /// Whether a constructor takes what the call passed: it either is that constructor, or is the same one on
    /// the type actually allocated. A constructor picked for any other reason is one the compiled code never
    /// called, and what it was passed says nothing about what this one takes.
    /// </summary>
    private static bool TakesTheCallsArguments(MethodAnalysisContext constructor, MethodAnalysisContext? called)
        => called != null
           && (ReferenceEquals(constructor, called)
               || (constructor.Name == ".ctor" && constructor.Parameters.Count == called.Parameters.Count
                   && !CanBeNamed(called.DeclaringType)));

    /// <summary>
    /// Whether a type is one of the stand-ins il2cpp records a shared generic body against.
    ///
    /// They are declared like any other type in the metadata il2cpp writes, and are public there, but they are
    /// not in the runtime library the project is built against - no assembly Unity ships has a
    /// <c>System.Int32Enum</c>. Nothing tells them apart except that they are these, so they are listed.
    /// </summary>
    private static bool IsSharedEnumStandIn(TypeAnalysisContext type)
        => type.Namespace == "System"
           && type.Name is "SByteEnum" or "ByteEnum" or "Int16Enum" or "UInt16Enum"
                        or "Int32Enum" or "UInt32Enum" or "Int64Enum" or "UInt64Enum";

    private static bool DelegateTakesItsTarget(MethodAnalysisContext constructor, Instruction? call)
    {
        var invoke = constructor.DeclaringType?.Methods.FirstOrDefault(m => m is { Name: "Invoke", IsStatic: false });

        if (invoke == null || call == null)
            return true;

        foreach (var operand in call.Operands.Skip(2))
            if (operand is RuntimeMethodInfoAnalysisContext { RepresentedMethod: { } target })
                return target.Parameters.Count == invoke.Parameters.Count;

        return true;
    }
    /// <summary>
    /// Loads a field that sits inside a struct held in another field, one step at a time.
    /// </summary>
    /// <remarks>
    /// Only the first step decides where the walk starts - a static field is simply there, an instance one
    /// is on something. After that each step is the same instruction, because a struct on the stack is read
    /// through exactly as an object reference is.
    /// </remarks>
    private static void LoadNestedField(NestedFieldReference nested, MethodDefinition method,
        Dictionary<LocalVariable, CilLocalVariable> locals, ModuleDefinition module)
    {
        var instructions = method.CilMethodBody!.Instructions;
        var outer = nested.Path[0];

        if (outer.IsStatic)
        {
            instructions.Add(CilOpCodes.Ldsfld, outer.ToFieldDescriptor(module));
        }
        else
        {
            LoadLocal(nested.Local, method, locals);
            instructions.Add(CilOpCodes.Ldfld, outer.ToFieldDescriptor(module));
        }

        for (var i = 1; i < nested.Path.Length; i++)
            instructions.Add(CilOpCodes.Ldfld, nested.Path[i].ToFieldDescriptor(module));
    }
    /// <summary>
    /// Gives the body an ending, where the code it was built from never had one.
    /// </summary>
    /// <remarks>
    /// A <c>ret</c> is written only where the ISIL says the method returns, and plenty of bodies never say
    /// it: one whose only instruction is an undecoded jump, one whose last block falls into the exit rather
    /// than branching to it, one that ends in a call the analysis could not see past. The instructions are
    /// then all correct and the body still cannot be read, because control runs off the end of it - which a
    /// reader reports as the stack being wrong, at whatever offset it walked off.
    ///
    /// Only three kinds of instruction end a path: returning, throwing, and branching somewhere
    /// unconditionally. Anything else falls through, and if it is the last thing in the body it falls out of
    /// the method. What the method is supposed to hand back is not known here, so it hands back nothing in
    /// particular - which is what a body that was given up on is worth, and is the difference between a
    /// method that reads and one that is thrown away whole.
    /// </remarks>
    /// <summary>
    /// Writes the <c>catch</c> clause <see cref="Analysis.CatchClauses"/> recovered as a real CIL exception
    /// handler, so that the handler's body is reachable and the decompiler writes it out.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Everything the clause needs from the layout is already true by the time this runs: the analysis moved
    /// the handler's blocks to the end of the graph and left them reachable from nothing, so
    /// <see cref="LayoutOrder"/>'s "a block the walk never reached is still written out" clause emits them as
    /// one contiguous run at the end of the body. That run is the handler range; the guarded block's own run
    /// is the <c>try</c> range. Neither has to be adjacent to the other.
    /// </para>
    /// <para>
    /// Three things the CIL rules ask for and the ISIL does not know about. A handler is entered with the
    /// exception on the stack, so it opens with a <c>pop</c> - none of these handlers names the exception,
    /// because C++ landing pads read it out of the runtime rather than off the stack. Control may not
    /// <b>fall</b> into a handler, so where the block before it does, a branch to the epilogue goes in - which
    /// is the same answer running off the end of the body already gave, since <c>InitializeLocals</c> makes
    /// the returned local a default. And <c>ret</c> may not be used to leave a protected region, so every
    /// <c>ret</c> in the handler becomes a store to one local and a <c>leave</c> to the one epilogue.
    /// </para>
    /// <para>
    /// It runs after everything that moves or removes an instruction, and every boundary is looked up by
    /// identity: if any of the four is no longer in the body, the clause is dropped rather than written at a
    /// guessed offset.
    /// </para>
    /// </remarks>
    private static void AddCatchClauses(MethodAnalysisContext context, MethodDefinition definition,
        ModuleDefinition module, ReferenceImporter importer,
        Dictionary<Instruction, List<CilInstruction>> instructionMap)
    {
        if (Analysis.CatchClauses.Of(context) is not { Count: > 0 } clauses)
            return;

        var body = definition.CilMethodBody!;
        var instructions = body.Instructions;

        //Every boundary looked up by identity, all of them, before anything is moved: if one clause cannot be
        //placed the method keeps none, because a half-written set of ranges is worse than no ranges at all.
        var placed = new List<Placement>();

        foreach (var clause in clauses)
        {
            var (guardedFirst, guardedLast) = EmittedRange(clause.Guarded, instructionMap);
            var (handlerFirst, _) = EmittedRange(clause.Handler[0], instructionMap);

            if (guardedFirst is null || guardedLast is null || handlerFirst is null)
            {
                Refused(context, "a clause's own blocks emitted nothing");
                return;
            }

            if (clause.Caught.ToTypeSignature(module).ToTypeDefOrRef() is not { } caught)
            {
                Refused(context, $"no type reference for {clause.Caught.FullName}");
                return;
            }

            placed.Add(new Placement(clause, guardedFirst, guardedLast, handlerFirst, caught));
        }

        //In the order the handlers were laid out, which is the order their ranges have to be built in.
        placed.Sort((a, b) => At(instructions, a.HandlerFirst).CompareTo(At(instructions, b.HandlerFirst)));

        var firstHandler = At(instructions, placed[0].HandlerFirst);

        foreach (var placement in placed)
        {
            var tryStart = At(instructions, placement.GuardedFirst);
            var tryLast = At(instructions, placement.GuardedLast);

            if (tryStart < 0 || tryLast < tryStart || tryLast >= firstHandler)
            {
                Refused(context, $"the ranges do not hold: try {tryStart}..{tryLast}, first handler {firstHandler}");
                return;
            }
        }

        var epilogue = new CilInstruction(CilOpCodes.Nop);
        var epilogueLabel = new CilInstructionLabel(epilogue);

        var returned = context.ReturnType is { } type && type.FullName != "System.Void"
            ? ContextToTypeSignature.LocalTypeSignature(type, context, module)
            : null;

        CilLocalVariable? answer = null;
        if (returned != null)
        {
            answer = new CilLocalVariable(returned);
            body.LocalVariables.Add(answer);
        }

        //How each try is left. A protected region may not be fallen out of, returned out of, or branched out
        //of - `leave` is the only way out, and it is what unwinds the region on the way. Where the try ends
        //in a throw there is nothing to do, which is every clause found by following a throw; where the
        //table named the range instead, the try ends in whatever the protected code ended in.
        foreach (var placement in placed)
        {
            var tryLast = At(instructions, placement.GuardedLast);
            var last = instructions[tryLast];

            switch (last.OpCode.Code)
            {
                case CilCode.Throw or CilCode.Rethrow:
                    placement.TryLast = last;
                    break;

                case CilCode.Ret:
                    last.OpCode = CilOpCodes.Leave;
                    last.Operand = epilogueLabel;

                    if (answer != null)
                        instructions.Insert(tryLast, new CilInstruction(CilOpCodes.Stloc, answer));

                    placement.TryLast = last;
                    break;

                case CilCode.Br or CilCode.Br_S:
                    last.OpCode = CilOpCodes.Leave;
                    placement.TryLast = last;
                    break;

                //A `leave` is unconditional, so a two-way exit from a protected region has no CIL spelling.
                case CilCode.Brtrue or CilCode.Brtrue_S or CilCode.Brfalse or CilCode.Brfalse_S or CilCode.Switch:
                    Refused(context, $"the try is left conditionally, by {last.OpCode}");
                    return;

                default:
                    //It falls out. `leave` to where it was going to fall says the same thing and is legal.
                    if (tryLast + 1 >= At(instructions, placed[0].HandlerFirst))
                    {
                        Refused(context, "the try would fall straight into a handler");
                        return;
                    }

                    var leave = new CilInstruction(CilOpCodes.Leave, new CilInstructionLabel(instructions[tryLast + 1]));
                    instructions.Insert(tryLast + 1, leave);
                    placement.TryLast = leave;
                    break;
            }
        }

        //Each handler is entered with the exception on the stack. None of these names it - a C++ landing pad
        //reads it out of the runtime rather than off the stack - so each opens by dropping it.
        foreach (var placement in placed)
        {
            var pop = new CilInstruction(CilOpCodes.Pop);
            instructions.Insert(At(instructions, placement.HandlerFirst), pop);
            placement.Pop = pop;
        }

        instructions.Add(epilogue);

        if (answer != null)
            instructions.Add(CilOpCodes.Ldloc, answer);

        instructions.Add(CilOpCodes.Ret);

        var territory = At(instructions, placed[0].Pop!);
        var beyond = At(instructions, epilogue);

        //`ret` may not be used to leave a protected region either, so every one in a handler becomes a store
        //to the one local and a `leave` to the one epilogue. Backwards, so the insertions do not move the
        //indices still to be visited.
        for (var i = beyond - 1; i >= territory; i--)
        {
            if (instructions[i].OpCode.Code != CilCode.Ret)
                continue;

            instructions[i].OpCode = CilOpCodes.Leave;
            instructions[i].Operand = epilogueLabel;

            if (answer != null)
                instructions.Insert(i, new CilInstruction(CilOpCodes.Stloc, answer));
        }

        //Nothing may fall into a handler. Backwards again, so an insertion before one handler cannot move
        //another that has already been settled.
        for (var i = placed.Count - 1; i >= 0; i--)
        {
            var at = At(instructions, placed[i].Pop!);

            if (at <= 0 || instructions[at - 1].OpCode.FlowControl
                is CilFlowControl.Return or CilFlowControl.Throw or CilFlowControl.Branch)
                continue;

            instructions.Insert(at, new CilInstruction(CilOpCodes.Br, epilogueLabel));
        }

        //A handler that does not return runs back into the method, and a plain branch may not leave a
        //protected region. Only branches to somewhere outside the handlers' own territory: one inside is an
        //ordinary jump, and the two are told apart by position, which is exact because each handler is a
        //contiguous run and the runs are consecutive.
        territory = At(instructions, placed[0].Pop!);
        beyond = At(instructions, epilogue);

        for (var i = territory; i < beyond; i++)
        {
            if (instructions[i].OpCode.Code is not (CilCode.Br or CilCode.Br_S))
                continue;

            if (instructions[i].Operand is not CilInstructionLabel { Instruction: { } target })
                continue;

            var to = At(instructions, target);

            if (to >= territory && to < beyond)
                continue;

            instructions[i].OpCode = CilOpCodes.Leave;
        }

        //And the ranges, last, when nothing else is going to move. A handler ends where the next one begins,
        //and the final one ends at the epilogue.
        for (var i = 0; i < placed.Count; i++)
        {
            var placement = placed[i];
            var handlerEnd = i + 1 < placed.Count ? placed[i + 1].Pop! : epilogue;

            body.ExceptionHandlers.Add(new CilExceptionHandler
            {
                HandlerType = CilExceptionHandlerType.Exception,
                ExceptionType = placement.Caught,
                TryStart = new CilInstructionLabel(placement.GuardedFirst),
                TryEnd = new CilInstructionLabel(instructions[At(instructions, placement.TryLast!) + 1]),
                HandlerStart = new CilInstructionLabel(placement.Pop!),
                HandlerEnd = new CilInstructionLabel(handlerEnd),
            });

            Refused(context, $"wrote catch ({placement.Clause.Caught.FullName})");
        }
    }

    /// <summary>One clause on its way from blocks to a CIL range, as each boundary is settled.</summary>
    private sealed class Placement(Analysis.CatchClause clause, CilInstruction guardedFirst, CilInstruction guardedLast,
        CilInstruction handlerFirst, ITypeDefOrRef caught)
    {
        public readonly Analysis.CatchClause Clause = clause;
        public readonly CilInstruction GuardedFirst = guardedFirst;
        public readonly CilInstruction GuardedLast = guardedLast;
        public readonly CilInstruction HandlerFirst = handlerFirst;
        public readonly ITypeDefOrRef Caught = caught;

        /// <summary>What the try really ends on, once leaving it has been written.</summary>
        public CilInstruction? TryLast;

        /// <summary>The <c>pop</c> that opens the handler, which is where its range starts.</summary>
        public CilInstruction? Pop;
    }

    /// <summary>
    /// Where an instruction sits in the body, <b>by identity</b>. <c>IndexOf</c> compares values, and a
    /// handler that opens on a <c>nop</c> would be found at the first <c>nop</c> in the method instead of at
    /// its own - which is exactly how the first attempt at this silently refused every clause it recovered.
    /// </summary>
    private static int At(CilInstructionCollection instructions, CilInstruction wanted)
    {
        for (var i = 0; i < instructions.Count; i++)
        {
            if (ReferenceEquals(instructions[i], wanted))
                return i;
        }

        return -1;
    }

    /// <summary>Says why a recovered clause was not written, when CATCH_TRACE asks.</summary>
    private static void Refused(MethodAnalysisContext context, string why)
    {
        if (System.Environment.GetEnvironmentVariable("CATCH_TRACE") == "1")
            System.Console.Error.WriteLine($"CATCH {context.DeclaringType?.Name}::{context.Name}: {why}");
    }

    /// <summary>The first and last CIL instruction a block was emitted as, where it produced any.</summary>
    private static (CilInstruction? First, CilInstruction? Last) EmittedRange(Block block,
        Dictionary<Instruction, List<CilInstruction>> instructionMap)
    {
        CilInstruction? first = null, last = null;

        foreach (var instruction in block.Instructions)
        {
            if (!instructionMap.TryGetValue(instruction, out var generated) || generated.Count == 0)
                continue;

            first ??= generated[0];
            last = generated[^1];
        }

        return (first, last);
    }

    private static void EnsureTheBodyEnds(MethodAnalysisContext context, MethodDefinition definition,
        ModuleDefinition module, ReferenceImporter importer)
    {
        var instructions = definition.CilMethodBody!.Instructions;

        if (instructions.Count > 0
            && instructions[^1].OpCode.FlowControl is CilFlowControl.Return or CilFlowControl.Throw or CilFlowControl.Branch)
        {
            return;
        }

        if (context.ReturnType is { } returned && returned.FullName != "System.Void")
        {
            AddDefaultValue(instructions, returned, module, importer);
        }

        instructions.Add(CilOpCodes.Ret);
    }
    /// <summary>
    /// The instruction a branch to this block arrives at, following on where the block itself has none left.
    /// </summary>
    /// <remarks>
    /// A block is emptied whenever everything in it turns out to be dead - most often a tail that only loaded
    /// the return value, once the value is known from elsewhere - and the graph then drops it and re-points the
    /// edges around it. The branches are not re-pointed: their operand is still the block, and a branch whose
    /// operand is a block rather than an instruction was **thrown away entirely**, condition and all. That is
    /// what a lost `if` looks like in the output - the comparison is still computed and then discarded:
    /// <code>
    /// _ = cells.Length &lt; 4;                              // the test, with nothing left to read it
    /// if (cells[0] == Colour.None || cells[0] != cells[3]) { _ = cells[1] == Colour.None; }
    /// return true;                                        // whatever the answer was
    /// </code>
    /// An empty block has no branch of its own and so exactly one successor, so following it is unambiguous.
    /// </remarks>
    private static Instruction? FirstInstructionFrom(Block block)
    {
        var seen = new HashSet<Block>();
        var pending = new Queue<Block>();
        pending.Enqueue(block);

        while (pending.Count > 0)
        {
            var next = pending.Dequeue();

            if (!seen.Add(next))
                continue;

            if (next.Instructions.Count > 0)
                return next.Instructions[0];

            foreach (var successor in next.Successors)
                pending.Enqueue(successor);
        }

        return null;
    }

    /// <summary>
    /// Takes a branch out where its target could not be worked out, without upsetting the stack.
    /// </summary>
    /// <remarks>
    /// A branch that goes nowhere has to go, but the two kinds do not leave the same thing behind. An
    /// unconditional one reads nothing and can simply be dropped. A conditional one has already had its
    /// condition put on the stack by the instructions in front of it, and dropping it strands that value
    /// there - so the block after this one is reached along one path with a value on the stack and along
    /// another without, which is exactly the disagreement that makes a body unreadable. Discarding the
    /// condition is what the branch would have done with it.
    /// </remarks>
    private static void StopBranching(CilInstruction branch)
    {
        branch.OpCode = branch.OpCode.StackBehaviourPop == CilStackBehaviour.PopI ? CilOpCodes.Pop : CilOpCodes.Nop;
        branch.Operand = null;
    }

    /// <summary>
    /// The constructor of a four-byte packed struct and the byte fields it takes, in the order they lie.
    /// </summary>
    /// <remarks>
    /// <c>Color32</c> is four bytes and the compiler hands one round as a single 32-bit number - so a field
    /// initialiser written <c>new Color32(0x1a, 0xbc, 0x9c, 0xFF)</c> arrives as the constant
    /// <c>4288461850</c> and came out <c>(Color32)4288461850L</c>, which C# will not say. The bytes are in
    /// the number and the fields are in the metadata; nothing here is inferred. The parameter names have to
    /// match the fields they stand for, which is what refuses a constructor that takes them in another order
    /// or means something else by them.
    /// </remarks>
    private static (MethodAnalysisContext Constructor, FieldAnalysisContext[] Bytes)? PackedBytes(TypeAnalysisContext? type)
    {
        if (type is not { IsValueType: true } || Analysis.Aapcs64.SizeOf(type) is not 4)
            return null;

        var bytes = type.Fields
            .Where(f => !f.IsStatic && f.FieldType.FullName == "System.Byte"
                && f.BackingData?.FieldOffset is >= 0 and < 4)
            .OrderBy(f => f.BackingData!.FieldOffset)
            .ToArray();

        if (bytes.Length != 4 || bytes[0].BackingData!.FieldOffset != 0)
            return null;

        foreach (var candidate in type.Methods)
        {
            if (candidate.Name != ".ctor" || candidate.Parameters.Count != 4)
                continue;

            var matches = true;

            for (var i = 0; i < 4 && matches; i++)
            {
                matches = candidate.Parameters[i].ParameterType.FullName == "System.Byte"
                    && candidate.Parameters[i].ParameterName == bytes[i].Name;
            }

            if (matches)
                return (candidate, bytes);
        }

        return null;
    }

    /// <summary>
    /// The thirty-two bits a constant stands for, where it is one a four-byte struct could have been packed
    /// into.
    /// </summary>
    /// <remarks>
    /// A float is taken by its bits, because that is how the compiler moved it - <c>brownColor</c> reaches the
    /// generator as <c>-2.66289398E+38</c>, whose pattern is <c>0xFF485579</c>. **A double is refused**: one
    /// of these is a NaN, and widening a float NaN to double is not required to keep the payload, so the
    /// number that arrives may no longer be the bytes that were written.
    /// </remarks>
    private static uint? PackedBitsOf(object operand) => operand switch
    {
        int i => unchecked((uint)i),
        uint u => u,
        long l when l is >= uint.MinValue and <= uint.MaxValue => (uint)l,
        ulong u when u <= uint.MaxValue => (uint)u,
        float f => System.BitConverter.SingleToUInt32Bits(f),
        _ => null,
    };

    /// <summary>
    /// Writes a member of a struct held in a field, through the address of that field. Answers whether it did.
    /// </summary>    /// <summary>
    /// Writes a member of a struct held in a field, through the address of that field. Answers whether it did.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A struct in a field lies where it is, so <c>obj.field.x</c> is one address and one offset. Reading it
    /// back has been recovered for a long time; writing it was refused, because <c>stfld</c> of the inner
    /// field wants the address of the outer one underneath the value and nothing produced that. The store
    /// stayed a memory operand the generator could not place, and it was dropped in silence.
    /// </para>
    /// <para>
    /// An inlined <c>_ped.position = screenPos</c> writes <c>m_Position.x</c> at <c>+0x108</c> of a
    /// <c>PointerEventData</c>. The IL is the object, one <c>ldflda</c> per step in front of the last, the
    /// value, and <c>stfld</c> - which is exactly what C# compiles <c>obj.field.x = v</c> to, and what the
    /// decompiler writes back out as that.
    /// </para>
    /// <para>
    /// Every step in front of the last has to be a struct: the address of a field holding a *reference* is
    /// not the object, and storing through it would be writing the reference itself. A static at the head is
    /// refused as well - <c>ldsflda</c> is a different instruction and a static struct's members usually want
    /// a setter that may not exist.
    /// </para>
    /// </remarks>
    private static bool TryStoreNestedField(NestedFieldReference nested, object value, MethodDefinition method,
        Dictionary<LocalVariable, CilLocalVariable> locals, MemberReference writeLine, MemberReference stringCtor)
    {
        if (nested.Field.IsStatic)
            return false;

        for (var step = 0; step < nested.Path.Length - 1; step++)
            if (nested.Path[step].FieldType is not { IsValueType: true })
                return false;

        var module = method.DeclaringModule!;
        var instructions = method.CilMethodBody!.Instructions;

        //A struct in *static* storage is reached the same way, from the address of the static itself. Only
        //where the project can name that static: an inaccessible one would need a setter, which is a call and
        //not a place.
        var first = 0;

        if (nested.Path[0].IsStatic)
        {
            if (!MetadataResolver.ReachableFrom(nested.Path[0], CurrentContext!))
                return false;

            instructions.Add(CilOpCodes.Ldsflda, nested.Path[0].ToFieldDescriptor(module));
            first = 1;
        }
        else if (!ValueTypeFieldOwner(nested, method, locals))
        {
            LoadLocal(nested.Local, method, locals);
        }

        for (var step = first; step < nested.Path.Length - 1; step++)
            instructions.Add(CilOpCodes.Ldflda, nested.Path[step].ToFieldDescriptor(module));

        LoadOperand(value, method, locals, writeLine, stringCtor, nested.Field.FieldType);
        instructions.Add(CilOpCodes.Stfld, nested.Field.ToFieldDescriptor(module));
        return true;
    }

    /// <summary>
    /// Loads what a <see cref="CilOpCodes.Stfld"/> needs underneath the value, where the field belongs to a
    /// struct held in a local or passed by value. Answers whether it did.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>stfld</c> takes an object reference or a managed pointer. A class local is a reference already, so
    /// loading it is right - but a struct local is the value itself, and storing into a copy on the
    /// evaluation stack says nothing. The IL does not verify, and ILSpy renders the refusal as the pointer
    /// write it literally is: <c>((SubCellRef*)(nint)subCellRef)-&gt;cellIndex = num21</c>, commented out.
    /// </para>
    /// <para>
    /// What the source said was <c>new SubCellRef { cellIndex = ci, subIndex = sub }</c>, and an object
    /// initialiser on a struct is exactly a default followed by two field stores - so the address of the
    /// local is what those stores want. <c>this</c> inside a struct is already a managed pointer and is left
    /// to upstream's <c>ldarg.0</c>.
    /// </para>
    /// </remarks>
    private static bool ValueTypeFieldOwner(FieldReference field, MethodDefinition method,
        Dictionary<LocalVariable, CilLocalVariable> locals)
    {
        if (field.Local is not { IsThis: false } owner)
            return false;

        var parameter = method.Parameters.FirstOrDefault(p => p.Name == owner.Name);

        if (parameter != null)
        {
            if (parameter.ParameterType is not { IsValueType: true } or ByReferenceTypeSignature)
                return false;

            method.CilMethodBody!.Instructions.Add(CilOpCodes.Ldarga, parameter);
            return true;
        }

        if (!locals.TryGetValue(owner, out var held) || held.VariableType is not { IsValueType: true }
            || held.VariableType is ByReferenceTypeSignature or PointerTypeSignature)
            return false;

        method.CilMethodBody!.Instructions.Add(CilOpCodes.Ldloca, held);
        return true;
    }

    /// <summary>
    /// The <c>String</c> constructor a call to <c>String::CreateString</c> stands for.
    /// </summary>
    /// <remarks>
    /// A string constructor is not a constructor at the IL level: <c>new string('0', n)</c> compiles to a
    /// static-looking call to <c>System.String::CreateString(char, int)</c> whose receiver is null, and the
    /// generator writes it out literally - <c>((string)null).CreateString('0', decimals)</c> - which does not
    /// compile, so the local it defines and every statement after it are commented out. Two members lose
    /// their whole body to it, `Utility::RoundDecimalsString` and `StringExtension::Reverse`, and in both it
    /// is the only thing wrong.
    /// </remarks>
    private static MethodAnalysisContext? StringConstructionCall(MethodAnalysisContext target, Instruction instruction)
    {
        if (target.Name != "CreateString" || target.DeclaringType?.FullName != "System.String")
            return null;

        foreach (var candidate in target.DeclaringType.Methods)
        {
            if (candidate is { IsStatic: false, Name: ".ctor" }
                && candidate.Parameters.Count == target.Parameters.Count
                && candidate.Parameters.Select((p, at) => p.ParameterType.FullName == target.Parameters[at].ParameterType.FullName).All(same => same))
                return candidate;
        }

        return null;
    }

    /// <summary>Emits that constructor as a <c>newobj</c>, with the call's arguments and without its receiver.</summary>
    private static bool TryEmitStringConstruction(MethodAnalysisContext constructor, Instruction instruction,
        MethodDefinition method, Dictionary<LocalVariable, CilLocalVariable> locals,
        MemberReference writeLine, MemberReference stringCtor)
    {
        var module = method.DeclaringModule!;
        var instructions = method.CilMethodBody!.Instructions;

        //`CreateString` is declared as an instance method, so the receiver sits where `this` would and the
        //arguments begin one later.
        var first = instruction.OpCode == OpCode.Call ? 3 : 2;

        for (var i = 0; i < constructor.Parameters.Count; i++)
        {
            if (first + i >= instruction.Operands.Count)
                return false;

            LoadOperand(instruction.Operands[first + i], method, locals, writeLine, stringCtor,
                constructor.Parameters[i].ParameterType);
        }

        instructions.Add(CilOpCodes.Newobj, module.DefaultImporter!.ImportMethod(constructor.ToMethodDescriptor(module)));

        if (instruction.OpCode == OpCode.Call)
            StoreToOperand(instruction.Operands[1], method, locals, writeLine);
        else
            instructions.Add(CilOpCodes.Pop);

        return true;
    }

    /// <summary>
    /// Throws the object the method built, where <see cref="Analysis.ThrowTheConstructedException"/> has said
    /// which one it is.
    /// </summary>
    /// <remarks>
    /// The opcode carries a <em>type</em> and the generator makes a fresh instance of it, which is right for
    /// the throws the runtime raises on its own account and wrong for every <c>throw new Foo(message)</c> the
    /// program wrote - there the object exists already, with its message in it, and a new one of some other
    /// type was being thrown in its place.
    /// </remarks>
    private static bool TryThrowTheObject(Instruction instruction, MethodDefinition method,
        Dictionary<LocalVariable, CilLocalVariable> locals)
    {
        if (instruction.Operands is not [LocalVariable thrown] || !locals.TryGetValue(thrown, out var held))
            return false;

        method.CilMethodBody!.Instructions.Add(CilOpCodes.Ldloc, held);
        return true;
    }
}
