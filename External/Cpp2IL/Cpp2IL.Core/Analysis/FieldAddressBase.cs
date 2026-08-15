using System;
using System.Collections.Generic;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Gives an address built on a field a local to rest on.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="FieldAddressRecovery"/> and <see cref="FieldAddressSinking"/> both read an address as
/// <c>Add made, &lt;local of known type&gt;, constant</c>. Copy propagation often folds the field straight
/// into the addition instead - <c>Add v104, this._settings (BoardSettingSO), 160</c> - and then neither pass
/// recognises it, though it is the same address.
/// </para>
/// <para>
/// One arm of a four-way choice being folded that way was enough to refuse the whole chain: the walk reaches
/// that member, finds its definition is an addition rather than a copy, and gives up at <c>notACopy</c>,
/// taking all four arms and the forty statements behind them. <c>SubCellVisual::ApplyFaceParts</c> is the
/// case; <c>CellDraggable::BeginDragInternal</c> is named in the sinking pass's own remarks.
/// </para>
/// <para>
/// So the fold is undone rather than taught to both passes: the field is read into a local of its own and the
/// addition rests on that. Reading a field has no side effect, and if nothing wants the address after all,
/// the copy is collected with everything else.
/// </para>
/// </remarks>
public static class FieldAddressBase
{
    public static void Run(MethodAnalysisContext method)
    {
        if (method.ControlFlowGraph is not { } graph)
            return;

        RestOnAHolder(graph);

        foreach (var block in graph.Blocks)
        {
            for (var at = 0; at < block.Instructions.Count; at++)
            {
                if (block.Instructions[at] is not { OpCode: OpCode.Add, Operands: [LocalVariable, FieldReference held, { } offset] })
                    continue;

                //Only where the two passes below would have taken it: a real distance into something that has
                //members to be at that distance.
                if (!IsAnOffset(offset)
                    || held.Field.FieldType is not { IsEnumType: false } owner
                    || (owner.IsValueType && owner.Namespace == nameof(System)))
                {
                    continue;
                }

                var rested = new LocalVariable($"base{method.Locals.Count}", new Register(null, "BASE"), owner);
                method.Locals.Add(rested);

                block.Instructions.Insert(at, new Instruction(block.Instructions[at].Index, OpCode.Move, rested, held));
                block.Instructions[at + 1].Operands[1] = rested;
                at++;
            }
        }
    }

    /// <summary>
    /// Rests an address worked out from a <em>read</em> on a local that already holds that read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The other half of the same fold. Copy propagation puts an array element straight into the addition -
    /// <c>Add v482, [words + 0x20 + i*8], 16</c> - and the two field passes both want a local of a known type
    /// on the left, so the chain is refused and the address is written out as <c>words[i] + 16L</c>.
    /// <c>StringExtension::PrepareTextForBubble</c> keeps seven statements behind exactly that.
    /// </para>
    /// <para>
    /// <b>Nothing is read twice and nothing is hoisted.</b> The compiler has invariably put the element in a
    /// register of its own first - it is about to call methods on it - so the local is there to be found, and
    /// this only points the addition at it. Where no local holds the read the addition is left alone, rather
    /// than inserting a second read whose position would have to be argued about
    /// ([[il2cpp-hoisting-a-read-handed-it-to-the-remover]]).
    /// </para>
    /// </remarks>
    private static void RestOnAHolder(Graphs.ISILControlFlowGraph graph)
    {
        var order = new Dictionary<Instruction, int>();
        var holders = new Dictionary<Instruction, (MemoryOperand Read, LocalVariable Local)>();
        var position = 0;

        foreach (var instruction in graph.Instructions)
        {
            order[instruction] = position++;

            if (instruction is { OpCode: OpCode.Move, Operands: [LocalVariable { Type: not null } into, MemoryOperand read] })
                holders[instruction] = (read, into);
        }

        foreach (var instruction in graph.Instructions)
        {
            if (instruction is not { OpCode: OpCode.Add, Operands: [LocalVariable, MemoryOperand element, { } offset] }
                || !IsAnOffset(offset))
                continue;

            foreach (var (made, holder) in holders)
            {
                //Only a holder the method has already filled, so the value is the one this address is into.
                if (order[made] > order[instruction] || !SameRead(holder.Read, element))
                    continue;

                instruction.Operands[1] = holder.Local;
                break;
            }
        }
    }

    private static bool SameRead(MemoryOperand one, MemoryOperand other)
        => ReferenceEquals(one.Base, other.Base) && ReferenceEquals(one.Index, other.Index)
            && one.Addend == other.Addend && one.Scale == other.Scale;

    private static bool IsAnOffset(object operand)
    {
        try
        {
            return operand is not (string or LocalVariable or Register or MemoryOperand)
                && Convert.ToInt64(operand) > 0;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
