using System.Linq;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.ISIL;

/// <summary>
/// A field reached through one or more struct fields in front of it.
/// </summary>
/// <remarks>
/// A struct held in a field is stored where it lies rather than pointed at, so reading one of its members is
/// a read at a fixed distance from the outer field and looks exactly like a read of the outer type - one
/// offset, no indirection. Matching that offset against the outer type's own fields finds nothing, because
/// the offset belongs to the struct sitting inside it.
///
/// <see cref="FieldReference.Field"/> is the innermost field, so everything that asks a reference what value
/// it names - its type, its name, whether it is a boolean - keeps working unchanged. Only reaching it needs
/// the rest of the path, which is walked one step at a time in front of it.
/// </remarks>
public class NestedFieldReference(FieldAnalysisContext[] path, LocalVariable local, int offset)
    : FieldReference(path[^1], local, offset)
{
    /// <summary>Outermost first, ending at <see cref="FieldReference.Field"/>.</summary>
    public FieldAnalysisContext[] Path = path;

    public override string ToString()
        => $"{Local.Name}.{string.Join('.', Path.Select(f => f.Name))} ({Field.FieldType.FullName})";
}
