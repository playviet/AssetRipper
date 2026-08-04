using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// The library method an arm64 floating point instruction was compiled from.
/// </summary>
/// <remarks>
/// A call to <c>Mathf.Abs</c> or <c>Mathf.Sqrt</c> does not survive as a call: the method is small enough that
/// il2cpp inlines it, and what is left is the one instruction the architecture has for it. There is nothing in
/// that instruction naming a method, so it was left untranslated - and because a statement holding an
/// instruction the lifter cannot translate is written out as a placeholder, one <c>Mathf.Abs</c> took the whole
/// expression it was part of with it.
///
/// The instruction is the whole of what the method did, so naming the method back is exact rather than a guess.
/// Writing it as a call rather than as arithmetic is also what makes the recovered source read like the source
/// it came from - <c>Mathf.Sqrt(x)</c> and not a square root spelled out.
/// </remarks>
public static class MathIntrinsics
{
    private const string UnityMathf = "UnityEngine.Mathf";

    /// <summary>
    /// The method of the given name taking that many arguments of the width the instruction worked at.
    /// </summary>
    /// <remarks>
    /// Which of <c>Mathf</c> and <c>Math</c> is meant is decided by the width, not by preference: the
    /// architecture has a single-precision and a double-precision form of each of these instructions, and
    /// <c>Mathf</c> is the single-precision library while <c>Math</c> is the double-precision one. Naming the
    /// wrong one puts a conversion into the recovered source that the original did not have, and hands the
    /// method an argument of a type it does not take.
    /// </remarks>
    public static MethodAnalysisContext? Resolve(ApplicationAnalysisContext appContext, string name, int parameters, bool isDouble)
        => Resolve(appContext, [name], parameters, isDouble);

    /// <summary>
    /// The same, where the two libraries call the method different things.
    /// </summary>
    /// <remarks>
    /// Rounding up is <c>Mathf.Ceil</c> in Unity's library and <c>Math.Ceiling</c> in the runtime's, so which name
    /// to look for depends on which library the width already chose. Every name is tried against every candidate,
    /// which costs nothing: only one of them can exist on the type that is reached.
    /// </remarks>
    public static MethodAnalysisContext? Resolve(ApplicationAnalysisContext appContext, string[] names, int parameters, bool isDouble)
    {
        var wanted = isDouble ? "System.Double" : "System.Single";

        List<TypeAnalysisContext?> candidates = isDouble
            ?
            [
                appContext.GetAssemblyByName("mscorlib")?.GetTypeByFullName("System.Math"),
            ]
            :
            [
                appContext.GetAssemblyByName("UnityEngine.CoreModule")?.GetTypeByFullName(UnityMathf),
                appContext.GetAssemblyByName("UnityEngine")?.GetTypeByFullName(UnityMathf),
                appContext.GetAssemblyByName("mscorlib")?.GetTypeByFullName("System.Math"),
            ];

        foreach (var type in candidates)
        {
            foreach (var name in names)
            {
                if (type?.Methods.FirstOrDefault(m => m.Name == name && m.IsStatic && m.Parameters.Count == parameters
                    && m.ReturnType.FullName == wanted
                    && m.Parameters.All(p => p.ParameterType.FullName == wanted)) is { } found)
                    return found;
            }
        }

        return null;
    }
}
