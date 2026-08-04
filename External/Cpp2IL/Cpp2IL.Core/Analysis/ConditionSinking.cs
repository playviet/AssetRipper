using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Moves a comparison down to the branch that asks it.
///
/// A condition is computed into a register and branched on later, sometimes with several instructions in
/// between, so it becomes a named boolean that is written on one line and read on another - <c>bool flag =
/// x != null; ...; if (flag)</c> where the source wrote <c>if (x != null)</c>. Standing next to its branch
/// the comparison needs no name at all: the value goes straight from one instruction to the next, and the
/// decompiler folds it into the condition.
///
/// A comparison only reads, so moving it is safe as long as nothing between the two places changes what it
/// reads. Anything that writes one of its operands, and any call at all when it reads memory, stops it.
/// </summary>
public static class ConditionSinking
{
    public static void Run(MethodAnalysisContext method)
    {
        ReuseLoadedFields(method);

        var uses = UseCounts(method);

        foreach (var block in method.ControlFlowGraph!.Blocks)
        {
            var jump = block.Instructions.Count - 1;

            if (jump < 1 || block.Instructions[jump].OpCode != OpCode.ConditionalJump)
                continue;

            if (block.Instructions[jump].Operands.Count < 2 || block.Instructions[jump].Operands[1] is not LocalVariable condition)
                continue;

            //A condition read anywhere else still needs its name.
            if (uses.GetValueOrDefault(condition) != 1)
                continue;

            var definition = IndexOfDefinition(block, condition, jump);

            if (definition < 0 || definition == jump - 1)
                continue;

            if (!IsComparison(block.Instructions[definition]) || !CanMoveDown(block, definition, jump))
                continue;

            var comparison = block.Instructions[definition];
            block.Instructions.RemoveAt(definition);
            block.Instructions.Insert(jump - 1, comparison);
        }
    }

    /// <summary>
    /// Makes a comparison read the value already loaded rather than reading the field a second time.
    ///
    /// Where a field is copied into a local and then compared, the compiled code keeps one copy and tests
    /// it, but the two reads come back separately - so the test names the field again where the source
    /// named the variable. Both read the same thing, and testing the local is the shape the decompiler
    /// expects: the cached-lambda pattern it folds back into a lambda expression is written that way, and
    /// with the field named twice it does not match, leaving the class the compiler generated to hold the
    /// cache exposed in the recovered source.
    /// </summary>
    private static void ReuseLoadedFields(MethodAnalysisContext method)
    {
        foreach (var block in method.ControlFlowGraph!.Blocks)
        {
            for (var i = 0; i < block.Instructions.Count; i++)
            {
                if (!IsComparison(block.Instructions[i]))
                    continue;

                for (var operand = 1; operand < block.Instructions[i].Operands.Count; operand++)
                {
                    if (block.Instructions[i].Operands[operand] is not FieldReference field)
                        continue;

                    if (LoadedInto(block, field, i) is { } already)
                        block.Instructions[i].Operands[operand] = already;
                }
            }
        }
    }

    /// <summary>
    /// The local a field was most recently copied into, if it still holds what the field holds. Any call, or
    /// any store, between the copy and here could have changed the field, and ends the search.
    ///
    /// The search continues into the block before when there is only one, because the copies that carry a
    /// value between blocks are written on the edge - at the end of the predecessor - so a value loaded for
    /// a test at the top of a block is found there and nowhere else.
    /// </summary>
    private static LocalVariable? LoadedInto(Block block, FieldReference field, int before)
    {
        var current = block;
        var start = before - 1;

        for (var depth = 0; depth < 2; depth++)
        {
            for (var i = start; i >= 0; i--)
            {
                var instruction = current.Instructions[i];

                if (instruction.OpCode == OpCode.Move && instruction.Operands.Count == 2
                    && instruction.Operands[0] is LocalVariable loaded && instruction.Operands[1] is FieldReference source
                    && SameField(source, field))
                    return loaded;

                //A jump is not a reason to stop: it is how the block ends, and it changes nothing that is read.
                if (instruction.OpCode is OpCode.Jump or OpCode.ConditionalJump)
                    continue;

                if (instruction.IsCall || instruction.Operands.FirstOrDefault() is FieldReference or MemoryOperand)
                    return null;
            }

            if (current.Predecessors.Count != 1)
                return null;

            current = current.Predecessors[0];
            start = current.Instructions.Count - 1;
        }

        return null;
    }

    private static bool SameField(FieldReference left, FieldReference right)
        => ReferenceEquals(left.Field, right.Field) && left.Offset == right.Offset
            && ReferenceEquals(left.Local, right.Local);

    private static bool IsComparison(Instruction instruction) => instruction.OpCode is OpCode.CheckEqual
        or OpCode.CheckNotEqual or OpCode.CheckGreater or OpCode.CheckLess
        or OpCode.CheckGreaterOrEqual or OpCode.CheckLessOrEqual;

    /// <summary>
    /// Whether nothing between the comparison and the branch changes what the comparison reads. A call is
    /// only a problem for a comparison that reads memory, since a local is not something a call can reach.
    /// </summary>
    private static bool CanMoveDown(Block block, int definition, int jump)
    {
        var comparison = block.Instructions[definition];
        var readsMemory = comparison.Operands.Skip(1).Any(o => o is FieldReference or MemoryOperand);
        var operands = comparison.Operands.Skip(1).OfType<LocalVariable>().ToHashSet();

        for (var i = definition + 1; i < jump; i++)
        {
            var between = block.Instructions[i];

            if (readsMemory && (between.IsCall || between.Operands.FirstOrDefault() is FieldReference or MemoryOperand))
                return false;

            if (between.Destination is LocalVariable written && operands.Contains(written))
                return false;
        }

        return true;
    }

    private static int IndexOfDefinition(Block block, LocalVariable local, int before)
    {
        for (var i = before - 1; i >= 0; i--)
        {
            if (block.Instructions[i].Destination is LocalVariable destination && ReferenceEquals(destination, local))
                return i;
        }

        return -1;
    }

    /// <summary>How many times each local is read, so that only a value with one reader is moved to it.</summary>
    private static Dictionary<LocalVariable, int> UseCounts(MethodAnalysisContext method)
    {
        var uses = new Dictionary<LocalVariable, int>();

        foreach (var instruction in method.ControlFlowGraph!.Instructions)
        {
            for (var i = 0; i < instruction.Operands.Count; i++)
            {
                //Operand zero is what the instruction writes, not something it reads.
                if (i == 0 && instruction.Destination != null)
                    continue;

                switch (instruction.Operands[i])
                {
                    case LocalVariable read:
                        uses[read] = uses.GetValueOrDefault(read) + 1;
                        break;
                    case MemoryOperand { Base: LocalVariable memoryBase }:
                        uses[memoryBase] = uses.GetValueOrDefault(memoryBase) + 1;
                        break;
                }
            }
        }

        return uses;
    }
}
