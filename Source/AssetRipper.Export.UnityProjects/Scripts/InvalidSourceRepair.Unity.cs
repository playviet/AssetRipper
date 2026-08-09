using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AssetRipper.Export.UnityProjects.Scripts;

/// <summary>
/// The half of the repair that says a member the framework keeps to itself by a name the project may use.
/// </summary>
/// <remarks>
/// Recovery names what the native code reached, and some of what it reached is <c>internal</c> or
/// <c>private</c> in the assembly the editor compiles against. Where the framework has a public member that
/// does exactly the same thing, saying it that way loses nothing - the equivalence is arithmetic or an
/// alias, never a guess.
/// </remarks>
internal static partial class InvalidSourceRepair
{
    /// <summary>Degrees in a radian, and radians in a degree, as Unity writes them.</summary>
    private const string ToDegrees = "57.29578f", ToRadians = "0.017453292f";

    /// <summary>
    /// <c>System.EmptyArray&lt;T&gt;.Value</c>, which the runtime keeps internal, is <c>Array.Empty&lt;T&gt;()</c>.
    /// </summary>
    private static string? RewriteEmptyArray(SyntaxNode node, SemanticModel model)
    {
        if (node is not MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Value" } access
            || Argument(access.Expression, "EmptyArray") is not { } element)
        {
            return null;
        }

        return $"global::System.Array.Empty<{element}>()";
    }

    /// <summary>
    /// The two <c>Quaternion</c> conversions Unity marks private, which differ from the public ones only by
    /// the unit the angles are in.
    /// </summary>
    /// <remarks>
    /// <c>Internal_FromEulerRad</c> takes radians where <c>Quaternion.Euler</c> takes degrees, and
    /// <c>Internal_ToEulerRad</c> gives radians where <c>eulerAngles</c> gives degrees. Multiplying by the
    /// conversion is the whole of the difference. <c>Internal_MakePositive</c> has no public counterpart and
    /// is left alone rather than approximated.
    /// </remarks>
    private static string? RewriteQuaternionInternal(SyntaxNode node, SemanticModel model)
    {
        if (node is not InvocationExpressionSyntax
            {
                Expression: MemberAccessExpressionSyntax { Expression: { } receiver, Name.Identifier.ValueText: { } called },
                ArgumentList.Arguments: [{ Expression: { } argument }],
            }
            || receiver.ToString() is not ("Quaternion" or "UnityEngine.Quaternion" or "global::UnityEngine.Quaternion"))
        {
            return null;
        }

        return called switch
        {
            "Internal_FromEulerRad" => $"global::UnityEngine.Quaternion.Euler({argument} * {ToDegrees})",
            "Internal_ToEulerRad" => $"({argument}).eulerAngles * {ToRadians}",
            _ => null,
        };
    }

    /// <summary>The single type argument of a generic name, where the name is the one wanted.</summary>
    private static string? Argument(ExpressionSyntax expression, string called)
    {
        var name = expression switch
        {
            GenericNameSyntax generic => generic,
            MemberAccessExpressionSyntax { Name: GenericNameSyntax generic } => generic,
            QualifiedNameSyntax { Right: GenericNameSyntax generic } => generic,
            _ => null,
        };

        return name is { TypeArgumentList.Arguments: [{ } only] } && name.Identifier.ValueText == called
            ? only.ToString()
            : null;
    }
}
