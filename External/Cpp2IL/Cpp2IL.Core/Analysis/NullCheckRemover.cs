using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Removes the null checks il2cpp writes out in front of every dereference.
///
/// C# throws a <c>NullReferenceException</c> when a member is used on null, and says nothing about it in the
/// source; il2cpp has to emit the test and the throw, because C++ would not. Recovering them faithfully puts an
/// <c>if (x != null)</c> around every statement and a <c>throw new NullReferenceException()</c> at the end of
/// every method, none of which the source contained - it buries the code that was actually written.
///
/// Dropping them keeps what the method does: the dereference that follows throws the same exception on the same
/// input, because that is what the language guarantees.
/// </summary>
public static class NullCheckRemover
{
    public static void Run(MethodAnalysisContext method)
    {
        var graph = method.ControlFlowGraph!;
        var removedAny = false;

        foreach (var guard in graph.Blocks.ToList())
        {
            if (guard.BlockType != BlockType.TwoWay || guard.Successors.Count != 2)
                continue;

            var terminator = guard.Instructions.LastOrDefault(i => i.OpCode != OpCode.Nop);

            if (terminator is not { OpCode: OpCode.ConditionalJump, Operands: [_, LocalVariable condition] })
                continue;

            if (Definition(guard, condition) is not { } test || !IsNullTest(test, out var testIsEquality))
                continue;

            //Whichever way the test reads, the branch taken when the value is null is the one that throws. The
            //jump names where it goes either as the block or as the instruction it starts with, depending on how
            //far the graph has been rewritten by this point.
            var taken = terminator.Operands[0] switch
            {
                Block block => block,
                Instruction instruction => graph.FindBlockByInstruction(instruction),
                _ => null,
            };

            if (taken is null)
                continue;

            var fallThrough = guard.Successors.FirstOrDefault(s => !ReferenceEquals(s, taken));

            var onNull = testIsEquality ? taken : fallThrough;
            var otherwise = testIsEquality ? fallThrough : taken;

            if (onNull is null || otherwise is null || ReferenceEquals(onNull, otherwise) || !ThrowsNullReference(onNull))
                continue;

            Excise(graph, guard, terminator, onNull, otherwise);
            removedAny = true;
        }

        if (removedAny)
            graph.RemoveUnreachableBlocks();
    }

    /// <summary>
    /// Whether the instruction compares something against zero in a way that only makes sense for a reference.
    /// A number compared against zero is a real question the source asked; a reference compared against zero is
    /// the check the language makes on its own.
    /// </summary>
    private static bool IsNullTest(Instruction test, out bool isEquality)
    {
        isEquality = test.OpCode == OpCode.CheckEqual;

        if (test.OpCode is not (OpCode.CheckEqual or OpCode.CheckNotEqual) || test.Operands.Count < 3)
            return false;

        var subject = test.Operands[1];
        var against = test.Operands[2];

        if (!Instruction.IsConstantValue(against) || !IsZero(against))
            return false;

        return TypeOf(subject) is { IsValueType: false };
    }

    private static TypeAnalysisContext? TypeOf(object operand) => operand switch
    {
        LocalVariable local => local.Type,
        FieldReference field => field.Field.FieldType,
        _ => null,
    };

    private static bool IsZero(object operand)
    {
        try
        {
            return operand is not string && System.Convert.ToInt64(operand) == 0;
        }
        catch (System.Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Whether the block does nothing but throw a null reference, which is what a check that failed does.
    /// </summary>
    private static bool ThrowsNullReference(Block block)
    {
        var instructions = block.Instructions.Where(i => i.OpCode != OpCode.Nop).ToList();

        return instructions is [{ OpCode: OpCode.Throw, Operands: [TypeAnalysisContext { FullName: "System.NullReferenceException" }] }];
    }

    private static Instruction? Definition(Block block, LocalVariable local)
    {
        for (var i = block.Instructions.Count - 1; i >= 0; i--)
            if (block.Instructions[i].Destination is LocalVariable destination && ReferenceEquals(destination, local))
                return block.Instructions[i];

        return null;
    }

    private static void Excise(ISILControlFlowGraph graph, Block guard, Instruction terminator, Block onNull, Block otherwise)
    {
        //The block the check guarded is where the code carries on, so the branch becomes a plain jump to it.
        terminator.OpCode = OpCode.Jump;
        terminator.Operands = [otherwise];

        guard.Successors.Remove(onNull);
        onNull.Predecessors.Remove(guard);
        guard.CalculateBlockType();
    }
}
