using System.Collections.Generic;
using Cpp2IL.Core.Extensions;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Removes the call that sets a class up the first time something touches it.
/// </summary>
/// <remarks>
/// A class carries a bit saying its static state has been prepared, and every method that reaches one opens
/// with a test of that bit and a call to prepare it. The language says nothing about any of this - a static
/// field is simply there - so what is left in the recovered method is a read of unmanaged memory and a call to
/// an address with no name.
///
/// Excising the whole guard was tried twice and does not work here, because the region it would take is not
/// the simple diamond that shape expects: on this build the preparation sits behind a test of its own. But the
/// call is the only thing in there that does anything, and taking just the call leaves the test reading a
/// value nobody wants, which the collection afterwards removes along with the class pointer it was read from.
///
/// It is recognised without a name, because it can be: it is handed a runtime class and nothing else that
/// matters, and it hands nothing back that is read. Managed code never passes a runtime class to anything, and
/// the runtime calls that do take one - allocating an object, most of all - are called for what they return.
/// </remarks>
public static class ClassInitCallRemover
{
    public static bool Run(MethodAnalysisContext method)
    {
        var reads = new Dictionary<LocalVariable, int>();

        foreach (var instruction in method.ControlFlowGraph!.Instructions)
        {
            var destination = instruction.Destination as LocalVariable;

            foreach (var operand in instruction.Operands)
            {
                switch (operand)
                {
                    case LocalVariable local when !ReferenceEquals(local, destination):
                        reads[local] = reads.GetValueOrDefault(local) + 1;
                        break;
                    case MemoryOperand { Base: LocalVariable held }:
                        reads[held] = reads.GetValueOrDefault(held) + 1;
                        break;
                }
            }
        }

        var changed = false;

        foreach (var instruction in method.ControlFlowGraph.Instructions)
        {
            //Still an address: a call that resolved to a method is something the program does.
            if (instruction.OpCode != OpCode.Call || instruction.Operands.Count < 3 || !instruction.Operands[0].IsNumeric())
                continue;

            //Handed a runtime class, which no managed call ever is. It reaches the call either in a value
            //typed as one or, where the class was known outright, as the type itself.
            if (!IsRuntimeClass(instruction.Operands[2]))
                continue;

            //And asked for nothing back. Allocating takes a class too, but its answer is the whole point.
            if (instruction.Operands[1] is not LocalVariable result || reads.GetValueOrDefault(result) > 0)
                continue;

            instruction.OpCode = OpCode.Nop;
            instruction.Operands = [];
            changed = true;
        }

        return changed;
    }

    /// <summary>Whether the operand is a runtime class, however it reached the call.</summary>
    private static bool IsRuntimeClass(object operand) => operand switch
    {
        LocalVariable { Type: RuntimeClassTypeAnalysisContext } => true,
        RuntimeClassTypeAnalysisContext => true,
        //A type named outright is the class metadata for it, which is what the runtime is being handed.
        TypeAnalysisContext and not (StaticFieldStorageTypeAnalysisContext or RgctxTableTypeAnalysisContext
            or MethodRgctxTableTypeAnalysisContext or RuntimeMethodInfoAnalysisContext) => true,
        _ => false,
    };
}
