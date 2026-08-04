using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AssetRipper.Export.UnityProjects.Scripts;

/// <summary>
/// The half of the repair that says a private member by the name the project is allowed to use.
/// </summary>
/// <remarks>
/// il2cpp inlines a property whose getter does nothing but return a field, so what the native code contains is a read
/// of the field itself. Recovery writes down what it sees, and the compiler rejects it - CS0122, inaccessible - because
/// the field is private to another type. The statement is then commented out, and every later statement that used the
/// value goes with it: it is the largest single cause of a method losing its branching, eleven of the twenty-seven
/// methods measured against the original that keep fewer decisions than they should.
///
/// Nothing is lost, though. The property the compiler wants is still there in the same file, and it forwards to exactly
/// that field, so writing the property's name says the same thing in a way the language allows. The equality is
/// structural rather than assumed: the getter has to be that field and nothing else, so there is no way for this to
/// produce a statement that compiles and means something different.
/// </remarks>
internal static partial class InvalidSourceRepair
{
	/// <summary>
	/// A read of an inaccessible field, written as the property that forwards to it.
	/// </summary>
	private static string? RewriteInaccessibleMember(SyntaxNode node, SemanticModel model)
	{
		if (node is not MemberAccessExpressionSyntax access)
		{
			return null;
		}

		SymbolInfo info = model.GetSymbolInfo(access);

		//Only where the compiler found the member and refused it. Anything else is a different problem.
		if (info.Symbol is not null
			|| info.CandidateReason != CandidateReason.Inaccessible
			|| info.CandidateSymbols.Length != 1
			|| info.CandidateSymbols[0] is not IFieldSymbol field)
		{
			return null;
		}

		//Assigning through the property would need a setter that forwards just as exactly. Writing one was tried and
		//never fired: a statement that assigns an inaccessible field always had something else wrong with it too, and
		//a rewrite is only made when it answers everything the compiler objected to. The same was true of a table of
		//the runtime library's own fields - `String._stringLength` is exactly `Length` - so neither is carried.
		if (access.Parent is AssignmentExpressionSyntax assignment && assignment.Left == access)
		{
			return null;
		}

		if (ForwardingProperty(field, model, access.SpanStart) is not { } property)
		{
			return null;
		}

		return $"{access.Expression}.{property.Name}";
	}

	/// <summary>
	/// The accessible property of the field's own type whose getter is that field and nothing else.
	/// </summary>
	private static IPropertySymbol? ForwardingProperty(IFieldSymbol field, SemanticModel model, int position)
	{
		foreach (ISymbol member in field.ContainingType.GetMembers())
		{
			if (member is not IPropertySymbol property
				|| property.GetMethod is null
				|| property.IsIndexer
				|| !model.IsAccessible(position, property))
			{
				continue;
			}

			if (Forwards(property.GetMethod, field))
			{
				return property;
			}
		}

		return null;
	}

	/// <summary>
	/// Whether a getter's whole body is a read of this field.
	/// </summary>
	/// <remarks>
	/// Read off the syntax rather than resolved again, because the field has already been identified and a type cannot
	/// declare two fields of one name - so the name in the getter can only be this field. An automatic property has no
	/// body here and is refused, which is right: its backing field is not the one that was read.
	/// </remarks>
	private static bool Forwards(IMethodSymbol getter, IFieldSymbol field)
	{
		foreach (SyntaxReference reference in getter.DeclaringSyntaxReferences)
		{
			ExpressionSyntax? returned = reference.GetSyntax() switch
			{
				ArrowExpressionClauseSyntax arrow => arrow.Expression,
				AccessorDeclarationSyntax { ExpressionBody: { } arrow } => arrow.Expression,
				AccessorDeclarationSyntax { Body.Statements: [ReturnStatementSyntax { Expression: { } value }] } => value,
				_ => null,
			};

			//A property written as an expression rather than an accessor block declares the getter on itself.
			returned ??= reference.GetSyntax() is PropertyDeclarationSyntax { ExpressionBody: { } body } ? body.Expression : null;

			if (Names(returned) == field.Name)
			{
				return true;
			}
		}

		return false;
	}

	/// <summary>The field an expression names, where it names one directly and does nothing else.</summary>
	private static string? Names(ExpressionSyntax? expression) => expression switch
	{
		IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
		MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax, Name: IdentifierNameSyntax named } => named.Identifier.ValueText,
		_ => null,
	};
}
