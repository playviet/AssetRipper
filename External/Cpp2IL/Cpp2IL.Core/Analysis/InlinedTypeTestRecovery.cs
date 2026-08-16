using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Puts back the <c>is</c> that a walk of the class hierarchy was compiled from.
/// </summary>
/// <remarks>
/// <para>
/// Asking whether an object is of a type is small enough that il2cpp inlines it rather than calling the
/// runtime, and what it inlines is a lookup in the chain of base classes every class carries:
/// </para>
/// <code>
/// CheckGreaterOrEqual _, [klass + 0x130], [target + 0x130]   // deep enough to be one?
/// ShiftLeft shifted,    [target + 0x130], 3                  // the target's depth, in pointers
/// Add       entry,      [klass + 0xC8], shifted              // typeHierarchy + that
/// CheckEqual answer,    [entry - 8], typeof(Target)          // and is the class there the target?
/// </code>
/// <para>
/// Nothing in it names a test, so all four reads came out as unmanaged memory and the statement they belonged
/// to went with them - and since the answer feeds a branch, so did everything that branch guarded.
/// <c>typeHierarchyDepth</c> at 0x130 and <c>typeHierarchy</c> at 0xC8 are named by
/// <see cref="Il2CppClassLayout"/>, computed from the structure Unity ships; before that this shape could
/// only be guessed at.
/// </para>
/// <para>
/// The comparison becomes the call the rest of the pipeline already knows how to write - the same one an
/// un-inlined cast leaves behind - so <c>IlGenerator</c> emits a real <c>isinst</c> and the four reads are
/// left for the collector.
/// </para>
/// </remarks>
public static class InlinedTypeTestRecovery
{
    private const string ObjectIsInstance = "il2cpp_vm_object_is_inst";

    /// <summary>An entry is a pointer wide, and the walk indexes one past the class it wants.</summary>
    private const int EntriesToBytes = 3;
    private const long PreviousEntry = -8;

    public static void Run(MethodAnalysisContext method)
    {
        if (method.ControlFlowGraph is not { } graph)
            return;

        //The latest assignment to each value, kept as the walk goes forward rather than gathered once. This
        //pass runs after single assignment form is taken apart, so the pseudo-register the shift lands in is
        //reused by every test in the method: asking for a value's only definition finds several and refuses
        //them all, which is why half of these were missed the first time. What a comparison reads is whatever
        //was last written before it.
        var latest = new Dictionary<LocalVariable, Instruction>();

        foreach (var block in graph.Blocks)
        {
            for (var i = 0; i < block.Instructions.Count; i++)
            {
                if (Match(block.Instructions[i], latest) is not var (answer, tested, target, equal))
                {
                    Guard(block.Instructions[i]);

                    if (block.Instructions[i].Destination is LocalVariable written)
                        latest[written] = block.Instructions[i];

                    continue;
                }

                var value = new LocalVariable($"instance{method.Locals.Count}", new Register(null, "ISINST"), target);
                method.Locals.Add(value);

                //The call the pipeline already understands, and then the question the comparison was asking.
                //An equality against the entry means the object *is* one, so the answer is that the call
                //returned something.
                block.Instructions[i] = new Instruction(block.Instructions[i].Index, OpCode.Call,
                    ObjectIsInstance, value, tested, target);

                block.Instructions.Insert(i + 1, new Instruction(block.Instructions[i].Index,
                    equal ? OpCode.CheckNotEqual : OpCode.CheckEqual, answer, value, 0));

                //And what the answer is for: below the branch the object IS one, so every statement there
                //should read the narrowed value rather than the tested one. See TypeTestNarrowing.
                TypeTestNarrowing.Run(graph, block, answer, tested, value);

                i++;
            }
        }
    }

    /// <summary>
    /// Answers the fast path in front of the walk, which the walk itself makes redundant.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The inlined test opens by asking whether the object's chain is deep enough to hold the target at all:
    /// </para>
    /// <code>
    /// CheckLess _, [obj-&gt;klass + 0x130], [typeof(T) + 0x130]   // and if so, fail outright
    /// </code>
    /// <para>
    /// Both of its reads are of a runtime structure and neither has a name, so the guard survived as two
    /// <c>unmanaged</c> markers and an <c>if</c> in front of a test that was otherwise recovered whole. It
    /// decides nothing once the walk below it has become an <c>isinst</c>: the guard's failure edge and the
    /// walk's are the same edge, and a shallower chain fails the walk anyway. So the answer is written down -
    /// not deep enough is <b>false</b> - and the branch and its two reads go with the rest.
    /// </para>
    /// </remarks>
    private static void Guard(Instruction instruction)
    {
        if (instruction is not { OpCode: OpCode.CheckLess or OpCode.CheckGreaterOrEqual, Operands: [LocalVariable answer, { } mine, { } theirs] }
            || !IsDepth(mine) || !IsDepth(theirs))
        {
            return;
        }

        //`CheckLess` asks "shallower than the target", which is what fails; the other way round is the same
        //question answered the other way. Read before the opcode is overwritten.
        var shallower = instruction.OpCode == OpCode.CheckLess;

        instruction.OpCode = OpCode.Move;
        instruction.Operands = [answer, shallower ? 0 : 1];
    }

    private static bool IsDepth(object operand)
        => operand is MemoryOperand { Index: null, Scale: 0, Addend: Il2CppClassLayout.TypeHierarchyDepth };

    /// <summary>
    /// The comparison that ends the walk, and what it is really asking.
    /// </summary>
    private static (LocalVariable Answer, object Tested, TypeAnalysisContext Target, bool Equal)? Match(
        Instruction instruction, Dictionary<LocalVariable, Instruction> definitions)
    {
        if (instruction.OpCode is not (OpCode.CheckEqual or OpCode.CheckNotEqual)
            || instruction.Operands.Count < 3
            || instruction.Operands[0] is not LocalVariable answer)
            return null;

        var read = instruction.Operands[1] as MemoryOperand? ?? instruction.Operands[2] as MemoryOperand?;
        var against = instruction.Operands[1] is MemoryOperand ? instruction.Operands[2] : instruction.Operands[1];

        if (read is not { Index: null, Scale: 0, Addend: PreviousEntry, Base: LocalVariable entry }
            || Named(against) is not { } target)
            return null;

        //The entry is the class's hierarchy, stepped along by the depth of the type being asked about.
        if (Single(entry, definitions) is not { OpCode: OpCode.Add, Operands: [_, { } hierarchy, LocalVariable scaled] } sum)
            return null;

        var chain = hierarchy as MemoryOperand? ?? sum.Operands[2] as MemoryOperand?;

        if (chain is not { Index: null, Scale: 0, Addend: Il2CppClassLayout.TypeHierarchy, Base: LocalVariable klass })
            return null;

        if (Single(scaled, definitions) is not { OpCode: OpCode.ShiftLeft, Operands: [_, { } depth, { } by] }
            || Constant(by) != EntriesToBytes
            || depth is not MemoryOperand { Index: null, Scale: 0, Addend: Il2CppClassLayout.TypeHierarchyDepth })
            return null;

        //And the class walked is the one belonging to the object being asked about.
        if (Single(klass, definitions) is not { OpCode: OpCode.Move, Operands: [_, MemoryOperand { Index: null, Scale: 0, Addend: 0, Base: { } tested }] })
            return null;

        return (answer, tested, target, instruction.OpCode == OpCode.CheckEqual);
    }

    /// <summary>The type an operand names outright, however it carries it.</summary>
    private static TypeAnalysisContext? Named(object? operand) => operand switch
    {
        RuntimeClassTypeAnalysisContext { RepresentedType: var represented } => represented,
        LocalVariable { Type: RuntimeClassTypeAnalysisContext { RepresentedType: var held } } => held,
        TypeAnalysisContext type => type,
        _ => null,
    };

    private static long? Constant(object operand) => operand switch
    {
        int i => i,
        long l => l,
        uint u => u,
        ulong ul => (long)ul,
        _ => null,
    };

    private static Instruction? Single(LocalVariable local, Dictionary<LocalVariable, Instruction> definitions)
        => definitions.GetValueOrDefault(local);
}
