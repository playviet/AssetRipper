using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.ISIL;

/// <summary>
/// A field named as a handle rather than read as a value - what <c>ldtoken</c> pushes.
/// </summary>
/// <remarks>
/// The only thing that wants one is <c>RuntimeHelpers.InitializeArray</c>, and it is how an array initialiser
/// survives: <c>int[] t = { 1, 2, 3, 4 }</c> is not four stores but <c>newarr</c> plus a copy from a hidden
/// field whose bytes are in the binary. Every other operand here stands for a value; this one stands for the
/// field itself, which is why it cannot be a <see cref="FieldReference"/>.
/// </remarks>
public class FieldToken(FieldAnalysisContext field)
{
    public FieldAnalysisContext Field = field;

    public override string ToString() => $"ldtoken {Field.DeclaringType?.Name}::{Field.Name}";
}
