using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Gives back the constant a merged return was computing.
///
/// A compiler routinely merges several returns into one block that works the value out rather than naming it -
/// an iterator's <c>MoveNext</c> ends in <c>cmp w21, #0; cset w0, eq</c>, which is <c>return num == 0</c> for
/// every path at once. That is the same value the source returned, but it is no longer the literal the source
/// wrote, and a reader looking for the shape of an iterator needs the literal: it wants the yielding path to end
/// in <c>return true</c>, and there is no <c>true</c> anywhere in the machine code to find.
///
/// Where a path reaching the return has already tested exactly the condition the return computes, that path
/// knows the answer, so the return is copied into it with the answer written out. Paths that do not know keep
/// the block they had, and it keeps working the value out for them.
/// </summary>
public static class MergedReturnSplitter
{
    public static void Run(MethodAnalysisContext method)
    {
        var graph = method.ControlFlowGraph!;

        //The same value is written two ways here: the snapshot the method took of a field, and the field read
        //that was folded into the test further up. Following every copy back to where the value came from lets
        //the two be recognised as the one thing they are.
        var definitions = new Dictionary<LocalVariable, Instruction>();

        foreach (var instruction in graph.Instructions)
            if (instruction.Destination is LocalVariable destination)
                definitions[destination] = instruction;

        foreach (var returnBlock in graph.Blocks.ToList())
        {
            if (!IsComputedReturn(returnBlock, out var condition, out var value, out var relation))
                continue;

            if (returnBlock.Predecessors.Count < 2)
                continue;

            foreach (var predecessor in returnBlock.Predecessors.ToList())
            {
                if (predecessor.Successors.Count is not (1 or 2))
                    continue;

                if (KnownOutcome(graph, definitions, predecessor, condition, value, relation) is not { } outcome)
                    continue;

                //A path with nowhere else to go can take the return into itself. One that also branches
                //somewhere else cannot, because the return would then sit on both of its ways out, so that
                //edge gets a block of its own to hold it.
                if (predecessor.Successors.Count == 1)
                    Excise(graph, returnBlock, predecessor, outcome);
                else
                    SplitEdge(graph, returnBlock, predecessor, outcome);
            }
        }

        ReturnTheConstantEachPathHas(graph);
    }

    /// <summary>
    /// Moves a shared return into the paths that already know what they are returning.
    /// </summary>
    /// <remarks>
    /// The other way a compiler merges returns is to leave the value in a register and jump: each path writes
    /// its own constant and they all end at one <c>ret</c>. Nothing needs working out here - the constant is
    /// written down on every path - but the returning instruction is still somewhere else, and what it returns
    /// is a register rather than a literal.
    ///
    /// That is the difference between a recognisable iterator and an unrecognisable one. Reading
    /// <c>yield return</c> back out of a state machine means finding the path that sets the current value and
    /// then returns <em>true</em>; a path that sets the current value and jumps to a shared return of whatever
    /// is in a register does not say what it returns, and the reader gives up on the whole method. Giving each
    /// path the return, with the literal it already had, changes nothing about what runs.
    /// </remarks>
    private static void ReturnTheConstantEachPathHas(ISILControlFlowGraph graph)
    {
        foreach (var returnBlock in graph.Blocks.ToList())
        {
            if (returnBlock.Instructions.Where(i => i.OpCode != OpCode.Nop).ToList() is not
                [{ OpCode: OpCode.Return, Operands: [LocalVariable returned] }])
                continue;

            if (returnBlock.Predecessors.Count < 2)
                continue;

            foreach (var predecessor in returnBlock.Predecessors.ToList())
            {
                //A path that also goes somewhere else cannot hold the return, and one that has to work the
                //value out is the case the pass above deals with.
                if (predecessor.Successors.Count != 1)
                    continue;

                if (Definition(predecessor, returned) is not { OpCode: OpCode.Move, Operands: [_, { } constant] } given
                    || !Instruction.IsConstantValue(constant))
                    continue;

                //The copy was only ever read by the return that is being taken out of the shared block.
                given.OpCode = OpCode.Nop;
                given.Operands = [];

                var jump = predecessor.Instructions.LastOrDefault(i => i.OpCode != OpCode.Nop);

                if (jump is { OpCode: OpCode.Jump })
                {
                    jump.OpCode = OpCode.Nop;
                    jump.Operands = [];
                }

                predecessor.AddInstruction(new Instruction(predecessor.Instructions.Count, OpCode.Return, constant));
                predecessor.CalculateBlockType();

                predecessor.Successors.Remove(returnBlock);
                returnBlock.Predecessors.Remove(predecessor);

                if (!predecessor.Successors.Contains(graph.ExitBlock))
                {
                    predecessor.Successors.Add(graph.ExitBlock);
                    graph.ExitBlock.Predecessors.Add(predecessor);
                }
            }
        }
    }

    /// <summary>
    /// A block that computes one relational condition and returns it, which is what a merged return looks like.
    /// </summary>
    private static bool IsComputedReturn(Block block, out object condition, out object value, out OpCode relation)
    {
        condition = null!;
        value = null!;
        relation = OpCode.Invalid;

        var instructions = block.Instructions.Where(i => i.OpCode != OpCode.Nop).ToList();

        if (instructions is not [{ Operands: [LocalVariable result, { } left, { } right] } comparison, { OpCode: OpCode.Return, Operands: [LocalVariable returned] }])
            return false;

        if (comparison.OpCode is not (>= OpCode.CheckEqual and <= OpCode.CheckLessOrEqual) || !ReferenceEquals(result, returned))
            return false;

        //One side has to be the thing being tested and the other the value tested against, or there is nothing
        //a branch further up could have decided.
        if (left is LocalVariable && Instruction.IsConstantValue(right))
        {
            condition = left;
            value = right;
        }
        else if (right is LocalVariable && Instruction.IsConstantValue(left))
        {
            condition = right;
            value = left;
        }
        else
        {
            return false;
        }

        relation = comparison.OpCode;
        return true;
    }

    /// <summary>
    /// Whether every path to this block has already decided the condition, and to what. Walks back while there
    /// is only one way in, since a join is a place two answers could meet.
    /// </summary>
    private static bool? KnownOutcome(ISILControlFlowGraph graph, Dictionary<LocalVariable, Instruction> definitions, Block block, object condition, object value, OpCode relation)
    {
        const int maxDepth = 16;

        for (var depth = 0; depth < maxDepth; depth++)
        {
            if (block.Predecessors.Count != 1)
                return null;

            var predecessor = block.Predecessors[0];
            var terminator = predecessor.Instructions.LastOrDefault(i => i.OpCode != OpCode.Nop);

            if (terminator is { OpCode: OpCode.ConditionalJump, Operands: [_, LocalVariable branchCondition] }
                && Definition(predecessor, branchCondition) is { } test
                && Tests(test, condition, value, definitions) is { } tested)
            {
                //The jump's target is where the condition held; falling through is where it did not.
                var taken = ReferenceEquals(TargetOf(graph, terminator), block);
                var branchSaysTrue = taken == tested;

                return relation == test.OpCode ? branchSaysTrue : !branchSaysTrue;
            }

            block = predecessor;
        }

        return null;
    }

    /// <summary>
    /// Whether the instruction is a test of the same thing against the same value, and whether it reads as
    /// written or inverted with respect to the relation being asked about.
    /// </summary>
    private static bool? Tests(Instruction test, object condition, object value, Dictionary<LocalVariable, Instruction> definitions)
    {
        if (test.Operands is not [_, { } left, { } right])
            return null;
        var sameWayRound = SameValue(left, condition, definitions) && Equal(right, value);
        var otherWayRound = SameValue(right, condition, definitions) && Equal(left, value);

        if (!sameWayRound && !otherWayRound)
            return null;

        //Equality reads the same either way round; an ordering does not, and nothing here needs it to.
        if (otherWayRound && test.OpCode is not (OpCode.CheckEqual or OpCode.CheckNotEqual))
            return null;

        return true;
    }

    /// <summary>
    /// Whether two operands name the same value, following however many copies were made of it. A field read
    /// and the local a copy of it was kept in are the same value until something writes that field, and the
    /// step that stops a read being carried past a write has already run by this point.
    /// </summary>
    private static bool SameValue(object left, object right, Dictionary<LocalVariable, Instruction> definitions)
    {
        var a = Source(left, definitions);
        var b = Source(right, definitions);

        return (a, b) switch
        {
            (LocalVariable x, LocalVariable y) => ReferenceEquals(x, y),
            (FieldReference x, FieldReference y) => ReferenceEquals(x.Field, y.Field) && ReferenceEquals(x.Local, y.Local),
            _ => false,
        };
    }

    // Where a value came from, following copies back until something other than a copy produced it.
    private static object Source(object operand, Dictionary<LocalVariable, Instruction> definitions)
    {
        const int maxDepth = 8;

        for (var depth = 0; depth < maxDepth; depth++)
        {
            if (operand is not LocalVariable local
                || !definitions.TryGetValue(local, out var definition)
                || definition.OpCode != OpCode.Move
                || definition.Operands.Count < 2)
                return operand;

            operand = definition.Operands[1];
        }

        return operand;
    }

    /// <summary>
    /// Whether two constants are the same number. They routinely arrive as different types - the width a value
    /// was written at is not the width it was compared at - and a boxed int never equals a boxed long.
    /// </summary>
    private static bool Equal(object left, object right)
    {
        if (!Instruction.IsConstantValue(left) || !Instruction.IsConstantValue(right))
            return false;

        if (IsIntegral(left) && IsIntegral(right))
        {
            try
            {
                return System.Convert.ToInt64(left) == System.Convert.ToInt64(right);
            }
            catch (System.OverflowException)
            {
                return false;
            }
        }

        return Equals(left, right);
    }

    private static bool IsIntegral(object operand) =>
        operand is byte or sbyte or short or ushort or int or uint or long or ulong;

    private static Instruction? Definition(Block block, LocalVariable local)
    {
        for (var i = block.Instructions.Count - 1; i >= 0; i--)
            if (block.Instructions[i].Destination is LocalVariable destination && ReferenceEquals(destination, local))
                return block.Instructions[i];

        return null;
    }

    private static Block? TargetOf(ISILControlFlowGraph graph, Instruction jump) => jump.Operands[0] switch
    {
        Block block => block,
        Instruction instruction => graph.FindBlockByInstruction(instruction),
        _ => null,
    };

    /// <summary>
    /// Gives the edge into the shared return a block of its own holding the return, for a path that also
    /// branches elsewhere and so cannot hold it directly.
    /// </summary>
    private static void SplitEdge(ISILControlFlowGraph graph, Block returnBlock, Block predecessor, bool outcome)
    {
        var comparison = returnBlock.Instructions.First(i => i.OpCode != OpCode.Nop);
        var result = (LocalVariable)comparison.Operands[0];

        var landing = new Block { ID = graph.Blocks.Max(b => b.ID) + 1 };
        landing.AddInstruction(new Instruction(0, OpCode.Move, result, outcome ? 1 : 0));
        landing.AddInstruction(new Instruction(1, OpCode.Return, result));
        landing.CalculateBlockType();
        graph.Blocks.Add(landing);

        //Only the taken side of a branch names where it goes; the other side is wherever the block falls to,
        //which the successor list is the only record of.
        var terminator = predecessor.Instructions.LastOrDefault(i => i.OpCode != OpCode.Nop);

        if (terminator is { OpCode: OpCode.ConditionalJump } && ReferenceEquals(TargetOf(graph, terminator), returnBlock))
            terminator.Operands[0] = landing.Instructions[0];

        predecessor.Successors[predecessor.Successors.IndexOf(returnBlock)] = landing;
        returnBlock.Predecessors.Remove(predecessor);

        landing.Predecessors.Add(predecessor);
        landing.Successors.Add(graph.ExitBlock);
        graph.ExitBlock.Predecessors.Add(landing);
    }

    /// <summary>
    /// Replaces the path's jump into the shared return with the return itself, saying what it returns.
    /// </summary>
    private static void Excise(ISILControlFlowGraph graph, Block returnBlock, Block predecessor, bool outcome)
    {
        var comparison = returnBlock.Instructions.First(i => i.OpCode != OpCode.Nop);
        var result = (LocalVariable)comparison.Operands[0];

        //A jump into the return is on its way there and has no other purpose; anything else falls through.
        var last = predecessor.Instructions.LastOrDefault(i => i.OpCode != OpCode.Nop);

        if (last is { OpCode: OpCode.Jump })
        {
            last.OpCode = OpCode.Nop;
            last.Operands = [];
        }

        predecessor.AddInstruction(new Instruction(predecessor.Instructions.Count, OpCode.Move, result, outcome ? 1 : 0));
        predecessor.AddInstruction(new Instruction(predecessor.Instructions.Count, OpCode.Return, result));
        predecessor.CalculateBlockType();

        predecessor.Successors.Remove(returnBlock);
        returnBlock.Predecessors.Remove(predecessor);

        if (!predecessor.Successors.Contains(graph.ExitBlock))
        {
            predecessor.Successors.Add(graph.ExitBlock);
            graph.ExitBlock.Predecessors.Add(predecessor);
        }
    }
}
