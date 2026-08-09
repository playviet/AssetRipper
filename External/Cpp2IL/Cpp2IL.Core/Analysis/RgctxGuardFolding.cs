using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Answers the guard a shared generic body opens with, which asks whether its runtime context is there yet.
/// </summary>
/// <remarks>
/// <para>
/// A body shared between instantiations is handed the <c>MethodInfo*</c> of the one it is running as, and
/// the generic context hanging off it is filled in lazily. So the body begins by asking whether that has
/// happened:
/// </para>
/// <code>
/// CheckNotEqual v32, [methodInfo @ X4 (Il2CppMethodInfo)+38], 0
/// </code>
/// <para>
/// which is <c>MethodInfo::rgctx_data</c> tested against null. Nothing in the original source asked it, and
/// the read has no name a C# reader could use - it came out as unmanaged memory and took the statement with
/// it, in 15 of the 51 bodies that nothing but that marker stands between and whole.
/// </para>
/// <para>
/// The answer is known: by the time any of the method's own code runs, the context is there. That is exactly
/// the position <see cref="MetadataInitGuardRemover"/> already takes for a class's initialised flag - the
/// guard says nothing the reader needs, and answering it lets constant folding take the branch away with it.
/// </para>
/// </remarks>
public static class RgctxGuardFolding
{
    /// <summary>What a shared body reaches its own context and class through.</summary>
    private const long RgctxData = 0x38, Klass = 0x20;

    public static void Run(MethodAnalysisContext method)
    {
        if (method.ControlFlowGraph is not { } graph)
            return;

        foreach (var instruction in graph.Instructions)
        {
            var equal = instruction.OpCode == OpCode.CheckEqual;

            if (!equal && instruction.OpCode != OpCode.CheckNotEqual)
                continue;

            if (instruction.Operands is not [{ } answer, MemoryOperand read, { } against]
                || !IsZero(against) || !AsksAboutTheContext(read))
                continue;

            //The context is there, so "is it null" is false and "is it not null" is true.
            instruction.OpCode = OpCode.Move;
            instruction.Operands = [answer, equal ? 0L : 1L];
        }
    }

    private static bool AsksAboutTheContext(MemoryOperand read)
        => read is { Index: null, Scale: 0, Base: LocalVariable { IsMethodInfo: true } }
            && read.Addend is RgctxData or Klass;

    private static bool IsZero(object against) => against switch
    {
        long value => value == 0,
        int value => value == 0,
        ulong value => value == 0,
        uint value => value == 0,
        _ => false,
    };
}
