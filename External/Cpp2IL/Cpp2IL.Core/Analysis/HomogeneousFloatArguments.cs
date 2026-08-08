using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Puts a struct of floats back together from the registers it was handed over in, at a call that takes one.
/// </summary>
/// <remarks>
/// <para>
/// Aapcs64 passes a struct whose every field is a float in one vector register per field, and the lifter names
/// the argument by the first of them - <c>GetArgumentOperandsForCall</c> says so itself. So
/// <c>self.localScale = scale</c> reaches the generator as one <c>float</c> where a <c>Vector3</c> belongs,
/// and comes out as <c>self.localScale = (Vector3)v</c>, which does not compile. It is the largest family
/// the compiler's own diagnostics name: 616 invalid casts from a float to a Unity geometry type.
/// </para>
/// <para>
/// <b>This was attempted once before and reverted</b>, and the difference is which registers are taken. That
/// attempt read them out of the operand list a call carries <i>before its callee is known</i>, which holds
/// all eight of a run whether or not anything ever wrote them; it cost branches and crashed bodies. Here the
/// callee is resolved, so its signature says exactly which registers the struct occupies, and each one is
/// only used when a definition of it actually reaches this call. Where any of them has none - 582 of the 905
/// sites in this game, because the value was left in the register by an earlier call and never touched - the
/// argument is left exactly as it was.
/// </para>
/// <para>
/// Runs at the end of the pipeline. The operand it leaves is not a value any other pass can reason about, and
/// nothing needs to: the generator is the only thing that reads it.
/// </para>
/// </remarks>
public static class HomogeneousFloatArguments
{
    /// <summary>How far back through single-predecessor blocks a register's definition is looked for.</summary>
    private const int Depth = 4;

    public static void Run(MethodAnalysisContext method)
    {
        if (method.ControlFlowGraph is not { } graph)
            return;

        foreach (var block in graph.Blocks)
        {
            for (var at = 0; at < block.Instructions.Count; at++)
            {
                var instruction = block.Instructions[at];

                if (!instruction.IsCall || instruction.Operands.Count == 0
                    || instruction.Operands[0] is not MethodAnalysisContext callee)
                    continue;

                Assemble(block, at, instruction, callee);
            }
        }
    }

    private static void Assemble(Block block, int at,
        Instruction instruction, MethodAnalysisContext callee)
    {
        //The same walk the lifter made when it chose the registers, so the two cannot disagree about which
        //register a parameter arrived in.
        var vector = 0;
        var first = instruction.OpCode == OpCode.Call
            ? (callee.IsStatic ? 2 : 3)
            : (callee.IsStatic ? 1 : 2);

        for (var i = 0; i < callee.Parameters.Count; i++)
        {
            var type = callee.Parameters[i].ParameterType;

            if (type is { Namespace: nameof(System), Name: "Single" or "Double" })
            {
                vector++;
                continue;
            }

            if (HomogeneousFloatStruct.Count(type) is not { } floats)
                continue;

            var start = vector;
            vector += floats;

            if (floats < 2 || first + i >= instruction.Operands.Count)
                continue;

            if (FloatConstructor(type, floats) is not { } constructor)
                continue;

            var parts = new List<object> { instruction.Operands[first + i] };

            for (var field = 1; field < floats && parts.Count == field; field++)
            {
                if (Reaching(block, at, start + field) is { } held)
                    parts.Add(held);
            }


            if (parts.Count == floats)
                instruction.Operands[first + i] = new FloatStructAssembly(constructor, parts);
        }
    }

    /// <summary>The constructor that takes the struct's fields one float at a time, in field order.</summary>
    private static MethodAnalysisContext? FloatConstructor(TypeAnalysisContext type, int floats)
        => type.Methods.FirstOrDefault(m => m is { Name: ".ctor", IsStatic: false }
            && m.Parameters.Count == floats
            && m.Parameters.All(p => p.ParameterType.FullName == "System.Single"));

    /// <summary>
    /// What last wrote a vector register before this call, following single-predecessor blocks back.
    /// </summary>
    /// <remarks>
    /// A register with no definition reaching the call was not written in this method at all: the value was
    /// left there by something earlier and the compiler had no reason to touch it. There is nothing to name,
    /// so the argument keeps the one register it had.
    /// </remarks>
    private static object? Reaching(Block block, int at, int register)
    {
        var name = "V" + register;

        for (var depth = 0; depth < Depth; depth++)
        {
            for (var i = at - 1; i >= 0; i--)
            {
                var instruction = block.Instructions[i];

                if (Defined(instruction) is { } written && Names(written, name))
                    return written;

                if (instruction.IsCall)
                    return null;

                //Every vector register is caller-saved, so a call destroys whatever was in this one and a
                //definition from before it says nothing about what is there now.
                //
                //A call *returning* a struct of floats does leave one field in each of these registers, and
                //taking them from there was built and measured: `cfscore` 362 -> 349 whole. The reason is
                //that only the register the first field is in has an answer from the ISIL - the lifter names
                //a struct return `x0` - so the fields after it came from the call while the first came from
                //whatever the value flow said, and where the caller had overwritten none of them the result
                //was a struct built half from one place and half from another.
            }

            if (block.Predecessors.Count != 1)
                return null;

            at = block.Predecessors[0].Instructions.Count;
            block = block.Predecessors[0];
        }

        return null;
    }

    /// <summary>The local a single instruction writes, which for a call is where its result comes back.</summary>
    private static LocalVariable? Defined(Instruction instruction) => instruction.OpCode switch
    {
        OpCode.CallVoid or OpCode.Jump or OpCode.ConditionalJump or OpCode.Return => null,
        OpCode.Call => instruction.Operands.Count > 1 ? instruction.Operands[1] as LocalVariable : null,
        _ => instruction.Operands.Count > 0 ? instruction.Operands[0] as LocalVariable : null,
    };

    /// <summary>
    /// Whether a local is that vector register. A float, a double and a whole vector share one register and
    /// the lifter may have named any of the three, so the letter is not part of the answer - the number is.
    /// </summary>
    private static bool Names(LocalVariable local, string register)
        => local.Register is { Name: { Length: > 1 } name }
            && (name[0] is 'V' or 'S' or 'D')
            && "V" + name[1..] == register;
}

/// <summary>
/// A struct of floats named by the registers its fields arrived in, and the constructor that puts them back
/// together. Only the generator reads this; see <see cref="HomogeneousFloatArguments"/>.
/// </summary>
public sealed class FloatStructAssembly(MethodAnalysisContext constructor, List<object> parts)
{
    public MethodAnalysisContext Constructor { get; } = constructor;

    public List<object> Parts { get; } = parts;

    public override string ToString() => $"new {Constructor.DeclaringType?.Name}({string.Join(", ", Parts)})";
}
