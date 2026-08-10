using System.Collections.Generic;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using LibCpp2IL.BinaryStructures;

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
    /// <summary>Every local a phi decides, so a consumer's type is not forced back on to one.</summary>
    internal static HashSet<LocalVariable> PhiDestinations(ISILControlFlowGraph cfg)
    {
        var merged = new HashSet<LocalVariable>();

        foreach (var block in cfg.Blocks)
            foreach (var instruction in block.Instructions)
                if (instruction.OpCode == OpCode.Phi && instruction.Operands.Count > 0
                    && instruction.Operands[0] is LocalVariable destination)
                    merged.Add(destination);

        return merged;
    }

    /// <summary>Whether control leaving this block by an edge is impossible, because the block throws.</summary>
    /// <remarks>
    /// <para>
    /// The call that raises a null-reference exception becomes an <see cref="OpCode.Throw"/> only after the
    /// graph was built, and nothing then takes the block's fall-through edge away. Taking single assignment
    /// form apart therefore writes a phi copy on an edge control can never follow -
    /// <c>IL_052c: num9 = num; throw new NullReferenceException();</c> - and that copy is the one thing
    /// keeping a whole chain of dead values alive: in <c>BoardController::InitBoard</c> it holds up the
    /// address of a field the write barrier wanted, and all fourteen of that method's commented statements
    /// are the cascade. Around 300 of these edges exist in the game.
    /// </para>
    /// <para>
    /// The whole block is searched rather than just its terminator: block merging can leave the throw in the
    /// middle, and then the copies land after it. The edge itself is left alone - phi operands are aligned
    /// positionally to <c>Block.Predecessors</c>, so removing a predecessor here would misalign every phi.
    /// </para>
    /// </remarks>
    private static bool NeverReachesItsSuccessors(Block block)
    {
        foreach (var instruction in block.Instructions)
            if (instruction.OpCode == OpCode.Throw)
                return true;

        return false;
    }

    /// <summary>
    /// Whether two of a phi's inputs cannot be holding the same value, so the phi says nothing about what
    /// its result is.
    /// </summary>
    /// <remarks>
    /// A register is taken back the moment the value in it is finished with, so a phi at a join often merges
    /// two unrelated things. Reading a type off the first input that has one then gives the result whichever
    /// role happens to come first in the operand list: `Corpus::Tally`'s running total was typed `string[]`,
    /// after the array parameter the same register carried through the loop before it.
    /// </remarks>
    internal static bool InputsDisagree(Instruction phi)
    {
        for (var i = 1; i < phi.Operands.Count; i++)
            for (var j = i + 1; j < phi.Operands.Count; j++)
                if (CannotBeTheSameValue(phi.Operands[i], phi.Operands[j]))
                    return true;

        return false;
    }

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

        //Reading a slot gives what is in the slot. The local standing for an out parameter is typed as the
        //address it was handed over as - `System.Int32&` - so where the callee's answer is read straight into
        //the register the result leaves in, the two ends of the edge differ by nothing but the `&`. That is
        //the value moving. Dropped as a reuse, `ParseOrDefault` returned its fallback for input it had just
        //parsed: `int.TryParse(raw, out var _)`, with the answer thrown away and no marker anywhere.
        if (Pointee(held) == Pointee(given))
            return false;

        //Two numbers meeting is ordinary, and for the same reason two references are: **ISIL carries no
        //width**, so which integer type each end was given says more about which pass typed it than about
        //what the register holds. `Corpus::Guarded` returns `10 / (n % 3)` on one path and `1` on the other,
        //and the divide's result was typed `Int64` against an `Int32` return - so the edge was dropped as a
        //reuse, the phi kept only the constant, and the method returned 1 for every input while the whole
        //division died as unread. A conversion between two numbers is something the language will say.
        if (IsNumber(held) && IsNumber(given))
            return false;

        //A number is never a reference, and a struct is never a different struct.
        if (held.IsValueType || given.IsValueType)
            return true;

        //The structures the runtime keeps to itself are never anything the program holds.
        return IsRuntimeStructure(held) != IsRuntimeStructure(given);
    }

    /// <summary>Whether the type is one of the numbers a register can hold without any conversion.</summary>
    private static bool IsNumber(TypeAnalysisContext type)
        => type.Type is Il2CppTypeEnum.IL2CPP_TYPE_I1 or Il2CppTypeEnum.IL2CPP_TYPE_U1
            or Il2CppTypeEnum.IL2CPP_TYPE_I2 or Il2CppTypeEnum.IL2CPP_TYPE_U2
            or Il2CppTypeEnum.IL2CPP_TYPE_I4 or Il2CppTypeEnum.IL2CPP_TYPE_U4
            or Il2CppTypeEnum.IL2CPP_TYPE_I8 or Il2CppTypeEnum.IL2CPP_TYPE_U8
            or Il2CppTypeEnum.IL2CPP_TYPE_I or Il2CppTypeEnum.IL2CPP_TYPE_U
            or Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN or Il2CppTypeEnum.IL2CPP_TYPE_CHAR;

    /// <summary>The type without the marks that say it is being reached through an address.</summary>
    private static string Pointee(TypeAnalysisContext type) => type.FullName?.TrimEnd('&', '*') ?? "";

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
