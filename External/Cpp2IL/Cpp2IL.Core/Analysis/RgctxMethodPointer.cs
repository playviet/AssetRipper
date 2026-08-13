using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// A method taken out of the runtime generic context is put in the operand, not only on the local's type.
/// </summary>
/// <remarks>
/// <para>
/// A lambda inside a generic type cannot name its own method at compile time, so the compiler reads it out of
/// the runtime generic context and hands it to the delegate's constructor:
/// </para>
/// <code>
/// LDR X8, [X19 + 0x20]   ; methodInfo->klass
/// LDR X8, [X8 + 0xC0]    ; klass->rgctx_data
/// LDR X2, [X8 + 0x60]    ; the MethodInfo* of &lt;GetDataById&gt;b__0
/// BL  Predicate`1&lt;T&gt;..ctor
/// </code>
/// <para>
/// <see cref="RgctxResolver"/> resolves that entry perfectly well - and then writes the answer onto
/// <c>destination.Type</c> and nowhere else. The constructor's operand stays a plain local, and the generator
/// emits <c>ldftn</c> only where the operand <b>is</b> a runtime method: a local merely typed with one falls
/// through to an ordinary load, so the delegate came out as <c>new Predicate&lt;T&gt;(displayClass, method)</c>
/// with <c>nint method = default(nint)</c> beside it, which C# cannot say - and ILSpy's own delegate transform
/// never fires, so the statement is commented and the body with it.
/// </para>
/// <para>
/// This is the same gap <see cref="VirtualMethodPointer"/> closes for a vtable slot, and its comment states
/// the reason this cannot be left to copy propagation: the local is versioned, so nothing folds it. Six
/// bodies, all in generic types or generic methods - the identical closure in a non-generic type recovers
/// today, because there the method pointer is a <c>methodof</c> straight from the method table.
/// </para>
/// <para>
/// Both tables: a generic <em>type</em> keeps its entries under <c>klass->rgctx_data</c> and a generic
/// <em>method</em> under its own <c>MethodInfo</c>, and one of the six goes through the second.
/// </para>
/// </remarks>
public static class RgctxMethodPointer
{
    public static bool Run(MethodAnalysisContext method)
    {
        if (method.ControlFlowGraph is not { } graph)
            return false;

        var changed = false;

        foreach (var instruction in graph.Instructions)
        {
            if (instruction is not { OpCode: OpCode.Move, Operands: [LocalVariable held, MemoryOperand { Index: null, Scale: 0, Base: LocalVariable table }] })
                continue;

            if (table.Type is not (RgctxTableTypeAnalysisContext or MethodRgctxTableTypeAnalysisContext))
                continue;

            //What the entry turned out to be. Only the resolver can say - the offset alone means nothing
            //without the table it is read from, which is why this asks the type rather than the address.
            if (held.Type is not RuntimeMethodInfoAnalysisContext pointer)
                continue;

            instruction.Operands[1] = pointer;

            //And at every place that reads it, for the reason `VirtualMethodPointer` gives: the constructor
            //has to hold the method itself, not a local that holds it.
            foreach (var reader in graph.Instructions)
            {
                if (ReferenceEquals(reader, instruction))
                    continue;

                for (var i = 1; i < reader.Operands.Count; i++)
                    if (ReferenceEquals(reader.Operands[i], held))
                        reader.Operands[i] = pointer;
            }

            changed = true;
        }

        return changed;
    }
}
