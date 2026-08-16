using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Whether the code registered against a generic method DEFINITION is a shared body, or one specialisation's.
/// </summary>
/// <remarks>
/// <para>
/// il2cpp shares one body per <b>reference</b> instantiation and per enum width. It shares <b>nothing</b> for
/// an ordinary value type - it compiles a separate body for each - so a generic method whose type parameter is
/// only ever a value type has no body of its own, and the address registered against the definition is one of
/// the specialisations'. See <c>il2cpp-a-value-type-generic-has-no-shared-body</c>, which diagnosed that as a
/// typing puzzle it is not: every statement in such a body is correct code about the wrong type.
/// </para>
/// <para>
/// The detector is exact rather than heuristic, and it is not "the definition shares a pointer with an
/// instantiation" - every one of them does, that is how sharing is recorded. It is <b>which</b> instantiation:
/// </para>
/// <list type="table">
/// <item><term>all stand-in arguments</term><description>a genuine shared body; the definition may be written
/// generically and returns a <c>T</c> through the hidden buffer</description></item>
/// <item><term>any concrete argument</term><description>no shared body was compiled; this is that
/// specialisation's code, and it uses that specialisation's calling convention</description></item>
/// </list>
/// <para>
/// The distinction matters because two decisions are made from the <b>declared</b> return type and are wrong
/// for the second case: <c>SeedSharedReturnBuffer</c> renames a register <c>returnBuffer</c> and rewrites
/// every <c>Return</c> to read it, and <c>AddRuntimeMethodOperand</c> steps the <c>MethodInfo</c> along by
/// one to make room for it. A value-type specialisation returns in <c>x0</c> like anything else, so the
/// buffer is a register nothing ever writes and the method returns <c>default(T)</c> - whole, with no marker.
/// <c>Corpus::Pick&lt;T&gt;</c> recovered its null check, both magic divisions and its bounds check and then
/// returned <c>default(T)</c>, taking <c>SharedPick</c> and <c>ValuePick</c> down with it.
/// </para>
/// <para>
/// <b>1604 generic definitions have a body in this game; 605 are a specialisation's, and 85 of those return a
/// bare type parameter</b> - which is the population this changes. In <c>Assembly-CSharp</c> the mis-shared
/// family is three and none of them returns one, so this is expected to be invisible to every scorer.
/// </para>
/// </remarks>
public static class SharedBody
{
    private static readonly object Gate = new();

    private static ApplicationAnalysisContext? computedFor;

    private static HashSet<MethodAnalysisContext>? specialised;

    /// <summary>
    /// Whether what is registered against this generic definition is one specialisation's code rather than a
    /// shared body.
    /// </summary>
    public static bool IsASpecialisation(MethodAnalysisContext method)
    {
        if (method is ConcreteGenericMethodAnalysisContext
            || method.GenericParameters.Count == 0
            || method.UnderlyingPointer == 0
            || method.AppContext is not { } application)
        {
            return false;
        }

        return Specialised(application).Contains(method);
    }

    private static HashSet<MethodAnalysisContext> Specialised(ApplicationAnalysisContext application)
    {
        //One pass over every instantiation in the build, held until the application changes. Methods are
        //analysed in parallel, so the build is behind a lock rather than a lazy field - it happens once.
        lock (Gate)
        {
            if (ReferenceEquals(computedFor, application) && specialised is not null)
                return specialised;

            var found = new HashSet<MethodAnalysisContext>();

            foreach (var concrete in application.ConcreteGenericMethodsByRef.Values)
            {
                if (concrete.UnderlyingPointer == 0
                    || concrete.BaseMethodContext is not { } definition
                    || concrete.UnderlyingPointer != definition.UnderlyingPointer
                    || concrete.MethodGenericParameters.Count == 0
                    || concrete.MethodGenericParameters.All(IsAStandIn))
                {
                    continue;
                }

                found.Add(definition);
            }

            computedFor = application;
            specialised = found;
            return found;
        }
    }

    /// <summary>
    /// Whether a type argument is one of the stand-ins il2cpp instantiates a shared body at.
    /// </summary>
    /// <remarks>
    /// <c>__Il2CppFullySharedGenericStructType</c> is easy to leave out and doing so makes
    /// <c>JitHelpers::UnsafeEnumCastLong</c> a false positive - a correct shared body reported as
    /// mis-shared. Matched by prefix so that a sibling stand-in cannot be missed the same way.
    /// </remarks>
    public static bool IsAStandIn(TypeAnalysisContext argument) => argument.FullName switch
    {
        "System.Object" => true,
        "System.Int32Enum" or "System.Int16Enum" or "System.SByteEnum" or "System.Int64Enum" => true,
        "System.UInt32Enum" or "System.UInt16Enum" or "System.ByteEnum" or "System.UInt64Enum" => true,
        { } name => name.StartsWith("Unity.IL2CPP.Metadata.__Il2CppFullyShared"),
        _ => false,
    };
}
