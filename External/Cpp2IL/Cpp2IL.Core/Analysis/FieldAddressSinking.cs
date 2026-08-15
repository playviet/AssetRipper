using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using LibCpp2IL.BinaryStructures;

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
        var addresses = new Dictionary<LocalVariable, (Instruction Addition, FieldAnalysisContext Field, LocalVariable Owner, int Offset, FieldAnalysisContext? Outer, int OuterOffset)>();

        foreach (var instruction in graph.Instructions)
        {
            //A second field's address worked out from the first, which is what the compiler writes when two
            //fields are near each other: `&CampaignName` is `this + 56` and `&UtmCampaign` is that less 24,
            //never `this + 32`. Only an address this pass already knows, so the owner and the arithmetic are
            //both settled - and instructions are walked in order, so the first is always registered first.
            if (instruction.OpCode is OpCode.Add or OpCode.Subtract && instruction.Operands.Count == 3
                && instruction.Operands[0] is LocalVariable derived
                && instruction.Operands[1] is LocalVariable from && addresses.TryGetValue(from, out var already)
                && instruction.Operands[2] is (long or int or ulong or uint) && already.Owner.Type is { } owns)
            {
                var away = System.Convert.ToInt64(instruction.Operands[2]);
                var moved = already.Offset + (instruction.OpCode == OpCode.Add ? away : -away);

                if (FieldAt(owns, moved, header) is { } alongside)
                {
                    addresses[derived] = (instruction, alongside, already.Owner, (int)moved, null, 0);
                    continue;
                }
            }

            if (instruction.OpCode != OpCode.Add || instruction.Operands.Count != 3
                || instruction.Operands[0] is not LocalVariable made
                || instruction.Operands[1] is not LocalVariable { Type: { } owner } held
                || owner.IsEnumType
                || (owner.IsValueType && (owner.Namespace == nameof(System) || !owner.Fields.Any(f => !f.IsStatic)))
                || instruction.Operands[2] is not (long or int or ulong or uint))
                continue;

            var offset = System.Convert.ToInt64(instruction.Operands[2]);

            if (FieldAt(owner, offset, header) is { } field)
            {
                addresses[made] = (instruction, field, held, (int)offset, null, 0);
                continue;
            }

            //And a distance that lands on a **member of** a struct field. The refusal above is about reading
            //a struct's address at nought, which takes only its front - but a member that is itself a
            //primitive has no rest to lose, so the read is exact. `FPSCounter` holds three `Color`s at 56, 72
            //and 88 and reads one a component at a time (`this + 56`, `+ 60`, `+ 64`, `+ 68`); only 56 was
            //even a field, and one unnameable producer makes the whole chain refuse, so all four were lost.
            if (WithinAField(owner, offset, header) is var (outer, inner) && outer is not null && inner is not null)
                addresses[made] = (instruction, inner, held, (int)(offset - outer.Offset), outer, (int)outer.Offset);
        }

        if (addresses.Count == 0)
            return false;

        //Every chain is worked out against the **unmodified** graph and only then written back. Rewriting one
        //as it is found turns its addition into a `Move` from a field, and the next chain that reaches the
        //same local sees a source that is no longer a local and refuses the whole of itself - which is how
        //`CellDraggable::BeginDragInternal` kept twenty-nine commented statements behind two addresses.
        var covered = new HashSet<LocalVariable>();
        var accepted = new List<(HashSet<LocalVariable> Members, List<(Instruction At, int Operand, LocalVariable Behind)> Reads)>();

        foreach (var start in addresses.Keys)
        {
            if (covered.Contains(start) || Chain(graph, start, addresses) is not { } chain)
                continue;

            accepted.Add(chain);
            covered.UnionWith(chain.Members);
        }

        foreach (var chain in accepted)
        {
            foreach (var (instruction, operand, behind) in chain.Reads)
                instruction.Operands[operand] = behind;

            foreach (var carried in chain.Members)
                if (addresses.TryGetValue(carried, out var address))
                {
                    //A member of a struct field is two steps, because a field reference names one field: the
                    //struct into a local of its own, then the member out of that. `InaccessibleFieldRecovery`
                    //keeps a chain the same way, and its `STEP` register is one `IlGenerator` already accepts
                    //as a receiver - and one `DeadCopyCycles` will not collect, which matters because the step
                    //is written and read exactly once.
                    if (address.Outer is { } outer)
                    {
                        var step = new LocalVariable($"step{method.Locals.Count}",
                            new Register(null, InaccessibleFieldRecovery.ChainStep), outer.FieldType);
                        method.Locals.Add(step);

                        if (graph.Blocks.FirstOrDefault(b => b.Instructions.Contains(address.Addition)) is { } owning)
                            owning.Instructions.Insert(owning.Instructions.IndexOf(address.Addition),
                                new Instruction(address.Addition.Index, OpCode.Move, step,
                                    new FieldReference(outer, address.Owner, address.OuterOffset)));

                        address.Addition.OpCode = OpCode.Move;
                        address.Addition.Operands = [carried, new FieldReference(address.Field, step, address.Offset)];
                        continue;
                    }

                    address.Addition.OpCode = OpCode.Move;
                    address.Addition.Operands = [carried, new FieldReference(address.Field, address.Owner, address.Offset)];
                }
        }

        return accepted.Count > 0;
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
        //As above: recorded from the struct's own start, so no header to add.
        var wanted = offset;

        return owner.Fields.FirstOrDefault(f => !f.IsStatic && f.Offset == wanted
            && f.FieldType is { IsValueType: false });
    }

    /// <summary>
    /// The struct field that <b>contains</b> that distance, and the member of it lying exactly there.
    /// </summary>
    /// <remarks>
    /// The refusal in <see cref="FieldAt"/> is about a struct's own address: reading it at nought takes only
    /// the front. That does not apply to a member which is itself a <b>primitive</b> - a `float` has no rest
    /// to lose, so the read at nought is exactly it. Anything else is refused, including a nested struct,
    /// because then the same objection returns one level down.
    /// </remarks>
    private static (FieldAnalysisContext? Outer, FieldAnalysisContext? Inner) WithinAField(
        TypeAnalysisContext owner, long offset, int header)
    {
        FieldAnalysisContext? outer = null;

        foreach (var candidate in owner.Fields)
        {
            if (candidate.IsStatic || candidate.Offset > offset)
                continue;

            if (candidate.FieldType is not { IsValueType: true } held || held.IsEnumType
                || !held.Fields.Any(f => !f.IsStatic))
                continue;

            if (outer is null || candidate.Offset > outer.Offset)
                outer = candidate;
        }

        if (outer is null)
            return (null, null);

        var inside = offset - outer.Offset;

        //A struct records its members from its own start, but a boxed one carries the header, and which the
        //metadata used is not worth guessing: ask for both and take the one that lands exactly.
        var member = outer.FieldType.Fields.FirstOrDefault(f => !f.IsStatic && f.Offset == inside && IsWholeAtNought(f))
                     ?? outer.FieldType.Fields.FirstOrDefault(f => !f.IsStatic && f.Offset == inside + header && IsWholeAtNought(f));

        return member is null ? (null, null) : (outer, member);
    }

    /// <summary>Whether reading this field's address at nought yields the whole of it.</summary>
    private static bool IsWholeAtNought(FieldAnalysisContext field) => field.FieldType.Type
        is Il2CppTypeEnum.IL2CPP_TYPE_R4 or Il2CppTypeEnum.IL2CPP_TYPE_R8
        or Il2CppTypeEnum.IL2CPP_TYPE_I1 or Il2CppTypeEnum.IL2CPP_TYPE_U1
        or Il2CppTypeEnum.IL2CPP_TYPE_I2 or Il2CppTypeEnum.IL2CPP_TYPE_U2
        or Il2CppTypeEnum.IL2CPP_TYPE_I4 or Il2CppTypeEnum.IL2CPP_TYPE_U4
        or Il2CppTypeEnum.IL2CPP_TYPE_I8 or Il2CppTypeEnum.IL2CPP_TYPE_U8
        or Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN or Il2CppTypeEnum.IL2CPP_TYPE_CHAR;

    /// <summary>
    /// Everything the address flows into, where every step is a copy and every end is a read at nought.
    /// </summary>
    private static (HashSet<LocalVariable> Members, List<(Instruction At, int Operand, LocalVariable Behind)> Reads)? Chain(
        ISILControlFlowGraph graph, LocalVariable start,
        Dictionary<LocalVariable, (Instruction Addition, FieldAnalysisContext Field, LocalVariable Owner, int Offset, FieldAnalysisContext? Outer, int OuterOffset)> addresses)
    {
        var members = new HashSet<LocalVariable> { start };
        var reads = new List<(Instruction, int, LocalVariable)>();
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
                            //Anything but a copy wants the value, and where the value is the address that
                            //ends the chain. A member that already holds a **reference** is not an address:
                            //`FieldAddressRecovery` resolved it to the field's own value, and the null test
                            //or the call it feeds is asking about that value, not about where it lives.
                            //An instruction that derives *another* address from this one is not asking for
                            //the value; the pass rewrites that one on its own account, so the chain simply
                            //does not follow it.
                            if (instruction.Operands[0] is LocalVariable next && addresses.TryGetValue(next, out var derivedHere)
                                && ReferenceEquals(derivedHere.Addition, instruction))
                            {
                                continue;
                            }

                            if (instruction.OpCode is not (OpCode.Move or OpCode.Select)
                                && !(carried.Type is { IsValueType: false } && !addresses.ContainsKey(carried)))
                                return Refuse("notACopy " + instruction);

                            if (instruction.OpCode is not (OpCode.Move or OpCode.Select))
                                continue;

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
                                //A read at nought through another address is the field's own value, which is
                                //what this chain is turning that address into - so it joins, and the read is
                                //rewritten with the rest.
                                if (instruction.Operands[source] is MemoryOperand
                                    { Index: null, Scale: 0, Addend: 0, Base: LocalVariable behind })
                                {
                                    if (members.Add(behind))
                                        pending.Enqueue(behind);

                                    reads.Add((instruction, source, behind));
                                    continue;
                                }

                                //A field named outright, into a place that holds a reference: that is a
                                //field's **value**, which is what the rest of the chain is being turned
                                //into. FieldAddressRecovery has already resolved some of these.
                                if (instruction.Operands[source] is FieldReference
                                    && instruction.Operands[0] is LocalVariable { Type: { IsValueType: false } })
                                    continue;

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

                            reads.Add((instruction, operand, carried));
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

        return (members, reads);
    }

    private static (HashSet<LocalVariable>, List<(Instruction, int, LocalVariable)>)? Refuse(string why)
    {
        if (System.Environment.GetEnvironmentVariable("SINK_TRACE") == "1")
            System.Console.Error.WriteLine("SINK " + why);
        return null;
    }
}
