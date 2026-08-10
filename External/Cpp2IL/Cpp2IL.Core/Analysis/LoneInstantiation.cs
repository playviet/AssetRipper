using System.Collections.Generic;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Gives a local still wearing a shared stand-in the one real instantiation the method has of that generic.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="SharedStandInRetyping"/> carries the real type along copies and out of a call's declaration,
/// which reaches most of them. What it cannot reach is a value that never travels by copy - a struct the
/// compiler kept in the frame, read back through a slot address:
/// </para>
/// <code>
/// 109 Call SerializableDictionaryBase`2&lt;ETutorialIntroType, GameObject&gt;.GetEnumerator, v153 (…Enumerator&lt;ETutorialIntroType, GameObject&gt;), …
/// 117 Call Enumerator&lt;System.Int32Enum, System.Object&gt;.MoveNext, v178, v148 @ stackaddr_-50 (…Enumerator&lt;System.Int32Enum, System.Object&gt;)
/// 228 CallVoid Enumerator&lt;System.Int32Enum, System.Object&gt;.Dispose,  v73 @ stack_-58   (…Enumerator&lt;System.Int32Enum, System.Object&gt;)
/// </code>
/// <para>
/// One enumerator, three frame slots, no copy joining them. <c>TutorialIntroMenu::Show</c> and
/// <c>BoardController::ComputeHighlights</c> both lose their <c>foreach</c> to it.
/// </para>
/// <para>
/// <b>Why the lone instantiation is a safe answer, and not a guess.</b> The type it replaces exists in
/// il2cpp's metadata and in **no assembly Unity ships**, so a declaration naming it never compiles - the
/// value it stands on is lost either way. Where the method has exactly one real instantiation of the same
/// generic definition, that is what the stand-in stands for; where it has two, nothing here can tell which,
/// and the stand-in is left alone rather than guessed at.
/// </para>
/// </remarks>
public static class LoneInstantiation
{
    public static void Run(MethodAnalysisContext method)
    {
        if (method.ControlFlowGraph is null)
            return;

        //Every real instantiation the method mentions, by the generic it instantiates.
        Dictionary<string, GenericInstanceTypeAnalysisContext?> real = [];

        foreach (var local in method.Locals)
        {
            if (local.Type is not GenericInstanceTypeAnalysisContext instance || IsShared(instance))
                continue;

            var of = instance.GenericType.FullName ?? "";

            if (real.TryGetValue(of, out var already))
            {
                //Two of them, and nothing here can say which a stand-in meant.
                if (already is null || already.FullName != instance.FullName)
                    real[of] = null;
            }
            else
            {
                real[of] = instance;
            }
        }

        if (real.Count == 0)
            return;

        foreach (var local in method.Locals)
        {
            if (local.Type is not GenericInstanceTypeAnalysisContext shared || !IsShared(shared))
                continue;

            if (real.GetValueOrDefault(shared.GenericType.FullName ?? "") is { } only)
                local.Type = only;
        }
    }

    /// <summary>Whether every argument is one il2cpp shares a body under.</summary>
    private static bool IsShared(GenericInstanceTypeAnalysisContext instance)
    {
        var standIns = 0;

        foreach (var argument in instance.GenericArguments)
        {
            if (argument.FullName is "System.Object" || argument.FullName?.EndsWith("Enum", System.StringComparison.Ordinal) == true)
                standIns++;
        }

        return standIns > 0 && standIns == instance.GenericArguments.Count;
    }
}
