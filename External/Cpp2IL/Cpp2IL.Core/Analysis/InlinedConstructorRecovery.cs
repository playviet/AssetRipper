using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Puts the arguments back on an allocation whose constructor il2cpp inlined into the caller.
/// </summary>
/// <remarks>
/// <para>
/// A constructor small enough to inline leaves the allocation, a call to the base constructor, and the field
/// writes the constructor body did - and those writes are to fields the caller cannot name, often
/// <c>readonly</c> as well, so there is no form of C# that can express them. They are lost, and the object is
/// then built with whatever the generator can default its parameters to:
/// </para>
/// <code>
/// Box box = new Box(0);
/// //AssetRipper: commented out, this could not be kept as code.
/// //box._side = n;
/// </code>
/// <para>
/// That is worse than a lost statement. The allocation still compiles, so nothing marks it, and the object
/// simply has the wrong values in it - <c>Corpus::Areas</c> answered 13 where the original answers 83200,
/// because every shape it measured had been built with a zero side.
/// </para>
/// <para>
/// The generator already picks the allocated type's own constructor and already reads arguments off the
/// allocation; what was missing is anyone to put them there. This matches each write to the parameter that
/// stands for it - by the field's name without its <c>_</c> or <c>m_</c>, and by type - and only folds when
/// every parameter is matched exactly once, so a constructor that does more than store its arguments is left
/// alone.
/// </para>
/// </remarks>
public static class InlinedConstructorRecovery
{
    public static bool Run(MethodAnalysisContext method)
    {
        if (method.ControlFlowGraph is not { } graph)
            return false;

        var changed = false;

        foreach (var block in graph.Blocks)
        {
            foreach (var allocation in block.Instructions.ToList())
            {
                if (allocation.OpCode != OpCode.Newobj
                    || allocation.Operands.Count != 2
                    || allocation.Operands[0] is not LocalVariable { Type: { } allocated } instance)
                    continue;

                var baseCalls = new List<Instruction>();
                var writes = WritesFollowing(graph, block, allocation, instance, allocated, baseCalls);

                if (writes.Count == 0 || Matching(allocated, writes) is not { } arguments)
                    continue;

                //Built where the last of the writes was, not where the allocation was. An argument is often
                //worked out between the two - `new Box(n + 1)` computes the sum after the allocation - and an
                //allocation left where it was would be handed a local that has not been assigned yet, which
                //reads back as the zero this pass exists to get rid of. Nothing between the two touches the
                //object, so this is where the source built it anyway.
                var built = writes[^1].Write;
                var type = allocation.Operands[1];

                allocation.OpCode = OpCode.Nop;
                allocation.Operands = [];

                built.OpCode = OpCode.Newobj;
                built.Operands = [instance, type, .. arguments];

                foreach (var (write, _) in writes)
                {
                    if (ReferenceEquals(write, built))
                        continue;

                    write.OpCode = OpCode.Nop;
                    write.Operands = [];
                }

                //The base constructor the inlined body called. The generator nops one it fuses into an
                //allocation, but it only looks forward, and the allocation now stands after this - so left
                //alone it is written out as a call to `.ctor` by name, which is not C# and loses the statement.
                foreach (var baseCall in baseCalls)
                {
                    baseCall.OpCode = OpCode.Nop;
                    baseCall.Operands = [];
                }

                changed = true;
            }
        }

        return changed;
    }

    /// <summary>
    /// The writes to the new object's own fields that directly follow the allocation, or nothing if anything
    /// else happens to it first.
    /// </summary>
    /// <remarks>
    /// The allocation ends a block, so the writes are usually in the next one; the walk follows a successor
    /// only where it is the single way on and the single way in, so what is collected is what runs, in order,
    /// however the blocks happen to be cut.
    /// </remarks>
    private static List<(Instruction Write, FieldAnalysisContext Field)> WritesFollowing(
        ISILControlFlowGraph graph, Block start, Instruction allocation, LocalVariable instance,
        TypeAnalysisContext allocated, List<Instruction> baseCalls)
    {
        var writes = new List<(Instruction, FieldAnalysisContext)>();
        var current = start;
        var index = current.Instructions.IndexOf(allocation) + 1;

        for (var steps = 0; steps < 64; steps++)
        {
            if (index >= current.Instructions.Count)
            {
                if (current.Successors.Count != 1 || current.Successors[0].Predecessors.Count != 1)
                    return writes;

                current = current.Successors[0];
                index = 0;
                continue;
            }

            var instruction = current.Instructions[index++];

            switch (instruction)
            {
                case { OpCode: OpCode.Nop }:
                    continue;

                //The base constructor the inlined body still calls - it is not what built the object.
                case { OpCode: OpCode.CallVoid, Operands: [MethodAnalysisContext { Name: ".ctor" }, { } target, ..] }
                    when ReferenceEquals(target, instance):
                    baseCalls.Add(instruction);
                    continue;

                case { OpCode: OpCode.Move, Operands: [FieldReference { Field: { IsStatic: false } field, Local: { } through }, _] }
                    when ReferenceEquals(through, instance) && ReferenceEquals(field.DeclaringType, allocated):
                    writes.Add((instruction, field));
                    continue;

                //Anything that never mentions the object cannot be part of building it and cannot observe it
                //half-built either - most often it is an argument being worked out, which is precisely what
                //the constructor is about to be handed.
                case var other when !Mentions(other, instance):
                    continue;

                default:
                    return writes;
            }
        }

        return writes;
    }

    /// <summary>
    /// The values to hand the constructor, in parameter order, if one constructor plainly takes exactly these
    /// fields.
    /// </summary>
    private static List<object>? Matching(TypeAnalysisContext allocated, List<(Instruction Write, FieldAnalysisContext Field)> writes)
    {
        foreach (var constructor in allocated.Methods.Where(m => m is { IsStatic: false, Name: ".ctor" }))
        {
            if (constructor.Parameters.Count != writes.Count)
                continue;

            var arguments = new List<object>();
            var taken = new HashSet<FieldAnalysisContext>();

            foreach (var parameter in constructor.Parameters)
            {
                var match = writes.FirstOrDefault(w => !taken.Contains(w.Field)
                    && Bare(w.Field.Name) is { } bare
                    && string.Equals(bare, parameter.Name, System.StringComparison.OrdinalIgnoreCase)
                    && w.Field.FieldType.FullName == parameter.ParameterType.FullName);

                if (match.Write == null)
                    break;

                taken.Add(match.Field);
                arguments.Add(match.Write.Operands[1]);
            }

            if (arguments.Count == constructor.Parameters.Count)
                return arguments;

            //A constructor whose fields are not named after its parameters, taken in order instead. The
            //writes are already known to be consecutive, to the fresh object, with nothing else touching it
            //(see WritesFollowing), so their order is the constructor body's own; requiring the whole type
            //sequence to line up is what stops two same-typed parameters being taken the wrong way round.
            //Without this, one name that does not match rejects the entire constructor - DOTween's
            //`WaitForCompletion(Tween tween)` assigns it to a field called `t`, and six of its members were
            //built with `null` and a commented `waitForCompletion.t = t;` after them.
            if (constructor.Parameters.Count == writes.Count
                && constructor.Parameters.Select((p, at) => p.ParameterType.FullName == writes[at].Field.FieldType.FullName).All(same => same))
                return writes.Select(w => w.Write.Operands[1]).ToList();
        }

        return null;
    }

    /// <summary>Whether the instruction names the local at all, in any position.</summary>
    private static bool Mentions(Instruction instruction, LocalVariable instance)
    {
        foreach (var operand in instruction.Operands)
        {
            var mentions = operand switch
            {
                LocalVariable local => ReferenceEquals(local, instance),
                MemoryOperand memory => ReferenceEquals(memory.Base, instance) || ReferenceEquals(memory.Index, instance),
                FieldReference field => ReferenceEquals(field.Local, instance),
                _ => false,
            };

            if (mentions)
                return true;
        }

        return false;
    }

    /// <summary>The name a backing field carries once the mark that it is one is taken off.</summary>
    private static string? Bare(string? field)
    {
        if (field == null)
            return null;

        var bare = field.StartsWith("m_") ? field[2..] : field.StartsWith('_') ? field[1..] : field;

        return string.IsNullOrEmpty(bare) ? null : bare;
    }
}
