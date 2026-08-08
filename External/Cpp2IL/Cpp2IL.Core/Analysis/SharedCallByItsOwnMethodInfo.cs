using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Resolves a shared generic call from the runtime method the caller hands it, where several are in reach.
/// </summary>
/// <remarks>
/// <para>
/// A generic method compiled once and shared between its instantiations has one address and several methods
/// at it, so nothing about the address says which is being called. What does say is the
/// <c>MethodInfo*</c> il2cpp passes so the shared body can find its own type arguments, and
/// <see cref="MetadataResolver.ResolveCallsViaMethodInfo"/> reads it - but it takes the last one in the
/// operand list and stops. A call carries every argument register, and one of them often still holds the
/// runtime method of an <em>earlier</em> call:
/// </para>
/// <code>
/// Call 0x2BDBF20, v67,
///      methodof(PersistenceMonoBehaviour`1&lt;TrackingManager&gt;::get_I),   // x0 - the one that says
///      v50, v21,
///      methodof(Dictionary`2&lt;ETutorialIntroType, String&gt;::TryGetValue), // x3 - left over from before
///      ...
/// </code>
/// <para>
/// Scanning backwards finds the leftover first, sees that it names nothing at this address, and gives up -
/// so the call stays <c>Method not found @2BDBF20</c> while the answer sits three operands away. The same
/// method resolves perfectly a few lines later in the same body, where no earlier call left a runtime
/// method behind.
/// </para>
/// <para>
/// This asks all of them instead, and keeps the one whose method definition is the definition of a method
/// recorded at that address. That is not a preference between candidates: a runtime method naming some
/// other method says nothing about where this call goes, and one naming an instantiation of the very method
/// compiled at this address can only be the call being made. Where two operands name two different
/// instantiations of it, both are possible and neither is taken.
/// </para>
/// </remarks>
public static class SharedCallByItsOwnMethodInfo
{
    public static bool Run(MethodAnalysisContext method)
    {
        if (method.ControlFlowGraph is not { } graph)
            return false;

        var changed = false;

        foreach (var instruction in graph.Instructions)
        {
            if (!instruction.IsCall || instruction.Operands.Count == 0 || instruction.Operands[0] is not ulong target)
                continue;

            if (!method.AppContext.MethodsByAddress.TryGetValue(target, out var candidates) || candidates.Count == 0)
                continue;

            var compiled = new HashSet<MethodAnalysisContext>(candidates.Select(Definitional));
            MethodAnalysisContext? named = null;

            foreach (var handed in HandedOver(instruction))
            {
                if (!compiled.Contains(Definitional(handed)))
                    continue;

                if (named != null && !ReferenceEquals(named, handed))
                {
                    named = null;
                    break;
                }

                named = handed;
            }

            if (named == null)
                continue;

            instruction.Operands[0] = named;
            changed = true;
        }

        return changed;
    }

    /// <summary>Every runtime method this call is handed, in whichever register it arrived in.</summary>
    private static IEnumerable<MethodAnalysisContext> HandedOver(Instruction call)
    {
        var firstArgument = call.OpCode == OpCode.CallVoid ? 1 : 2;

        for (var i = firstArgument; i < call.Operands.Count; i++)
        {
            //The method's own hidden argument says which instantiation this body is running as, not where a
            //call inside it goes - the same reason upstream skips it.
            if (call.Operands[i] is LocalVariable { IsMethodInfo: true })
                continue;

            var runtime = call.Operands[i] as RuntimeMethodInfoAnalysisContext
                ?? (call.Operands[i] as LocalVariable)?.Type as RuntimeMethodInfoAnalysisContext;

            if (runtime?.RepresentedMethod is { } represented)
                yield return represented;
        }
    }

    /// <summary>The method as it was declared, which is what every instantiation of it shares.</summary>
    private static MethodAnalysisContext Definitional(MethodAnalysisContext method)
        => method is ConcreteGenericMethodAnalysisContext { BaseMethodContext: { } declared } ? declared : method;
}
