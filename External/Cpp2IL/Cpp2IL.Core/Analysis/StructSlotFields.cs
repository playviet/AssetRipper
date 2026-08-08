using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Reads a slot that lies inside a struct on the stack as the field of that struct.
/// </summary>
/// <remarks>
/// <para>
/// A struct the frame holds spans several slots, and the stack analysis gives each offset a variable of its
/// own - so a field of it has no connection to the struct it belongs to. Where a callee writes that field
/// through a pointer, as <c>MoveNext</c> writes an enumerator's current element, nothing in the caller's view
/// ever assigns the variable and it reads back as whatever the compiler cleared the slot to:
/// </para>
/// <code>
/// List&lt;int&gt;.Enumerator enumerator = xs.GetEnumerator();
/// int num3 = default(int);                  // this is enumerator.Current
/// while (enumerator.MoveNext())
///     num = (keep(num3) ? num3 : 0) + num;  // so every element read as zero
/// </code>
/// <para>
/// The slot is not a variable of its own at all: it is the struct's field. Naming it so gives the loop its
/// element back, and where the field is private - <c>List&lt;T&gt;.Enumerator._current</c> is -
/// <see cref="InaccessibleFieldRecovery"/> then says it by its property, which is why this runs before that.
/// </para>
/// <para>
/// The layout comes from <c>MetadataResolver.FieldOfStructValue</c> rather than from anything worked out
/// here: it already walks a generic instantiation's fields at their natural alignment and hands back a
/// <see cref="ConcreteGenericFieldAnalysisContext"/>, which is what keeps <c>_current</c> from coming out as
/// the type parameter it is declared as. A first version laid the fields out by hand off the open definition
/// and wrote <c>List&lt;&gt;.Enumerator</c>, which is not a type anything can name.
/// </para>
/// </remarks>
public static class StructSlotFields
{
    public static bool Run(MethodAnalysisContext method)
    {
        if (method.ControlFlowGraph is not { } graph)
            return false;

        //Every slot the frame has, by where it starts, **taken from the graph**. `method.Locals` holds only
        //what the stack analysis registered at the start, which is the callee-saved area; the enumerator's
        //own slot and the fields a callee wrote through it are made by the passes above and appear only as
        //operands - and those are exactly the ones this pass exists for. Every version of a slot is kept,
        //since single assignment form gives one per write and each has to be named.
        var slots = new Dictionary<int, List<LocalVariable>>();

        foreach (var instruction in graph.Instructions)
            foreach (var operand in instruction.Operands)
                if (SlotLocal(operand) is { } local && OffsetOf(local) is { } offset)
                {
                    if (!slots.TryGetValue(offset, out var versions))
                        slots[offset] = versions = [];

                    if (!versions.Contains(local))
                        versions.Add(local);
                }

        if (slots.Count < 2)
            return false;

        //What each slot inside a struct stands for - a chain of fields, since a struct's field may be a
        //struct with the slot inside *it*, which is where a dictionary keeps its key and its value.
        var fields = new Dictionary<LocalVariable, (LocalVariable Struct, FieldAnalysisContext[] Path, int Offset)>();

        //Nearest first. Two struct slots can overlap - single assignment form gives a copy of a struct its
        //own slot, and the copy sits a few bytes from the original - and then a slot inside the nearer one
        //is also some distance into the further one, at a different field. The nearer is the one it is
        //actually in: `Corpus::Tally` named its key `_current.value` of the copy eight bytes below it.
        foreach (var (start, versions) in slots.OrderByDescending(entry => entry.Key))
        {
            //An instantiation only. il2cpp records no field offsets for one - which is the whole reason this
            //pass has to lay it out at all - so no other pass has had a chance to name the slot and the
            //arithmetic is the only thing that can. An ordinary struct's offsets *are* recorded, and then
            //the arithmetic is speculation competing with metadata: it renamed a Quaternion temporary that
            //merely sat above a `CharTransform` into that struct's `scale`, and
            //`DOTweenTMPAnimator.SetCharScale` lost its call to a cast that cannot be written.
            if (versions.Find(v => v.Type is GenericInstanceTypeAnalysisContext { IsValueType: true } candidate
                    && candidate.Namespace != nameof(System)
                    && !IsSharedStandIn(candidate)) is not { } held
                || held.Type is not GenericInstanceTypeAnalysisContext structure)
                continue;

            foreach (var (offset, inner) in slots)
            {
                //Only the slots above this one, and not the struct's own first slot, which is the struct.
                if (offset <= start)
                    continue;

                if (MetadataResolver.PathIntoStructValue(structure, offset - start, method) is not { } path)
                    continue;

                foreach (var inside in inner)
                    if (!ReferenceEquals(inside, held) && !fields.ContainsKey(inside) && CanBeTheField(inside, path[^1]))
                        fields[inside] = (held, path, offset - start);
            }
        }

        if (fields.Count == 0)
            return false;

        var changed = false;

        foreach (var instruction in graph.Instructions)
        {
            //Reads only. A write to the slot is the caller filling the field in, which is true as it stands,
            //and turning it into a store needs the address of the struct rather than its value.
            for (var operand = instruction.IsAssignment ? 1 : 0; operand < instruction.Operands.Count; operand++)
            {
                if (instruction.Operands[operand] is LocalVariable local && fields.TryGetValue(local, out var named))
                {
                    instruction.Operands[operand] = named.Path.Length == 1
                        ? new FieldReference(named.Path[0], named.Struct, named.Offset)
                        : new NestedFieldReference(named.Path, named.Struct, named.Offset);
                    changed = true;
                }
            }
        }

        return changed;
    }

    /// <summary>
    /// Whether the slot can actually be that field, rather than merely sitting where one would.
    /// </summary>
    /// <remarks>
    /// Containment is inferred from two frame offsets and nothing else, so a slot that already holds
    /// something of its own can be claimed as a field of whatever lies below it. In <c>Corpus::Tally</c> the
    /// dictionary enumerator's own slot sits eight bytes above a **copy of itself**, and naming it
    /// <c>_version</c> - an <c>int</c> - turned the <c>foreach</c> into pointer arithmetic between two
    /// instantiations, with the whole loop commented out.
    ///
    /// A slot with no type is the shape this pass exists for: nothing in the caller's view ever assigned it,
    /// which is exactly why it needs naming. Where it does have one, that type is evidence and has to agree.
    /// </remarks>
    private static bool CanBeTheField(LocalVariable slot, FieldAnalysisContext field)
        => slot.Type == null || slot.Type.FullName == field.FieldType.FullName;

    /// <summary>
    /// Whether the instantiation is one a shared body writes rather than one the program has.
    /// </summary>
    /// <remarks>
    /// A shared body names its arguments by stand-ins - <c>object</c> for every reference and
    /// <c>System.Int32Enum</c> for every enum of that width - so <c>Dictionary&lt;string, object&gt;</c>
    /// arrives as <c>Dictionary&lt;object, object&gt;</c>. Laying that out gives the offsets of a type the
    /// program does not have, and the field named at an offset is then simply the wrong one:
    /// <c>DictToJson</c> got <c>enumerator._version</c> - an int, cast to string - where the key belongs, and
    /// the statement and everything after it was commented out. A real instantiation like
    /// <c>List&lt;int&gt;.Enumerator</c> is laid out as the program has it, and is what this pass is for.
    /// </remarks>
    private static bool IsSharedStandIn(GenericInstanceTypeAnalysisContext structure)
        => structure.GenericArguments.Any(argument => argument.FullName
            is "System.Object" or "System.SByteEnum" or "System.ByteEnum" or "System.Int16Enum"
            or "System.UInt16Enum" or "System.Int32Enum" or "System.UInt32Enum" or "System.Int64Enum"
            or "System.UInt64Enum");

    /// <summary>The local a slot operand names, whether it stands alone or is the base of an address.</summary>
    private static LocalVariable? SlotLocal(object operand) => operand switch
    {
        LocalVariable local => local,
        MemoryOperand { Base: LocalVariable local } => local,
        _ => null,
    };

    /// <summary>Where in the frame a local sits, if it is a slot rather than a register.</summary>
    private static int? OffsetOf(LocalVariable local)
    {
        var name = local.Register.Name;

        var suffix = name.StartsWith(StackSlots.AddressPrefix) ? name[StackSlots.AddressPrefix.Length..]
            : name.StartsWith(StackSlots.ValuePrefix) ? name[StackSlots.ValuePrefix.Length..]
            : null;

        if (suffix == null)
            return null;

        var negative = suffix.StartsWith('-');

        return int.TryParse(negative ? suffix[1..] : suffix, NumberStyles.HexNumber, null, out var value)
            ? negative ? -value : value
            : null;
    }
}
