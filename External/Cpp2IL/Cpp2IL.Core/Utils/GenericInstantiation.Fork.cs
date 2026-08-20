using System.Collections.Generic;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Utils;

internal static class GenericInstantiationFork
{
    /// <summary>
    /// The substitution a generic parameter asks for, or the parameter itself where the caller has nothing
    /// to substitute.
    /// </summary>
    /// <remarks>
    /// Upstream indexes the two lists directly, and an index past the end throws
    /// <see cref="System.ArgumentOutOfRangeException"/> out of <c>Instantiate</c>. That exception is not
    /// caught until <c>AsmResolverDllOutputFormatIlRecovery.FillMethodBody</c>, so it does not cost the one
    /// type it could not name - it costs the **entire method body**, which is then written out as a
    /// rethrowing stub and scores as dead.
    ///
    /// Two callers pass a list that cannot cover every parameter:
    /// <c>ConcreteGenericFieldAnalysisContext</c> passes the declaring instance's arguments as the type
    /// parameters and an **empty** list for the method parameters, so any <c>!!n</c> reached from a field
    /// type is out of range by construction; and a generic instance built from a shared body records fewer
    /// arguments than the definition declares.
    ///
    /// Leaving the parameter un-substituted is what the analysis already does for a type it cannot close -
    /// the result is an open type where a closed one was wanted, which later passes are written to expect -
    /// and it keeps the other ninety-nine statements of the method.
    /// </remarks>
    public static TypeAnalysisContext Bind(GenericParameterTypeAnalysisContext parameter, IReadOnlyList<TypeAnalysisContext> arguments)
        => (uint)parameter.Index < (uint)arguments.Count ? arguments[parameter.Index] : parameter;
}
