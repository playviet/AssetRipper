using System;
using System.Collections.Generic;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Gives back the answers a <c>switch</c> was compiled into a table of.
/// </summary>
/// <remarks>
/// <para>
/// A <c>switch</c> whose cases are dense and whose arms are constants does not become branches. The compiler
/// puts the answers in a table and indexes it:
/// </para>
/// <code>
/// Subtract        index, c, 1          ; the cases start at one
/// CheckGreater    out,   index, 4      ; five of them
/// ConditionalJump default, out
/// Move            table, 1603184       ; where the answers are
/// Return          [table + index * 4]
/// </code>
/// <para>
/// The read is from the binary's own data rather than from anything managed, so it resolved to nothing and the
/// method returned the default for every input - <c>Corpus.Weight</c> answered zero for all five colours. The
/// answers are right there: the address is a constant by the time this runs, the count is in the comparison
/// that guards the read, and the width is the scale of the index.
/// </para>
/// <para>
/// What replaces it is a chain of choices rather than a <c>switch</c>, because ISIL has no switch - the
/// decompiler writes it back as nested conditionals. That is not the shape the source had, but it is the
/// answer the source gave, which is the thing worth having.
/// </para>
/// </remarks>
public static class SwitchTableRecovery
{
    /// <summary>
    /// Past this many arms the chain of choices is worse to read than the placeholder it replaces, and a table
    /// that large is more likely to be something else that happens to look like one.
    /// </summary>
    private const int MostArms = 16;

    public static void Run(MethodAnalysisContext method)
    {
        if (method.ControlFlowGraph is not { } graph)
            return;

        foreach (var block in graph.Blocks)
        {
            for (var i = 0; i < block.Instructions.Count; i++)
            {
                var instruction = block.Instructions[i];

                for (var operand = 0; operand < instruction.Operands.Count; operand++)
                {
                    if (instruction.Operands[operand] is not MemoryOperand
                        {
                            Base: LocalVariable table, Index: LocalVariable index, Scale: var width and (4 or 8), Addend: 0,
                        })
                        continue;

                    if (Answers(method, graph, table, index, width) is not { } arms)
                        continue;

                    instruction.Operands[operand] = Chain(method, block, ref i, index, arms, width);
                }
            }
        }
    }

    /// <summary>
    /// The values the table holds, where everything needed to say what they are is in the method.
    /// </summary>
    private static List<object>? Answers(MethodAnalysisContext method, ISILControlFlowGraph graph,
        LocalVariable table, LocalVariable index, int width)
    {
        if (AssignedConstant(graph, table) is not { } address || address <= 0)
            return null;

        if (Bound(graph, index) is not { } count || count is < 1 or > MostArms)
            return null;

        var binary = method.AppContext.Binary;
        var content = binary.GetRawBinaryContent();
        var answers = new List<object>(count);

        for (var arm = 0; arm < count; arm++)
        {
            if (!binary.TryMapVirtualAddressToRaw((ulong)(address + (long)arm * width), out var raw)
                || raw <= 0 || raw + width > content.Length)
                return null;

            answers.Add(width == 4
                ? BitConverter.ToInt32(content.Slice((int)raw, 4))
                : BitConverter.ToInt64(content.Slice((int)raw, 8)));
        }

        return answers;
    }

    /// <summary>The constant a local was last given, where it was given one outright.</summary>
    private static long? AssignedConstant(ISILControlFlowGraph graph, LocalVariable local)
    {
        long? assigned = null;

        foreach (var instruction in graph.Instructions)
        {
            if (instruction.OpCode != OpCode.Move || instruction.Operands.Count != 2
                || !ReferenceEquals(instruction.Operands[0], local))
                continue;

            //Written more than once, or written with something that is not a constant: nothing can be said
            //about where the table is, and reading the binary at a guess would answer with whatever is there.
            if (assigned is not null || Constant(instruction.Operands[1]) is not { } value)
                return null;

            assigned = value;
        }

        return assigned;
    }

    /// <summary>
    /// How many arms the comparison guarding the read allows.
    /// </summary>
    /// <remarks>
    /// The compiler always bounds an indexed table before reading it - the default arm is what the bound
    /// jumps to - so a read with no such comparison is not one of these, and is left alone.
    /// </remarks>
    private static int? Bound(ISILControlFlowGraph graph, LocalVariable index)
    {
        int? count = null;

        foreach (var instruction in graph.Instructions)
        {
            if (instruction.Operands.Count != 3 || instruction.Operands[1] is not LocalVariable compared
                || !ReferenceEquals(compared, index) || Constant(instruction.Operands[2]) is not { } limit)
                continue;

            var arms = instruction.OpCode switch
            {
                OpCode.CheckGreater => limit + 1,
                OpCode.CheckGreaterOrEqual => limit,
                _ => (long?)null,
            };

            if (arms is { } allowed && allowed is > 0 and <= MostArms && (count is null || allowed < count))
                count = (int)allowed;
        }

        return count;
    }

    /// <summary>
    /// Emits the choice between the arms, and gives back the value to read in place of the table.
    /// </summary>
    /// <remarks>
    /// Built from the last arm backwards so that the earlier tests win, which is what the order of a table
    /// means: index zero is the first answer.
    /// </remarks>
    private static LocalVariable Chain(MethodAnalysisContext method, Block block, ref int at,
        LocalVariable index, List<object> arms, int width)
    {
        //Typed as what the table holds. Left untyped the chain lowers to a native integer, and the answer it
        //chooses then meets the `int` the method returns as a different width entirely.
        var types = method.AppContext.SystemTypes;
        var chosen = Local(method, "switched", width == 4 ? types.SystemInt32Type : types.SystemInt64Type);
        var inserted = new List<Instruction>();
        var order = block.Instructions[at].Index;

        //Nothing, rather than the last arm. An index outside the table is a case the original did not read the
        //table for at all - it jumped to the default - and the guard that says so is an *unsigned* comparison,
        //which ISIL has no word for and lifts as a signed one. So the block is entered for a negative index
        //that the original excluded, and answering with the last arm would be answering confidently with the
        //wrong case. Every arm is tested and anything else falls through.
        inserted.Add(new Instruction(order, OpCode.Move, chosen, 0L));

        for (var arm = arms.Count - 1; arm >= 0; arm--)
        {
            var matches = Local(method, "isArm", types.SystemBooleanType);

            //As a long, because the index is a value the analysis never typed and an untyped value lowers to
            //a native integer - an `int` on the other side of the comparison is a different width.
            inserted.Add(new Instruction(order, OpCode.CheckEqual, matches, index, (long)arm));
            inserted.Add(new Instruction(order, OpCode.Select, chosen, matches, arms[arm], chosen));
        }

        block.Instructions.InsertRange(at, inserted);
        at += inserted.Count;

        return chosen;
    }

    private static LocalVariable Local(MethodAnalysisContext method, string what, TypeAnalysisContext type)
    {
        var local = new LocalVariable($"{what}{method.Locals.Count}", new Register(null, "SWITCH"), type);
        method.Locals.Add(local);
        return local;
    }

    private static long? Constant(object operand) => operand switch
    {
        long v => v,
        ulong v => unchecked((long)v),
        int v => v,
        uint v => v,
        _ => null,
    };
}
