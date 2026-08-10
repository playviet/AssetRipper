using System.Collections.Generic;
using System.Linq;
using AssetRipper.IO.Files;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace AssetRipper.Export.UnityProjects.Scripts;

/// <summary>
/// Takes <c>unsafe</c> off a method that ended up an iterator, which C# forbids outright.
/// </summary>
/// <remarks>
/// <para>
/// The repair loop above cannot see this one. A method carries <c>unsafe</c> because <i>some</i> statement
/// in it needed a pointer, and while those statements are there the method is not an iterator - it has no
/// <c>yield</c>. It becomes one on the last attempt, where a body that would not settle is emptied down to
/// whatever still compiles, and a <c>yield return</c> is exactly the kind of statement that survives. No
/// compile follows that, so CS1629 is never among the diagnostics the loop reads - measured: 3107
/// diagnostics over a whole export and not one of them.
/// </para>
/// <para>
/// It needs no compiler anyway. <c>unsafe</c> and <c>yield</c> are never legal together, whatever else is
/// wrong with the file, so the sweep is a syntactic rule rather than a diagnostic to answer: a method
/// declaration with the modifier and a <c>yield</c> below it loses the modifier.
/// </para>
/// <para>
/// <c>CF.AllDesignConfig.CRPullAll</c> is the case - declared <c>unsafe IEnumerator</c> with every statement
/// commented out - and the three errors it raised were the only thing between a whole export and an assembly
/// that builds.
/// </para>
/// </remarks>
internal static partial class InvalidSourceRepair
{
    private static void RemoveUnsafeFromIterators(string outputFolder, FileSystem fileSystem,
        CSharpParseOptions parseOptions, HashSet<string> repairedFiles)
    {
        foreach (SourceFile file in Parse(outputFolder, fileSystem, parseOptions))
        {
            List<TextSpan> modifiers = [];

            foreach (SyntaxNode node in file.Root.DescendantNodes())
            {
                if (node is not BaseMethodDeclarationSyntax method
                    || method.Modifiers.FirstOrDefault(modifier => modifier.IsKind(SyntaxKind.UnsafeKeyword))
                        is not { RawKind: not 0 } keyword)
                {
                    continue;
                }

                //Nothing unsafe under it. The modifier is there because *some* statement in the body
                //needed a pointer, and the repair above commented that statement out - a comment is trivia,
                //so what is left is a method declared `unsafe` with no unsafe syntax anywhere in it. That is
                //meaningless at best, and where the body also became an iterator it is illegal outright:
                //`AllDesignConfig.CRPullAll` is declared `unsafe IEnumerator` with every statement commented,
                //and the three errors it raised were the only thing between a whole export and an assembly
                //that builds.
                if (method.DescendantNodes().Any(IsUnsafe))
                {
                    continue;
                }

                //The keyword and the space after it, so `unsafe IEnumerator` does not become ` IEnumerator`.
                modifiers.Add(TextSpan.FromBounds(keyword.SpanStart, keyword.Span.End
                    + (keyword.TrailingTrivia.Count > 0 ? keyword.TrailingTrivia.Span.Length : 0)));
            }

            if (modifiers.Count == 0)
            {
                continue;
            }

            string text = file.Text;

            //From the back, so an earlier span is still where it was said to be.
            foreach (TextSpan span in modifiers.OrderByDescending(span => span.Start))
            {
                text = text.Remove(span.Start, span.Length);
            }

            fileSystem.File.WriteAllText(file.Path, text);
            repairedFiles.Add(file.Path);
        }
    }

    /// <summary>Syntax the language only allows in an unsafe context.</summary>
    private static bool IsUnsafe(SyntaxNode node) => node is PointerTypeSyntax or FunctionPointerTypeSyntax
        or StackAllocArrayCreationExpressionSyntax or ImplicitStackAllocArrayCreationExpressionSyntax
        or FixedStatementSyntax or SizeOfExpressionSyntax or UnsafeStatementSyntax
        || node is PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.AddressOfExpression
            or (int)SyntaxKind.PointerIndirectionExpression }
        || node is MemberAccessExpressionSyntax { RawKind: (int)SyntaxKind.PointerMemberAccessExpression };
}
