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
                continue;
            }

            //Carried through the copies single assignment form leaves between the answer and the select.
            if (instruction is { OpCode: OpCode.Move, Operands: [LocalVariable copy, { } from] })
            {
                if (Constant(from) is { } number)
                    settled[copy] = number;
                else if (from is LocalVariable earlier && settled.TryGetValue(earlier, out var known))
                    settled[copy] = known;
                else
                    settled.Remove(copy);

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
