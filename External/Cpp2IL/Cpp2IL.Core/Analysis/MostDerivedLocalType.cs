using System.Collections.Generic;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Gives a local the type of the one thing that assigns it, where that says more than its own does.
/// </summary>
/// <remarks>
/// <para>
/// A local is typed from whatever the analysis learns about it, and one of the things it learns is the
/// parameter type of a call it is handed to. That is true but weak: <c>MonoBehaviorExtension.InvokeDelay</c>
/// takes a <c>MonoBehaviour</c>, so a value passed to it is called one - even where the field it was read
/// from says exactly what it is.
/// </para>
/// <code>
/// Move       v76 (UnityEngine.MonoBehaviour), this.&lt;context&gt;k__BackingField (CF.LevelManager)
/// CheckGreater _, [v76 (UnityEngine.MonoBehaviour) + 0x28], 2
/// </code>
/// <para>
/// and the read at <c>0x28</c> is <c>LevelManager.levelEndReason</c>, which <c>MonoBehaviour</c> does not
/// have - so the whole <c>switch</c> on it came out as unmanaged memory and five statements went with it.
/// </para>
/// <para>
/// Where a local is assigned in exactly one place and that place says a type <b>derived from</b> the one it
/// carries, the derived one is what it holds: a base class cannot be more accurate than the thing assigned
/// to it. Only one definition, because a register reused for two purposes is the case this must not touch.
/// </para>
/// </remarks>
public static class MostDerivedLocalType
{
    public static bool Run(MethodAnalysisContext method)
    {
        if (method.ControlFlowGraph is not { } graph)
            return false;

        //Every place a local is given a value. After single assignment form is taken apart the phi copies
        //are ordinary moves, so a local that is written once in the source is written several times here -
        //asking for a lone definition finds almost nothing.
        var definitions = new Dictionary<LocalVariable, List<Instruction>>();

        foreach (var instruction in graph.Instructions)
            if (instruction.Destination is LocalVariable written)
            {
                if (!definitions.TryGetValue(written, out var places))
                    definitions[written] = places = [];

                places.Add(instruction);
            }

        var changed = false;
        var header = method.AppContext.Binary.is32Bit ? 8 : 0x10;

        foreach (var (local, places) in definitions)
        {
            if (local.Type is not { } carried)
                continue;

            TypeAnalysisContext? agreed = null;

            foreach (var place in places)
            {
                //One place that is not a plain copy of something typed, and there is no agreement to have.
                if (place is not { OpCode: OpCode.Move, Operands.Count: 2 }
                    || Assigned(place.Operands[1], header) is not { } given
                    || (agreed != null && !ReferenceEquals(Definition(agreed), Definition(given))))
                {
                    agreed = null;
                    break;
                }

                agreed = given;
            }

            if (agreed == null || !DerivesFrom(agreed, carried))
            {
                if (System.Environment.GetEnvironmentVariable("DERIVED_TRACE") == "1")
                    System.Console.Error.WriteLine($"DERIVED refused {local} <- {agreed?.FullName ?? "nothing"} ({places.Count} places)");

                continue;
            }

            local.Type = agreed;
            changed = true;
        }

        return changed;
    }

    /// <summary>What the one thing assigning a local says it is.</summary>
    /// <remarks>
    /// The memory case is the one that pays, and it is the reason this pass was inert where it was written to
    /// fire: it runs <b>before</b> the resolution that turns a read into a field reference, so at this point
    /// <c>this.&lt;context&gt;k__BackingField</c> is still <c>[this + 0x18]</c> and there was nothing to read a
    /// type off. It has to run before that resolution - the whole point is to let the reads *through* this
    /// local resolve - so the field is looked up here instead of waited for.
    /// </remarks>
    private static TypeAnalysisContext? Assigned(object operand, int header) => operand switch
    {
        FieldReference field => field.Field.FieldType,
        LocalVariable { Type: { } held } => held,
        MemoryOperand { Index: null, Scale: 0, Base: LocalVariable { Type: { } owner } } memory
            => FieldAt(owner, memory.Addend, header),
        _ => null,
    };

    /// <summary>The type of the instance field lying at that distance into a class.</summary>
    /// <remarks>
    /// The base chain is the case that pays, and the one a type's own declarations miss entirely:
    /// <c>LevelStateEnd</c> declares no fields at all, and the <c>context</c> it reads is on
    /// <c>State&lt;LevelManager&gt;</c>. Naming it has to go through the instantiation or its type is the
    /// parameter <c>T</c>, which says nothing. Same rules as the resolution below - see the base walk in
    /// <see cref="MetadataResolver"/>, which this deliberately mirrors rather than shares, so that a merge
    /// never has to reconcile two callers of one upstream declaration.
    /// </remarks>
    private static TypeAnalysisContext? FieldAt(TypeAnalysisContext owner, long offset, int header)
    {
        //Only a class: a struct's fields are laid out from its own start with no header, and a read at a
        //distance into one is a member of it rather than something assigned to the local whole.
        if (owner.IsValueType || owner.IsEnumType)
            return null;

        foreach (var field in owner.Fields)
            if (!field.IsStatic && field.BackingData?.FieldOffset == offset)
                return field.FieldType;

        for (var baseType = owner.BaseType; baseType is not null; baseType = baseType.BaseType)
        {
            var declaring = (baseType as GenericInstanceTypeAnalysisContext)?.GenericType ?? baseType;

            var found = Declared(declaring, offset, 0);

            //A generic base records every offset as nought, so where the chain above it is closed its fields
            //begin at the object header and are laid out from there - the layout rule, not a guess.
            if (found is null && declaring.BaseType is null or { FullName: "System.Object" })
                found = Declared(declaring, offset, header);

            if (found is null)
                continue;

            return baseType is GenericInstanceTypeAnalysisContext genericBase
                ? new ConcreteGenericFieldAnalysisContext(found, genericBase).FieldType
                : found.FieldType;
        }

        return null;
    }

    private static FieldAnalysisContext? Declared(TypeAnalysisContext owner, long offset, int header)
    {
        foreach (var field in owner.Fields)
            if (!field.IsStatic && field.BackingData?.FieldOffset + header == offset)
                return field;

        return null;
    }

    /// <summary>Whether the first type is the second one, further down the chain.</summary>
    private static bool DerivesFrom(TypeAnalysisContext derived, TypeAnalysisContext held)
    {
        //A value type has no chain worth walking, and a stand-in for a generic parameter is not a class at
        //all - both are somebody else's question.
        if (derived.IsValueType || held.IsValueType || ReferenceEquals(derived, held))
            return false;

        //Through the definition at every step, and bounded. A generic instance is a wrapper around the type
        //it instantiates and reports no base of its own, so a chain that passes through one - which is every
        //singleton here - ended one type early and `LevelManager` was never seen to be a `MonoBehaviour`.
        var steps = 0;

        for (var walk = Up(derived); walk != null && steps < 16; walk = Up(walk), steps++)
            if (ReferenceEquals(Definition(walk), Definition(held)))
                return true;

        return false;
    }

    private static TypeAnalysisContext? Up(TypeAnalysisContext type)
        => type is GenericInstanceTypeAnalysisContext generic ? generic.GenericType.BaseType : type.BaseType;

    private static TypeAnalysisContext Definition(TypeAnalysisContext type)
        => (type as GenericInstanceTypeAnalysisContext)?.GenericType ?? type;
}
