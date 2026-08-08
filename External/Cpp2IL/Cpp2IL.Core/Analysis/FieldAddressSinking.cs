using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Reads the field where the address of one is only ever copied about and then read through.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="FieldAddressRecovery"/> names <c>this + 184</c> as a field wherever the address itself is what
/// the program wanted. Where it is not - where the compiler works out several field addresses, chooses
/// between them and only then loads - no single field reference can speak for the choice, and the whole
/// chain is written out as arithmetic on <c>this</c>:
/// </para>
/// <code>
/// //obj  = this + 184L;          //obj2 = this + 176L;          //obj3 = this + 168L;
/// //object obj4 = obj2;          //obj4 = obj;                  //obj4 = obj3;
/// //IAdsNetwork adsNetwork = (IAdsNetwork)obj4;
/// </code>
/// <para>
/// Nothing there is unresolved: the fields are known and the choice is ordinary. What cannot be written is
/// the address. So the load is moved to where the addresses are made - each becomes the field's own
/// **value**, and the choice picks between values, which is what the source said. Reading a field of
/// <c>this</c> has no side effect, so doing all of them and choosing afterwards computes the same answer.
/// </para>
/// <para>
/// Every producer feeding the chain has to be a field address this can rewrite, and every use of it a copy
/// or a read at distance nought. One use that wants the address for anything else - a by-reference argument,
/// a call receiver, a further offset - and the chain is left exactly as it was, because then the address
/// really is the value.
/// </para>
/// </remarks>
public static class FieldAddressSinking
{
    public static bool Run(MethodAnalysisContext method)
    {
        if (method.ControlFlowGraph is not { } graph)
            return false;

        var header = method.AppContext.Binary.is32Bit ? 8 : 0x10;
        var addresses = new Dictionary<LocalVariable, (Instruction Addition, FieldAnalysisContext Field, LocalVariable Owner, int Offset)>();

        foreach (var instruction in graph.Instructions)
        {
            if (instruction.OpCode != OpCode.Add || instruction.Operands.Count != 3
                || instruction.Operands[0] is not LocalVariable made
                || instruction.Operands[1] is not LocalVariable { Type: { IsValueType: false } owner } held
                || instruction.Operands[2] is not (long or int or ulong or uint))
                continue;

            var offset = System.Convert.ToInt64(instruction.Operands[2]);

            if (FieldAt(owner, offset, header) is { } field)
                addresses[made] = (instruction, field, held, (int)offset);
        }

        if (addresses.Count == 0)
            return false;

        var changed = false;

        foreach (var start in addresses.Keys.ToList())
        {
            if (!addresses.ContainsKey(start) || Chain(graph, start, addresses) is not { } chain)
                continue;

            foreach (var (instruction, operand) in chain.Reads)
                instruction.Operands[operand] = chain.Through[instruction] is { } local ? local : instruction.Operands[operand];

            foreach (var carried in chain.Members)
            {
                if (!addresses.TryGetValue(carried, out var address))
                    continue;

                address.Addition.OpCode = OpCode.Move;
                address.Addition.Operands = [carried, new FieldReference(address.Field, address.Owner, address.Offset)];
                addresses.Remove(carried);
            }

            changed = true;
        }

        return changed;
    }

    /// <summary>
    /// The instance field lying at that distance into the type, if one does and it holds a reference.
    /// </summary>
    /// <remarks>
    /// Only a reference field may be read this way. Its value is exactly one machine word, so a read at
    /// distance nought through its address is the field and nothing else. A value-type field's address is a
    /// by-reference, and a read at nought through it takes only the front of the struct - so sinking the load
    /// would hand back the whole of <c>_dragOffset</c> where a single float was wanted, and a write through
    /// it would be lost. Those belong to the <c>ldflda</c> family, which is a different question.
    /// </remarks>
    private static FieldAnalysisContext? FieldAt(TypeAnalysisContext owner, long offset, int header)
    {
        var wanted = owner.IsValueType ? offset + header : offset;

        return owner.Fields.FirstOrDefault(f => !f.IsStatic && f.BackingData?.FieldOffset == wanted
            && f.FieldType is { IsValueType: false });
    }

    /// <summary>
    /// Everything the address flows into, where every step is a copy and every end is a read at nought.
    /// </summary>
    private static (HashSet<LocalVariable> Members, List<(Instruction, int)> Reads, Dictionary<Instruction, LocalVariable?> Through)? Chain(
        ISILControlFlowGraph graph, LocalVariable start,
        Dictionary<LocalVariable, (Instruction Addition, FieldAnalysisContext Field, LocalVariable Owner, int Offset)> addresses)
    {
        var members = new HashSet<LocalVariable> { start };
        var reads = new List<(Instruction, int)>();
        var through = new Dictionary<Instruction, LocalVariable?>();
        var pending = new Queue<LocalVariable>();
        pending.Enqueue(start);

        while (pending.Count > 0)
        {
            var carried = pending.Dequeue();
            var defined = addresses.ContainsKey(carried);

            foreach (var instruction in graph.Instructions)
            {
                //The addition that made this member is its definition, and one this pass rewrites.
                if (addresses.TryGetValue(carried, out var own) && ReferenceEquals(instruction, own.Addition))
                    continue;

                for (var operand = 0; operand < instruction.Operands.Count; operand++)
                {
                    switch (instruction.Operands[operand])
                    {
                        case LocalVariable other when ReferenceEquals(other, carried):
                            if (instruction.OpCode is not (OpCode.Move or OpCode.Select))
                                return Refuse("notACopy " + instruction);

                            if (instruction.OpCode == OpCode.Select && operand == 1)
                                return Refuse("usedAsCondition " + instruction);

                            //Written here: this is one of the places the member is given a value, so every
                            //source joins the chain and has to answer for itself in turn. Walking the
                            //definitions as well as the uses is what keeps one local from holding an address
                            //on one path and a field's value on another.
                            if (operand == 0)
                                defined = true;
                            else if (instruction.Operands[0] is not LocalVariable destination)
                                return Refuse("noDestination " + instruction);
                            else if (members.Add(destination))
                                pending.Enqueue(destination);

                            for (var source = instruction.OpCode == OpCode.Select ? 2 : 1; source < instruction.Operands.Count; source++)
                            {
                                if (instruction.Operands[source] is not LocalVariable feeding)
                                    return Refuse("sourceNotALocal " + instruction);

                                if (members.Add(feeding))
                                    pending.Enqueue(feeding);
                            }

                            continue;

                        //The read the whole chain was for.
                        case MemoryOperand { Index: null, Scale: 0, Addend: 0, Base: LocalVariable through2 }
                            when ReferenceEquals(through2, carried):

                            if (operand == 0 && instruction.IsAssignment)
                                return Refuse("writtenThrough " + instruction);

                            reads.Add((instruction, operand));
                            through[instruction] = carried;
                            continue;

                        case MemoryOperand memory when ReferenceEquals(memory.Base, carried) || ReferenceEquals(memory.Index, carried):
                            return Refuse("otherMemoryUse " + instruction);
                    }
                }
            }

            //A member nothing in the chain defines holds whatever it was handed - a call's result, a
            //parameter - and that is not an address this may read through.
            if (!defined)
                return Refuse("definedElsewhere " + carried);
        }

        if (reads.Count == 0)
            return Refuse("noRead");

        return (members, reads, through);
    }

    private static (HashSet<LocalVariable>, List<(Instruction, int)>, Dictionary<Instruction, LocalVariable?>)? Refuse(string why)
    {
        if (System.Environment.GetEnvironmentVariable("SINK_TRACE") == "1")
            System.Console.Error.WriteLine("SINK " + why);
        return null;
    }
}
