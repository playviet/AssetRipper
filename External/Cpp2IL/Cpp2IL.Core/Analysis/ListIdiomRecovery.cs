using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Writes the inlined body of a <see cref="System.Collections.Generic.List{T}"/> member back as the member.
///
/// <c>Count</c>, <c>Add</c> and <c>Clear</c> are small enough that il2cpp inlines them everywhere, so what a
/// method that used a list came back as was a handful of reads and writes of <c>_items</c>, <c>_size</c> and
/// <c>_version</c> - fields the runtime library keeps to itself, which a game assembly cannot name. None of
/// those statements compile, so a loop over a list lost its condition and a line that added one item lost the
/// whole method.
///
/// Each of them is recognised by the one thing in it that is still a call, or by the field it reads:
///
/// <list type="bullet">
/// <item><c>Count</c> is a read of <c>_size</c>, and the property returns exactly that field.</item>
/// <item><c>Add</c> is a test of <c>_size</c> against the array's length, with the item written straight into
/// the array on one side and <c>AddWithResize</c> called on the other.</item>
/// <item><c>Clear</c> is <c>Array.Clear(_items, 0, _size)</c> behind a test that there is anything to clear.</item>
/// </list>
///
/// Both branches of <c>Add</c> add the item and both branches of <c>Clear</c> leave the list empty, so the
/// test between them decides only which bookkeeping runs - and that bookkeeping is the list's own. Deciding
/// it one way and writing the member the whole thing is says what the source said.
/// </summary>
public static class ListIdiomRecovery
{
    private const string ListTypeName = "System.Collections.Generic.List`1";

    public static void Run(MethodAnalysisContext method)
    {
        var cfg = method.ControlFlowGraph!;
        var changed = false;
        var definitions = new Dictionary<LocalVariable, Instruction>();

        foreach (var instruction in cfg.Instructions)
            if (instruction.Destination is LocalVariable destination)
                definitions[destination] = instruction;

        foreach (var block in cfg.Blocks.ToList())
        {
            foreach (var instruction in block.Instructions.ToList())
                changed |= TryRecoverAdd(cfg, block, instruction)
                    || TryRecoverClear(cfg, block, instruction, definitions)
                    || TryRecoverEmptying(block, instruction, definitions);
        }

        //The bookkeeping the member now does is dead, and dropping it first means the reads that fed it are
        //gone before a count is put in front of every one of them.
        if (changed)
            DeadCodeEliminator.Run(cfg);

        if (RecoverCount(method))
            changed = true;
    }

    /// <summary>
    /// <c>AddWithResize</c> is only reached when the fast path cannot be taken, so it is the half of an
    /// inlined <c>Add</c> that survived as a call - and calling <c>Add</c> instead does both halves.
    /// </summary>
    private static bool TryRecoverAdd(ISILControlFlowGraph cfg, Block block, Instruction instruction)
    {
        if (instruction.OpCode is not (OpCode.Call or OpCode.CallVoid))
            return false;

        var receiverIndex = instruction.OpCode == OpCode.Call ? 2 : 1;

        if (instruction.Operands is not [MethodAnalysisContext { Name: "AddWithResize" } callee, ..]
            || instruction.Operands.Count <= receiverIndex + 1)
            return false;

        if (ListOf(callee.DeclaringType) is not { } list || Member(list, "Add", 1) is not { } add)
            return false;

        var receiver = instruction.Operands[receiverIndex];
        var item = instruction.Operands[receiverIndex + 1];

        if (receiver is not LocalVariable holder || !TakeThisWay(cfg, block))
            return false;

        instruction.OpCode = OpCode.CallVoid;
        instruction.Operands = [add, receiver, item];
        DropBookkeeping(block, holder);
        return true;
    }

    /// <summary>
    /// Clearing a list zeroes the part of the array that was in use, which is the one call the inlined body
    /// makes. What it is called on says which list, and the member says the rest.
    /// </summary>
    private static bool TryRecoverClear(ISILControlFlowGraph cfg, Block block, Instruction instruction,
        Dictionary<LocalVariable, Instruction> definitions)
    {
        if (instruction.OpCode is not OpCode.CallVoid)
            return false;

        if (instruction.Operands is not [MethodAnalysisContext { Name: "Clear", DeclaringType.FullName: "System.Array" }, var items, var from, _])
            return false;

        //The count it clears is whatever the body last read out of _size, which may be a copy by now - the
        //array it clears is what says which list this is, and clearing from anywhere but the front is not
        //something the member does.
        if (ArrayOfList(items, definitions) is not { } holder || !IsZero(from))
            return false;

        if (ListOf(holder.Type) is not { } list || Member(list, "Clear", 0) is not { } clear)
            return false;

        if (!TakeThisWay(cfg, block))
            return false;

        instruction.Operands = [clear, holder];
        DropBookkeeping(block, holder);
        return true;
    }

    /// <summary>
    /// Clearing a list of something with no references in it does not zero the array at all - there is
    /// nothing in there for the collector to let go of - so all that is left of <c>Clear</c> is the count
    /// going to zero and the version going up. There is no call in it to recognise it by, so the pair of
    /// writes is what says so: nothing else sets a list's count to nothing.
    /// </summary>
    private static bool TryRecoverEmptying(Block block, Instruction instruction, Dictionary<LocalVariable, Instruction> definitions)
    {
        if (!WritesOperandZero(instruction.OpCode) || instruction.Operands.Count != 2)
            return false;

        if (instruction.Operands[0] is not FieldReference { Field.Name: "_size", Local: { } holder } || !IsZeroValue(instruction.Operands[1], definitions))
            return false;

        if (ListOf(holder.Type) is not { } list || Member(list, "Clear", 0) is not { } clear)
            return false;

        //The version has to go up in the same breath, or this is something else writing a count.
        var bumped = block.Instructions.Any(other => WritesOperandZero(other.OpCode) && other.Operands.Count > 0
            && other.Operands[0] is FieldReference { Field.Name: "_version", Local: { } versioned }
            && ReferenceEquals(versioned, holder));

        if (!bumped)
            return false;

        instruction.OpCode = OpCode.CallVoid;
        instruction.Operands = [clear, holder];
        DropBookkeeping(block, holder);
        return true;
    }

    /// <summary>
    /// Reading <c>_size</c> is what <c>Count</c> does, and the property is the only way to say it from
    /// outside the library. The read is left where it was, with a call put in front of it.
    /// </summary>
    private static bool RecoverCount(MethodAnalysisContext method)
    {
        var integer = method.AppContext.SystemTypes.SystemInt32Type;
        var changed = false;

        foreach (var block in method.ControlFlowGraph!.Blocks)
        {
            for (var i = 0; i < block.Instructions.Count; i++)
            {
                var instruction = block.Instructions[i];

                //A write to the field is not a read of the property.
                var first = WritesOperandZero(instruction.OpCode) ? 1 : 0;

                for (var operand = first; operand < instruction.Operands.Count; operand++)
                {
                    if (instruction.Operands[operand] is not FieldReference { Field.Name: "_size", Local: { } holder })
                        continue;

                    if (ListOf(holder.Type) is not { } list || Member(list, "get_Count", 0) is not { } count)
                        continue;

                    var value = new LocalVariable($"count{method.Locals.Count}", new Register(null, "COUNT"), integer);
                    method.Locals.Add(value);

                    block.Instructions.Insert(i, new Instruction(instruction.Index, OpCode.Call, count, value, holder));
                    instruction.Operands[operand] = value;
                    i++;
                    changed = true;
                }
            }
        }

        return changed;
    }

    /// <summary>
    /// Decides the test in front of the block, so that the path the recovered member is on is the one taken.
    /// The other side is then reached from nowhere and goes with the blocks that lead only to it.
    /// </summary>
    private static bool TakeThisWay(ISILControlFlowGraph cfg, Block block)
    {
        if (block.Predecessors.Count != 1)
            return false;

        var guard = block.Predecessors[0];

        if (guard.BlockType != BlockType.TwoWay || guard.Instructions.Count == 0)
            return false;

        if (guard.Instructions[^1].OpCode != OpCode.ConditionalJump)
            return false;

        if (guard.Successors.FirstOrDefault(successor => !ReferenceEquals(successor, block)) is not { } other)
            return false;

        guard.Instructions[^1].OpCode = OpCode.Jump;
        guard.Instructions[^1].Operands = [block];
        guard.Successors.Remove(other);
        other.Predecessors.Remove(guard);
        guard.CalculateBlockType();

        Prune(cfg, other);
        return true;
    }

    /// <summary>
    /// Drops the writes to the list's own fields that led up to the recovered member, since the member does
    /// them itself. What fed them is then read by nothing and goes with the rest of the dead code.
    /// </summary>
    private static void DropBookkeeping(Block block, LocalVariable holder)
    {
        var current = block;

        for (var depth = 0; depth < 4 && current != null; depth++)
        {
            foreach (var instruction in current.Instructions)
            {
                if (WritesOperandZero(instruction.OpCode) && instruction.Operands.Count > 0
                    && instruction.Operands[0] is FieldReference { Field.Name: "_size" or "_version" or "_items", Local: { } written }
                    && ReferenceEquals(written, holder))
                {
                    instruction.OpCode = OpCode.Nop;
                    instruction.Operands = [];
                    continue;
                }

                //And the null check arm64 puts in front of the length read. Only the writes were being taken,
                //so `if (list._items == null)` survived - a private field of the runtime library that a game
                //assembly cannot name, commented out, taking the recovered `Add` inside it along. A live list
                //always has its storage, so the question has no answer to keep.
                if (instruction is { OpCode: OpCode.CheckEqual or OpCode.CheckNotEqual, Operands: [_, FieldReference { Field.Name: "_items", Local: { } read }, { } against] }
                    && ReferenceEquals(read, holder)
                    && IsZero(against))
                {
                    instruction.OpCode = OpCode.Nop;
                    instruction.Operands = [];
                }
            }

            current = current.Predecessors.Count == 1 ? current.Predecessors[0] : null;
        }
    }

    /// <summary>
    /// Whether the instruction writes its first operand. A store to a field is one of these, but it is not
    /// reported as a destination - only a value a local can hold is - so the operand has to be read directly.
    /// </summary>
    private static bool WritesOperandZero(OpCode opCode)
        => opCode is OpCode.Move or OpCode.Phi or OpCode.Add or OpCode.Subtract or OpCode.Multiply
            or OpCode.Divide or OpCode.ShiftLeft or OpCode.ShiftRight or OpCode.And or OpCode.Or
            or OpCode.Xor or OpCode.Not or OpCode.Negate or OpCode.Newobj or OpCode.Select
            or (>= OpCode.CheckEqual and <= OpCode.CheckLessOrEqual);

    /// <summary>Drops a block nothing reaches any more, and anything that only it reached.</summary>
    private static void Prune(ISILControlFlowGraph cfg, Block block)
    {
        var queue = new Queue<Block>();
        queue.Enqueue(block);

        while (queue.Count > 0)
        {
            var unreachable = queue.Dequeue();

            if (unreachable.Predecessors.Count > 0 || unreachable == cfg.EntryBlock || unreachable == cfg.ExitBlock)
                continue;

            foreach (var successor in unreachable.Successors.ToList())
            {
                successor.Predecessors.Remove(unreachable);
                queue.Enqueue(successor);
            }

            unreachable.Successors.Clear();
            unreachable.Instructions.Clear();
            cfg.Blocks.Remove(unreachable);
        }
    }

    /// <summary>
    /// Whether a value is zero, following the copies that may stand between it and the constant.
    /// </summary>
    /// <remarks>
    /// The count going to nothing is what says a list was emptied, but the zero does not always reach the
    /// write as a literal: it is often held in a register first, because the same zero is written to more
    /// than one place. Insisting on the literal missed those, and what was left of <c>Clear</c> was two
    /// writes to fields the runtime library keeps to itself - which cannot be written down at all, so the
    /// whole statement, and everything the decompiler had built around it, was dropped.
    /// </remarks>
    private static bool IsZeroValue(object operand, Dictionary<LocalVariable, Instruction> definitions)
    {
        for (var steps = 0; steps < 4; steps++)
        {
            if (IsZero(operand))
                return true;

            if (operand is not LocalVariable local
                || definitions.GetValueOrDefault(local) is not { OpCode: OpCode.Move, Operands: [_, { } source] })
                return false;

            operand = source;
        }

        return false;
    }

    private static bool IsZero(object operand)
        => operand switch
        {
            int i => i == 0, uint ui => ui == 0, long l => l == 0, ulong ul => ul == 0,
            short s => s == 0, ushort us => us == 0, byte b => b == 0, sbyte sb => sb == 0,
            _ => false,
        };

    /// <summary>
    /// The list whose backing array a value holds, whether the read of <c>_items</c> is the operand itself or
    /// the definition of the local standing in for it - which is what it is until a later pass names the field
    /// at the one place that reads it.
    /// </summary>
    private static LocalVariable? ArrayOfList(object operand, Dictionary<LocalVariable, Instruction> definitions)
    {
        if (operand is FieldReference { Field.Name: "_items", Local: { } named })
            return named;

        if (operand is LocalVariable local && definitions.TryGetValue(local, out var definition)
            && definition is { OpCode: OpCode.Move, Operands: [_, FieldReference { Field.Name: "_items", Local: { } holder }] })
            return holder;

        return null;
    }

    /// <summary>The list a value is an instance of, if that is what it is.</summary>
    private static GenericInstanceTypeAnalysisContext? ListOf(TypeAnalysisContext? type)
        => type is GenericInstanceTypeAnalysisContext instance && instance.GenericType.FullName == ListTypeName
            ? instance
            : null;

    /// <summary>The list member of that name, named at the instantiation the value has.</summary>
    private static MethodAnalysisContext? Member(GenericInstanceTypeAnalysisContext list, string name, int parameters)
    {
        if (list.GenericType.Methods.FirstOrDefault(m => m.Name == name && m.Parameters.Count == parameters) is not { } member)
            return null;

        return new ConcreteGenericMethodAnalysisContext(member, list.GenericArguments, []);
    }
}
