using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;

namespace Cpp2IL.Core.Utils.AsmResolver;

/// <summary>
/// The fork's half of <see cref="ContextToMethodDescriptor"/>: naming a member of a generic type the only way
/// the format allows.
/// </summary>
/// <remarks>
/// A call from inside a generic type's body to another member of the same generic type resolves to a plain
/// <c>MethodAnalysisContext</c>, and the descriptor for that is the bare <see cref="MethodDefinition"/> -
/// whose parent token is the <b>uninstantiated</b> type. No compiler emits that shape: a reference to a member
/// of a generic type always goes through a TypeSpec, so the call is
/// <c>BaseSaveManager`1&lt;!0&gt;::Save()</c>, never <c>BaseSaveManager`1::Save()</c>.
///
/// Left bare, <c>this</c> is <c>BaseSaveManager&lt;T&gt;</c> while the callee's declaring type is the open
/// definition; the two are unrelated, and ILSpy bridges them through <c>object</c> -
/// <c>((BaseSaveManager&lt;&gt;)(object)this).Save()</c>, which does not compile, so the statement goes and
/// often the whole body with it. Measured: 0 such references in Mono's own mscorlib, 29 here, 17 of them
/// written out as that cast, and seven bodies that keep nothing else.
///
/// The proof is in one method: inside <c>get_saveIO</c> the self-call is emitted instantiated and exports as
/// <c>SaveIO saveIO = this.saveIO;</c>; from <c>Init</c> the same call on the same receiver is emitted bare
/// and exports as <c>((BaseSaveManager&lt;&gt;)(object)this).saveIO.Load();</c>. Only the declaring type differs.
/// </remarks>
public static class SelfInstantiatedMethodReference
{
    /// <summary>
    /// Names <paramref name="method"/> on its declaring type instantiated over that type's own generic
    /// parameters. A non-generic declaring type is returned untouched.
    /// </summary>
    public static IMethodDescriptor OnItsOwnInstantiation(this MethodDefinition method, ModuleDefinition parentModule)
    {
        var declaringType = method.DeclaringType;

        if (declaringType is null || declaringType.GenericParameters.Count == 0)
            return method;

        var genericType = parentModule.DefaultImporter.ImportType(declaringType);

        var arguments = declaringType.GenericParameters
            .Select(p => (TypeSignature)new GenericParameterSignature(parentModule, GenericParameterType.Type, p.Number));

        var selfInstantiation = new GenericInstanceTypeSignature(genericType, declaringType.IsValueType, arguments);

        return new MemberReference(selfInstantiation.ToTypeDefOrRef(), method.Name, method.Signature);
    }
}
