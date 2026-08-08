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
    private static readonly Dictionary<TypeAnalysisContext, Dictionary<long, FieldAnalysisContext>?> Known = [];

    /// <summary>The field of a generic type lying at that distance into an instance, if one does.</summary>
    public static FieldAnalysisContext? FieldAt(TypeAnalysisContext owner, long offset, ApplicationAnalysisContext app, int header)
    {
        lock (Known)
        {
            if (!Known.TryGetValue(owner, out var layout))
                Known[owner] = layout = Verified(owner, app, header);

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
            || Compute(owner, app, header) is not var (offsets, end))
            return null;

        foreach (var derived in app.AllTypes)
        {
            if (!DerivesFrom(derived, owner)
                || derived.Fields.FirstOrDefault(f => !f.IsStatic && f.BackingData?.FieldOffset > 0) is not { } first
                || MetadataResolver.LaidOutSize(first.FieldType, app.Binary.is32Bit ? 4 : 8) is not { } size)
                continue;

            //The first field a derived type declares begins where the base ended, once aligned.
            return (long)first.BackingData!.FieldOffset == end + (size - end % size) % size ? offsets : null;
        }

        return null;
    }

    /// <summary>Where each field of the chain lands, and where the chain ends.</summary>
    private static (Dictionary<long, FieldAnalysisContext> Offsets, long End)? Compute(TypeAnalysisContext owner, ApplicationAnalysisContext app, int header)
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

                if (MetadataResolver.LaidOutSize(field.FieldType, pointerSize) is not { } size || size <= 0)
                    return null;

                at += (size - at % size) % size;
                offsets[at] = field;
                at += size;
            }

        return offsets.Count == 0 ? null : (offsets, at);
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
