using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// A struct returned through <c>x8</c> fills the whole slot, not the field at the front of it.
/// </summary>
/// <remarks>
/// <para>
/// A composite over sixteen bytes is returned indirectly: the caller passes the address of a slot in
/// <c>x8</c> and the callee writes it. The binding already works - the call's destination is that slot - but
/// the destination is a memory operand at distance nought, and field resolution names distance nought of a
/// struct as its <b>first member</b>:
/// </para>
/// <code>
/// Call List&lt;int&gt;::GetEnumerator, v42._list, values      // the whole enumerator, called `._list`
/// Call Enumerator&lt;int&gt;::MoveNext,  …, v42                // …run on a slot nothing wrote
/// </code>
/// <para>
/// So the recovered source calls <c>GetEnumerator()</c>, throws the answer away, and iterates
/// <c>default(List&lt;int&gt;.Enumerator)</c> - which is empty, so the loop never runs. <c>Corpus::Total</c>
/// and <c>Corpus::Tally</c> are both that; every <c>foreach</c> over a <c>List</c> or a <c>Dictionary</c> in
/// the game is the same shape.
/// </para>
/// <para>
/// The rule is exact rather than a guess about what a distance means: where the callee <b>returns
/// indirectly</b> and the slot is declared as the type it returns, a write at distance nought is the return
/// itself. A distance that is not nought, or a slot of some other type, is a genuine field write and is left
/// alone - and <c>FrontMember</c>, which named it, is right everywhere it is not this.
/// </para>
/// <para>Set <c>BUFFERWHOLE_OFF=1</c> to measure the same build without it.</para>
/// </remarks>
public static class TheBufferIsTheWholeStruct
{
    private static readonly bool Off = System.Environment.GetEnvironmentVariable("BUFFERWHOLE_OFF") == "1";

    public static void Run(MethodAnalysisContext method)
    {
        if (Off || method.ControlFlowGraph is not { } graph)
            return;

        foreach (var instruction in graph.Instructions)
        {
            if (instruction is not { OpCode: OpCode.Call, Operands: [MethodAnalysisContext callee, { } destination, ..] }
                || destination is not FieldReference { Offset: 0 } front
                || front.Local.Type is not { } slot
                || callee.ReturnType is not { } returned
                || slot.FullName != returned.FullName
                || !Aapcs64.ReturnsIndirectly(callee))
            {
                continue;
            }

            instruction.Operands[1] = front.Local;
        }
    }
}
