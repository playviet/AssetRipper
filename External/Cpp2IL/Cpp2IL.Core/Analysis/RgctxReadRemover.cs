using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Takes out the reads that only lead to a runtime context, once the context itself has been named.
/// </summary>
/// <remarks>
/// <para>
/// A shared generic body reaches what only its type arguments decide by walking runtime structures:
/// <c>methodInfo->rgctx_data</c>, or <c>methodInfo->klass->rgctx_data</c>, and then an entry of the table.
/// <see cref="RgctxResolver"/> already names each step - the destination of every one of those moves comes
/// out typed as the table or the class it points at, and the **entries** are resolved to the types and
/// methods they stand for.
/// </para>
/// <para>
/// What it does not do is take the read away, so each step is still a load of unmanaged memory and still a
/// statement saying so: 59 reads of <c>+0x38</c> and 23 of <c>+0x20</c> in this game. There is nothing to
/// answer them with - the pointer has no name in the language - and nothing needs them, because what reads
/// through them has already been named. That is exactly the argument
/// <see cref="RuntimeClassReadRemover"/> makes for a class's static storage, applied a step earlier.
/// </para>
/// </remarks>
public static class RgctxReadRemover
{
    public static bool Run(MethodAnalysisContext method)
    {
        if (method.ControlFlowGraph is not { } graph)
            return false;

        var is32Bit = method.AppContext.Binary.is32Bit;
        var klass = is32Bit ? 0x10 : 0x20;
        var typeRgctx = is32Bit ? 0x60 : 0xC0;
        var methodRgctx = is32Bit ? 0x1C : 0x38;

        var changed = false;

        foreach (var instruction in graph.Instructions)
        {
            if (instruction.OpCode != OpCode.Move || instruction.Operands.Count != 2
                || instruction.Operands[0] is not LocalVariable { Type: { } named }
                || instruction.Operands[1] is not MemoryOperand { Index: null, Scale: 0, Base: LocalVariable source } read)
                continue;

            //Only where the step the resolver named is the step this read performs. A local that happens to
            //carry one of these types for another reason keeps whatever put it there.
            var bookkeeping = (named, source.Type) switch
            {
                (RuntimeClassTypeAnalysisContext, RuntimeMethodInfoAnalysisContext) => read.Addend == klass,
                (RgctxTableTypeAnalysisContext, RuntimeClassTypeAnalysisContext) => read.Addend == typeRgctx,
                (MethodRgctxTableTypeAnalysisContext, RuntimeMethodInfoAnalysisContext) => read.Addend == methodRgctx,
                _ => false,
            };

            if (!bookkeeping)
                continue;

            instruction.OpCode = OpCode.Nop;
            instruction.Operands = [];
            changed = true;
        }

        return changed;
    }
}
