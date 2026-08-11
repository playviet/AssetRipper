using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Utils.AsmResolver;

/// <summary>
/// What this fork adds to type-signature conversion: writing the stand-in types il2cpp records a shared
/// generic body against as the primitives they are stored as.
///
/// Kept apart from the file it belongs to so that the file stays as close to upstream as it can, and a later
/// version of Cpp2IL can be merged without the two sets of changes meeting.
/// </summary>
public static partial class ContextToTypeSignature
{
    /// <summary>
    /// What one of il2cpp's shared-generic enum stand-ins is stored as, where the type is one of them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A generic body is compiled once for every argument that shares a representation, and il2cpp records
    /// that body against a placeholder rather than against any of the types it serves: an enum argument
    /// becomes <c>System.Int32Enum</c>. They are declared like any other type in il2cpp's own metadata, and
    /// public there, but **no assembly Unity ships has one** - so a local written
    /// <c>Dictionary&lt;System.Int32Enum, object&gt;.Enumerator</c> names a type the project cannot see,
    /// which does not compile and takes with it every later statement that used the local.
    /// <c>TutorialIntroMenu::Show</c> is four statements of exactly that.
    /// </para>
    /// <para>
    /// The primitive is what the shared body genuinely holds: an enum is its underlying type there, which is
    /// the whole reason one body serves them all. So the substitution loses the *name* of an enum the body
    /// never knew and keeps everything the body actually does with it.
    /// </para>
    /// <para>
    /// <see cref="Cpp2IL.Core.IlGenerator"/> already refuses to *fuse* a construction whose type is one of
    /// these (<c>CanBeNamed</c>); this is the same fact applied one level lower, where the type is written
    /// down rather than reasoned about.
    /// </para>
    /// </remarks>
    internal static TypeSignature? SharedEnumUnderlying(TypeAnalysisContext context, ModuleDefinition module)
    {
        if (context.Namespace != "System")
        {
            return null;
        }

        return context.Name switch
        {
            "SByteEnum" => module.CorLibTypeFactory.SByte,
            "ByteEnum" => module.CorLibTypeFactory.Byte,
            "Int16Enum" => module.CorLibTypeFactory.Int16,
            "UInt16Enum" => module.CorLibTypeFactory.UInt16,
            "Int32Enum" => module.CorLibTypeFactory.Int32,
            "UInt32Enum" => module.CorLibTypeFactory.UInt32,
            "Int64Enum" => module.CorLibTypeFactory.Int64,
            "UInt64Enum" => module.CorLibTypeFactory.UInt64,
            _ => null,
        };
    }
}
