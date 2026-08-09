using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// The field offsets of a generic type, which the metadata does not record.
/// </summary>
/// <remarks>
/// <para>
/// A generic type is laid out once per set of arguments, so there is no one layout to record and
/// <c>BackingData.FieldOffset</c> is <b>zero for every field it declares</b>. Anything that names a field by
/// the distance into the object therefore finds nothing, and in <c>Assembly-CSharp</c> that is 36 of the 97
/// additions of <c>this</c> and a constant that are still unnamed - 24 of them in one type, whose
/// <c>AppendSnapshotFields</c> loses forty lines to it.
/// </para>
/// <para>
/// The layout can be worked out: the header, then every field of every type in the base chain in
/// declaration order, each at its natural alignment. What makes that safe to use rather than merely
/// plausible is that it can be <b>checked against the metadata</b>. A concrete type deriving from the
/// generic one has its own fields recorded, and the first of them begins exactly where the base ends - so
/// laying out the base and comparing that end against a recorded offset either agrees or does not, and this
/// only answers where it agrees.
/// </para>
/// <para>
/// For <c>BaseTrackingSaveData`1</c> the walk ends at <c>0xC0</c> and <c>TrackingSaveData._currentGameId</c>
/// is recorded at <c>0xC0</c>.
/// </para>
/// </remarks>
public static class GenericFieldLayout
{
    //Keyed by the header as well as the type: a struct reached through a byref has none and a boxed one
    //has sixteen, so the same type has two different layouts and caching only the first served the wrong
    //one to the second caller.
    private static readonly Dictionary<(TypeAnalysisContext Owner, int Header), Dictionary<long, FieldAnalysisContext>?> Known = [];

    /// <summary>The field of a generic type lying at that distance into an instance, if one does.</summary>
    public static FieldAnalysisContext? FieldAt(TypeAnalysisContext owner, long offset, ApplicationAnalysisContext app, int header)
    {
        lock (Known)
        {
            if (!Known.TryGetValue((owner, header), out var layout))
                Known[(owner, header)] = layout = Verified(owner, app, header);

            if (layout == null || !layout.TryGetValue(offset, out var field))
                return null;

            //Named against the type's **own** parameters. Importing the definition names an unbound generic
            //- `((BaseTrackingSaveData<>)(object)this)._gameStartCount` - which is not a type C# can write,
            //so the statement was lost again a step further on.
            return new ConcreteGenericFieldAnalysisContext(field, owner.GenericParameters);
        }
    }

    /// <summary>The laid-out offsets of a type, but only where the metadata agrees with them.</summary>
    private static Dictionary<long, FieldAnalysisContext>? Verified(TypeAnalysisContext owner, ApplicationAnalysisContext app, int header)
    {
        //Only where there is nothing recorded. A type the metadata does lay out is not this pass's business,
        //and its own numbers are the ones to believe.
        if (owner.GenericParameters.Count == 0
            || owner.Fields.Any(f => !f.IsStatic && f.BackingData?.FieldOffset > 0)
            || Compute(owner, app, header) is not var (offsets, end, complete))
            return null;

        foreach (var derived in app.AllTypes)
        {
            if (!complete
                || !DerivesFrom(derived, owner)
                || derived.Fields.FirstOrDefault(f => !f.IsStatic && f.BackingData?.FieldOffset > 0) is not { } first
                || MetadataResolver.LaidOutSize(first.FieldType, app.Binary.is32Bit ? 4 : 8) is not { } size)
                continue;

            //The first field a derived type declares begins where the base ended, once aligned.
            return (long)first.BackingData!.FieldOffset == end + (size - end % size) % size ? offsets : null;
        }

        //Nothing derives from it. A **struct** deriving straight from `ValueType` still has an answer: it has
        //no inherited fields, so its layout is its own fields in declaration order, and the one uncertainty
        //the check above exists to remove - where the base ends - is not there to begin with. That is every
        //compiler-generated state machine.
        return owner.IsValueType && Definition(owner.BaseType)?.FullName == "System.ValueType" ? offsets : null;
    }

    /// <summary>
    /// Where each field of the chain lands, where it ends, and whether every field was sizeable.
    /// </summary>
    /// <remarks>
    /// A field whose size is unknown - a generic struct records none - ends the walk rather than abandoning
    /// it: everything **before** it is still at a known distance. A state machine's `<>t__builder` is the
    /// second field of six and only the first has to be sizeable for it. Where the walk stopped early there
    /// is no end for a derived type to be compared against, so only the value-type case can use it.
    /// </remarks>
    private static (Dictionary<long, FieldAnalysisContext> Offsets, long End, bool Complete)? Compute(TypeAnalysisContext owner, ApplicationAnalysisContext app, int header)
    {
        var pointerSize = app.Binary.is32Bit ? 4 : 8;
        var chain = new List<TypeAnalysisContext>();

        for (var walk = owner; walk != null; walk = Definition(walk.BaseType))
        {
            chain.Add(Definition(walk)!);

            if (chain.Count > 16)
                return null;
        }

        chain.Reverse();

        var offsets = new Dictionary<long, FieldAnalysisContext>();
        long at = header;

        foreach (var type in chain)
            foreach (var field in type.Fields)
            {
                if (field.IsStatic)
                    continue;

                if ((MetadataResolver.LaidOutSize(field.FieldType, pointerSize)
                     ?? Measured(field.FieldType, pointerSize, 0)) is not { } size || size <= 0)
                    return offsets.Count == 0 ? null : (offsets, at, false);

                //Aligned to the field's **alignment**, which is its size only while that is a power of two up
                //to the pointer. Aligning to the size itself gives a 32-byte struct 32-byte alignment, and
                //a state machine's `<>t__builder` then lands at 0x20 where the machine reads it at 8. It went
                //unnoticed because every field of a reference type is eight bytes, where the two agree.
                var alignment = size is 1 or 2 or 4 or 8 ? size : pointerSize;

                at += (alignment - at % alignment) % alignment;
                offsets[at] = field;
                at += size;
            }

        return offsets.Count == 0 ? null : (offsets, at, true);
    }

    /// <summary>
    /// How big a struct is, by laying it out - for the ones the metadata does not measure.
    /// </summary>
    /// <remarks>
    /// A generic struct records no size, and one of them in the middle of a chain used to end the walk: a
    /// state machine's second field is <c>AsyncTaskMethodBuilder&lt;T&gt;</c>, so nothing past
    /// <c>&lt;&gt;1__state</c> could be named. Its own fields are measurable in the same way, recursively -
    /// that builder holds one <c>Task&lt;T&gt;</c>, which is a reference and so eight bytes - and a struct's
    /// size is its fields at their alignments, rounded up to the widest of them.
    /// </remarks>
    private static long? Measured(TypeAnalysisContext type, int pointerSize, int depth)
    {
        if (depth > 4 || !type.IsValueType || type.IsEnumType || type.Namespace == nameof(System))
            return null;

        var declared = Definition(type);

        if (declared is null || declared.Fields.All(f => f.IsStatic))
            return null;

        long at = 0, widest = 1;

        foreach (var field in declared.Fields)
        {
            if (field.IsStatic)
                continue;

            if ((MetadataResolver.LaidOutSize(field.FieldType, pointerSize)
                 ?? Measured(field.FieldType, pointerSize, depth + 1)) is not { } size || size <= 0)
                return null;

            //A field's alignment is its size where that is a power of two up to the pointer, and the
            //pointer's otherwise - which is what a nested struct of several fields comes out as.
            var alignment = size is 1 or 2 or 4 or 8 ? size : pointerSize;

            at += (alignment - at % alignment) % alignment;
            at += size;
            widest = System.Math.Max(widest, alignment);
        }

        return at + (widest - at % widest) % widest;
    }

    /// <summary>The type as it was declared, which is what carries the fields.</summary>
    private static TypeAnalysisContext? Definition(TypeAnalysisContext? type)
        => type == null ? null : (type as GenericInstanceTypeAnalysisContext)?.GenericType ?? type;

    private static bool DerivesFrom(TypeAnalysisContext type, TypeAnalysisContext owner)
    {
        for (var walk = Definition(type.BaseType); walk != null; walk = Definition(walk.BaseType))
            if (ReferenceEquals(walk, owner))
                return true;

        return false;
    }
}
