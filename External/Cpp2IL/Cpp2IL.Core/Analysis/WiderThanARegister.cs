using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Drops a phi copy that claims a struct too big for a register is what a register held.
/// </summary>
/// <remarks>
/// <para>
/// Taking single assignment form apart writes a copy on each edge into a join, and where the register was
/// reused the two ends are unrelated. <see cref="SsaForm"/> already refuses the ones it can tell apart, but
/// it asks while the graph is being destructed - before the passes that settle what a local holds - so a
/// local that only becomes an enumerator later keeps its copy:
/// </para>
/// <code>
/// enumerator7 = (Dictionary&lt;int, bool[]&gt;.Enumerator)(nint)intPtr3;
/// </code>
/// <para>
/// which is not C#, so the statement goes and the whole <c>foreach</c> around it follows.
/// <c>BoardController::ComputeHighlights</c> is that.
/// </para>
/// <para>
/// <b>This is arithmetic, not a heuristic.</b> A <c>Dictionary&lt;K, V&gt;.Enumerator</c> is forty bytes; no
/// register holds one. So a copy between a value type wider than a register and something a register does
/// hold - a number, a pointer, a reference - cannot be the value moving, whatever either end is called. It is
/// the register being taken back, and leaving the copy out is what splits the two uses apart.
/// </para>
/// <para>
/// Only the copies single assignment form invented, which carry index -1. A real move of a struct is a
/// memory copy and never looks like this.
/// </para>
/// </remarks>
public static class WiderThanARegister
{
    public static void Run(MethodAnalysisContext method)
    {
        if (method.ControlFlowGraph is not { } graph)
            return;

        var register = method.AppContext.Binary.is32Bit ? 4 : 8;

        foreach (var instruction in graph.Instructions)
        {
            if (instruction is not { Index: -1, OpCode: OpCode.Move, Operands: [LocalVariable into, LocalVariable from] })
                continue;

            if (TooBig(into.Type, register) == TooBig(from.Type, register))
                continue;

            //One end is a struct no register can carry and the other is something a register does.
            if (FitsARegister(into.Type, register) || FitsARegister(from.Type, register))
            {
                instruction.OpCode = OpCode.Nop;
                instruction.Operands = [];
            }
        }
    }

    private static bool TooBig(TypeAnalysisContext? type, int register)
        => type is { IsValueType: true } held && Aapcs64.SizeOf(held) is { } size && size > register;

    /// <summary>Whether a value of this type is one a register holds whole.</summary>
    private static bool FitsARegister(TypeAnalysisContext? type, int register)
        => type is { } held
            && (!held.IsValueType || (Aapcs64.SizeOf(held) is { } size && size <= register));
}
