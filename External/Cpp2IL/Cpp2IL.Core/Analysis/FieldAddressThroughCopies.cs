using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Names a field whose address was copied about before it was used as one.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="FieldAddressRecovery"/> asks that every use of <c>this + 16</c> be a place an address belongs -
/// the receiver of a call on a value type, a by-reference parameter, a read at distance nought - and that is
/// right. What it misses is that the address often reaches those places through a register or two first:
/// <b>62 of the 77 additions it declines in <c>Assembly-CSharp</c> are declined at a plain copy.</b>
/// </para>
/// <code>
/// Add  v195, this, 16
/// Call Int32.ToString, v145, v195          // `_gameStartCount.ToString()`
/// </code>
/// <para>
/// which is the whole of <c>BaseTrackingSaveData::AppendSnapshotFields</c> - forty lines of it commented out
/// behind one address the analysis could not name. The receiver of a call on a value type is exactly where
/// <c>ldflda</c> belongs, and <see cref="IlGenerator"/> already writes it once the operand is a field.
/// </para>
/// <para>
/// The chain has to be closed: every member is defined only by copies from other members or by the addition
/// itself, and every use of every member is a copy, a place an address belongs, or a read at distance
/// nought. One use that wants it for anything else and the whole chain is left alone, because then the
/// address really is the value.
/// </para>
/// <para>
/// <b>At least one use has to want the address.</b> A chain that is only ever read through belongs to
/// <see cref="FieldAddressSinking"/>, which turns it into the field's value - better, where the field holds
/// a reference, than an address nothing needs.
/// </para>
/// </remarks>
public static class FieldAddressThroughCopies
{
    public static bool Run(MethodAnalysisContext method)
    {
        if (method.ControlFlowGraph is not { } graph)
            return false;

        var header = method.AppContext.Binary.is32Bit ? 8 : 0x10;
        var changed = false;

        foreach (var addition in graph.Instructions.ToList())
        {
            if (addition.OpCode != OpCode.Add || addition.Operands.Count != 3
                || addition.Operands[0] is not LocalVariable made
                || addition.Operands[1] is not LocalVariable { Type: { } owner } held
                || owner.IsEnumType
                || (owner.IsValueType && (owner.Namespace == nameof(System) || !owner.Fields.Any(f => !f.IsStatic)))
                || addition.Operands[2] is not (long or int or ulong or uint))
                continue;

            var offset = System.Convert.ToInt64(addition.Operands[2]);

            if ((FieldAt(owner, offset, header)
                    ?? GenericFieldLayout.FieldAt(owner, offset, method.AppContext, header)) is not { } field)
            {
                Trace("noFieldAt " + offset + " in " + owner.FullName);
                continue;
            }

            if (Chain(graph, addition, made) is not { } chain)
            {
                Trace("noChain " + addition);
                continue;
            }

            foreach (var (instruction, operand) in chain)
                instruction.Operands[operand] = new FieldReference(field, held, (int)offset);

            addition.OpCode = OpCode.Nop;
            addition.Operands = [];
            changed = true;
        }

        return changed;
    }

    private static void Trace(string why)
    {
        if (System.Environment.GetEnvironmentVariable("COPY_TRACE") == "1")
            System.Console.Error.WriteLine("COPY " + why);
    }

    /// <summary>The instance field lying at that distance into the type, if one does.</summary>
    private static FieldAnalysisContext? FieldAt(TypeAnalysisContext owner, long offset, int header)
    {
        //A struct's fields are recorded from its own start, so the addend is the recorded offset for both
        //kinds - see FieldAddressRecovery.FieldAt, where adding the header was wrong and untested.
        var wanted = offset;

        return owner.Fields.FirstOrDefault(f => !f.IsStatic && f.BackingData?.FieldOffset == wanted);
    }

    /// <summary>
    /// Every place the address ends up being used as one, where nothing along the way wants it for
    /// anything else. Null where any use is something this cannot speak for.
    /// </summary>
    private static List<(Instruction At, int Operand)>? Chain(ISILControlFlowGraph graph, Instruction addition, LocalVariable start)
    {
        var members = new HashSet<LocalVariable> { start };
        var uses = new List<(Instruction, int)>();
        var wanted = 0;
        var pending = new Queue<LocalVariable>();
        pending.Enqueue(start);

        while (pending.Count > 0)
        {
            var carried = pending.Dequeue();

            foreach (var instruction in graph.Instructions)
            {
                if (ReferenceEquals(instruction, addition))
                    continue;

                var addresses = AddressPositions(instruction);

                for (var operand = 0; operand < instruction.Operands.Count; operand++)
                {
                    switch (instruction.Operands[operand])
                    {
                        case LocalVariable other when ReferenceEquals(other, carried):
                            if (instruction.OpCode is OpCode.Move or OpCode.Select)
                            {
                                //Written here, so this is one of the places the member is given a value and
                                //every source has to be one of the chain's own - otherwise the local holds
                                //an address on one path and something else on another.
                                if (operand == 0)
                                {
                                    for (var source = instruction.OpCode == OpCode.Select ? 2 : 1; source < instruction.Operands.Count; source++)
                                        if (instruction.Operands[source] is not LocalVariable feeding || !members.Contains(feeding))
                                        {
                                            Trace("definedElsewhere " + instruction);
                                            return null;
                                        }

                                    continue;
                                }

                                if (instruction.Operands[0] is not LocalVariable destination)
                                    return null;

                                if (members.Add(destination))
                                    pending.Enqueue(destination);

                                continue;
                            }

                            if (!addresses.Contains(operand))
                            {
                                Trace($"notAnAddressPosition op{operand} " + instruction);
                                return null;
                            }

                            uses.Add((instruction, operand));
                            wanted++;
                            continue;

                        //Read through it, at no further distance. Writing through it is a store to the
                        //field, which is a different shape and is left to the pass that knows it.
                        case MemoryOperand { Index: null, Scale: 0, Addend: 0, Base: { } through }
                            when ReferenceEquals(through, carried):

                            if (operand == 0 && instruction.IsAssignment)
                                return null;

                            uses.Add((instruction, operand));
                            continue;

                        case MemoryOperand memory when ReferenceEquals(memory.Base, carried) || ReferenceEquals(memory.Index, carried):
                            return null;

                        case FieldReference reference when ReferenceEquals(reference.Local, carried):
                            return null;
                    }
                }
            }
        }

        //Nothing here wanted the address, so this is a chain of reads and belongs to the sinking pass.
        return wanted == 0 || uses.Count == 0 ? null : uses;
    }

    /// <summary>
    /// Which operands of a call are places an address belongs - the same question
    /// <see cref="FieldAddressRecovery"/> asks, kept here so the upstream method keeps its own shape.
    /// </summary>
    private static HashSet<int> AddressPositions(Instruction instruction)
    {
        var positions = new HashSet<int>();

        if (!instruction.IsCall || instruction.Operands.Count == 0
            || instruction.Operands[0] is not MethodAnalysisContext callee)
            return positions;

        var receiver = instruction.OpCode == OpCode.Call ? 2 : 1;

        //A method on a value type is called on the address of one.
        if (!callee.IsStatic && callee.DeclaringType is { IsValueType: true })
            positions.Add(receiver);

        var first = receiver + (callee.IsStatic ? 0 : 1);

        for (var i = 0; i < callee.Parameters.Count && first + i < instruction.Operands.Count; i++)
            if (callee.Parameters[i].ParameterType is ByRefTypeAnalysisContext)
                positions.Add(first + i);

        return positions;
    }
}
