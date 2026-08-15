using System.Collections.Generic;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// A body shared between instantiations is only ever entered with reference ones, so asking whether its
/// <c>T</c> is a value type has an answer.
/// </summary>
/// <remarks>
/// <para>
/// il2cpp compiles one body for every reference instantiation of a generic and a separate one for each value
/// instantiation - that is what sharing is. The shared body still has to hand a <c>T</c> around, and it does
/// so by asking the class each time:
/// </para>
/// <code>
/// CheckLess v109 (Boolean), [v98 (Il2CppClass&lt;T&gt;)+28], 0     // the value-type bit
/// Select    v110 (T), v109, &amp;value, value
/// </code>
/// <para>
/// <c>Il2CppType</c>'s last bit is <c>valuetype</c>, so a signed test of that word is that bit, and in a body
/// only reference instantiations reach it is <b>false</b>. Answering it settles the select - the value is
/// passed, not pointed at - and takes the class read with it, which is the last thing holding the runtime
/// generic context alive.
/// </para>
/// <para>
/// Only where the class is a <em>generic parameter's</em>. A real type's value-type bit is a fact about that
/// type and is not this question.
/// </para>
/// </remarks>
public static class SharingMeansAReference
{
    /// <summary>Where a class's <c>byval_arg</c> keeps the word the type bits and the flags share.</summary>
    private const long TypeBits = Il2CppClassLayout.ByValArg + 8;

    public static void Run(MethodAnalysisContext method)
    {
        if (method.ControlFlowGraph is not { } graph)
            return;

        var settled = new Dictionary<LocalVariable, long>();

        //Which of the settled locals descend from the value-type test, as opposed to being any old constant.
        //Only those may decide a comparison here: folding every comparison of two numbers is
        //`ConstantFolding`'s job and not this pass's, and doing it here would make the round unattributable.
        var fromTheTest = new HashSet<LocalVariable>();

        foreach (var instruction in graph.Instructions)
        {
            //Two ways the compiler asks the same question: a signed test of the word, and a mask of its top
            //bit. Both answer nought where the class is a shared body's own parameter.
            if (instruction is { Operands: [LocalVariable answer, { } left, { } right] }
                && (instruction.OpCode == OpCode.CheckLess && Constant(right) is 0
                    || instruction.OpCode == OpCode.And && Constant(right) is 0x80000000)
                && IsASharedClassesTypeBits(left))
            {
                instruction.OpCode = OpCode.Move;
                instruction.Operands = [answer, 0];
                settled[answer] = 0;
                fromTheTest.Add(answer);
                continue;
            }

            //Carried through the copies single assignment form leaves between the answer and the select.
            if (instruction is { OpCode: OpCode.Move, Operands: [LocalVariable copy, { } from] })
            {
                if (Constant(from) is { } number)
                {
                    settled[copy] = number;
                }
                else if (from is LocalVariable earlier && settled.TryGetValue(earlier, out var known))
                {
                    settled[copy] = known;

                    if (fromTheTest.Contains(earlier))
                        fromTheTest.Add(copy);
                }
                else
                {
                    settled.Remove(copy);
                    fromTheTest.Remove(copy);
                }

                continue;
            }

            //A comparison both of whose sides are settled is decided, and the branch reading it with it.
            //**This pass used to settle the test and stop there.** `SharingMeansAReference` rewrote the
            //value-type test into `Move v653, 0` and left `CheckNotEqual v654, v653, 0` standing;
            //`ConstantBranchFolding` asks for numbers at the comparison itself, so it could not fold that,
            //and the value-type arm of the branch stayed reachable. That arm is the one that hands the
            //invoker frame an **address** where the live arm hands it the value the address points at, so
            //the two edge copies into the merged local carry different things and the address wins:
            //`IDictionaryExtension::TryGetKeyByValue` got `W val5 = (W)(obj - 40L);` for its `value`
            //argument. This is the rule [[il2cpp-the-generic-seam-is-generic-methods]] already states -
            //**mark and rewrite the instruction the branch's condition is defined by, not the one you
            //happened to be looking at** - applied one link further along than it was the first time.
            if (Compare(instruction.OpCode) is { } compare && instruction.Operands is [LocalVariable verdict, { } left2, { } right2]
                && (IsFromTheTest(left2, fromTheTest) || IsFromTheTest(right2, fromTheTest))
                && Answered(left2, settled) is { } lhs && Answered(right2, settled) is { } rhs)
            {
                //**Left as a comparison, not turned into a Move.** `ConstantBranchFolding.Evaluate` decides a
                //branch by re-evaluating the comparison its condition is defined by, so it needs three
                //operands and an opcode it recognises; a `Move` of the answer has two and tells it nothing.
                //Substituting the constants and leaving the opcode alone is what it is built to read.
                instruction.Operands = [verdict, lhs, rhs];
                settled[verdict] = compare(lhs, rhs) ? 1 : 0;
                fromTheTest.Add(verdict);

                //**And rewriting it is still not enough; it has to be marked.** That pass will not fold a
                //branch unless some pass has said it settled the condition's definition - otherwise a
                //register that merely happens to hold a number would decide a branch. Doing the rewrite and
                //forgetting the mark leaves the branch standing and the whole point of the fold unrealised.
                ConstantBranchFolding.HasSettledAnswer(instruction);
                continue;
            }

            //A choice whose condition is settled is not a choice.
            if (instruction is { OpCode: OpCode.Select, Operands: [{ } destination, { } condition, { } whenTrue, { } whenFalse] }
                && Answered(condition, settled) is { } decided)
            {
                instruction.OpCode = OpCode.Move;
                instruction.Operands = [destination, decided != 0 ? whenTrue : whenFalse];
            }
        }
    }

    private static bool IsFromTheTest(object operand, HashSet<LocalVariable> fromTheTest)
        => operand is LocalVariable local && fromTheTest.Contains(local);

    /// <summary>What a comparison opcode decides, where both of its sides are known.</summary>
    private static System.Func<long, long, bool>? Compare(OpCode code) => code switch
    {
        OpCode.CheckEqual => (a, b) => a == b,
        OpCode.CheckNotEqual => (a, b) => a != b,
        OpCode.CheckLess => (a, b) => a < b,
        OpCode.CheckLessOrEqual => (a, b) => a <= b,
        OpCode.CheckGreater => (a, b) => a > b,
        OpCode.CheckGreaterOrEqual => (a, b) => a >= b,
        _ => null,
    };

    private static long? Answered(object condition, Dictionary<LocalVariable, long> settled)
        => Constant(condition) ?? (condition is LocalVariable local && settled.TryGetValue(local, out var known) ? known : null);

    private static bool IsASharedClassesTypeBits(object operand)
        => operand is MemoryOperand { Index: null, Scale: 0, Base: LocalVariable klass } read
            && read.Addend == TypeBits
            && klass.Type is RuntimeClassTypeAnalysisContext { RepresentedType: GenericParameterTypeAnalysisContext };

    private static long? Constant(object operand)
        => operand switch
        {
            int i => i,
            uint u => u,
            long l => l,
            ulong ul => (long)ul,
            bool b => b ? 1 : 0,
            _ => null,
        };
}
