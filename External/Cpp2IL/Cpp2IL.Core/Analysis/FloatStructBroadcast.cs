using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// A struct of floats filled from one immediate, which the architecture broadcasts into every lane.
/// </summary>
/// <remarks>
/// <para>
/// <c>initColor = Color.white</c> is two instructions - the immediate goes into all four lanes at once and
/// the whole sixteen bytes are stored:
/// </para>
/// <code>
/// FMOV V0.4S, 0x0    ; 1.0f, broadcast across four lanes
/// STR  V0, [X8]
/// </code>
/// <para>
/// The lifter keeps the immediate and loses the broadcast, so the ISIL says <c>Move this.initColor, 1d</c> -
/// one number where a <c>Color</c> belongs - and it comes out as <c>initColor = (Color)1.0;</c>, which does
/// not compile.
/// </para>
/// <para>
/// **The broadcast is the only reading.** One scalar cannot fill four fields any other way: a scalar store
/// writes four bytes and the remaining fields would each need a store of their own, which would be in the
/// ISIL. So where a place declared to hold a struct of floats is assigned a single number, every field of it
/// is that number.
/// </para>
/// </remarks>
public static class FloatStructBroadcast
{
    public static void Run(MethodAnalysisContext method)
    {
        if (method.ControlFlowGraph is not { } graph)
            return;

        foreach (var instruction in graph.Instructions)
        {
            if (instruction is not { OpCode: OpCode.Move, Operands: [{ } into, { } value] })
                continue;

            var held = into switch
            {
                FieldReference field => field.Field.FieldType,
                LocalVariable local => local.Type,
                _ => null,
            };

            if (held is null || HomogeneousFloatStruct.Count(held) is not { } floats || floats < 2
                || Constructor(held, floats) is not { } constructor
                || Fields(value, floats) is not { } fields)
                continue;

            instruction.Operands = [into, new FloatStructAssembly(constructor, fields)];
        }
    }

    /// <summary>
    /// What each field holds, where one immediate stands for all of them.
    /// </summary>
    /// <remarks>
    /// An immediate the lifter decoded as a real number is the value in every lane. An **integral** one is
    /// not a number at all - it is the bits, because that is what a `mov` into a register that is then
    /// broadcast leaves behind, and reading `1056964608` as a float gives `1.0569646E+09f` where the code
    /// says `0.5f`. Sixty-four bits are two fields rather than a broadcast, which only answers for a struct
    /// of two.
    /// </remarks>
    private static List<object>? Fields(object value, int floats)
    {
        if (value is double or float)
            return Enumerable.Repeat(value, floats).ToList();

        if (value is not (long or int or ulong or uint))
            return null;

        var bits = System.Convert.ToInt64(value);

        if (bits == (uint)bits)
            return Enumerable.Repeat<object>(System.BitConverter.Int32BitsToSingle((int)bits), floats).ToList();

        return floats == 2
            ? [System.BitConverter.Int32BitsToSingle((int)bits), System.BitConverter.Int32BitsToSingle((int)(bits >> 32))]
            : null;
    }

    private static MethodAnalysisContext? Constructor(TypeAnalysisContext type, int floats)
        => type.Methods.FirstOrDefault(m => m is { Name: ".ctor", IsStatic: false }
            && m.Parameters.Count == floats
            && m.Parameters.All(p => p.ParameterType.FullName == "System.Single"));
}
