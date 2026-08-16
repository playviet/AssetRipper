using System.Collections.Generic;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// What a box answers with is an <see cref="object"/>, and never the reference type it is merged with.
/// </summary>
/// <remarks>
/// <para>
/// <c>object o = flag ? (object)"text" : (object)7;</c> merges a string with a boxed <c>int</c>, and the two
/// arms are one register. The string arm types it <c>System.String</c> first, that type reaches the box's
/// result through the copies, and <c>SetTypeIfUnknown</c> will not revisit a type that already exists - so
/// the boxed seven is written out as <c>string text2 = (string)(object)7;</c>, which throws
/// <c>InvalidCastException</c> on the cast the source never had. <c>Corpus::AsOrNull</c> is that: the
/// <c>isinst</c> below it is recovered perfectly and never reached.
/// </para>
/// <para>
/// The static type of a box is <c>object</c> - that is the whole of what boxing does - so where the result
/// says something a boxed value type can never be, it is wrong and this is the type it should have had.
/// </para>
/// <para>
/// Narrow on purpose. A result already typed <c>object</c>, or typed as the value type being boxed, or as one
/// of the bases a box legitimately has, is left alone: those are answers something worked out, and
/// <c>il2cpp-a-cast-that-is-read-through-is-an-unbox</c> is the family that depends on them. Only a
/// <b>reference type the boxed value can never be</b> is overruled.
/// </para>
/// </remarks>
public static class BoxedIsAnObject
{
    private static readonly bool Off = System.Environment.GetEnvironmentVariable("BOXEDOBJECT_OFF") == "1";

    public static void Run(MethodAnalysisContext method)
    {
        if (Off || method.ControlFlowGraph is not { } graph)
            return;

        var boxed = new HashSet<LocalVariable>();

        foreach (var instruction in graph.Instructions)
        {
            if (instruction is not { OpCode: OpCode.Call, Operands: [string named, LocalVariable answer, ..] }
                || named is not ("il2cpp_vm_object_box" or "il2cpp_codegen_box")
                || answer.Type is not { IsValueType: false } claimed
                || !CannotBeABox(claimed))
            {
                continue;
            }

            answer.Type = method.AppContext.SystemTypes.SystemObjectType;
            boxed.Add(answer);
        }

        WidenWhatCarriesIt(graph, boxed, method.AppContext.SystemTypes.SystemObjectType);
    }

    /// <summary>
    /// Widens whatever a boxed value is merged into, which is the other half of the same correction.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Retyping the box alone changes nothing the output can show: the merge it feeds still says
    /// <c>System.String</c>, so the copy into it is written as a cast and <c>(string)(object)7</c> survives
    /// exactly as it was. A phi handed a boxed value cannot be a type that value can never have, and
    /// <c>object</c> is the only answer that is true on every edge.
    /// </para>
    /// <para>
    /// Bounded to the copy closure of the boxes this pass just retyped, rather than stated over every phi
    /// that sees an <c>object</c>: widening is safe for the language and expensive for the recovery - a read
    /// through a value typed <c>object</c> has no field to resolve against - so it is only done where the
    /// type that is there is known to be wrong.
    /// </para>
    /// </remarks>
    private static void WidenWhatCarriesIt(Graphs.ISILControlFlowGraph graph, HashSet<LocalVariable> boxed,
        TypeAnalysisContext asObject)
    {
        if (boxed.Count == 0)
            return;

        for (var settling = true; settling;)
        {
            settling = false;

            foreach (var instruction in graph.Instructions)
            {
                if (instruction.OpCode is not (OpCode.Move or OpCode.Phi)
                    || instruction.Destination is not LocalVariable carried
                    || boxed.Contains(carried)
                    || carried.Type is not { IsValueType: false } claimed
                    || !CannotBeABox(claimed))
                {
                    continue;
                }

                for (var operand = 1; operand < instruction.Operands.Count; operand++)
                {
                    if (instruction.Operands[operand] is not LocalVariable source || !boxed.Contains(source))
                        continue;

                    carried.Type = asObject;
                    boxed.Add(carried);
                    settling = true;
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Whether this reference type is one no boxed value can have. <c>object</c>, <c>ValueType</c> and
    /// <c>Enum</c> are exactly the classes a box is an instance of, and an interface it may well implement.
    /// A generic parameter says nothing either way. Everything else - a <c>string</c>, an array, a class of
    /// the program's own - is a type the value being boxed simply is not.
    /// </summary>
    private static bool CannotBeABox(TypeAnalysisContext type)
        => type is not (GenericParameterTypeAnalysisContext or ByRefTypeAnalysisContext or PointerTypeAnalysisContext)
            && !type.IsInterface
            && type.FullName is not ("System.Object" or "System.ValueType" or "System.Enum");
}
