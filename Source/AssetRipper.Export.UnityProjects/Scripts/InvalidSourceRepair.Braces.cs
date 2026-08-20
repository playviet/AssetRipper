using AssetRipper.Import.Logging;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace AssetRipper.Export.UnityProjects.Scripts;

/// <summary>
/// The half of the repair that keeps the braces where they were.
/// </summary>
/// <remarks>
/// A statement is commented out with <c>//</c>, and a <c>//</c> comment runs to the end of the <b>physical line</b>,
/// not to the end of the span. Everywhere the decompiler writes one statement per line those two are the same thing;
/// where a repair has already put several statements on one line - <c>RewriteStructPropertyMember</c> writes a whole
/// read-modify-write block on one - they are not, and commenting the middle statement takes the rest of the line with
/// it. In <c>CameraTools.TryGetLevelBoundsWorld</c> the rest of the line was the block's <c>}</c>: the file lost a
/// brace, Roslyn re-parsed the method as running past its own end, and the next attempt commented out the method's
/// closing brace and the class's, because by then those really were inside the statement it was handed.
/// <para>
/// Nothing downstream can notice that, because the loss is textual: no span ever covered the swallowed text. So it is
/// checked here instead, twice over. A whole statement always has as many <c>{</c> as <c>}</c>, so an edit whose span
/// does not is an edit that has picked up a brace belonging to something outside it and is dropped; and a round that
/// would change a file's brace balance at all is dropped whole, with the file named in the log.
/// </para>
/// <para>
/// The counter is deliberately crude - it skips comments and literals and counts nothing else - so it must only ever
/// be used to compare one version of a text with another, never to judge a text on its own. A <c>#if</c> around a
/// brace is balanced across the whole file and not within one arm of the directive, and preprocessor directives are
/// left untouched by every edit here, so a before-and-after comparison is unaffected by them.
/// </para>
/// </remarks>
internal static partial class InvalidSourceRepair
{
	/// <summary>
	/// Whether anything other than whitespace follows a position before the end of its line.
	/// </summary>
	/// <remarks>
	/// This is the question that decides whether the source resumed after a commented span has to start on a line of
	/// its own. It does not matter what the text is - if it is there at all, and the last thing written was a
	/// <c>//</c> comment, it would be inside that comment.
	/// </remarks>
	private static bool HasCodeAfterOnLine(string text, int position)
	{
		for (int i = position; i < text.Length; i++)
		{
			char c = text[i];
			if (c == '\n')
			{
				return false;
			}

			if (!char.IsWhiteSpace(c))
			{
				return true;
			}
		}

		return false;
	}

	/// <summary>
	/// How many braces a stretch of text opens without closing, counting only the ones that are code.
	/// </summary>
	/// <remarks>
	/// Comments, string literals - verbatim and interpolated alike - and character literals are skipped, because a
	/// brace inside one is not a brace. Raw string literals are not, and a text containing one is reported as
	/// unknowable rather than guessed at: the decompiler does not write them, but a substituted original source could.
	/// </remarks>
	/// <returns>The balance, or <see langword="null"/> where the text cannot be read with confidence.</returns>
	private static int? CodeBraceBalance(string text, int start, int length)
	{
		int balance = 0;
		int end = start + length;

		for (int i = start; i < end; i++)
		{
			char c = text[i];

			if (c == '/' && i + 1 < end)
			{
				if (text[i + 1] == '/')
				{
					int newLine = text.IndexOf('\n', i);
					i = newLine < 0 || newLine >= end ? end : newLine;
					continue;
				}

				if (text[i + 1] == '*')
				{
					int close = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
					if (close < 0 || close >= end)
					{
						//A comment that is not closed inside this text: what follows cannot be read.
						return null;
					}
					i = close + 1;
					continue;
				}
			}

			if (c == '"')
			{
				//Raw string literals have their own quoting rules, and getting them wrong would mean counting the
				//braces of an interpolation hole. Refuse instead.
				if (i + 2 < end && text[i + 1] == '"' && text[i + 2] == '"')
				{
					return null;
				}

				bool verbatim = i > start && text[i - 1] == '@';
				i = SkipQuoted(text, i, end, '"', verbatim);
				if (i < 0)
				{
					return null;
				}
				continue;
			}

			if (c == '\'')
			{
				i = SkipQuoted(text, i, end, '\'', verbatim: false);
				if (i < 0)
				{
					return null;
				}
				continue;
			}

			if (c == '{')
			{
				balance++;
			}
			else if (c == '}')
			{
				balance--;
			}
		}

		return balance;
	}

	/// <inheritdoc cref="CodeBraceBalance(string, int, int)"/>
	private static int? CodeBraceBalance(string text) => CodeBraceBalance(text, 0, text.Length);

	/// <summary>
	/// The index of the closing quote of a literal that opens at <paramref name="start"/>, or -1 when it does not
	/// close inside the text.
	/// </summary>
	private static int SkipQuoted(string text, int start, int end, char quote, bool verbatim)
	{
		for (int i = start + 1; i < end; i++)
		{
			char c = text[i];

			if (verbatim)
			{
				if (c == quote)
				{
					//A doubled quote inside a verbatim string is one quote, not the end of it.
					if (i + 1 < end && text[i + 1] == quote)
					{
						i++;
						continue;
					}
					return i;
				}
				continue;
			}

			if (c == '\\')
			{
				i++;
				continue;
			}

			if (c == quote)
			{
				return i;
			}

			if (c == '\n')
			{
				//A non-verbatim literal cannot span lines, so this text does not say what it looked like.
				return -1;
			}
		}

		return -1;
	}

	/// <summary>
	/// Whether a span may be commented out: a whole statement balances its braces, and one that does not is one that
	/// has picked up a brace from outside itself.
	/// </summary>
	/// <remarks>
	/// This is the enforcement of "the repair never comments a token outside the body it is repairing". It is checked
	/// against the text rather than the tree on purpose: the tree is built from a file a previous round may already
	/// have damaged, and it is the text that the editor will read.
	/// </remarks>
	private static bool SpanKeepsBraces(SourceFile file, TextSpan span)
	{
		if (span.IsEmpty)
		{
			return true;
		}

		int? balance = CodeBraceBalance(file.Text, span.Start, span.Length);
		if (balance is 0 or null)
		{
			return true;
		}

		Logger.Warning(LogCategory.Export,
			$"Refused to comment out {span.Length} characters at offset {span.Start} of {file.Path}: "
			+ $"the span is {Math.Abs(balance.Value)} brace{(Math.Abs(balance.Value) == 1 ? "" : "s")} "
			+ $"{(balance.Value < 0 ? "short of" : "past")} what it opened, so it reaches outside the statement.");
		return false;
	}

	/// <summary>
	/// Whether a statement is one the repair itself wrote, which it must never comment out.
	/// </summary>
	/// <remarks>
	/// Emptying a method leaves a stand-in <c>return default;</c> behind, announced by <see cref="EmptiedNote"/>.
	/// Commenting that out empties the body again, which writes another stand-in, which the next attempt comments out
	/// in its turn - a quartet of lines per attempt, all the way to <see cref="MaxAttempts"/>, and a method that can
	/// never settle because the loop is arguing with itself. There is nothing to gain from commenting it either way:
	/// it is already the least the method can say.
	/// </remarks>
	private static bool IsOwnInsertion(SyntaxNode statement)
		=> statement.ToFullString().Contains(EmptiedNote, StringComparison.Ordinal);

	/// <summary>
	/// The repaired text of a file, or <see langword="null"/> where applying the edits would change how many braces
	/// the file has.
	/// </summary>
	/// <remarks>
	/// The last line of defence, and the only one that does not depend on any of the reasoning above being right: a
	/// repair removes statements, and removing statements never changes a file's brace balance. Where it has, the
	/// round is dropped rather than written, and the file is named so the edit can be found.
	/// </remarks>
	private static string? RepairedText(SourceFile file, List<Edit> edits)
	{
		string repaired = ApplyEdits(file.Text, edits);

		int? before = CodeBraceBalance(file.Text);
		int? after = CodeBraceBalance(repaired);

		if (before is null || after is null || before == after)
		{
			return repaired;
		}

		Logger.Warning(LogCategory.Export,
			$"Dropped {edits.Count} repair{(edits.Count == 1 ? "" : "s")} to {file.Path}: they would have left the "
			+ $"file with a brace balance of {after} where it had {before}, which no repair is allowed to do.");
		return null;
	}
}
