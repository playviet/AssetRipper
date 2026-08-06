using System.Collections.Generic;
using System.Reflection;
using System.Linq;
using Cpp2IL.Core.Extensions;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.Il2CppApiFunctions;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using LibCpp2IL;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// What this fork adds to metadata resolution: folding the address arithmetic a field access is
/// compiled into, following a value through the copies that carry it, and trimming the arguments a call
/// was given before its callee was known.
///
/// Kept apart from the file it belongs to so that the file stays as close to upstream as it can,
/// and a later version of Cpp2IL can be merged without the two sets of changes meeting.
/// </summary>
public static partial class MetadataResolver
{
    /// <summary>
    /// The address a metadata usage really lives at, given the address a load names.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Position-independent code does not name a global directly. It loads the global's address out of a slot
    /// in the global offset table and reads through that, so the address in <c>Move local, [addr]</c> is the
    /// <i>slot</i> and the usage is at whatever the slot holds. Nothing was ever found at the slot, so every
    /// one of these came out as an unmanaged load of a bare address - and with it went the string, type,
    /// method or field it named.
    /// </para>
    /// <para>
    /// The relocation that fills the slot is <c>R_AARCH64_RELATIVE</c>, which LibCpp2IL already applies when
    /// it loads the image, so the pointer is simply there to be read.
    /// </para>
    /// <para>
    /// Following it is safe because the answer is only used when it lands exactly on a known usage: an
    /// address that is not a slot either fails to map or holds something no usage is registered at, and is
    /// handed back unchanged.
    /// </para>
    /// </remarks>
    internal static ulong ThroughGlobalOffsetTable(MethodAnalysisContext method, ulong address)
    {
        var context = method.AppContext.LibCpp2IlContext;

        //The ordinary, un-indirected form: the load names the usage itself and there is nothing to follow.
        if (context.GetAnyGlobalByAddress(address) != null)
            return address;

        var binary = method.AppContext.Binary;

        if (!binary.TryMapVirtualAddressToRaw(address, out _))
            return address;

        var slot = binary.ReadPointerAtVirtualAddress(address);

        return slot != 0 && context.GetAnyGlobalByAddress(slot) != null ? slot : address;
    }

    /// <summary>
    /// Whether the field is somewhere rather than something.
    ///
    /// A <c>const</c> is written into the code that reads it and has no storage, but it is still recorded as
    /// a static field of its type with an offset of zero - so it sits in front of the field that really is at
    /// zero and gets named in its place. <c>Vector3.zero</c> came back as <c>Vector3.kEpsilon</c>.
    /// </summary>
    private static bool HasStorage(FieldAnalysisContext field)
        => (field.Attributes & FieldAttributes.Literal) == 0;

    /// <summary>
    /// The field of a generic type an offset names, worked out from the layout the shared body sees.
    ///
    /// Where a type's fields land depends on its type arguments, so il2cpp records neither offsets nor a size
    /// for a generic type - every field reads as zero - which leaves a body reading through one, whether it is
    /// the definition's own accessors or a caller holding an instance of it, reading unmanaged memory at an
    /// offset that matches nothing. That is every access to a <c>List</c> or a <c>Dictionary</c> the compiler
    /// inlined, as well as the fields a generic class of the game's own declares.
    ///
    /// One body is shared between every instantiation whose arguments are references, and that is the body
    /// being read, so in it a type parameter is a pointer like any other reference. Its own fields then follow
    /// one another at the sizes the shared body gives them, each aligned to its own, starting where whatever
    /// it derives from ends: the object header, a recorded size where the base is not generic, or the same
    /// walk again where it is. A field whose size is not known from here - a struct, an enum - ends the walk,
    /// because nothing after it can be placed.
    /// </summary>
    private static FieldAnalysisContext? FieldOfOpenGeneric(TypeAnalysisContext type, long offset, MethodAnalysisContext method, bool statics = false)
    {
        if (type is StaticFieldStorageTypeAnalysisContext)
            return null;

        var instance = type as GenericInstanceTypeAnalysisContext;
        var definition = instance?.GenericType ?? type;

        if (definition.GenericParameters.Count == 0)
            return null;

        var pointerSize = method.AppContext.Binary.is32Bit ? 4 : 8;

        //A type's static storage is its own and begins at nothing; its instance fields carry whatever it
        //inherits in front of them.
        if ((statics ? 0 : LayoutStart(definition, pointerSize)) is not { } position)
            return null;

        foreach (var field in definition.Fields)
        {
            if (field.IsStatic != statics)
                continue;

            //Only where the offsets really are missing. A generic type whose layout is recorded does not
            //need this, and using it there would name the wrong field.
            if (field.BackingData?.FieldOffset != 0)
                return null;

            if (SizeOf(field.FieldType, pointerSize) is not { } size)
                return null;

            //Each field starts at a multiple of its own size, which is what the generated struct does.
            position += (size - position % size) % size;

            if (position == offset)
            {
                //Named through the arguments the type has here rather than as a field of the bare definition:
                //the object it is read from is an instance, and a field of the definition belongs to no
                //instance, so writing one reads back as a store through a cast to an unbound type.
                return instance == null
                    ? new ConcreteGenericFieldAnalysisContext(field, definition.GenericParameters)
                    : new ConcreteGenericFieldAnalysisContext(field, instance);
            }

            if (position > offset)
                return null;

            position += size;
        }

        return null;
    }

    /// <summary>
    /// The field of a generic base an offset names, where the base's layout can be worked out.
    /// </summary>
    /// <remarks>
    /// A type that is not itself generic can still inherit from one, and then its inherited fields are at
    /// offsets nothing records: the base is generic, so all of its fields read as zero. Looking for the offset
    /// among the base's declarations therefore finds nothing, and every inherited field is read as unmanaged
    /// memory - which for this game is most of its configuration data, all of which derives from a generic
    /// <c>ScriptableObject</c>.
    ///
    /// Walking such a layout was measured before and refused, because it named more fields wrongly than
    /// rightly once the chain above the base was itself generic - there is no recorded size for a generic
    /// type, so the walk had nothing to start from and started from a guess. It is used here because the walk
    /// now says so: it begins at the first recorded size above the base and gives back nothing at all when it
    /// cannot reach one. Where the base derives from something ordinary the chain closes exactly.
    /// </remarks>
    private static FieldAnalysisContext? FieldOfGenericBase(TypeAnalysisContext type, long offset, MethodAnalysisContext method)
    {
        var seen = 0;

        for (var baseType = BaseOfPossiblyGeneric(type); baseType != null && seen++ < 8; baseType = BaseOfPossiblyGeneric(baseType))
        {
            if (baseType is GenericInstanceTypeAnalysisContext && FieldOfOpenGeneric(baseType, offset, method) is { } field)
                return field;
        }

        return null;
    }

    /// <summary>
    /// What a type derives from. An instance of a generic type carries no base of its own - only the
    /// definition it instantiates does - so asking the instance directly ends the walk one type early.
    /// </summary>
    private static TypeAnalysisContext? BaseOfPossiblyGeneric(TypeAnalysisContext type)
        => type.BaseType ?? (type as GenericInstanceTypeAnalysisContext)?.GenericType.BaseType;

    /// <summary>Where a generic type's own instance fields begin, or null when that cannot be worked out.</summary>
    private static long? LayoutStart(TypeAnalysisContext definition, int pointerSize, int depth = 0)
    {
        if (depth > 8)
            return null;

        if (definition.BaseType is not { } baseType || baseType.FullName == "System.Object")
            return pointerSize == 4 ? 8 : 0x10;

        var baseDefinition = (baseType as GenericInstanceTypeAnalysisContext)?.GenericType ?? baseType;

        //A type that is not generic has one layout, and il2cpp records how big it is.
        if (baseDefinition.GenericParameters.Count == 0)
            return RecordedInstanceSize(baseDefinition);

        if (LayoutStart(baseDefinition, pointerSize, depth + 1) is not { } start)
            return null;

        foreach (var field in baseDefinition.Fields)
        {
            if (field.IsStatic)
                continue;

            if (field.BackingData?.FieldOffset != 0 || SizeOf(field.FieldType, pointerSize) is not { } size)
                return null;

            start += (size - start % size) % size + size;
        }

        return start;
    }

    /// <summary>The instance size il2cpp recorded for a type, if it recorded one.</summary>
    private static long? RecordedInstanceSize(TypeAnalysisContext type)
    {
        if (type.Definition is not { } definition)
            return null;

        try
        {
            var size = definition.RawSizes.instance_size;
            return size > 0 ? size : null;
        }
        catch (System.Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// How much room a value of this type takes in the body shared between a generic's instantiations. A type
    /// parameter is a pointer there, whatever it stands for elsewhere: the instantiations that would make it
    /// something else get a body of their own. Null where the size is not known from here.
    /// </summary>
    private static long? SizeOf(TypeAnalysisContext type, int pointerSize)
    {
        if (type is GenericParameterTypeAnalysisContext || !type.IsValueType)
            return pointerSize;

        return type.FullName switch
        {
            "System.Boolean" or "System.SByte" or "System.Byte" => 1,
            "System.Int16" or "System.UInt16" or "System.Char" => 2,
            "System.Int32" or "System.UInt32" or "System.Single" => 4,
            "System.Int64" or "System.UInt64" or "System.Double" or "System.IntPtr" or "System.UIntPtr" => 8,
            _ => null,
        };
    }

    /// <summary>
    /// Rewrites every read of <c>[local]</c> into <c>local</c>, for the locals that hold a metadata usage.
    /// </summary>
    /// <remarks>
    /// Where a binary reaches its usage globals through the global offset table there are two loads, one for the
    /// address of the usage and one for the usage itself. The first has just been resolved to the usage, so the second
    /// is now reading through something that is no longer an address. Collapsing it leaves the same shape a binary
    /// that addresses its globals directly produces, which is what everything downstream expects.
    /// </remarks>
    /// <summary>
    /// Carries "holds the usage rather than its address" along the copies that pass it on.
    ///
    /// A conditional picks between two string literals by choosing between the two slots that hold them and
    /// then reading through the choice, so the value that gets dereferenced is the result of the choice and
    /// not either slot. Without following the copy the read stays, and a pair of strings comes back as a
    /// choice between two of them loaded from somewhere - which has no type, so it is written out as an
    /// <c>object</c> and cast back.
    /// </summary>
    private static void PropagateResolved(MethodAnalysisContext method, HashSet<LocalVariable> resolved)
    {
        bool changed;

        do
        {
            changed = false;

            foreach (var instruction in method.ControlFlowGraph!.Instructions)
            {
                if (instruction.OpCode is not (OpCode.Move or OpCode.Select or OpCode.Phi))
                    continue;

                if (instruction.Operands[0] is not LocalVariable destination || resolved.Contains(destination))
                    continue;

                //A select's operand 1 is the condition and says nothing about the value chosen.
                var first = instruction.OpCode == OpCode.Select ? 2 : 1;

                if (instruction.Operands.Count <= first)
                    continue;

                var all = true;

                for (var i = first; i < instruction.Operands.Count && all; i++)
                    all = HoldsUsage(instruction.Operands[i], resolved);

                if (all)
                    changed |= resolved.Add(destination);
            }
        } while (changed);
    }

    /// <summary>Whether an operand is a metadata usage rather than the address of one.</summary>
    private static bool HoldsUsage(object operand, HashSet<LocalVariable> resolved) => operand switch
    {
        LocalVariable local => resolved.Contains(local),
        string or TypeAnalysisContext or MethodAnalysisContext => true,
        _ => false,
    };

    private static void CollapseUsageDereferences(MethodAnalysisContext method, HashSet<LocalVariable> resolved)
    {
        if (resolved.Count == 0)
            return;

        foreach (var instruction in method.ControlFlowGraph!.Instructions)
        {
            for (var i = 0; i < instruction.Operands.Count; i++)
            {
                if (instruction.Operands[i] is MemoryOperand { Index: null, Scale: 0, Addend: 0, Base: LocalVariable local }
                    && resolved.Contains(local))
                    instruction.Operands[i] = local;
            }
        }
    }

    /// <summary>
    /// Folds address arithmetic into the memory operand that uses it. Il2cpp computes the address of a
    /// field once and then reads through it - <c>t = this + 16; x = [t]</c> - and while that is the same
    /// thing as <c>x = [this + 16]</c>, only the second shape can be matched against a field, because an
    /// <c>Add</c> never gives its destination a type no matter how well typed its inputs are. Returns
    /// whether anything was folded, so this can sit in the same fixpoint as the field resolution it feeds.
    /// </summary>
    public static bool FoldAddressArithmetic(MethodAnalysisContext method)
    {
        var definitions = new Dictionary<LocalVariable, Instruction>();

        foreach (var instruction in method.ControlFlowGraph!.Instructions)
            if (instruction.Destination is LocalVariable destination)
                definitions[destination] = instruction;

        var changed = false;

        foreach (var instruction in method.ControlFlowGraph.Instructions)
        {
            for (var i = 0; i < instruction.Operands.Count; i++)
            {
                if (instruction.Operands[i] is not MemoryOperand { Index: null, Scale: 0, Base: LocalVariable { Type: null } } memory)
                    continue;

                if (TryFold(memory, definitions) is not { } folded)
                    continue;

                instruction.Operands[i] = folded;
                changed = true;
            }
        }

        return changed;
    }

    // Walks back through however many address computations were chained, and only rewrites once it has
    // reached a base that is not one of them. Stopping at the depth bound instead would rewrite to a base
    // that is still foldable, and the fixpoint this sits in would then never settle.
    private static MemoryOperand? TryFold(MemoryOperand memory, Dictionary<LocalVariable, Instruction> definitions)
    {
        const int maxDepth = 8;

        var current = memory;
        var folded = false;

        for (var depth = 0; depth <= maxDepth; depth++)
        {
            if (current.Base is not LocalVariable local || local.Type != null || !definitions.TryGetValue(local, out var definition))
                return folded ? current : null;

            if (definition.OpCode is not (OpCode.Add or OpCode.Subtract) || definition.Operands.Count < 3)
                return folded ? current : null;

            if (definition.Operands[1] is not LocalVariable addressBase || !definition.Operands[2].IsNumeric())
                return folded ? current : null;

            if (depth == maxDepth)
                return null;

            var offset = System.Convert.ToInt64(definition.Operands[2]);
            current = new MemoryOperand(addressBase, addend: current.Addend + (definition.OpCode == OpCode.Add ? offset : -offset));
            folded = true;
        }

        return null;
    }

    //Where Il2CppClass keeps the pointer to a type's static storage.
    private static long StaticFieldsOffset(MethodAnalysisContext method) => method.AppContext.Binary.is32Bit ? 0x5C : 0xB8;

    private static bool IsVisibleFrom(FieldAnalysisContext field, MethodAnalysisContext method)
    {
        var declaringAssembly = field.DeclaringType?.DeclaringAssembly;

        if (declaringAssembly is null || ReferenceEquals(declaringAssembly, method.DeclaringType?.DeclaringAssembly))
            return true;

        return (field.Attributes & FieldAttributes.FieldAccessMask) == FieldAttributes.Public;
    }

    /// <summary>
    /// Drops the operands a resolved call does not take. A call is lifted with every register the calling
    /// convention could have used, because nothing is known about the callee yet; once it is known, the surplus
    /// is not arguments at all. Il2cpp's hidden trailing argument is the usual one - a delegate invoke is handed
    /// the delegate's own method pointer - and while the emitted IL never reads past the signature, the operand
    /// still counts as a use, which keeps the load that produced it alive and writes it out as a placeholder.
    /// Returns whether anything was dropped.
    /// </summary>
    public static bool TrimResolvedCallArguments(MethodAnalysisContext method)
    {
        var changed = false;

        foreach (var instruction in method.ControlFlowGraph!.Instructions)
        {
            if (instruction.OpCode is not (OpCode.Call or OpCode.CallVoid))
                continue;

            if (instruction.Operands.Count == 0 || instruction.Operands[0] is not MethodAnalysisContext callee)
                continue;

            //Operand 0 is the callee, and a call that produces a value keeps it in operand 1.
            var kept = instruction.OpCode == OpCode.Call ? 2 : 1;

            if (!callee.IsStatic)
                kept++;

            kept += callee.Parameters.Count;

            if (instruction.Operands.Count <= kept)
                continue;

            instruction.Operands.RemoveRange(kept, instruction.Operands.Count - kept);
            changed = true;
        }

        return changed;
    }
    /// <summary>
    /// Finds the field an offset names when it lands inside a struct held in another field.
    /// </summary>
    /// <remarks>
    /// A struct field stores its members where it lies, so a read of one of them is a read at a fixed
    /// distance from the outer field and cannot be told apart from a read of the outer type. What decides
    /// which field an offset belongs to is the next field's offset: a field owns everything from where it
    /// starts to where the one after it does, which needs no type sizes - and sizes are the thing this
    /// metadata does not record for a generic or a value type reliably enough to divide by.
    ///
    /// Where a struct's own members are measured from is not fixed: a value type is laid out both boxed and
    /// bare, so its recorded offsets may or may not count the header a boxed one carries. Both readings are
    /// tried and the one that lands exactly on a member is the right one, which is a fact rather than a
    /// preference.
    /// </remarks>
    internal static FieldAnalysisContext[]? PathToNestedField(TypeAnalysisContext owner, long addend, bool statics, int header, int depth = 0)
    {
        //Three steps of nesting is past anything this has been seen to need, and stops a cycle in the
        //metadata - a struct that somehow contains itself - from running the analysis out of stack.
        if (depth > 3)
            return null;

        var fields = owner.Fields
            .Where(f => f.IsStatic == statics && HasStorage(f) && f.BackingData?.FieldOffset != null)
            .OrderBy(f => f.BackingData!.FieldOffset)
            .ToList();

        for (var i = 0; i < fields.Count; i++)
        {
            var start = (long)fields[i].BackingData!.FieldOffset;

            //Sorted, so once the offsets are past what is being read no later field can hold it.
            if (addend < start)
                break;

            var end = i + 1 < fields.Count ? (long)fields[i + 1].BackingData!.FieldOffset : long.MaxValue;

            if (addend >= end)
                continue;

            if (addend == start)
                return [fields[i]];

            //Only a struct has members of its own at a distance inside the field. A reference field at this
            //offset would need following, which is a different instruction and is already handled as one.
            if (fields[i].FieldType is not { IsValueType: true } inner || inner.Fields.Count == 0)
                return null;

            var inside = PathToNestedField(inner, addend - start, statics: false, header, depth + 1)
                ?? PathToNestedField(inner, addend - start + header, statics: false, header, depth + 1);

            return inside == null ? null : [fields[i], .. inside];
        }

        return null;
    }
}
