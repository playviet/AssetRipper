using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Drops a phi copy between one of the runtime's own structures and a reference the program names.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="SsaForm"/> already refuses this edge - <c>IsRuntimeStructure(held) != IsRuntimeStructure(given)</c>
/// is the last line of <c>CannotBeTheSameValue</c> - but it asks while the graph is being destructed, before
/// the passes that settle what a local holds. A register that carried a <c>MethodInfo*</c> earlier and an
/// array later has neither type yet at that point, so the copy survives and comes out as
/// </para>
/// <code>
/// num2 = (nint)array2;
/// </code>
/// <para>
/// which is not C#. <c>FirebaseTracking::FlushPending</c> has four of them, one on each edge into the loop
/// that logs a pending event, and the statements that built the array go with them.
/// </para>
/// <para>
/// <b>Managed code never holds a runtime structure.</b> A <c>MethodInfo*</c>, a class pointer or a static
/// storage pointer is something the runtime keeps; a <c>Parameter[]</c> is something the program has. If the
/// join really read what this path put there under the type the other path gave it the program would be
/// ill-typed, so on this path the local is not read afterwards and the copy is only how the register got
/// taken back.
/// </para>
/// <para>
/// After <see cref="StandInCopyType"/>, which gets first refusal: where the stand-in was never a class
/// pointer at all that pass corrects the type and the copy is a real one. What is left here is a register
/// carrying both things at different times, and only the copy between them can be dropped.
/// </para>
/// <para>
/// Only the copies single assignment form invented, which carry index -1, and only where the other end is a
/// reference the program itself names: a bare integer is what a <c>MethodInfo*</c> lowers to, so a copy
/// through one may well be the value moving.
/// </para>
/// </remarks>
public static class StandInEdgeCopy
{
    public static void Run(MethodAnalysisContext method)
    {
        if (method.ControlFlowGraph is not { } graph)
            return;

        foreach (var instruction in graph.Instructions)
        {
            if (instruction is not { Index: -1, OpCode: OpCode.Move, Operands: [LocalVariable into, LocalVariable from] })
                continue;

            var standIn = LocalVariables.IsRuntimeStandIn(into.Type);
            if (standIn == LocalVariables.IsRuntimeStandIn(from.Type))
                continue;

            if (!Nameable(standIn ? from.Type : into.Type))
                continue;

            instruction.OpCode = OpCode.Nop;
            instruction.Operands = [];
        }
    }

    /// <summary>A reference type the program itself has - which a runtime structure never is.</summary>
    private static bool Nameable(TypeAnalysisContext? type)
        => type is { IsValueType: false }
            and not (ByRefTypeAnalysisContext or PointerTypeAnalysisContext
                or GenericParameterTypeAnalysisContext)
            && type.FullName != "System.Object"
            && !LocalVariables.IsRuntimeStandIn(type);
}
