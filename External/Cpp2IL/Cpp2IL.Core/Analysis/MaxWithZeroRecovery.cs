using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Writes <c>x &amp; ~(x &gt;&gt; 31)</c> back as the <c>Max(0, x)</c> it is the body of.
///
/// Clamping a number at zero is short enough that it is compiled without a branch: shifting a signed
/// integer right by its width less one spreads its sign over the whole word, so the complement of that
/// is all ones for a value at or above zero and all zeroes below it, and anding the two gives the value
/// or nought. Arm64 has the complement built into its and, so the three operations arrive as two - a
/// shift and a <c>BIC</c> - and no call to Max survives for the analysis to name.
///
/// Nothing is guessed here: the rewritten form computes the same number for every input, so the only
/// thing it changes is that the line says what it means. Which Max was written is not recorded either
/// way; <see cref="UnityMathf"/> is preferred where the game has it, because a game that clamps a level
/// index wrote UnityEngine's.
/// </summary>
public static class MaxWithZeroRecovery
{
    private const string UnityMathf = "UnityEngine.Mathf";

    public static void Run(MethodAnalysisContext method)
    {
        var cfg = method.ControlFlowGraph!;
        Dictionary<LocalVariable, DefinitionCount>? definitions = null;
        var rewroteAny = false;

        foreach (var instruction in cfg.Instructions)
        {
            if (instruction.OpCode != OpCode.And || instruction.Operands.Count != 3)
                continue;

            if (instruction.Operands[0] is not LocalVariable destination)
                continue;

            definitions ??= SingleDefinitions(cfg);

            var clamped = ClampedValue(instruction.Operands[1], instruction.Operands[2], definitions)
                ?? ClampedValue(instruction.Operands[2], instruction.Operands[1], definitions);

            if (clamped is not { } match)
                continue;

            if (Max(method.AppContext, match.IsSixtyFourBit) is not { } max)
                continue;

            //Boxed at the width the call takes it at, so the literal meets the parameter as itself.
            object zero = match.IsSixtyFourBit ? 0L : 0;

            instruction.OpCode = OpCode.Call;
            instruction.Operands = [max, destination, zero, match.Value];
            rewroteAny = true;
        }

        //The mask the call replaced is now read by nothing, and a shift left standing on its own is written
        //out as a statement of its own - the two lines this pass exists to be rid of.
        if (rewroteAny)
            DeadCodeEliminator.Run(cfg);
    }

    /// <summary>
    /// The value <paramref name="value"/> holds, if <paramref name="mask"/> is the complement of that same
    /// value's sign - and whether it is a 64-bit one. Anything else gives null: a mask built from a
    /// different value, or shifted by anything other than the sign bit's distance, is not this idiom.
    /// </summary>
    private static (LocalVariable Value, bool IsSixtyFourBit)? ClampedValue(object value, object mask,
        Dictionary<LocalVariable, DefinitionCount> definitions)
    {
        if (value is not LocalVariable local || WrittenOnce(local, definitions) is null)
            return null;

        if (DefinitionOf(mask, OpCode.Not, definitions) is not { Operands: [_, { } negated] })
            return null;

        if (DefinitionOf(negated, OpCode.ShiftRight, definitions) is not { Operands: [_, LocalVariable shifted, { } distance] })
            return null;

        if (!ReferenceEquals(shifted, local))
            return null;

        //The type is what says which width the sign sits at, and a shift by the other width's distance is
        //some other calculation that happens to look like this one.
        return local.Type?.FullName switch
        {
            "System.Int32" when Is(distance, 31) => (local, false),
            "System.Int64" when Is(distance, 63) => (local, true),
            _ => null,
        };
    }

    /// <summary>The one instruction that wrote <paramref name="operand"/>, if it is the given operation.</summary>
    private static Instruction? DefinitionOf(object operand, OpCode opCode, Dictionary<LocalVariable, DefinitionCount> definitions)
    {
        if (operand is not LocalVariable local)
            return null;

        var definition = WrittenOnce(local, definitions);

        return definition?.OpCode == opCode ? definition : null;
    }

    private static Instruction? WrittenOnce(LocalVariable local, Dictionary<LocalVariable, DefinitionCount> definitions)
        => definitions.TryGetValue(local, out var definition) && definition.Count == 1 ? definition.Instruction : null;

    /// <summary>
    /// Every local the body writes, with what wrote it and how many times. A local written more than once
    /// does not identify the value the reader sees, so the count is what makes the match safe rather than
    /// the shape alone.
    /// </summary>
    private static Dictionary<LocalVariable, DefinitionCount> SingleDefinitions(Graphs.ISILControlFlowGraph cfg)
    {
        Dictionary<LocalVariable, DefinitionCount> definitions = [];

        foreach (var instruction in cfg.Instructions)
        {
            var written = instruction.OpCode switch
            {
                OpCode.Call => instruction.Operands.Count > 1 ? instruction.Operands[1] : null,
                OpCode.CallVoid or OpCode.IndirectCall or OpCode.Return or OpCode.Jump or OpCode.IndirectJump
                    or OpCode.ConditionalJump or OpCode.Throw or OpCode.Nop or OpCode.Interrupt => null,
                _ => instruction.Operands.Count > 0 ? instruction.Operands[0] : null,
            };

            if (written is not LocalVariable local)
                continue;

            definitions[local] = definitions.TryGetValue(local, out var existing)
                ? existing with { Count = existing.Count + 1 }
                : new DefinitionCount(instruction, 1);
        }

        return definitions;
    }

    private static bool Is(object operand, int distance) =>
        operand switch
        {
            int i => i == distance, uint ui => ui == distance, long l => l == distance, ulong ul => ul == (ulong)distance,
            short s => s == distance, ushort us => us == distance, byte b => b == distance, sbyte sb => sb == distance,
            _ => false,
        };

    private static MethodAnalysisContext? Max(ApplicationAnalysisContext appContext, bool isSixtyFourBit)
    {
        var integer = isSixtyFourBit ? "System.Int64" : "System.Int32";

        List<TypeAnalysisContext?> candidates = [];

        //Mathf holds only the 32-bit one, so a 64-bit clamp is Math's whichever library the game has.
        if (!isSixtyFourBit)
        {
            candidates.Add(appContext.GetAssemblyByName("UnityEngine.CoreModule")?.GetTypeByFullName(UnityMathf));
            candidates.Add(appContext.GetAssemblyByName("UnityEngine")?.GetTypeByFullName(UnityMathf));
        }

        candidates.Add(appContext.GetAssemblyByName("mscorlib")?.GetTypeByFullName("System.Math"));

        foreach (var type in candidates)
        {
            if (type?.Methods.FirstOrDefault(m => m is { Name: "Max", IsStatic: true, Parameters.Count: 2 }
                && m.Parameters.All(p => p.ParameterType.FullName == integer)) is { } max)
                return max;
        }

        return null;
    }

    private readonly record struct DefinitionCount(Instruction Instruction, int Count);
}
