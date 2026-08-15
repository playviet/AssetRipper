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
	/// <summary>
	/// The field a primitive stores itself in, which is the primitive.
	/// </summary>
	/// <remarks>
	/// <c>System.Int32</c> is a struct with one field, <c>m_value</c>, holding the number; the same is true of every
	/// other primitive. Recovery reaches a value through a receiver it has typed as the primitive and writes the field
	/// read out, so <c>num.m_value</c> is what the source says - and the field is private, and in the reference
	/// assemblies not present at all, so the statement is commented out and everything downstream of it with it.
	/// <para>
	/// There is nothing to resolve here: the field and the value it belongs to are the same storage, so the receiver
	/// alone says exactly what the field read said. That holds for a write as well - <c>num.m_value = 3</c> assigns
	/// the number - which is why the receiver replaces the whole access rather than being read out of it.
	/// </para>
	/// </remarks>
	private static string? RewritePrimitiveStorage(SyntaxNode node, SemanticModel model)
	{
		if (node is not MemberAccessExpressionSyntax { Name.Identifier.ValueText: "m_value" } access
			|| model.GetTypeInfo(access.Expression).Type is not { } receiver)
		{
			return null;
		}

		//A POINTER to the primitive rather than the primitive - `((float*)(nint)num)->m_value`, which is
		//how a value reached through an address arrives. The field and the pointed-at storage are the same
		//here too, so the access says exactly what a dereference says. The receiver cannot simply be
		//returned as it is above: that would hand back the address instead of the number, so this branch
		//has to spell the `*` and cannot share the line below. 22 of the 35 lost `CS1061` statements at
		//1.2.30 are this one shape - `long` 12, `int` 6, `float` 4.
		//`p[0]` and not `*p`, though they are the same dereference: the scorers split a file into members
		//with ast-grep, whose C# grammar fails on a unary `*` and collapses everything after it into one
		//member. Emitting the star cost 9 members of `IListExtension` and `IEnumerableExtension` as a
		//phantom `missing` - a measurement artefact that would have been read as a regression forever
		//after. Indexing a pointer is the same instruction and parses.
		if (receiver is IPointerTypeSymbol pointer)
		{
			return IsPrimitiveStorage(pointer.PointedAtType) ? $"({access.Expression})[0]" : null;
		}

		return IsPrimitiveStorage(receiver) ? access.Expression.ToString() : null;
	}

	/// <summary>
	/// Whether this type is one whose whole content is the single <c>m_value</c> field.
	/// </summary>
	private static bool IsPrimitiveStorage(ITypeSymbol type)
	{
		return type.SpecialType is SpecialType.System_Boolean or SpecialType.System_Char
			or SpecialType.System_SByte or SpecialType.System_Byte
			or SpecialType.System_Int16 or SpecialType.System_UInt16
			or SpecialType.System_Int32 or SpecialType.System_UInt32
			or SpecialType.System_Int64 or SpecialType.System_UInt64
			or SpecialType.System_Single or SpecialType.System_Double
			or SpecialType.System_IntPtr or SpecialType.System_UIntPtr;
	}

	private static string? RewriteBackingField(SyntaxNode node, SemanticModel model)
	{
		if (node is not MemberAccessExpressionSyntax access)
		{
			return null;
		}

		//Whatever the compiler made of it. Where the declaring assembly is one this export decompiled too, the
		//field is not merely inaccessible - it is **not there at all**: a decompiler reconstructs
		//`{ get; set; }` and writes no field, so the reference resolves to nothing.
		//Either the field is not there at all - a decompiler reconstructing `{ get; set; }` writes no field,
		//so the reference resolves to nothing - or it is there and **out of reach**, which is every serialized
		//field Unity declares `protected`. `gridLayoutGroup.m_Spacing.y` is the second kind, and the property
		//over it is `spacing`.
		ISymbol? resolved = model.GetSymbolInfo(access).Symbol;

		if (resolved is not null && model.IsAccessible(access.SpanStart, resolved))
		{
			return null;
		}

		if (PropertyBehind(access.Name.Identifier.ValueText) is not { } name
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
					&& model.IsAccessible(access.SpanStart, accessor)
					//Where the field is reachable enough to have a type, the property must hold the same
					//thing. Two members whose names differ only by Unity's prefix are not automatically the
					//same value, and saying so where they are not would be a wrong read that compiles.
					&& (resolved is not IFieldSymbol hidden
						|| SymbolEqualityComparer.Default.Equals(hidden.Type, property.Type)))
				{
					//And the qualifier has to be allowed to say it. `IsAccessible` answers for the *member*
					//- a protected one is reachable from a derived type - and not for what it is written
					//on: `((Graphic)this).useLegacyMeshGeneration` is CS1540, because a protected member
					//may only be reached through the derived type or something below it. ILSpy writes that
					//cast because the field it found is declared on the base; dropping it says the same
					//thing and compiles. `SlicedFilledImage..ctor` is one line and was lost entirely to it.
					var qualifier = access.Expression is CastExpressionSyntax { Expression: ThisExpressionSyntax } cast
						&& accessor.DeclaredAccessibility is Accessibility.Protected
							or Accessibility.ProtectedOrInternal or Accessibility.ProtectedAndInternal
						? cast.Expression.ToString()
						: access.Expression.ToString();

					return $"{qualifier}.{property.Name}";
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

		//Unity's own convention for a serialized field, which is `protected` on nearly every component it
		//ships: `m_Spacing` is read through the property `spacing`. The name alone is not the argument - the
		//caller checks with Roslyn that such a property exists, is reachable and holds the same type.
		if (field.Length > 2 && field[0] == 'm' && field[1] == '_' && char.IsUpper(field[2]))
		{
			return char.ToLowerInvariant(field[2]) + field[3..];
		}

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
