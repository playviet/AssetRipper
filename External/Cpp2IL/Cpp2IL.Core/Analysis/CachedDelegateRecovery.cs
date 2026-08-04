using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Removes the variable that shadows a cached delegate's field.
///
/// A lambda that captures nothing is built once and kept in a static field, and the code that uses it reads
/// the field, builds the delegate if it was null, and reads the field again. Between the two reads the value
/// travels in a register, which becomes a variable here - so the field is read once, the test names it a
/// second time, and the use names the variable.
///
/// The decompiler puts a lambda expression back only when it sees the field itself used: a test of the field,
/// a single store of the delegate, and one read after it. With a variable standing in for that read it finds
/// nothing to fold, keeps the class the compiler generated to hold the cache, and writes it out in the
/// recovered source - a <c>&lt;&gt;c</c> with a static field and the lambda's body as a method, none of which
/// was in the source.
///
/// The variable holds what the field holds on both paths - it is either read from it, or is the value just
/// stored into it - so naming the field instead says the same thing, and leaves the shape that folds.
/// </summary>
public static class CachedDelegateRecovery
{
    public static void Run(MethodAnalysisContext method)
    {
        var definitions = DefinitionsByLocal(method);

        foreach (var (local, writes) in definitions)
        {
            if (writes.Count != 2)
                continue;

            if (Mirrored(method, writes) is not { } field)
                continue;

            Replace(method, local, field);

            foreach (var (block, instruction) in writes)
                block.Instructions.Remove(instruction);
        }
    }

    /// <summary>
    /// The static field a local mirrors: one of its two definitions reads that field, and the other takes the
    /// value that was stored into it a moment earlier. Anything else, and the local is carrying its own value.
    /// </summary>
    private static FieldReference? Mirrored(MethodAnalysisContext method, List<(Block Block, Instruction Instruction)> writes)
    {
        foreach (var (read, stored) in new[] { (writes[0], writes[1]), (writes[1], writes[0]) })
        {
            if (!IsCopy(read.Instruction) || read.Instruction.Operands[1] is not FieldReference field || !field.Field.IsStatic)
                continue;

            if (!IsCopy(stored.Instruction) || stored.Instruction.Operands[1] is not LocalVariable value)
                continue;

            if (!StoredInto(stored.Block, stored.Instruction, field, value))
                continue;

            //If anything else writes the field, what the local holds and what the field holds can differ.
            if (Stores(method, field) != 1)
                continue;

            return field;
        }

        return null;
    }

    private static bool IsCopy(Instruction instruction)
        => instruction.OpCode == OpCode.Move && instruction.Operands.Count == 2;

    /// <summary>Whether the block stores <paramref name="value"/> into the field before the given instruction.</summary>
    private static bool StoredInto(Block block, Instruction before, FieldReference field, LocalVariable value)
    {
        for (var i = block.Instructions.IndexOf(before) - 1; i >= 0; i--)
        {
            var instruction = block.Instructions[i];

            if (IsCopy(instruction) && instruction.Operands[0] is FieldReference target && SameStaticField(target, field)
                && instruction.Operands[1] is LocalVariable source && ReferenceEquals(source, value))
                return true;
        }

        return false;
    }

    private static int Stores(MethodAnalysisContext method, FieldReference field)
        => method.ControlFlowGraph!.Instructions.Count(i =>
            i.Operands.Count > 0 && i.Operands[0] is FieldReference target && SameStaticField(target, field));

    /// <summary>
    /// Whether two references name the same static field. A static field belongs to its type rather than to
    /// an object, so the local each reference reads it through says nothing about which field it is - and
    /// every read of one goes through a different local here.
    /// </summary>
    private static bool SameStaticField(FieldReference left, FieldReference right)
        => ReferenceEquals(left.Field, right.Field) && left.Field.IsStatic && left.Offset == right.Offset;

    private static void Replace(MethodAnalysisContext method, LocalVariable local, FieldReference field)
    {
        foreach (var instruction in method.ControlFlowGraph!.Instructions)
        {
            for (var i = 0; i < instruction.Operands.Count; i++)
            {
                //Operand zero is written rather than read; the two definitions are removed, not rewritten.
                if (i == 0 && instruction.Destination != null)
                    continue;

                if (instruction.Operands[i] is LocalVariable used && ReferenceEquals(used, local))
                    instruction.Operands[i] = new FieldReference(field.Field, field.Local, field.Offset);
            }
        }
    }

    private static Dictionary<LocalVariable, List<(Block, Instruction)>> DefinitionsByLocal(MethodAnalysisContext method)
    {
        var definitions = new Dictionary<LocalVariable, List<(Block, Instruction)>>();

        foreach (var block in method.ControlFlowGraph!.Blocks)
        {
            foreach (var instruction in block.Instructions)
            {
                if (instruction.Destination is LocalVariable destination)
                {
                    if (!definitions.TryGetValue(destination, out var writes))
                        definitions[destination] = writes = [];

                    writes.Add((block, instruction));
                }
            }
        }

        return definitions;
    }
}
