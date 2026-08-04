using LibCpp2IL.BinaryStructures;

namespace Cpp2IL.Core.Model.Contexts;

/// <summary>
/// Synthetic type for a value holding <c>Il2CppMethodInfo::rgctx_data</c>.
///
/// A generic method shares one compiled body between its instantiations, so anything in that body which
/// depends on the type arguments cannot be an address the compiler wrote down. It is read out of a table
/// hanging off the runtime method instead - the method's own runtime generic context, the counterpart of
/// the one <see cref="RgctxTableTypeAnalysisContext"/> covers for types.
/// </summary>
public class MethodRgctxTableTypeAnalysisContext(MethodAnalysisContext ownerMethod, AssemblyAnalysisContext referencedFrom)
    : ReferencedTypeAnalysisContext(referencedFrom)
{
    /// <summary>The method whose runtime generic context this is.</summary>
    public MethodAnalysisContext OwnerMethod { get; } = ownerMethod;

    public override Il2CppTypeEnum Type => Il2CppTypeEnum.IL2CPP_TYPE_I;

    public override string DefaultName => $"Il2CppMethodRgctx<{OwnerMethod.Name}>";

    public override string DefaultNamespace => "";

    public override bool IsValueType => false;
}
