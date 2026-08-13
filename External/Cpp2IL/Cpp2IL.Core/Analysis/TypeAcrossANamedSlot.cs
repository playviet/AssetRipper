using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Carries a type across the copies that naming a frame slot creates, which nothing else ever sees.
/// </summary>
/// <remarks>
/// <para>
/// A value spilled to the frame and read back is <c>ldr x23, [x29, #-0x10]</c> - a memory operand, not a
/// copy - so while the type fixpoint was running there was no <c>Move a, b</c> for
/// <see cref="LocalVariables"/> to carry a type across. <see cref="SlotAddressRead"/> turns that read into
/// exactly such a copy from the named slot, and it runs <b>after</b>
/// <c>LocalVariables.ResolveTypesAndFields</c> - so the local on the receiving end is left with no type at
/// all, however well the slot itself is known:
/// </para>
/// <code>
/// Move stack_-10 (T[]), values (T[])   // the spill, typed
/// CallVoid Array.Resize, stack_-10, n
/// Move v75, stack_-10 (T[])            // the read back - and v75 has no type
/// CheckEqual v117, [v75 + 0x18], 0     // so this is a poke at unmanaged memory
/// </code>
/// <para>
/// <c>ArrayExtension::Add</c> is the whole of it: the array comes back from <c>Array.Resize</c> untyped, so
/// its length is read as a distance into nothing and the store that follows is not an element store.
/// </para>
/// <para>
/// The rule is the one <c>LocalVariables.PropagateMove</c> already applies and in the same direction - both
/// ways across a copy between two locals, never overwriting a type that is already there, so it stays
/// monotone and settles. It is only applied again here, to instructions that did not exist when that ran.
/// One side must be a named slot: this is not a general re-run of type propagation, and widening it to every
/// copy in the body would put a second, later opinion beside the fixpoint's, which is
/// <see cref="LocalVariables.SetTypeFromParameter">the mistake</see> that has been paid for before.
/// </para>
/// </remarks>
public static class TypeAcrossANamedSlot
{
    public static void Run(MethodAnalysisContext method)
    {
        if (method.ControlFlowGraph is not { } graph)
            return;

        var trace = System.Environment.GetEnvironmentVariable("SLOTTYPE_TRACE") is not null;

        //A chain of them - a slot read into a register that is then copied on - needs as many sweeps as it is
        //long. It settles because nothing here ever replaces a type, so a sweep that changes nothing is the
        //end of it.
        for (var sweep = 0; sweep < 8; sweep++)
        {
            var changed = false;

            foreach (var instruction in graph.Instructions)
            {
                if (instruction is not { OpCode: OpCode.Move, Operands: [LocalVariable left, LocalVariable right] })
                    continue;

                if (!IsANamedSlot(left) && !IsANamedSlot(right))
                    continue;

                if (left.Type is null && right.Type is not null)
                {
                    left.Type = right.Type;
                    changed = true;

                    if (trace)
                        System.Console.Error.WriteLine($"SLOTTYPE {method.DeclaringType?.Name}::{method.Name} {left.Name} <- {right.Register.Name} {right.Type.FullName}");
                }
                else if (right.Type is null && left.Type is not null)
                {
                    right.Type = left.Type;
                    changed = true;

                    if (trace)
                        System.Console.Error.WriteLine($"SLOTTYPE {method.DeclaringType?.Name}::{method.Name} {right.Name} <- {left.Register.Name} {left.Type.FullName}");
                }
            }

            if (!changed)
                return;
        }
    }

    /// <summary>Whether the local is one of the frame's slots, under either of the two prefixes.</summary>
    private static bool IsANamedSlot(LocalVariable local)
        => local.Register.Name.StartsWith(StackSlots.ValuePrefix) || local.Register.Name.StartsWith(StackSlots.AddressPrefix);
}
