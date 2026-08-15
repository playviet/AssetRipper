using System.Collections.Generic;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AssetRipper.Export.UnityProjects.Scripts;

/// <summary>
/// The half of the repair that keeps a label when the statement carrying it has to go.
/// </summary>
/// <remarks>
/// A recovered body is written as gotos and <c>IL_xxxx:</c> labels, because the control flow it came from is a graph
/// rather than a nesting of blocks. A label is not a statement of its own in C# - it is a prefix on one - so when the
/// statement it prefixes is commented out the label goes with it, and every <c>goto</c> aimed there stops compiling
/// (CS0159) and is commented out in its turn.
/// <para>
/// That trades one statement for a branch, which is the worse half of the bargain twice over: the goto was right, and
/// dropping it does not leave the method doing nothing - it leaves control falling through to whatever follows, which
/// is a different answer rather than a missing one. <c>BaseAbilityBuyPopup.Init</c> is three gotos to one dead
/// <c>IL_00f0</c>, and each of them fell into the next label along.
/// </para>
/// <para>
/// Nothing here rescues the statement: it is commented out exactly as before. Only the label survives it, as a label
/// on an empty statement, which is the one spelling that is valid wherever the labelled statement was - including as
/// the last statement of a block, where a bare label would not parse.
/// </para>
/// </remarks>
internal static partial class InvalidSourceRepair
{
	/// <summary>
	/// The edit that comments a statement out, keeping any label it carried.
	/// </summary>
	private static Edit CommentEdit(SourceFile file, StatementSyntax statement)
	{
		if (statement is not LabeledStatementSyntax)
		{
			return new Edit(statement.Span, null);
		}

		//A statement can carry more than one label - the decompiler writes one per incoming edge - and each of them
		//is a target something may still be aiming at, so all of them are kept.
		List<string> labels = [];
		StatementSyntax current = statement;
		while (current is LabeledStatementSyntax labeled)
		{
			labels.Add(labeled.Identifier.ValueText);
			current = labeled.Statement;
		}

		return new Edit(statement.Span, GetIndentation(file.Text, statement.SpanStart) + string.Join(": ", labels) + ": ;");
	}
}
