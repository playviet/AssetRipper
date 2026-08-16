using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// A type's static storage handed to a by-reference parameter is the address of the field at its front.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="FieldAddressRecovery"/> already reads <c>Add v121, [klass + 0xB8], 8</c> as
/// <c>&amp;Type.someStatic</c> and writes <c>ldsflda</c> for it - and its own remark names the caller this
/// exists for: "which is where <c>Interlocked.CompareExchange(ref SomeEvent, ...)</c> gets the place it
/// swaps. Every event accessor in the game is that call."
/// </para>
/// <para>
/// **The first static field is at distance nought, so there is no addition at all** and that pass never sees
/// it. The storage local goes straight into the call, the callee's signature retypes it
/// <c>System.Object&amp;</c>, and the generator writes a local:
/// </para>
/// <code>
/// object location = default(object);                                   // this is `ref Corpus.m_Adjust`
/// object obj4 = Interlocked.CompareExchange(ref location, value2, obj);
/// </code>
/// <para>
/// So the exchange swaps a throwaway and the event's backing field is never written. The whole
/// compare-exchange loop is recovered perfectly around it - the combine, the type test, the retry - and the
/// event stays null: <c>Corpus::EventRoundTrip</c> throws <c>NullReferenceException</c> on the very next
/// line, whole, with no marker.
/// </para>
/// <para>
/// Only a <b>by-reference</b> parameter, and only where the owner declares a static field at nought. A static
/// storage block reaching an ordinary parameter is the runtime pointer it is, and taking its address there
/// would say something the code did not.
/// </para>
/// <para>
/// Runs in <c>BeforeUnusedLocalsAreDropped</c>: by the last hook the local has been retyped from the
/// callee's signature and no longer says whose storage it is. Set <c>STATICREF_OFF=1</c> to measure the same
/// build without it.
/// </para>
/// </remarks>
public static class StaticStorageIsTheFirstField
{
    private static readonly bool Off = System.Environment.GetEnvironmentVariable("STATICREF_OFF") == "1";

    public static void Run(MethodAnalysisContext method)
    {
        if (Off || method.ControlFlowGraph is not { } graph)
            return;

        foreach (var instruction in graph.Instructions)
        {
            if (!instruction.IsCall || instruction.Operands.Count == 0
                || instruction.Operands[0] is not MethodAnalysisContext callee)
            {
                continue;
            }

            //A call's operands are the callee, then the result for `Call`, then the receiver where there is
            //one, then the arguments - so the first declared parameter sits this far along.
            var first = (instruction.OpCode == OpCode.Call ? 2 : 1) + (callee.IsStatic ? 0 : 1);

            for (var parameter = 0; parameter < callee.Parameters.Count; parameter++)
            {
                var at = first + parameter;

                if (at >= instruction.Operands.Count
                    || callee.Parameters[parameter].ParameterType is not ByRefTypeAnalysisContext
                    || instruction.Operands[at] is not LocalVariable held
                    || held.Type is not StaticFieldStorageTypeAnalysisContext { OwnerType: { } owner })
                {
                    continue;
                }

                var definition = (owner as GenericInstanceTypeAnalysisContext)?.GenericType ?? owner;

                foreach (var field in definition.Fields)
                {
                    if (!field.IsStatic || field.Offset != 0)
                        continue;

                    instruction.Operands[at] = new FieldReference(field, held, 0);
                    break;
                }
            }
        }
    }
}
