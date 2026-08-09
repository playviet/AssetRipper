using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Puts back together a struct of floats that was assigned to a field one member at a time.
/// </summary>
/// <remarks>
/// <para>
/// The inverse of <see cref="WideFieldStore"/>, which splits one store that covered two fields. Here the
/// compiler did the opposite: <c>_lastSize = new Vector2(rect.width, rect.height)</c> is two stores, four
/// bytes each, and only the first lands on a field. The second is <c>[this + 0x44]</c> - a distance *inside*
/// the <c>Vector2</c>, which matches no field at all - so it came out as unmanaged memory, and the first
/// came out as <c>_lastSize = (Vector2)rect.width</c>, which does not compile.
/// </para>
/// <para>
/// The stores are not adjacent: the call that produces each member sits between them. So the members are
/// looked for a bounded way ahead in the same block, and only where nothing in between writes the field
/// again - anything else is left exactly as it was.
/// </para>
/// </remarks>
public static class StructFieldStoreSplit
{
    /// <summary>How far ahead a member may be, which is one call and its operands per member.</summary>
    private const int Window = 12;

    public static void Run(MethodAnalysisContext method)
    {
        if (method.ControlFlowGraph is not { } graph)
            return;

        foreach (var block in graph.Blocks)
        {
            for (var at = 0; at < block.Instructions.Count; at++)
                Assemble(block.Instructions, at);
        }
    }

    private static void Assemble(List<Instruction> instructions, int at)
    {
        if (instructions[at] is not { OpCode: OpCode.Move, Operands: [FieldReference into, { } first] }
            || HomogeneousFloatStruct.Count(into.Field.FieldType) is not { } floats
            || floats < 2
            || into.Local is not { } holder
            || !IsAFloat(first))
        {
            return;
        }

        if (FloatConstructor(into.Field.FieldType, floats) is not { } constructor)
            return;

        var parts = new List<object> { first };
        var written = new List<int>();

        for (var i = at + 1; i < instructions.Count && i <= at + Window && parts.Count < floats; i++)
        {
            if (instructions[i] is not { OpCode: OpCode.Move, Operands: [{ } destination, { } value] })
                continue;

            //Anything that writes this same field again ends the search: what follows is a second assignment,
            //not the rest of this one.
            if (destination is FieldReference other && other.Field.Name == into.Field.Name)
                return;

            if (!IsMemberAt(destination, holder, into.Offset + parts.Count * 4) || !IsAFloat(value))
                continue;

            parts.Add(value);
            written.Add(i);
        }

        if (parts.Count != floats)
            return;

        instructions[at].Operands[1] = new FloatStructAssembly(constructor, parts);

        foreach (var member in written)
        {
            instructions[member].OpCode = OpCode.Nop;
            instructions[member].Operands = [];
        }
    }

    /// <summary>Whether a destination is the place that far into the same object.</summary>
    /// <remarks>
    /// A member past the first lands between two fields, so nothing resolved it and it is still a memory
    /// operand. The base has to be the very local the field was reached through, or this would gather a
    /// member of some other object that happens to sit at the same distance.
    /// </remarks>
    private static bool IsMemberAt(object destination, LocalVariable holder, int offset)
        => destination is MemoryOperand { Index: null, Scale: 0, Base: LocalVariable through } memory
            && ReferenceEquals(through, holder)
            && memory.Addend == offset;

    private static bool IsAFloat(object part) => part switch
    {
        float or double => true,
        FieldReference field => field.Field.FieldType.FullName == "System.Single",
        LocalVariable { Type: { } type } => type.FullName == "System.Single",
        _ => false,
    };

    private static MethodAnalysisContext? FloatConstructor(TypeAnalysisContext type, int floats)
        => type.Methods.FirstOrDefault(m => m is { Name: ".ctor", IsStatic: false }
            && m.Parameters.Count == floats
            && m.Parameters.All(p => p.ParameterType.FullName == "System.Single"));
}
