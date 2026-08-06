using System.Collections.Generic;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Works out the arithmetic whose operands are all constants.
/// </summary>
/// <remarks>
/// <para>
/// A constant too wide for one instruction is built a sixteen bit field at a time, so what reaches the
/// analysis is a chain of masks and ors over immediates rather than a number. The decompiler folds it in the
/// end, which is why the output has never looked wrong - but every pass in between sees a computed value where
/// there is a constant, and the ones that need to read it cannot. That is what stopped a store of two fields
/// at once from being recognised as one: <see cref="WideFieldStore"/> has to see the number.
/// </para>
/// <para>
/// Only where <b>every</b> source is already a constant, so the result is one too. That cannot take an operand
/// away from a pass that matches on a shape - a mask against a memory read, a flag against a comparison - since
/// every such shape has something in it that is not a constant.
/// </para>
/// </remarks>
public static class ConstantFolding
{
    public static void Run(MethodAnalysisContext method)
    {
        if (method.ControlFlowGraph is not { } graph)
            return;

        foreach (var block in graph.Blocks)
        {
            //Carried along the block rather than left to whichever propagation runs next, because the chain is
            //only foldable one link at a time: the mask needs the value the move-wide put there, and the or
            //after it needs what the mask produced. A local is only followed from where it is written to where
            //it is read within the one block, so nothing is assumed about how control reached it.
            var known = new Dictionary<LocalVariable, long>();

            foreach (var instruction in block.Instructions)
            {
                Substitute(instruction, known);

                if (instruction.Operands.Count > 0 && instruction.Operands[0] is LocalVariable written)
                    known.Remove(written);

                if (Folded(instruction) is not { } value)
                    continue;

                instruction.OpCode = OpCode.Move;
                instruction.Operands = [instruction.Operands[0], value];

                if (instruction.Operands[0] is LocalVariable destination)
                    known[destination] = value;
            }
        }
    }

    /// <summary>
    /// Puts a constant in place of a local that holds one, for the operands where a constant means the same
    /// thing as the local did.
    /// </summary>
    /// <remarks>
    /// Not into a memory operand: the base of an address is a place rather than a number, and the passes that
    /// resolve one read it as a register. Everything else is a value being computed with or stored.
    /// </remarks>
    private static void Substitute(Instruction instruction, Dictionary<LocalVariable, long> known)
    {
        for (var operand = 1; operand < instruction.Operands.Count; operand++)
        {
            if (instruction.Operands[operand] is LocalVariable local && known.TryGetValue(local, out var value))
                instruction.Operands[operand] = value;
        }
    }

    private static long? Folded(Instruction instruction)
    {
        if (instruction.Operands.Count < 2 || instruction.Operands[0] is not LocalVariable)
            return null;

        if (Constant(instruction.Operands[1]) is not { } left)
            return null;

        //The one-operand forms first, then the two-operand ones - and nothing else, because a divide can be by
        //zero and a comparison's result is a condition rather than a number.
        if (instruction.Operands.Count == 2)
        {
            return instruction.OpCode switch
            {
                OpCode.Not => ~left,
                OpCode.Negate => -left,
                _ => null,
            };
        }

        if (instruction.Operands.Count != 3 || Constant(instruction.Operands[2]) is not { } right)
            return null;

        return instruction.OpCode switch
        {
            OpCode.And => left & right,
            OpCode.Or => left | right,
            OpCode.Xor => left ^ right,
            OpCode.Add => unchecked(left + right),
            OpCode.Subtract => unchecked(left - right),
            OpCode.Multiply => unchecked(left * right),
            //A shift of more than the width is undefined rather than zero, so only the ones that mean
            //something are taken.
            OpCode.ShiftLeft when right is >= 0 and < 64 => unchecked(left << (int)right),
            //A logical shift brings zeroes in, so folding it as a signed shift would put a sign extension in
            //a constant the program never had - see LogicalShift.
            OpCode.ShiftRight when right is >= 0 and < 64 && LogicalShift.BringsInZeroes(instruction)
                => unchecked((long)((ulong)left >> (int)right)),
            OpCode.ShiftRight when right is >= 0 and < 64 => left >> (int)right,
            _ => null,
        };
    }

    private static long? Constant(object operand) => operand switch
    {
        long v => v,
        ulong v => unchecked((long)v),
        int v => v,
        uint v => v,
        short v => v,
        ushort v => v,
        sbyte v => v,
        byte v => v,
        _ => null,
    };
}
