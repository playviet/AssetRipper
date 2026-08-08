using System;
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
	/// An auto-property's backing field, said by the property it belongs to.
	/// </summary>
	/// <remarks>
	/// <para>
	/// A property declared <c>{ get; set; }</c> has a field the compiler names <c>&lt;Name&gt;k__BackingField</c>,
	/// and there is no source for it to forward from - so the rule above, which asks that a getter <i>be</i> the
	/// field, never fires for one. The name is the contract instead: that field belongs to <c>Name</c> and to
	/// nothing else, whichever compiler wrote it.
	/// </para>
	/// <para>
	/// This one does answer for an assignment, because an auto-property has a real setter that writes exactly that
	/// field. The rule above declines assignments on the strength of a measurement that no longer holds: eight
	/// bodies here are rooted at <c>adjustConfig._003CSkanUpdatedDelegate_003Ek__BackingField = …</c>, where the
	/// property is right there and accessible.
	/// </para>
	/// <para>
	/// The escaped form is what a field of this export is called - AssetRipper writes <c>&lt;</c> as
	/// <c>_003C</c> so the name is an identifier - and the raw form is what one from a referenced assembly is
	/// called, so both are read.
	/// </para>
	/// </remarks>
	private static string? RewriteBackingField(SyntaxNode node, SemanticModel model)
	{
		if (node is not MemberAccessExpressionSyntax access)
		{
			return null;
		}

		//Whatever the compiler made of it. Where the declaring assembly is one this export decompiled too, the
		//field is not merely inaccessible - it is **not there at all**: a decompiler reconstructs
		//`{ get; set; }` and writes no field, so the reference resolves to nothing.
		if (model.GetSymbolInfo(access).Symbol is not null
			|| PropertyBehind(access.Name.Identifier.ValueText) is not { } name
			|| model.GetTypeInfo(access.Expression).Type is not { } receiver)
		{
			return null;
		}

		bool written = access.Parent is AssignmentExpressionSyntax assignment && assignment.Left == access;

		for (ITypeSymbol? holder = receiver; holder is not null; holder = holder.BaseType)
		{
			foreach (ISymbol member in holder.GetMembers(name))
			{
				if (member is IPropertySymbol { IsIndexer: false } property
					&& (written ? property.SetMethod : property.GetMethod) is { } accessor
					&& model.IsAccessible(access.SpanStart, accessor))
				{
					return $"{access.Expression}.{property.Name}";
				}
			}
		}

		return null;
	}

	/// <summary>The property a compiler-generated backing field belongs to, by the name it was given.</summary>
	private static string? PropertyBehind(string field)
	{
		const string Escaped = "_003C", EscapedEnd = "_003E";
		const string Tail = "k__BackingField";

		if (!field.EndsWith(Tail, StringComparison.Ordinal))
		{
			return null;
		}

		string inner = field[..^Tail.Length];

		if (inner.StartsWith(Escaped, StringComparison.Ordinal) && inner.EndsWith(EscapedEnd, StringComparison.Ordinal))
		{
			return inner[Escaped.Length..^EscapedEnd.Length];
		}

		return inner.StartsWith('<') && inner.EndsWith('>') ? inner[1..^1] : null;
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
