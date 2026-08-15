using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AssetRipper.Export.UnityProjects.Scripts;

/// <summary>
/// Two conversions the recovered source states in a way C# will not accept, though both say something exact.
/// </summary>
/// <remarks>
/// Neither is a guess. In each the binary performed a conversion that the language also has a spelling for, and
/// recovery wrote the spelling that would be right if the type were something else. The statement is then commented
/// out and every later statement reading the value goes with it, which is why eleven statements at 1.2.30 cost far
/// more than eleven.
/// </remarks>
internal static partial class InvalidSourceRepair
{
	/// <summary>
	/// A null handed to something that cannot be null, which is a zeroed value.
	/// </summary>
	/// <remarks>
	/// A shared generic body clears the storage of a <c>T</c> it cannot know the size of, and recovery reads the
	/// cleared register as a null reference - <c>return (T)null;</c>, <c>val12 = (W)null;</c>. Where <c>T</c> is a
	/// value type, or a type parameter that may be one, C# refuses: <c>CS0037</c>, cannot convert null to a
	/// non-nullable value type. <c>default(T)</c> is the same zeroed storage and is what the instruction did, so the
	/// cast is replaced rather than the statement being lost. This is the null-shaped sibling of
	/// <c>RewriteZeroedStruct</c>, which does the same for a literal zero cast to a struct.
	/// </remarks>
	private static string? RewriteNullValueType(SyntaxNode node, SemanticModel model)
	{
		if (node is not CastExpressionSyntax cast
			|| !cast.Expression.IsKind(SyntaxKind.NullLiteralExpression)
			|| cast.Type.DescendantNodesAndSelf().OfType<OmittedTypeArgumentSyntax>().Any())
		{
			return null;
		}

		if (model.GetTypeInfo(cast.Type).Type is not { } type)
		{
			return null;
		}

		//Only where the language actually refuses the null. A reference type, and a type parameter known to be one,
		//take it, and there the cast means what it says.
		bool refuses = type switch
		{
			ITypeParameterSymbol parameter => !parameter.IsReferenceType,
			{ IsValueType: true } => type is not INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T },
			_ => false,
		};

		return refuses ? $"default({cast.Type})" : null;
	}

	/// <summary>
	/// A value stored into a narrower or differently-named type, which the binary converted and the source does not.
	/// </summary>
	/// <remarks>
	/// <c>array[0].weightedMode = weightedMode;</c> - an integer into an enum field. The store really was that
	/// conversion; il2cpp has no notion of an enum distinct from its underlying integer, so recovery names the value
	/// by the register's type and the assignment then needs a cast the language calls explicit (<c>CS0266</c>).
	/// <para>
	/// Deliberately narrow. It fires only where an explicit conversion **exists**, so it can never invent one, and it
	/// declines a conversion between two unrelated reference types, where an explicit cast would compile and then
	/// throw at run time rather than convert. That leaves the numeric and enum stores, which are what this shape is.
	/// </para>
	/// </remarks>
	private static string? RewriteNarrowingAssignment(SyntaxNode node, SemanticModel model)
	{
		if (node is not AssignmentExpressionSyntax { RawKind: (int)SyntaxKind.SimpleAssignmentExpression } assignment
			|| model.GetTypeInfo(assignment.Left).Type is not { } target
			|| model.GetTypeInfo(assignment.Right).Type is not { } source)
		{
			return null;
		}

		if (SymbolEqualityComparer.Default.Equals(target, source))
		{
			return null;
		}

		Conversion conversion = model.ClassifyConversion(assignment.Right, target);
		if (conversion.IsImplicit || !conversion.Exists || conversion.IsUserDefined)
		{
			return null;
		}

		//A reference conversion that only the run time can check is not this shape - it would compile and then throw.
		if (!target.IsValueType || !source.IsValueType)
		{
			return null;
		}

		return $"{assignment.Left} = ({target.ToDisplayString()})({assignment.Right})";
	}
}
