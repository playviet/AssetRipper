using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// A method that answers through the buffer its caller passed returns that buffer, not <c>x0</c>.
/// </summary>
/// <remarks>
/// <para>
/// A composite over sixteen bytes comes back through a pointer the caller leaves in <c>x8</c>, and the callee
/// writes its answer into that. <b>Nothing comes back in <c>x0</c> at all</b> - but the lifter models every
/// return as reading <c>x0</c>, so the value returned is whatever that register last held, which is usually
/// nothing:
/// </para>
/// <code>
/// public static RuleValue Number(double d)
/// {
///     RuleValue ruleValue = default(RuleValue);   // this is the buffer, and it is filled correctly
///     ruleValue._num = d;
///     ruleValue._str = null;
///     RuleValue result = default(RuleValue);      // and this is x0, which nothing ever assigned
///     return result;
/// }
/// </code>
/// <para>
/// That is the silent kind of wrong: the body compiles whole, every scorer counts it, and the method answers
/// <c>default</c>. All four of <c>SegmentRuleEvaluator.RuleValue</c>'s factories read that way, so every rule
/// the evaluator compares was built out of a null string and a nought.
/// </para>
/// <para>
/// <see cref="LocalVariables"/>'s <c>SeedIndirectReturnBuffer</c> already types the entry value of <c>x8</c>
/// as a reference to the return type, which is what turns the stores into fields; this is the other half of
/// the same fact, and the same one <c>SeedSharedReturnBuffer</c> states for a body that returns a shared
/// <c>T</c> - the buffer is the answer, so the return names it.
/// </para>
/// <para>
/// <b>Only where the value being returned is one nothing produced.</b> A method whose <c>x0</c> is assigned
/// somewhere has some other reading of it - a tail call's answer, an inlined body that really did leave
/// something there - and replacing that with the buffer would be swapping one guess for another. Where
/// nothing assigns it, the only value it can carry is the zero the generator writes for a value type, so
/// there is nothing to lose and the ABI says exactly what belongs in its place.
/// </para>
/// <para>
/// <b>And only a buffer something was written through.</b> A method that never stores into its own result has
/// not been recovered far enough for this to say anything, and returning an empty buffer would trade one
/// <c>default</c> for another while adding a local.
/// </para>
/// </remarks>
public static class IndirectReturnBuffer
{
    /// <summary>Which method to say what was decided about, and why. Off unless the variable is set.</summary>
    private static readonly string? Asked = System.Environment.GetEnvironmentVariable("INDIRECTRET_TRACE");

    public static bool Run(MethodAnalysisContext method)
    {
        if (method.ControlFlowGraph is not { } graph || method.ReturnType is not { } returned)
            return false;

        if (!Aapcs64.ReturnsIndirectly(method))
            return false;

        var trace = Asked is { } asked && method.Name.Contains(asked);

        if (trace)
        {
            System.Console.Error.WriteLine($"INDIRECTRET {method.Name}: returns {returned.FullName} indirectly");

            foreach (var local in method.Locals)
                System.Console.Error.WriteLine($"INDIRECTRET   local {local.Name} @ {local.Register.Name}_v{local.Register.Version} : {local.Type?.FullName ?? "?"}");
        }

        if (Buffer(method, returned) is not { } buffer || !FilledThrough(graph, buffer))
        {
            if (trace)
                System.Console.Error.WriteLine($"INDIRECTRET   no filled buffer (found={Buffer(method, returned)?.Name ?? "none"})");

            return false;
        }

        var changed = false;

        foreach (var instruction in graph.Instructions)
        {
            if (instruction is not { OpCode: OpCode.Return, Operands: [LocalVariable answer] })
                continue;

            if (trace)
                System.Console.Error.WriteLine($"INDIRECTRET   return {answer.Name} assigned={Assigned(graph, answer)} buffer={buffer.Name}");

            if (ReferenceEquals(answer, buffer) || Assigned(graph, answer))
                continue;

            instruction.Operands[0] = buffer;
            changed = true;
        }

        return changed;
    }

    /// <summary>
    /// The local holding the address the caller passed, which is the entry value of <c>x8</c>.
    /// </summary>
    /// <remarks>
    /// Found by the type <c>SeedIndirectReturnBuffer</c> put on it rather than by the register alone:
    /// <c>x8</c> is an ordinary scratch register as well, and every later version of it is some unrelated
    /// temporary. The unversioned local is the one that arrived.
    /// </remarks>
    private static LocalVariable? Buffer(MethodAnalysisContext method, TypeAnalysisContext returned)
    {
        foreach (var local in method.Locals)
        {
            if (local is { Register: { Version: -1, Name: "X8" }, Type: ByRefTypeAnalysisContext { ElementType: { } pointee } }
                && pointee.FullName == returned.FullName)
            {
                return local;
            }
        }

        return null;
    }

    /// <summary>Whether anything stores into the buffer, so that returning it says something.</summary>
    /// <remarks>
    /// Not <see cref="Instruction.Destination"/>: that reports a field store as having no destination at all,
    /// because <c>IsConstantValue</c> answers true for everything it does not recognise and a
    /// <see cref="FieldReference"/> is one of those - which is the whole shape being looked for here.
    /// </remarks>
    private static bool FilledThrough(ISILControlFlowGraph graph, LocalVariable buffer)
    {
        foreach (var instruction in graph.Instructions)
        {
            var wrote = WrittenBy(instruction) switch
            {
                FieldReference field => ReferenceEquals(field.Local, buffer),
                MemoryOperand memory => ReferenceEquals(memory.Base, buffer) || ReferenceEquals(memory.Index, buffer),
                LocalVariable local => ReferenceEquals(local, buffer),
                _ => false,
            };

            if (wrote)
                return true;
        }

        return false;
    }

    /// <summary>The place an instruction writes, whether or not it is one a local can stand in.</summary>
    private static object? WrittenBy(Instruction instruction) => instruction.OpCode switch
    {
        OpCode.Call or OpCode.IndirectCall => instruction.Operands.Count > 1 ? instruction.Operands[1] : null,

        OpCode.Move or OpCode.Phi or OpCode.Add or OpCode.Subtract or OpCode.Multiply or OpCode.Divide
            or OpCode.ShiftLeft or OpCode.ShiftRight or OpCode.And or OpCode.Or or OpCode.Xor
            or OpCode.Not or OpCode.Negate or OpCode.Newobj or OpCode.Select
            or OpCode.CheckEqual or OpCode.CheckNotEqual or OpCode.CheckGreater or OpCode.CheckLess
            or OpCode.CheckGreaterOrEqual or OpCode.CheckLessOrEqual
            => instruction.Operands.Count > 0 ? instruction.Operands[0] : null,

        _ => null,
    };

    /// <summary>Whether anything in the method produces the value being returned.</summary>
    private static bool Assigned(ISILControlFlowGraph graph, LocalVariable answer)
    {
        foreach (var instruction in graph.Instructions)
            if (WrittenBy(instruction) is LocalVariable written && ReferenceEquals(written, answer))
                return true;

        return false;
    }
}
