using System.Collections.Generic;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Writes a local that only ever holds an argument back as that argument.
///
/// An argument arrives in a register the compiled code cannot keep it in - it needs the register for the calls
/// it makes - so it is put somewhere that survives a call and read back afterwards, and on a method with
/// branches that happens once per branch. Coming out of SSA each of those becomes its own assignment, and a
/// local written in several places is one the propagation that runs there will not touch, because in general
/// it cannot know the value is the same every time. Here it can: every assignment copies the same argument,
/// and an argument is never assigned, so the local holds that argument wherever it is read.
///
/// What it costs to leave is out of proportion to how small it is. The local is the receiver of every call the
/// method makes on itself, so a closure or an iterator's state machine reads as an object handed around rather
/// than one that stays put - and a decompiler only writes those back as the lambda and the <c>yield return</c>
/// they were compiled from when it can see the instance never leaves the method.
/// </summary>
public static class ParameterCopyPropagation
{
    public static void Run(MethodAnalysisContext method)
    {
        var parameters = method.ParameterLocals;

        if (parameters.Count == 0)
            return;

        // Everything each local is ever assigned. A local assigned by anything that is not a copy of another
        // local cannot be one of these copies, and is left out from the start.
        var assignedFrom = new Dictionary<LocalVariable, List<LocalVariable>>();
        var ruledOut = new HashSet<LocalVariable>();

        foreach (var instruction in method.ControlFlowGraph!.Instructions)
        {
            if (instruction.Destination is not LocalVariable destination || parameters.Contains(destination))
                continue;

            if (instruction is { OpCode: OpCode.Move, Operands.Count: 2 } && instruction.Operands[1] is LocalVariable source)
                (assignedFrom.TryGetValue(destination, out var from) ? from : assignedFrom[destination] = []).Add(source);
            else
                ruledOut.Add(destination);
        }

        var settled = new Dictionary<LocalVariable, LocalVariable>();

        foreach (var parameter in parameters)
            foreach (var local in CopiesOf(parameter, assignedFrom, ruledOut, settled))
                settled[local] = parameter;

        if (settled.Count == 0)
            return;

        foreach (var instruction in method.ControlFlowGraph.Instructions)
        {
            //The copies themselves say nothing once every read of what they wrote reads the argument instead.
            if (instruction is { OpCode: OpCode.Move, Operands.Count: 2 }
                && instruction.Operands[0] is LocalVariable written
                && settled.ContainsKey(written))
            {
                instruction.OpCode = OpCode.Nop;
                instruction.Operands = [];
                continue;
            }

            ReadArgumentInstead(instruction, settled);
        }

        LocalVariables.RemoveUnused(method);
    }

    /// <summary>
    /// Every local that can only ever hold <paramref name="parameter"/>.
    ///
    /// The copies form a chain - the argument is read back into one place and passed along from there - and a
    /// chain that runs round a loop names a local before it has been decided, so this starts by supposing
    /// every candidate holds the argument and takes back any that turns out to be assigned something else.
    /// What is left over is closed: every assignment to a member is the argument or another member.
    ///
    /// Supposing it and taking it back accepts a group that only ever assigns to itself and is never handed
    /// the argument at all, so the survivors are then checked the other way round - each has to be reachable
    /// from the argument by following the assignments forwards.
    /// </summary>
    private static List<LocalVariable> CopiesOf(
        LocalVariable parameter,
        Dictionary<LocalVariable, List<LocalVariable>> assignedFrom,
        HashSet<LocalVariable> ruledOut,
        Dictionary<LocalVariable, LocalVariable> alreadySettled)
    {
        var supposed = new HashSet<LocalVariable>();

        foreach (var local in assignedFrom.Keys)
            if (!ruledOut.Contains(local) && !alreadySettled.ContainsKey(local))
                supposed.Add(local);

        bool takenBack;

        do
        {
            takenBack = false;

            foreach (var local in new List<LocalVariable>(supposed))
                foreach (var source in assignedFrom[local])
                    if (!ReferenceEquals(source, parameter) && !supposed.Contains(source))
                    {
                        supposed.Remove(local);
                        takenBack = true;
                        break;
                    }
        } while (takenBack);

        var reached = new List<LocalVariable>();
        var isReached = new HashSet<LocalVariable>();
        bool grew;

        do
        {
            grew = false;

            foreach (var local in supposed)
            {
                if (isReached.Contains(local))
                    continue;

                foreach (var source in assignedFrom[local])
                    if (ReferenceEquals(source, parameter) || isReached.Contains(source))
                    {
                        isReached.Add(local);
                        reached.Add(local);
                        grew = true;
                        break;
                    }
            }
        } while (grew);

        return reached;
    }

    /// <summary>Points every read of a settled local at the argument it was copied from.</summary>
    private static void ReadArgumentInstead(Instruction instruction, Dictionary<LocalVariable, LocalVariable> settled)
    {
        var destination = instruction.Destination;

        for (var i = 0; i < instruction.Operands.Count; i++)
        {
            switch (instruction.Operands[i])
            {
                case LocalVariable local when !ReferenceEquals(local, destination) && settled.TryGetValue(local, out var argument):
                    instruction.Operands[i] = argument;
                    break;

                case MemoryOperand memory:
                    if (memory.Base is LocalVariable baseLocal && settled.TryGetValue(baseLocal, out var baseArgument))
                        memory.Base = baseArgument;
                    if (memory.Index is LocalVariable indexLocal && settled.TryGetValue(indexLocal, out var indexArgument))
                        memory.Index = indexArgument;
                    instruction.Operands[i] = memory; //MemoryOperand is a struct, so the copy has to be written back
                    break;

                case FieldReference { Local: { } fieldLocal } field when settled.TryGetValue(fieldLocal, out var fieldArgument):
                    field.Local = fieldArgument;
                    break;
            }
        }
    }
}
