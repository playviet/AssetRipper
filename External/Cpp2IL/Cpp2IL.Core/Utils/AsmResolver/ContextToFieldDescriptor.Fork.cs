using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Utils.AsmResolver;

/// <summary>
/// What this fork adds to field-reference conversion: a field of a generic type is never written as a field
/// of the bare definition.
///
/// Kept apart from the file it belongs to so that the file stays as close to upstream as it can, and a later
/// version of Cpp2IL can be merged without the two sets of changes meeting.
/// </summary>
public static partial class ContextToFieldDescriptor
{
    /// <summary>
    /// A field whose owner is generic, named through the owner's own parameters.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Importing a <see cref="FieldDefinition"/> whose declaring type is a generic <b>definition</b> gives a
    /// reference that belongs to no instantiation, and the decompiler can only write it through an
    /// <b>unbound</b> generic name - <c>Unsafe.As&lt;&lt;LoadAsync&gt;d__2&lt;T&gt;,
    /// &lt;LoadAsync&gt;d__2&lt;&gt;&gt;(ref this).&lt;&gt;1__state</c>. C# allows that spelling only inside
    /// <c>typeof</c>, so every statement carrying one is commented out. It is <c>CS7003</c>, the one error id
    /// in this game with a single shape, and the last of it arrives here rather than at any of the passes
    /// that name a field: several of them answer with the field of the definition and only a few wrap it.
    /// </para>
    /// <para>
    /// The arguments to bind are the declaring type's <b>own</b> parameters. A reference to a field of a
    /// generic type is only ever produced for the type the method being generated belongs to - the analysis
    /// reaches every other one through a <see cref="GenericInstanceTypeAnalysisContext"/>, which is wrapped
    /// long before this - and inside a type's own body <c>!0</c> is that type's parameter. The alternative is
    /// not a different reading of the same reference: it is a reference no C# compiler will take.
    /// </para>
    /// <para>
    /// The field's signature stays as declared. A member reference into a generic instance is written in
    /// terms of the declaring type's parameters and takes its arguments from the parent, which is the same
    /// shape <c>ConcreteGenericFieldAnalysisContext</c> already emits.
    /// </para>
    /// </remarks>
    internal static IFieldDescriptor ImportBoundToItsOwner(FieldAnalysisContext context, ModuleDefinition parentModule)
    {
        var field = context.GetFieldDefinition();

        if (field.DeclaringType is not { GenericParameters.Count: > 0 } declaring || field.Signature is null)
            return parentModule.DefaultImporter.ImportField(field);

        var arguments = declaring.GenericParameters
            .Select(parameter => (TypeSignature)new GenericParameterSignature(parentModule, GenericParameterType.Type, parameter.Number));

        var owner = new GenericInstanceTypeSignature(
            parentModule.DefaultImporter.ImportType(declaring),
            declaring.IsValueType,
            [.. arguments]);

        return new MemberReference(owner.ToTypeDefOrRef(), field.Name, field.Signature);
    }
}
