using System.Linq;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Reads the value out of a <c>Nullable&lt;bool&gt;</c> that the compiler compared as one packed number.
/// </summary>
/// <remarks>
/// <para>
/// A <c>Nullable&lt;bool&gt;</c> is two bytes - <c>hasValue</c> then <c>value</c> - and the compiler reads
/// both at once and asks one question of the pair:
/// </para>
/// <code>
/// STRH W31, [X31 + 0xC]          ; clear the pair
/// BL   Nullable`1&lt;bool&gt;..ctor    ; hasValue = true, value = v
/// LDRH W8, [X31 + 0xC]           ; read both bytes
/// CMP  W8, 0xFF
/// CSET W0, HI                    ; the high byte is not zero
/// </code>
/// <para>
/// The high byte is <c>value</c>, so the question is exactly <c>GetValueOrDefault()</c> - and which byte that
/// is was not guessed: <c>System.Nullable`1</c> declares <c>hasValue</c> before <c>value</c> in this game's
/// own metadata. Getting it the wrong way round would say <c>HasValue</c>, which is true wherever the
/// constructor ran, so the method would always return true and compile perfectly while being wrong.
/// </para>
/// <para>
/// Recovery wrote it out as <c>(long)flag &gt; 255L</c>, which does not compile because the language has no
/// conversion from <c>bool?</c> to a number - and it is the first statement lost in four bodies.
/// </para>
/// </remarks>
public static class NullablePackedCompare
{
    /// <summary>Everything below this is the low byte, which holds <c>hasValue</c>.</summary>
    private const long HighByte = 255;

    public static void Run(MethodAnalysisContext method)
    {
        if (method.ControlFlowGraph is not { } graph)
            return;

        foreach (var instruction in graph.Instructions)
        {
            if (instruction is not { OpCode: OpCode.CheckGreater, Operands: [_, LocalVariable packed, { } against] }
                || !IsHighByte(against)
                || packed.Type is not { } type || !IsNullableBoolean(type)
                || Getter(type) is not { } getter)
                continue;

            instruction.OpCode = OpCode.Call;
            instruction.Operands = [getter, instruction.Operands[0], packed];
        }

        AskHasValue(method, graph);
    }

    /// <summary>
    /// Reads <c>packed &amp; 0xFF</c> on a <c>Nullable&lt;T&gt;</c> as the question it is: <c>HasValue</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The other half of the same packing. Where the value is wider than a byte the compiler cannot ask both
    /// questions with one compare, so it loads the whole thing and masks the field it wants - and
    /// <c>hasValue</c> is declared first and is one byte, so the low byte <b>is</b> <c>HasValue</c>:
    /// </para>
    /// <code>
    /// BL   Nullable`1&lt;int&gt;..ctor    ; hasValue = true, value = v
    /// LDR  X8, [X31 + 0x8]           ; the whole eight bytes
    /// ANDS X31, X8, 0xFF             ; the low byte, result discarded - this is the flag
    /// B.EQ ...                       ; so: if (!maybe.HasValue)
    /// </code>
    /// <para>
    /// Written out as an <c>and</c> it is <c>((T?)num &amp; 0xFFL) != 0</c>, which the language has no
    /// operator for: <c>Corpus::NullableChain</c> lost its whole <c>if</c> body and returned "none" for every
    /// input. Which byte is which is read from the metadata and not inferred, for the reason the compare
    /// above gives - the other way round says <c>GetValueOrDefault</c>, compiles perfectly and is wrong.
    /// </para>
    /// <para>
    /// The mask is the whole of the evidence and it is enough: byte zero of a <c>Nullable</c> holds nothing
    /// else, whatever <c>T</c> is.
    /// </para>
    /// </remarks>
    private static void AskHasValue(MethodAnalysisContext method, Graphs.ISILControlFlowGraph graph)
    {
        foreach (var instruction in graph.Instructions)
        {
            if (instruction is not { OpCode: OpCode.And, Operands: [LocalVariable answer, LocalVariable packed, { } mask] }
                || !IsHighByte(mask)
                || packed.Type is not GenericInstanceTypeAnalysisContext { GenericType.FullName: "System.Nullable`1" } type
                || HasValue(type) is not { } getter)
            {
                continue;
            }

            instruction.OpCode = OpCode.Call;
            instruction.Operands = [getter, answer, packed];

            //The mask produced a number and the call produces a bool, so what it is stored in has to say so -
            //otherwise the branch below reads `(long)maybe.HasValue`, which is not a conversion C# has.
            answer.Type = method.AppContext.SystemTypes.SystemBooleanType;
        }
    }

    /// <summary>The <c>HasValue</c> getter, named at the instantiation for the reason <see cref="Getter"/> gives.</summary>
    private static MethodAnalysisContext? HasValue(GenericInstanceTypeAnalysisContext instance)
        => instance.GenericType.Methods.FirstOrDefault(m
               => m is { Name: "get_HasValue", IsStatic: false, Parameters.Count: 0 }) is { } member
            ? new ConcreteGenericMethodAnalysisContext(member, instance.GenericArguments, [])
            : null;

    /// <summary>The bound the compiler compares against, whatever width the lifter gave the constant.</summary>
    private static bool IsHighByte(object against)
        => against is long and HighByte || against is int and (int)HighByte
            || against is ulong and HighByte || against is uint and (uint)HighByte;

    private static bool IsNullableBoolean(TypeAnalysisContext type)
        => type.FullName is { } name && name.StartsWith("System.Nullable") && name.Contains("System.Boolean");

    /// <summary>
    /// <c>GetValueOrDefault()</c> and not <c>Value</c>: the pair is read whether or not it was ever set, and
    /// <c>Value</c> throws where the machine simply reads a zero.
    /// </summary>
    /// <remarks>
    /// Through the generic definition where the type is an instantiation: an instance declares no members of
    /// its own, and asking it directly is how three answers came out wrong in one sitting.
    /// </remarks>
    private static MethodAnalysisContext? Getter(TypeAnalysisContext type)
    {
        if (type is not GenericInstanceTypeAnalysisContext instance)
            return null;

        //Found on the definition and then named at the instantiation the value actually has. Naming the
        //definition's own method says `T?` where `bool?` belongs, which the language cannot write.
        return instance.GenericType.Methods.FirstOrDefault(m
                   => m is { Name: "GetValueOrDefault", IsStatic: false, Parameters.Count: 0 }) is { } member
            ? new ConcreteGenericMethodAnalysisContext(member, instance.GenericArguments, [])
            : null;
    }
}
