using System.Collections.Generic;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// A value that only ever goes round a loop of copies is dead, however many times it is read.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="DeadCodeEliminator"/> proves a definition dead by counting uses and finding none, which is
/// exactly right in single assignment form and blind afterwards: taking that form apart turns a phi into a
/// copy on each edge, and a value carried round a loop is copied back to itself.
/// </para>
/// <code>
/// Add  v127 (System.Int32), this (CF.BoardController), 192   //the write barrier's address
/// Move v48, v56
/// Move v56, v48
/// </code>
/// <para>
/// Each of the three is read, so each keeps the others alive, and none of them reaches anything that does
/// anything - the address came out as <c>num3 = (int)(this + 192L)</c>, a statement that does not compile,
/// in front of a store that had recovered perfectly. Twenty-five instructions in one method were held up
/// this way by a single closed cycle.
/// </para>
/// <para>
/// So the question is asked the other way round. Assume nothing is live; make a value live where something
/// with an effect reads it - a call, a store, a return, a branch - and carry that backwards through the
/// arithmetic and the copies to a fixpoint. What is left over reaches no effect by any path, whatever it is
/// copied into. Only instructions with no effect of their own are ever removed, which is the same set
/// <see cref="DeadCodeEliminator"/> works on and for the same reason.
/// </para>
/// </remarks>
public static class DeadCopyCycles
{
    public static void Run(MethodAnalysisContext method)
    {
        if (method.ControlFlowGraph is not { } graph)
            return;

        //Everything an instruction with an effect reads, and then everything those depend on.
        var live = new HashSet<LocalVariable>();
        var pending = new Queue<LocalVariable>();

        void Reach(object? operand)
        {
            switch (operand)
            {
                case LocalVariable local when live.Add(local):
                    pending.Enqueue(local);
                    break;

                case MemoryOperand memory:
                    Reach(memory.Base);
                    Reach(memory.Index);
                    break;

                case FloatStructAssembly assembly:
                    foreach (var part in assembly.Parts)
                        Reach(part);
                    break;

                //A field named on a local is a read of that local, wherever in the operand list it sits -
                //the passes that recover a field write the base into the reference and leave nothing else
                //behind, so missing this case would collect the definition of a base still being read.
                case FieldReference { Local: { } owner }:
                    Reach(owner);
                    break;

                case MultiDimensionalElement element:
                    Reach(element.Array);
                    foreach (var index in element.Indices)
                        Reach(index);
                    break;
            }
        }

        var trace = System.Environment.GetEnvironmentVariable("CYCLE_TRACE") is { } wanted && method.Name.Contains(wanted);

        foreach (var instruction in graph.Instructions)
        {
            if (HasNoEffect(instruction.OpCode))
            {
                //Even a pure instruction's destination may be a place rather than a value - a store through
                //a memory operand or into a field - and then what it writes is an effect.
                if (instruction.Operands.Count > 0 && instruction.Operands[0] is not LocalVariable)
                    foreach (var operand in instruction.Operands)
                        Reach(operand);

                continue;
            }

            var before = live.Count;

            foreach (var operand in instruction.Operands)
                Reach(operand);

            if (trace && live.Count != before)
                System.Console.WriteLine($"CYCLE seed {instruction.OpCode} {instruction}");
        }

        //What the live values were made from.
        var definitions = new Dictionary<LocalVariable, List<Instruction>>();

        foreach (var instruction in graph.Instructions)
            if (HasNoEffect(instruction.OpCode) && instruction.Operands is [LocalVariable written, ..])
            {
                if (!definitions.TryGetValue(written, out var places))
                    definitions[written] = places = [];

                places.Add(instruction);
            }

        while (pending.Count > 0)
        {
            if (!definitions.TryGetValue(pending.Dequeue(), out var places))
                continue;

            foreach (var place in places)
                for (var i = 1; i < place.Operands.Count; i++)
                    Reach(place.Operands[i]);
        }

        foreach (var (written, places) in definitions)
        {
            if (trace)
                System.Console.WriteLine($"CYCLE {written.Name} @ {written.Register} live={live.Contains(written)} invented={IsInvented(written)}");

            if (live.Contains(written) || IsInvented(written))
                continue;

            foreach (var place in places)
            {
                place.OpCode = OpCode.Nop;
                place.Operands = [];
            }
        }
    }

    /// <summary>
    /// Whether the value lives in a register the lifter or a pass invented rather than one the machine has.
    /// </summary>
    /// <remarks>
    /// Those are the ones recorded somewhere this graph does not show - <c>InaccessibleFieldRecovery</c> keeps
    /// its chain steps in a table of its own, and taking one away threw
    /// <c>KeyNotFoundException: step577 @ STEP</c> out of the generator. A machine register only ever holds
    /// what the code put in it, so the dataflow here is the whole story for those and for nothing else.
    /// </remarks>
    /// <remarks>
    /// This was <c>Register.Name is not null</c>, which is never true - <c>Register</c>'s constructor fills
    /// the name in on all three of its branches - so the pass has never removed anything since it was written.
    /// A machine register is named by the lifter as <c>X</c> or <c>V</c> and a number; everything else -
    /// <c>STEP</c>, <c>LENGTH</c>, <c>PROPERTY</c>, <c>stack_*</c> and the rest - is a pass's own bookkeeping.
    /// Only <c>X</c> for now: the float-struct family is rebuilt out of V registers by four passes that run
    /// after this one, and taking a V copy away here would be taking it from them.
    /// </remarks>
    private static bool IsInvented(LocalVariable local)
    {
        var name = local.Register.Name;

        if (name is not { Length: >= 2 } || name[0] != 'X')
            return true;

        for (var i = 1; i < name.Length; i++)
            if (name[i] is < '0' or > '9')
                return true;

        return false;
    }

    /// <summary>
    /// Opcodes that compute a value and do nothing else - the same set <see cref="DeadCodeEliminator"/>
    /// will remove, mirrored rather than shared so that neither has to change for the other.
    /// </summary>
    private static bool HasNoEffect(OpCode opCode) => opCode switch
    {
        OpCode.Move or OpCode.Phi or OpCode.Select
            or OpCode.Add or OpCode.Subtract or OpCode.Multiply or OpCode.Divide
            or OpCode.ShiftLeft or OpCode.ShiftRight
            or OpCode.And or OpCode.Or or OpCode.Xor
            or OpCode.Not or OpCode.Negate => true,
        >= OpCode.CheckEqual and <= OpCode.CheckLessOrEqual => true,
        _ => false,
    };
}
