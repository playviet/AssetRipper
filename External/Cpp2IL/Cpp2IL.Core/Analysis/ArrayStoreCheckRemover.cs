using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Extensions;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Removes the check the runtime makes before storing a reference into an array.
/// </summary>
/// <remarks>
/// An array of references can be handed out as an array of anything it derives from, so storing into one has
/// to ask whether the value really fits before it is written. The language makes that promise itself and says
/// nothing about it - <c>a[i] = x</c> is the whole statement - but il2cpp writes the question out at the store:
/// it takes the array's class, reads the element class out of it, calls a helper with the value, and throws
/// where the answer is no.
///
/// None of that names a method, so the helper stays an address and the statement holding it is lost - and the
/// store it guards goes with it. It is one of the most common things left in this game, because everything that
/// puts a value into an <c>object[]</c> has one: every interpolated string, every params call.
///
/// The check is recognised by what it reads rather than by the helper, which has no name to go on: the second
/// argument is the element class of an array, taken out of the array's own class, and the answer is only ever
/// used to decide whether to throw. Nothing else has that shape.
/// </remarks>
public static class ArrayStoreCheckRemover
{
    /// <summary>Where a class records the class of the elements it holds, when it is an array.</summary>
    private const long ElementClassOffset = 0x40;

    public static bool Run(MethodAnalysisContext method)
    {
        if (method.AppContext.Binary.is32Bit)
            return false;

        var definitions = new Dictionary<LocalVariable, Instruction>();

        foreach (var instruction in method.ControlFlowGraph!.Instructions)
            if (instruction.Destination is LocalVariable destination)
                definitions[destination] = instruction;

        var changed = false;

        foreach (var block in method.ControlFlowGraph.Blocks.ToList())
        {
            if (block.BlockType != BlockType.TwoWay || block.Successors.Count != 2
                || block.Instructions.Count == 0 || block.Instructions[^1] is not { OpCode: OpCode.ConditionalJump } branch)
                continue;

            if (Check(block, definitions) is not { } check)
                continue;

            //The branch is taken when the answer is no, which is the path that throws. The value really is
            //going into the array, so what is left is the store, which is already there.
            if (branch.Operands is not [Block refused, _] || !ReferenceEquals(refused, block.Successors[0]) && !ReferenceEquals(refused, block.Successors[1]))
                continue;

            var kept = ReferenceEquals(refused, block.Successors[0]) ? block.Successors[1] : block.Successors[0];

            check.OpCode = OpCode.Nop;
            check.Operands = [];

            refused.Predecessors.Remove(block);
            block.Successors.Remove(refused);
            branch.OpCode = OpCode.Jump;
            branch.Operands = [kept];
            block.CalculateBlockType();

            changed = true;
        }

        return changed;
    }

    /// <summary>
    /// The call that asks whether the value fits, where the block is one asking it.
    /// </summary>
    private static Instruction? Check(Block block, Dictionary<LocalVariable, Instruction> definitions)
    {
        foreach (var instruction in block.Instructions)
        {
            //Still an address: a call that resolved to a method is something the program does, not a check.
            if (instruction.OpCode != OpCode.Call || instruction.Operands.Count < 4 || !instruction.Operands[0].IsNumeric())
                continue;

            if (instruction.Operands[1] is not LocalVariable answer || instruction.Operands[3] is not LocalVariable elementClass)
                continue;

            //The element class is read out of the class of the array being stored into, and nothing else is.
            if (definitions.GetValueOrDefault(elementClass) is not
                    { OpCode: OpCode.Move, Operands: [_, MemoryOperand { Index: null, Scale: 0, Base: LocalVariable arrayClass } read] }
                || read.Addend != ElementClassOffset)
                continue;

            if (definitions.GetValueOrDefault(arrayClass) is not
                { OpCode: OpCode.Move, Operands: [_, MemoryOperand { Index: null, Scale: 0, Addend: 0, Base: LocalVariable }] })
                continue;

            //The answer decides one thing only: whether to throw.
            if (!block.Instructions.Any(other => other.OpCode is OpCode.CheckEqual or OpCode.CheckNotEqual
                    && other.Operands.Count > 2 && ReferenceEquals(other.Operands[1], answer) && IsZero(other.Operands[2])))
                continue;

            return instruction;
        }

        return null;
    }

    private static bool IsZero(object operand)
        => operand switch
        {
            int i => i == 0, uint ui => ui == 0, long l => l == 0, ulong ul => ul == 0,
            _ => false,
        };
}
