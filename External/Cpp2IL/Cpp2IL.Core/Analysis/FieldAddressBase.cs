using System;
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
