using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Lets the one instruction that reads a copied-out field name the field instead of the copy.
///
/// A field is loaded into a register well before the thing that uses it - the compiler is free to schedule
/// the load early, and does. Where the source wrote <c>context.InvokeDelay(0.1f, () =&gt; ...)</c> the load
/// sits above the whole cached-lambda dance and the call below it, so the value has to travel in a local,
/// and a local is what the decompiler writes out: <c>MonoBehaviour self = base.context;</c> on one line and
/// the call on the next.
///
/// The copy exists only because of where the load was put. Naming the field at the one place the value is
/// read says the same thing in the shape the source had it, and leaves nothing to be named.
///
/// It is only done where the field cannot have changed in between: nothing on the way may write that field
/// or any memory at all, and a call is allowed only when it is a delegate's constructor, which writes the
/// delegate it was handed and nothing else. A path that runs back through the load is not followed either -
/// around a loop the two reads are not the same read.
/// </summary>
public static class FieldReadSinking
{
    /// <summary>Set <c>FIELDSINK_OFF=1</c> to measure the same build without this - see ROUND-LOG.md.</summary>
    private static readonly bool Off = System.Environment.GetEnvironmentVariable("FIELDSINK_OFF") == "1";

    public static void Run(MethodAnalysisContext method)
    {
        if (Off)
            return;

        var cfg = method.ControlFlowGraph!;
        var uses = UseCounts(cfg);
        var definitions = DefinitionCounts(cfg);

        foreach (var definition in cfg.Instructions.ToList())
        {
            if (definition.OpCode != OpCode.Move || definition.Operands.Count != 2)
                continue;

            if (definition.Operands[0] is not LocalVariable copy || definition.Operands[1] is not FieldReference source)
                continue;

            if (uses.GetValueOrDefault(copy) != 1 || definitions.GetValueOrDefault(copy) != 1)
                continue;

            if (SoleReader(cfg, copy) is not { } reader || ReferenceEquals(reader.Use, definition))
                continue;

            if (!NothingInBetweenChangesIt(cfg, definition, reader.Use, source))
                continue;

            reader.Use.Operands[reader.Operand] = source;
            definition.OpCode = OpCode.Nop;
            definition.Operands = [];
        }
    }

    /// <summary>
    /// The instruction that reads the local, and which of its operands is the read - given that exactly one
    /// does. A read buried inside an address or a field's object is not one that can name a field instead,
    /// so those give nothing.
    /// </summary>
    private static (Instruction Use, int Operand)? SoleReader(ISILControlFlowGraph cfg, LocalVariable copy)
    {
        foreach (var instruction in cfg.Instructions)
        {
            for (var i = 0; i < instruction.Operands.Count; i++)
            {
                if (i == 0 && instruction.Destination != null)
                    continue;

                if (instruction.Operands[i] is LocalVariable read && ReferenceEquals(read, copy))
                    return (instruction, i);
            }
        }

        return null;
    }

    private static bool NothingInBetweenChangesIt(ISILControlFlowGraph cfg, Instruction definition, Instruction use, FieldReference source)
    {
        if (!Locate(cfg, definition, out var definitionBlock, out var definitionIndex)
            || !Locate(cfg, use, out var useBlock, out var useIndex))
            return false;

        bool Clear(Block block, int from, int to)
        {
            for (var i = from; i < to; i++)
            {
                if (!Harmless(block.Instructions[i], source))
                    return false;
            }

            return true;
        }

        if (ReferenceEquals(definitionBlock, useBlock))
        {
            return definitionIndex < useIndex && Clear(definitionBlock, definitionIndex + 1, useIndex);
        }

        if (!Clear(definitionBlock, definitionIndex + 1, definitionBlock.Instructions.Count))
            return false;

        var seen = new HashSet<Block>();
        var queue = new Queue<Block>(definitionBlock.Successors);

        while (queue.Count > 0)
        {
            var block = queue.Dequeue();

            if (!seen.Add(block))
                continue;

            //Reaching the load again means the way to the read goes round a loop, and the value read on the
            //second time round is not the one the copy holds.
            if (ReferenceEquals(block, definitionBlock))
                return false;

            if (ReferenceEquals(block, useBlock))
            {
                if (!Clear(block, 0, useIndex))
                    return false;

                //Nothing past the read matters, and not walking through it is what keeps the block from
                //being entered a second time.
                continue;
            }

            if (!Clear(block, 0, block.Instructions.Count))
                return false;

            foreach (var successor in block.Successors)
                queue.Enqueue(successor);
        }

        return seen.Contains(useBlock);
    }

    /// <summary>
    /// Whether an instruction leaves the field holding what it held. Anything that writes memory is a reason
    /// to stop unless it names a different field, since two fields are never the same place. A call is too,
    /// because what it does is not known here - except a delegate's constructor, which fills in the delegate
    /// it was given.
    /// </summary>
    private static bool Harmless(Instruction instruction, FieldReference source)
    {
        //Where the instruction writes, asked of StoreTarget rather than of `Instruction.Destination` - which
        //is null for a store into a field or into memory, so all three of these questions used to be asked
        //of nothing at all and the read was carried straight over the write that makes it stale. See
        //StoreTarget; `Corpus+<Steps>d__73::MoveNext` is what it cost.
        var target = StoreTarget.Of(instruction);

        if (target is LocalVariable written && ReferenceEquals(written, source.Local))
            return false;

        if (target is MemoryOperand)
            return false;

        if (target is FieldReference stored && StoreTarget.IsTheSameField(stored.Field, source.Field))
            return false;

        if (instruction.OpCode is OpCode.IndirectCall)
            return false;

        if (instruction.OpCode is OpCode.Call or OpCode.CallVoid)
            return instruction.Operands.FirstOrDefault() is MethodAnalysisContext { Name: ".ctor" } constructor
                && IsDelegate(constructor.DeclaringType);

        return true;
    }

    private static bool IsDelegate(TypeAnalysisContext? type)
    {
        var seen = 0;

        for (var current = type; current != null && seen++ < 8; current = current.BaseType)
        {
            if (current.FullName is "System.MulticastDelegate" or "System.Delegate")
                return true;
        }

        return false;
    }

    private static bool Locate(ISILControlFlowGraph cfg, Instruction instruction, out Block block, out int index)
    {
        foreach (var candidate in cfg.Blocks)
        {
            var found = candidate.Instructions.IndexOf(instruction);

            if (found < 0)
                continue;

            block = candidate;
            index = found;
            return true;
        }

        block = null!;
        index = -1;
        return false;
    }

    /// <summary>How many times each local is read, counting a read through an address or a field's object.</summary>
    private static Dictionary<LocalVariable, int> UseCounts(ISILControlFlowGraph cfg)
    {
        var uses = new Dictionary<LocalVariable, int>();

        foreach (var instruction in cfg.Instructions)
        {
            for (var i = 0; i < instruction.Operands.Count; i++)
            {
                //Operand zero is what the instruction writes - but a store still reads whatever says where
                //it writes, so the object of a field and the base of an address count wherever they appear.
                var written = i == 0 && instruction.Destination != null;

                switch (instruction.Operands[i])
                {
                    case LocalVariable read when !written:
                        uses[read] = uses.GetValueOrDefault(read) + 1;
                        break;
                    case MemoryOperand { Base: LocalVariable address }:
                        uses[address] = uses.GetValueOrDefault(address) + 1;
                        break;
                    case FieldReference { Local: { } holder }:
                        uses[holder] = uses.GetValueOrDefault(holder) + 1;
                        break;
                }
            }
        }

        return uses;
    }

    private static Dictionary<LocalVariable, int> DefinitionCounts(ISILControlFlowGraph cfg)
    {
        var definitions = new Dictionary<LocalVariable, int>();

        foreach (var instruction in cfg.Instructions)
        {
            if (instruction.Destination is LocalVariable written)
                definitions[written] = definitions.GetValueOrDefault(written) + 1;
        }

        return definitions;
    }
}
