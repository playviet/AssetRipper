using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Counts, and does nothing else: every surviving addition of a reference and a byte offset, split by what
/// the base's type is and by what defines it.
/// </summary>
/// <remarks>
/// <para>
/// This is the <c>CS0019</c> population - "operator cannot be applied to <c>object</c> and <c>long</c>" -
/// seen at the level the passes work on rather than at the compiler's end. Two sessions have guessed at the
/// split and lost rounds to it, so it is measured before anything is built.
/// </para>
/// <para>
/// Gated on <c>ADDBASE_CENSUS</c> naming a file to append to; with the variable unset nothing runs, so a
/// build carrying this pass is also a re-measured baseline.
/// </para>
/// </remarks>
public static class UntypedBaseCensus
{
    private static readonly object Gate = new();
    private static string? _path;
    private static bool _asked;

    public static void Run(MethodAnalysisContext method)
    {
        if (!_asked)
        {
            lock (Gate)
            {
                _asked = true;
                _path = Environment.GetEnvironmentVariable("ADDBASE_CENSUS");
            }
        }

        if (_path is null || method.ControlFlowGraph is not { } graph)
            return;

        var definition = new Dictionary<LocalVariable, Instruction>();

        foreach (var instruction in graph.Instructions)
            if (instruction.Destination is LocalVariable written)
                definition[written] = instruction;

        var rows = new List<string>();

        foreach (var instruction in graph.Instructions)
        {
            if (instruction.OpCode is not (OpCode.Add or OpCode.Subtract) || instruction.Operands.Count != 3)
                continue;

            if (instruction.Operands[1] is not LocalVariable held
                || instruction.Operands[2] is not (long or int or ulong or uint))
            {
                continue;
            }

            var kind = Kind(held.Type);

            if (kind is null)
                continue;

            var offset = Convert.ToInt64(instruction.Operands[2]);
            var sign = instruction.OpCode == OpCode.Add ? "+" : "-";

            rows.Add(string.Join('\t',
                $"{method.DeclaringType?.DeclaringAssembly.Name}",
                $"{method.DeclaringType?.Name}::{method.Name}",
                kind,
                held.Type?.Name ?? "-",
                $"{sign}{offset}",
                Root(held, definition),
                Consumers(graph, instruction),
                Slot(method, held, instruction.OpCode == OpCode.Add ? offset : -offset),
                Eventually(graph, instruction),
                instruction.ToString()));
        }

        if (rows.Count == 0)
            return;

        lock (Gate)
            File.AppendAllLines(_path, rows);
    }

    /// <summary>The shapes the generator writes as a reference meeting a number, or null for the rest.</summary>
    private static string? Kind(TypeAnalysisContext? type) => type switch
    {
        null => "untyped",
        ArrayTypeAnalysisContext => "array",
        GenericParameterTypeAnalysisContext => "generic parameter",
        ByRefTypeAnalysisContext or PointerTypeAnalysisContext => null,
        RuntimeClassTypeAnalysisContext or RuntimeMethodInfoAnalysisContext => null,
        { IsEnumType: true } => null,
        { IsValueType: true } => null,
        { Name: "Object", Namespace: "System" } => "System.Object",
        { Name: var n } when n.StartsWith("Il2Cpp") => null,
        { DeclaringAssembly.Name: "Assembly-CSharp" } => "class (game)",
        _ => "class (framework)",
    };

    /// <summary>What the base rests on, followed back through copies.</summary>
    private static string Root(LocalVariable held, Dictionary<LocalVariable, Instruction> definition)
    {
        var root = held;
        Instruction? made;

        for (var hop = 0; hop < 24; hop++)
        {
            if (!definition.TryGetValue(root, out made))
                break;

            var next = made.OpCode == OpCode.Move && made.Operands.Count > 1
                ? made.Operands[1] as LocalVariable
                : null;

            if (next is null || ReferenceEquals(next, root))
                break;

            root = next;
        }

        if (!definition.TryGetValue(root, out made))
        {
            return root.Name == "this" ? "this"
                : root.Register.Name.StartsWith("X31") || root.Register.Name.StartsWith("stack") ? "the frame"
                : "a register never written here";
        }

        if (made.OpCode == OpCode.Move && made.Operands.Count > 1)
        {
            return "moved from " + made.Operands[1] switch
            {
                MemoryOperand { Base: LocalVariable { Type: null } } => "a read through an untyped base",
                MemoryOperand => "a memory read",
                NestedFieldReference => "a nested field",
                FieldReference => "a field",
                StackOffset => "a stack slot",
                StackSlotAddress => "a slot address",
                long or int or ulong or uint => "a constant",
                null => "nothing",
                { } other => other.GetType().Name,
            };
        }

        if (made.IsCall)
        {
            return made.Operands[0] switch
            {
                MethodAnalysisContext known => $"a call: {known.DeclaringType?.Name}.{known.Name}",
                string named => $"the key function {named}",
                _ => "a call to an address",
            };
        }

        return made.OpCode.ToString();
    }

    /// <summary>Where in the frame the answer points, and whether the frame has a variable there.</summary>
    private static string Slot(MethodAnalysisContext method, LocalVariable held, long moved)
    {
        if (held.Register.Name is not { } anchor || !anchor.StartsWith(StackSlots.AddressPrefix))
            return "-";

        var written = anchor[StackSlots.AddressPrefix.Length..];
        var negative = written.StartsWith('-');

        if (!long.TryParse(negative ? written[1..] : written, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out var start))
        {
            return "-";
        }

        var at = (negative ? -start : start) + moved;
        var wanted = StackSlots.ValuePrefix + (at < 0 ? "-" + (-at).ToString("X") : at.ToString("X"));

        foreach (var local in method.Locals)
            if (local.Register.Name == wanted)
                return "names " + wanted;

        return "no variable at " + wanted;
    }

    /// <summary>What reads the address once it has been carried through copies.</summary>
    private static string Eventually(Graphs.ISILControlFlowGraph graph, Instruction addition)
    {
        if (addition.Operands[0] is not LocalVariable start)
            return "-";

        var carried = new HashSet<LocalVariable> { start };
        var pending = new Queue<LocalVariable>();
        var seen = new SortedSet<string>();
        pending.Enqueue(start);

        while (pending.Count > 0)
        {
            var one = pending.Dequeue();

            foreach (var instruction in graph.Instructions)
            {
                if (ReferenceEquals(instruction, addition))
                    continue;

                for (var operand = 0; operand < instruction.Operands.Count; operand++)
                {
                    if (instruction.Operands[operand] is MemoryOperand memory
                        && (ReferenceEquals(memory.Base, one) || ReferenceEquals(memory.Index, one)))
                    {
                        seen.Add(operand == 0 && instruction.IsAssignment ? "written through" : "read through");
                        continue;
                    }

                    if (!ReferenceEquals(instruction.Operands[operand], one))
                        continue;

                    if (instruction.OpCode == OpCode.Move && operand == 1 && instruction.Operands[0] is LocalVariable onward)
                    {
                        if (carried.Add(onward))
                            pending.Enqueue(onward);

                        continue;
                    }

                    if (instruction.OpCode == OpCode.Move && operand == 1)
                    {
                        seen.Add("stored into " + (instruction.Operands[0]?.GetType().Name ?? "nothing"));
                        continue;
                    }

                    if (instruction.IsCall)
                    {
                        seen.Add(instruction.Operands[0] switch
                        {
                            MethodAnalysisContext => "handed to a resolved call",
                            string => "handed to a key function",
                            _ => "handed to an unresolved call",
                        });

                        continue;
                    }

                    seen.Add(instruction.OpCode.ToString());
                }
            }
        }

        return seen.Count == 0 ? "nothing reads it" : string.Join(',', seen);
    }

    /// <summary>How the address the addition made is then used - which decides whether typing alone helps.</summary>
    private static string Consumers(Graphs.ISILControlFlowGraph graph, Instruction addition)
    {
        if (addition.Operands[0] is not LocalVariable address)
            return "no destination";

        var seen = new SortedSet<string>();

        foreach (var instruction in graph.Instructions)
        {
            if (ReferenceEquals(instruction, addition))
                continue;

            for (var operand = 0; operand < instruction.Operands.Count; operand++)
            {
                switch (instruction.Operands[operand])
                {
                    case MemoryOperand { Index: null, Scale: 0, Addend: 0, Base: { } at } when ReferenceEquals(at, address):
                        seen.Add(operand == 0 && instruction.IsAssignment ? "written through" : "read at nought");
                        break;
                    case MemoryOperand { Index: not null, Base: { } indexed } when ReferenceEquals(indexed, address):
                        seen.Add("indexed through");
                        break;
                    case MemoryOperand { Base: { } further } when ReferenceEquals(further, address):
                        seen.Add("read at a further distance");
                        break;
                    case MemoryOperand memory when ReferenceEquals(memory.Index, address):
                        seen.Add("used as an index");
                        break;
                    case LocalVariable itself when ReferenceEquals(itself, address):
                        seen.Add(instruction.IsCall ? $"a call argument ({instruction.OpCode})" : instruction.OpCode.ToString());
                        break;
                }
            }
        }

        return seen.Count == 0 ? "nothing reads it" : string.Join(',', seen);
    }
}
