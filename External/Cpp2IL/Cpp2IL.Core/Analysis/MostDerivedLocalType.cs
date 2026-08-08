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

        foreach (var (local, places) in definitions)
        {
            if (local.Type is not { } carried)
                continue;

            TypeAnalysisContext? agreed = null;

            foreach (var place in places)
            {
                //One place that is not a plain copy of something typed, and there is no agreement to have.
                if (place is not { OpCode: OpCode.Move, Operands.Count: 2 }
                    || Assigned(place.Operands[1]) is not { } given
                    || (agreed != null && !ReferenceEquals(Definition(agreed), Definition(given))))
                {
                    agreed = null;
                    break;
                }

                agreed = given;
            }

            if (agreed == null || !DerivesFrom(agreed, carried))
                continue;

            local.Type = agreed;
            changed = true;
        }

        return changed;
    }

    /// <summary>What the one thing assigning a local says it is.</summary>
    private static TypeAnalysisContext? Assigned(object operand) => operand switch
    {
        FieldReference field => field.Field.FieldType,
        LocalVariable { Type: { } held } => held,
        _ => null,
    };

    /// <summary>Whether the first type is the second one, further down the chain.</summary>
    private static bool DerivesFrom(TypeAnalysisContext derived, TypeAnalysisContext held)
    {
        //A value type has no chain worth walking, and a stand-in for a generic parameter is not a class at
        //all - both are somebody else's question.
        if (derived.IsValueType || held.IsValueType || ReferenceEquals(derived, held))
            return false;

        for (var walk = derived.BaseType; walk != null; walk = walk.BaseType)
            if (ReferenceEquals(Definition(walk), Definition(held)))
                return true;

        return false;
    }

    private static TypeAnalysisContext Definition(TypeAnalysisContext type)
        => (type as GenericInstanceTypeAnalysisContext)?.GenericType ?? type;
}
