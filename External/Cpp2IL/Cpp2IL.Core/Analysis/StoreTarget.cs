using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Where an instruction writes - which <see cref="Instruction.Destination"/> does not answer for a store.
/// </summary>
/// <remarks>
/// <para>
/// <c>Destination</c> returns <c>null</c> whenever operand zero is a <see cref="FieldReference"/> or a
/// <see cref="MemoryOperand"/>, because <c>Instruction.IsConstantValue</c> counts both as constants. That is
/// to say it is null for <b>exactly the stores</b> that the passes which carry a value forward have to
/// notice, and two of them - <see cref="Simplifier"/>'s <c>Invalidates</c> and
/// <see cref="FieldReadSinking"/>'s <c>Harmless</c> - asked it. Both were written to refuse to carry a field
/// read past a write to that field, and neither had ever done so.
/// </para>
/// <para>
/// <c>Corpus+&lt;Steps&gt;d__73::MoveNext</c> is the clean statement of it. The state machine loads
/// <c>&lt;&gt;1__state</c> at entry, stores -1 over it, and then compares the loop counter - which the
/// compiler keeps in the very register the load left it in, because at state 0 that register already holds
/// 0. Carried past the store, the comparison read the field a second time and got -1, so the first element
/// of every <c>yield</c> loop came out as <c>(-1) * (-1)</c> rather than <c>0 * 0</c>: <c>Steps(111)</c>
/// began {1,1,4,…} where the source says {0,1,4,…} and <c>SumSteps</c> was one too many - whole, with no
/// marker, rated <c>full</c>.
/// </para>
/// <para>
/// Read off the opcode rather than by correcting <c>IsConstantValue</c>, which drives <c>Sources</c>, dead
/// code elimination and the stack analysis and whose blast radius is the whole analysis. Calls and jumps are
/// excluded because their operand zero is a target rather than a destination.
/// </para>
/// <para>Set <c>STALEREAD_OFF=1</c> to measure the same build without it.</para>
/// </remarks>
public static class StoreTarget
{
    private static readonly bool Off = System.Environment.GetEnvironmentVariable("STALEREAD_OFF") == "1";

    public static object? Of(Instruction instruction)
    {
        if (instruction.Destination is { } destination)
            return destination;

        return !Off
            && instruction.OpCode is OpCode.Move or OpCode.Add or OpCode.Subtract or OpCode.Multiply
                or OpCode.Divide or OpCode.ShiftLeft or OpCode.ShiftRight or OpCode.And or OpCode.Or
                or OpCode.Xor or OpCode.Not or OpCode.Negate or OpCode.Newobj or OpCode.Select
            && instruction.Operands.Count > 0
            && instruction.Operands[0] is FieldReference or MemoryOperand
            ? instruction.Operands[0]
            : null;
    }

    /// <summary>
    /// Whether two references name the same field. A field of a generic type is named through the arguments
    /// the type has where it is read, and that naming is worked out fresh at each place - so two references
    /// to one field are never the same object, and comparing objects would let a read be carried past the
    /// write that makes it stale.
    /// </summary>
    public static bool IsTheSameField(FieldAnalysisContext left, FieldAnalysisContext right)
        => ReferenceEquals(Underlying(left), Underlying(right));

    private static FieldAnalysisContext Underlying(FieldAnalysisContext field)
        => (field as ConcreteGenericFieldAnalysisContext)?.BaseFieldContext ?? field;
}
