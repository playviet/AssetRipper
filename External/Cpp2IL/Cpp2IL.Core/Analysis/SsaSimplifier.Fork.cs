using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// The fork's half of <see cref="SsaSimplifier"/>: phis that merge one value.
///
/// A phi says which value a register holds depending on the path taken to reach it. Where every path hands it
/// the same value the phi decides nothing, and leaving it in place is expensive: destroying SSA form writes a
/// phi back as one copy per incoming edge, so a register that only ever held <c>this</c> becomes a local
/// assigned <c>this</c> at the end of every branch of the method.
///
/// That matters beyond tidiness. A compiler-generated class - the closure a lambda captures into, the state
/// machine an iterator is compiled to - is only written back as the lambda or the <c>yield return</c> it came
/// from when the decompiler can see that the instance never leaves the method. Copied into a local on every
/// branch, it plainly does leave, and the whole method is written out as the machinery instead of as what was
/// written.
/// </summary>
public static partial class SsaSimplifier
{
    /// <summary>
    /// Rewrites every phi whose incoming values all come to the same thing as a copy of that thing, which the
    /// propagation this runs in front of then forwards to the uses and drops.
    /// </summary>
    private static void CollapseTrivialPhis(ISILControlFlowGraph cfg, List<LocalVariable> parameterLocals, Dictionary<LocalVariable, object> forwarded)
    {
        var phis = cfg.Blocks
            .SelectMany(block => block.Instructions)
            .Where(instruction => instruction.OpCode == OpCode.Phi
                                  && instruction.Operands.Count > 1
                                  && instruction.Operands[0] is LocalVariable local
                                  && !parameterLocals.Contains(local))
            .ToList();

        if (phis.Count == 0)
            return;

        // A phi can be handed a value another phi decides, and that one may not have been looked at yet, so
        // this goes round until a round decides nothing further.
        bool decided;

        do
        {
            decided = false;

            foreach (var phi in phis)
            {
                if (phi.OpCode != OpCode.Phi)
                    continue;

                var destination = (LocalVariable)phi.Operands[0];

                if (SingleIncomingValue(phi, destination, forwarded) is not { } value)
                    continue;

                forwarded[destination] = value;
                phi.OpCode = OpCode.Move;
                phi.Operands = [destination, value];
                decided = true;
            }
        } while (decided);
    }

    /// <summary>
    /// The one value a phi is handed along every path, or null if the paths disagree.
    ///
    /// A phi at the top of a loop is handed its own result along the back edge - the value the previous turn
    /// of the loop left - which says nothing about what the value is and so is not counted. What is left has
    /// to be a single value: in SSA a name is written once, so a name every path agrees on is defined before
    /// the phi on all of them and can stand in its place.
    /// </summary>
    private static object? SingleIncomingValue(Instruction phi, LocalVariable destination, Dictionary<LocalVariable, object> forwarded)
    {
        object? only = null;

        for (var i = 1; i < phi.Operands.Count; i++)
        {
            var incoming = Settled(phi.Operands[i], forwarded);

            if (ReferenceEquals(incoming, destination))
                continue;

            if (!IsForwardable(incoming))
                return null;

            if (only == null)
                only = incoming;
            else if (only is LocalVariable ? !ReferenceEquals(only, incoming) : !only.Equals(incoming))
                return null;
        }

        return only;
    }

    /// <summary>What a value comes to once the copies and collapsed phis are followed through.</summary>
    private static object Settled(object value, Dictionary<LocalVariable, object> forwarded)
    {
        var seen = new HashSet<LocalVariable>();

        while (value is LocalVariable local && seen.Add(local) && forwarded.TryGetValue(local, out var settled))
            value = settled;

        return value;
    }
}
