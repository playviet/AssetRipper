using System.Collections.Generic;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// The fork's half of <see cref="SsaForm"/>: which registers are still worth anything where the paths meet.
///
/// Minimal SSA puts a phi wherever a register is written on one path into a block reachable another way,
/// whether or not anything is going to read it there. Compiled code writes every register it is allowed to
/// keep a value in, and reuses each one for something else the moment the last read of the old value is past,
/// so most of those joins are between a value and a value that is already finished with.
///
/// Merging the two is not harmless. The phi says the register holds either, so what it holds cannot be said,
/// and the type carried into that join is carried on out of it: a register that held <c>this</c> until the
/// loop that reuses it as a counter reads afterwards as an object of the enclosing class, and every use of
/// the counter reads as a use of that object. Everything downstream then works on the wrong thing - the
/// arithmetic will not be written down, and a closure or an iterator's state machine looks like an object
/// being copied about rather than one that stays where it was put.
///
/// The register is only merged where something can still read it, which is what liveness says. That is the
/// standard pruned form of the same construction, and the block-level use/def lists the graph already keeps
/// are nearly what it needs - nearly, because those record every read in a block, while liveness wants only
/// the reads that happen before the block writes the register itself.
/// </summary>
public partial class SsaForm
{
    /// <summary>
    /// Whether the two ends of a phi's edge cannot be holding the same value, whatever the phi says.
    /// </summary>
    /// <remarks>
    /// Taking a graph out of SSA writes, on each edge into a join, a copy of what that path had into the local
    /// the join reads. Where the register was reused - which is most of a long method, since compiled code
    /// takes a register back the moment the value in it is finished with - the two paths carry unrelated
    /// things, and the copy claims one is the other. In one method here that is sixty-three copies: a runtime
    /// method assigned an integer, a class pointer assigned a struct.
    ///
    /// A copy like that cannot be the value moving. If the join really read what this path put there under the
    /// type the other path gave it, the program would be ill-typed - so on this path the local is simply not
    /// read afterwards, and writing it is only how the register got reused. Leaving the copy out is what
    /// splits the two uses apart; keeping it costs the declaration, because a cast between unrelated types is
    /// not something the language will say, and with the declaration goes every later statement that used it.
    ///
    /// Only where both types are known and cannot be reconciled. Two references meeting is ordinary - a base
    /// and something derived from it - and says nothing.
    /// </remarks>
    private static bool CannotBeTheSameValue(object destination, object source)
    {
        if (destination is not LocalVariable { Type: { } held } || source is not LocalVariable { Type: { } given })
            return false;

        if (held.FullName == given.FullName)
            return false;

        //A number is never a reference, and one kind of number is never another.
        if (held.IsValueType || given.IsValueType)
            return true;

        //The structures the runtime keeps to itself are never anything the program holds.
        return IsRuntimeStructure(held) != IsRuntimeStructure(given);
    }

    /// <summary>Whether the type stands for something the runtime keeps rather than for a value the program has.</summary>
    private static bool IsRuntimeStructure(TypeAnalysisContext type)
        => type is RuntimeClassTypeAnalysisContext or RuntimeMethodInfoAnalysisContext
            or RgctxTableTypeAnalysisContext or MethodRgctxTableTypeAnalysisContext
            or StaticFieldStorageTypeAnalysisContext;

    /// <summary>The register numbers that can still be read on entry to each block.</summary>
    private static Dictionary<Block, HashSet<int>> LiveInRegisters(ISILControlFlowGraph graph)
    {
        var exposed = new Dictionary<Block, HashSet<int>>();
        var written = new Dictionary<Block, HashSet<int>>();

        foreach (var block in graph.Blocks)
        {
            var readsFirst = new HashSet<int>();
            var writes = new HashSet<int>();

            foreach (var instruction in block.Instructions)
            {
                foreach (var read in RegistersRead(instruction))
                    if (!writes.Contains(read))
                        readsFirst.Add(read);

                if (instruction.Destination is Register destination)
                    writes.Add(destination.Number);
            }

            exposed[block] = readsFirst;
            written[block] = writes;
        }

        var liveIn = new Dictionary<Block, HashSet<int>>();

        foreach (var block in graph.Blocks)
            liveIn[block] = new HashSet<int>(exposed[block]);

        // Liveness travels backwards along the edges, so this goes round the graph until a round adds nothing.
        bool grew;

        do
        {
            grew = false;

            for (var i = graph.Blocks.Count - 1; i >= 0; i--)
            {
                var block = graph.Blocks[i];

                foreach (var successor in block.Successors)
                    foreach (var register in liveIn[successor])
                        if (!written[block].Contains(register) && liveIn[block].Add(register))
                            grew = true;
            }
        } while (grew);

        return liveIn;
    }

    /// <summary>
    /// The registers an instruction reads. Everything it names except the one it is writing to - and an
    /// address it writes through is read, not written, so a store reads the register holding the address.
    /// </summary>
    private static IEnumerable<int> RegistersRead(Instruction instruction)
    {
        var destination = instruction.Destination is Register written0 ? written0 : (Register?)null;
        var written = false;

        foreach (var operand in instruction.Operands)
        {
            switch (operand)
            {
                case Register register:
                    // The destination is named once, in its own slot; the same register named again is read.
                    if (!written && destination is { } target && register.Number == target.Number && register.Version == target.Version)
                        written = true;
                    else
                        yield return register.Number;
                    break;

                case MemoryOperand memory:
                    if (memory.Base is Register baseRegister)
                        yield return baseRegister.Number;
                    if (memory.Index is Register indexRegister)
                        yield return indexRegister.Number;
                    break;
            }
        }
    }
}
