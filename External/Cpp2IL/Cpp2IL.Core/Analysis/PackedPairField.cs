using System.Collections.Generic;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// A struct of two words travels in one register, and its halves are its fields.
/// </summary>
/// <remarks>
/// <para>
/// <c>BoardLogic.SubCellRef</c> is <c>{ int cellIndex; int subIndex; }</c> - eight bytes, so it is passed and
/// returned whole in one <c>x</c> register, and nothing said which half is which:
/// </para>
/// <code>
/// Call        List&lt;SubCellRef&gt;.get_Item, v64 (SubCellRef), ..
/// Move        v146 (CellData), [this._boardData + 20 + v64*8]   ; really .cellIndex
/// ShiftRight  v153, v64, 32                                     ; really .subIndex
/// </code>
/// <para>
/// which came out as <c>_boardData[(object)subCellRef]</c>. The same defect one width down from
/// <see cref="PackedByteField"/>, and the same answer: the shift says which half, the metadata says which
/// field is at that distance, and neither is inferred.
/// </para>
/// <para>
/// Only the two shapes that can mean nothing else - the top half brought down by a shift of thirty-two, and
/// the register used as an array subscript, which indexes with the low word. A use of the whole value is
/// left exactly as it is, because that is what it is.
/// </para>
/// </remarks>
public static class PackedPairField
{
    public static void Run(MethodAnalysisContext method)
    {
        if (method.ControlFlowGraph is not { } graph)
            return;

        var halves = new Dictionary<TypeAnalysisContext, (FieldAnalysisContext Low, FieldAnalysisContext High)>();

        foreach (var instruction in graph.Instructions)
        {
            for (var i = 0; i < instruction.Operands.Count; i++)
            {
                //The top half, brought down.
                if (i == 1 && instruction is { OpCode: OpCode.ShiftRight, Operands: [{ } result, LocalVariable { Type: { } held } packed, { } places] }
                    && Places(places) == 32
                    && Pair(held, halves) is { } pairShifted)
                {
                    instruction.OpCode = OpCode.Move;
                    instruction.Operands = [result, new FieldReference(pairShifted.High, packed, 4)];
                    break;
                }

                //And the low half, which is what an array subscript in such a register is: the architecture
                //extends the word rather than using the whole register, and the lifter drops the extension.
                if (instruction.Operands[i] is MemoryOperand { Index: LocalVariable { Type: { } indexed } subscript } memory
                    && Pair(indexed, halves) is { } pairIndexed)
                {
                    memory.Index = new FieldReference(pairIndexed.Low, subscript, 0);
                    instruction.Operands[i] = memory;
                }
            }
        }
    }

    /// <summary>The two four-byte fields a struct of exactly two words holds, if that is what it is.</summary>
    private static (FieldAnalysisContext Low, FieldAnalysisContext High)? Pair(
        TypeAnalysisContext type, Dictionary<TypeAnalysisContext, (FieldAnalysisContext, FieldAnalysisContext)> known)
    {
        if (known.TryGetValue(type, out var already))
            return already;

        FieldAnalysisContext? low = null;
        FieldAnalysisContext? high = null;
        var members = 0;

        if (type is { IsValueType: true, IsEnumType: false } && type.Namespace != nameof(System))
        {
            foreach (var field in type.Fields)
            {
                if (field.IsStatic)
                    continue;

                members++;

                if (!IsAWord(field.FieldType))
                    return null;

                if (field.BackingData?.FieldOffset == 0)
                    low = field;
                else if (field.BackingData?.FieldOffset == 4)
                    high = field;
            }
        }

        if (members != 2 || low is null || high is null)
            return null;

        known[type] = (low, high);
        return (low, high);
    }

    private static bool IsAWord(TypeAnalysisContext? type)
        => type is not null
            && (type.IsEnumType || type.FullName is "System.Int32" or "System.UInt32" or "System.Single");

    private static int? Places(object operand)
    {
        try
        {
            return operand is string or LocalVariable or Register or MemoryOperand
                ? null
                : (int)System.Convert.ToInt64(operand);
        }
        catch (System.Exception)
        {
            return null;
        }
    }
}
