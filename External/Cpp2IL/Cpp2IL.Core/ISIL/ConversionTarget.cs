using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.ISIL;

/// <summary>
/// The type a conversion produces, carried by the move that stands for the conversion.
/// </summary>
/// <remarks>
/// <para>
/// ISIL has no width, so every instruction that converts between one - <c>scvtf</c>, <c>fcvt</c>,
/// <c>fcvtzs</c>, <c>sxtb</c> and the rest - is lifted as a plain move. That is right about where the value
/// goes and wrong about what it is: a move says the two sides are the same thing, and the whole point of a
/// conversion is that they are not. So a value came out carrying the type of whatever it was converted
/// <i>from</i>, and three separate defects follow from it:
/// </para>
/// <list type="bullet">
/// <item>a float divided by an int count is written as an int division, truncating a result the original
/// did not truncate;</item>
/// <item>the decompiler writes <c>Expected F4, but got I4</c> where the two disagree, and where it cannot
/// reconcile them it gives up on the statement;</item>
/// <item>naming an inlined <c>Math.Floor(double)</c> - which is exact and correct - puts a properly typed
/// call into an expression the lifting believes is integral, and the body then does not compile at all.</item>
/// </list>
/// <para>
/// The conversion is marked rather than given an opcode of its own. An opcode would be one line in the enum
/// and fifty places that match <c>OpCode.Move</c> silently not matching any more; an extra operand is ignored
/// by everything that reads a move's destination and source, and only the four places that must know about it
/// ask. Those are: the type propagation, which pins the destination instead of copying across it; the two
/// simplifiers, which must not forward a move whose two sides differ; and the generator, which now emits the
/// conversion the instruction actually performs.
/// </para>
/// </remarks>
public readonly struct ConversionTarget(TypeAnalysisContext type)
{
    public readonly TypeAnalysisContext Type = type;

    /// <summary>The type this instruction converts to, or null where it is an ordinary move.</summary>
    public static TypeAnalysisContext? Of(Instruction instruction)
        => instruction is { OpCode: OpCode.Move, Operands: [_, _, ConversionTarget target] } ? target.Type : null;

    /// <summary>
    /// Whether a pass that would replace this instruction's result with its source has to leave it alone.
    /// </summary>
    /// <remarks>
    /// Forwarding a conversion deletes it, and with it both the type it pins and the arithmetic it performs -
    /// which is the difference between <c>(float)n</c> and <c>n</c>.
    /// </remarks>
    public static bool CannotBeForwarded(Instruction instruction) => Of(instruction) is not null;

    public override string ToString() => $"as {Type.Name}";
}
