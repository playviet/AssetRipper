using System.Collections.Generic;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Drops a read of an array's length that nothing goes on to read.
/// </summary>
/// <remarks>
/// <para>
/// il2cpp bounds-checks every indexer, so <c>list[i]</c> arrives as a read of the backing array, a read of
/// its length, and a comparison. Once the access itself is recovered the comparison and the index are gone
/// and the length read is left behind, feeding nothing:
/// </para>
/// <code>
/// //AssetRipper: commented out, this could not be kept as code.
/// //_ = list._items.Length;
/// </code>
/// <para>
/// It says nothing - no statement reads the value - and it cannot be written down either, because
/// <c>List&lt;T&gt;._items</c> is private to another assembly. So it is a commented statement per indexed
/// access, and in seven of the ninety-six methods it is the *only* thing keeping the method from scoring as
/// recovered whole.
/// </para>
/// <para>
/// <see cref="DeadCodeEliminator"/> will not take it: the read is an <see cref="OpCode.Call"/> by the time it
/// gets here - <c>Array.get_Length</c> - and that pass excludes calls, correctly, since it cannot know what an
/// arbitrary callee does. This knows what this one does. <c>Array.get_Length</c> reads the length a header
/// already holds; it has no side effect and can fail only on a null array, which recovered C# checks for
/// itself. Nothing else is taken - a getter in general may do anything.
/// </para>
/// </remarks>
public static class UnusedLengthRead
{
    public static bool Run(MethodAnalysisContext method)
    {
        if (method.ControlFlowGraph is not { } graph)
            return false;

        var changed = false;

        //A removal can only ever remove reads, so one more pass can only find more; the loop settles because
        //each turn nops at least one instruction and nothing is ever put back.
        for (var settled = false; !settled;)
        {
            settled = true;
            var reads = ReadCounts(graph);

            foreach (var block in graph.Blocks)
            {
                foreach (var instruction in block.Instructions)
                {
                    if (instruction.OpCode != OpCode.Call || !IsArrayLength(instruction))
                        continue;

                    if (instruction.Destination is not LocalVariable destination
                        || (reads.TryGetValue(destination, out var count) && count > 0))
                        continue;

                    instruction.OpCode = OpCode.Nop;
                    instruction.Operands = [];
                    changed = true;
                    settled = false;
                }
            }
        }

        return changed;
    }

    private static bool IsArrayLength(Instruction instruction)
        => instruction.Operands.Count > 0
            && instruction.Operands[0] is MethodAnalysisContext { Name: "get_Length" } callee
            && callee.DeclaringType?.FullName == "System.Array";

    /// <summary>
    /// How many times each local is read. The destination position is not a read; the object a field is
    /// reached through, and the base and index of an address, always are.
    /// </summary>
    private static Dictionary<LocalVariable, int> ReadCounts(ISILControlFlowGraph graph)
    {
        var counts = new Dictionary<LocalVariable, int>();

        void Count(LocalVariable? local)
        {
            if (local != null)
                counts[local] = counts.TryGetValue(local, out var c) ? c + 1 : 1;
        }

        foreach (var block in graph.Blocks)
        {
            foreach (var instruction in block.Instructions)
            {
                var destination = instruction.Destination as LocalVariable;

                foreach (var operand in instruction.Operands)
                {
                    switch (operand)
                    {
                        case LocalVariable local when !ReferenceEquals(local, destination):
                            Count(local);
                            break;
                        case MemoryOperand memory:
                            Count(memory.Base as LocalVariable);
                            Count(memory.Index as LocalVariable);
                            break;
                        case FieldReference { Field.IsStatic: false, Local: { } through }:
                            Count(through);
                            break;
                    }
                }
            }
        }

        return counts;
    }
}
