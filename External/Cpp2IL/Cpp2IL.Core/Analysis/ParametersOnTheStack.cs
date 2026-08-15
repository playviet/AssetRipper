using System.Collections.Generic;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// A parameter that did not fit in the registers arrives on the caller's stack, and says where.
/// </summary>
/// <remarks>
/// <para>
/// Aapcs64 hands arguments over in two independent runs of eight - <c>x0..x7</c> and <c>v0..v7</c> - and
/// <b>everything after a run is used up goes on the stack</b>. The walk that names a method's parameters has
/// no notion of either run ending: it keeps counting, so the ninth float is named <c>v8</c>, which is a
/// callee-saved register holding whatever the method last put there.
/// </para>
/// <para>
/// <c>GizmosDrawer::DrawArrowEnd(Vector3, Vector3, Color, float, float)</c> is the shape exactly. The two
/// <c>Vector3</c>s take v0..v5, the <c>Color</c> wants four more and only two are left, so the colour and both
/// floats after it are on the stack - and the body says so:
/// </para>
/// <code>
/// SUB X31, X31, 0xC0
/// LDP S9, S14, [X31 + 0xC0]   // colour, +0 and +4
/// LDP S12, S1,  [X31 + 0xC8]  // +8 and +C
/// LDR S15, [X31 + 0xD0]       // arrowHeadLength, +10
/// LDR S13, [X31 + 0xD8]       // arrowHeadAngle,  +18
/// </code>
/// <para>
/// which is the rule written out: the next stacked argument sits at the running offset rounded up to the
/// larger of eight and its own alignment, and the offset then advances by its size. Sixteen bytes of colour,
/// then a float at 0x10, then - the offset having been rounded from 0x14 back up to a multiple of eight -
/// another at 0x18.
/// </para>
/// <para>
/// Saying so costs nothing downstream: <see cref="StackAnalyzer"/> already turns a <see cref="StackOffset"/>
/// parameter into the <c>stack_</c> local the body reads, and everything that types a parameter works off
/// that local. What it replaces is not a marker but a <b>wrong value</b> - the method quietly computed with
/// whatever was in v8 - so no scorer was ever going to report this.
/// </para>
/// <para>
/// <b>Only the callee side.</b> A stacked argument at a <em>call</em> is written into the caller's own frame
/// before the branch, which is a different set of slots entirely. Applying this there as well was built at
/// 1.1.22 and measured **worse**: `full` 3219 -> 3216, commented 698 -> 711, unmanaged 532 -> 536. The
/// offsets are named raw, so an outgoing `StackOffset(0)` becomes the same `stack_0` as the caller's *own*
/// first stacked parameter - the `default(...)` count fell by 59 not because 59 values were recovered but
/// because 59 became a confident wrong one. Recovering the call side needs the store into the outgoing slot
/// to be found and followed, which is a different piece of work.
/// </para>
/// </remarks>
public static class ParametersOnTheStack
{
    /// <summary>What a stacked argument's offset is always rounded up to, whatever its own alignment.</summary>
    private const long Granule = 8;

    /// <summary>
    /// Replaces the operand of every parameter that had no register left with the stack slot it arrives in.
    /// </summary>
    public static void Place(List<object> operands, MethodAnalysisContext method)
    {
        //Before the stack question, the register one: a composite of nine to sixteen bytes takes TWO of the
        //integer registers, and the walk hands out one apiece.
        Widen(operands, method);

        //`this` occupies the first integer register and the first operand.
        var first = method.IsStatic ? 0 : 1;

        if (operands.Count < first + method.Parameters.Count || Placement(method) is not { Count: > 0 } placed)
            return;

        //Every vector register past the first that a float struct occupies is handed over after the
        //parameters, in parameter order. One that goes on the stack occupies none of them, so its share of
        //that tail has to go with it or the tail names registers holding something else.
        var beyond = new List<int>();
        var dropped = new List<int>();

        for (var i = 0; i < method.Parameters.Count; i++)
        {
            var (registers, isVector, _, _) = Occupies(method.Parameters[i].ParameterType);
            var extra = beyond.Count;

            if (isVector)
                for (var more = 1; more < registers; more++)
                    beyond.Add(i);

            if (!placed.TryGetValue(i, out var at))
                continue;

            operands[first + i] = new StackOffset((int)at);

            for (var more = extra; more < beyond.Count; more++)
                dropped.Add(more);
        }

        //From the back, so the earlier indices still say where they said.
        for (var i = dropped.Count - 1; i >= 0; i--)
        {
            var at = first + method.Parameters.Count + dropped[i];

            if (at < operands.Count)
                operands.RemoveAt(at);
        }
    }

    /// <summary>
    /// Where each parameter that had no register left arrives, by its index in the parameter list.
    /// </summary>
    /// <remarks>
    /// Public because the answer is needed twice: here, to say which slot the parameter itself is, and in
    /// <see cref="HomogeneousFloatParameters"/>, to say which slots the rest of a float struct's fields are.
    /// A struct on the stack is one slot per field just as it is one register per field in the run.
    /// </remarks>
    public static Dictionary<int, long> Placement(MethodAnalysisContext method)
    {
        var placed = new Dictionary<int, long>();
        var vectors = 0;
        var integers = method.IsStatic ? 0 : 1;
        var vectorsGone = false;
        var integersGone = false;
        long nsaa = 0;

        for (var i = 0; i < method.Parameters.Count; i++)
        {
            var (registers, isVector, size, alignment) = Occupies(method.Parameters[i].ParameterType);

            var onTheStack = isVector
                ? vectorsGone || vectors + registers > Aapcs64.RegistersPerRun
                : integersGone || integers + registers > Aapcs64.RegistersPerRun;

            if (!onTheStack)
            {
                if (isVector)
                    vectors += registers;
                else
                    integers += registers;

                continue;
            }

            //A run does not resume: once one argument has gone to the stack every later one of that kind
            //follows it, even where a smaller one would still have fitted.
            if (isVector)
                vectorsGone = true;
            else
                integersGone = true;

            nsaa = RoundUp(nsaa, System.Math.Max(Granule, alignment));
            placed[i] = nsaa;
            nsaa += size;
        }

        return placed;
    }

    /// <summary>
    /// Renames the integer-register parameters after one that takes two of them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// AAPCS64 passes a composite by value in <b>consecutive</b> general registers while it fits in two, and
    /// passes anything larger indirectly - the caller copies it somewhere and hands over a pointer, which is
    /// one register like any other. So the only case the one-register-per-parameter walk gets wrong is the
    /// composite of <b>nine to sixteen bytes</b>, and every parameter after one of those is in the wrong
    /// register.
    /// </para>
    /// <para>
    /// <c>Pool::Spawn(Nullable&lt;Vector3&gt;, Nullable&lt;Quaternion&gt;, Nullable&lt;Vector3&gt;, Transform)</c>
    /// says it outright. A <c>Nullable&lt;Vector3&gt;</c> is sixteen bytes and takes two; a
    /// <c>Nullable&lt;Quaternion&gt;</c> is twenty and goes indirectly in one. The walk named the parameters
    /// x1, x2, x3, x4 and the machine used x1-x2, x3, x4-x5, x6 - so the body's
    /// <c>And v144, parent @ X4, 255</c> is a <c>Nullable</c>'s <c>hasValue</c> byte read out of a register
    /// called <c>parent</c>, and <c>ShiftRight v165 (Single), methodInfo @ X5, 32</c> is a float taken out of
    /// one called <c>methodInfo</c>. A <c>Transform</c> is not masked with 255 and a <c>MethodInfo</c> is not
    /// shifted for a float: these are <b>wrong values</b>, and no scorer can see them.
    /// </para>
    /// <para>
    /// <b>It does nothing at all unless some parameter is one of those.</b> That is deliberate and is the
    /// property to check when this is measured: a method whose every parameter fits in a register must come
    /// out byte-identical, and the count of methods whose naming moves must equal the count of methods with
    /// such a parameter and nothing else.
    /// </para>
    /// <para>
    /// The vector run is left alone entirely. The two runs are independent, so widening an integer parameter
    /// cannot move a float one, and touching the vector naming here would risk the homogeneous-float work
    /// that already owns it.
    /// </para>
    /// </remarks>
    public static void Widen(List<object> operands, MethodAnalysisContext method)
    {
        //A short circuit, so that the before and after of this one rule can be taken from one build. The
        //property being checked is that a method with no such parameter comes out byte-identical, and that
        //is only worth anything if both readings are of the same code.
        if (System.Environment.GetEnvironmentVariable("PARAMWIDEN_OFF") == "1")
            return;

        var first = method.IsStatic ? 0 : 1;

        if (operands.Count < first + method.Parameters.Count)
            return;

        var widens = false;

        foreach (var parameter in method.Parameters)
            if (Occupies(parameter.ParameterType) is { Registers: > 1, IsVector: false })
                widens = true;

        if (!widens)
            return;

        var integers = method.IsStatic ? 0 : 1;

        for (var i = 0; i < method.Parameters.Count; i++)
        {
            var (registers, isVector, _, _) = Occupies(method.Parameters[i].ParameterType);

            if (isVector)
                continue;

            //Only where the walk gave it a register at all. One already replaced - by this pass on an earlier
            //call, or by anything else - is not renamed back into the run.
            if (integers < Aapcs64.RegistersPerRun && operands[first + i] is Register)
                operands[first + i] = new Register(null, "X" + integers);

            integers += registers;
        }
    }

    /// <summary>
    /// How many integer registers a method's parameters take beyond the one apiece the walk hands out.
    /// </summary>
    /// <remarks>
    /// The runtime method pointer arrives after the declared parameters, so it moves along with them. Stated
    /// as the <em>difference</em> so that <see cref="NewArmV8InstructionSet"/>'s own count - which has its own
    /// rules about which parameters consume an integer register - is left exactly as it was for every method
    /// that has no such parameter.
    /// </remarks>
    public static int ExtraIntegerRegisters(MethodAnalysisContext method)
    {
        var extra = 0;

        foreach (var parameter in method.Parameters)
            if (Occupies(parameter.ParameterType) is { Registers: var registers, IsVector: false } && registers > 1)
                extra += registers - 1;

        return extra;
    }

    /// <summary>Which run a parameter is passed in, how much of it it takes, and how much room it needs.</summary>
    private static (int Registers, bool IsVector, long Size, long Alignment) Occupies(TypeAnalysisContext? type)
    {
        //A composite by value: two registers while it fits in two, and indirectly - one register holding a
        //pointer - once it does not. **Asked before the namespace test below**, because `System.Nullable<T>`
        //is in `System` and would otherwise be taken for a scalar and given one register, which is the whole
        //defect. Only a value type larger than a word can reach it, so no primitive is disturbed.
        if (type is { IsValueType: true, IsEnumType: false } && HomogeneousFloatStruct.Count(type) is not > 0
            && CompositeSize(type) is { } bytes && bytes > 8)
        {
            return bytes <= 16 ? (2, false, bytes, 8) : (1, false, 8, 8);
        }

        if (type is { Namespace: nameof(System) } scalar)
        {
            return scalar.Name switch
            {
                "Single" => (1, true, 4, 4),
                "Double" => (1, true, 8, 8),
                _ => (1, false, 8, 8),
            };
        }

        //The same test the walk itself makes: a struct whose every field is a float travels one field to a
        //vector register. Its size is what the fields add up to, which is what a copy of it on the stack
        //takes up.
        if (type is not null && HomogeneousFloatStruct.Count(type) is { } floats && floats > 0)
            return (floats, true, Aapcs64.SizeOf(type) ?? floats * 4, 4);

        return (1, false, 8, 8);
    }

    /// <summary>
    /// How many bytes a composite occupies by value, including the generic instantiations whose size the
    /// metadata does not record.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Aapcs64.SizeOf"/> reads the recorded instance size and takes the object header off it,
    /// which is exact for an ordinary struct and useless for a generic instance: a generic type records no
    /// layout at all, so <c>Nullable&lt;Vector3&gt;</c>, <c>Nullable&lt;Color&gt;</c> and
    /// <c>Nullable&lt;Single&gt;</c> all come back as nine bytes. Sixteen, twenty and eight is the truth, and
    /// they land on three different sides of the two boundaries that matter.
    /// </para>
    /// <para>
    /// So an instantiation is added up from the fields of the type it instantiates, with each field's own
    /// type substituted from the arguments and laid out at its natural alignment. An unknown field stops the
    /// sum rather than being guessed at - one wrong answer here moves every parameter after it.
    /// </para>
    /// </remarks>
    private static long? CompositeSize(TypeAnalysisContext type)
    {
        if (type is not GenericInstanceTypeAnalysisContext instance)
            return Aapcs64.SizeOf(type);

        long total = 0;
        long widest = 1;

        foreach (var field in instance.GenericType.Fields)
        {
            if (field.IsStatic)
                continue;

            var held = field.FieldType is GenericParameterTypeAnalysisContext { Index: var index }
                && index < instance.GenericArguments.Count
                    ? instance.GenericArguments[index]
                    : field.FieldType;

            var (size, alignment) = SizeAndAlignment(held);

            if (size <= 0)
                return null;

            total = RoundUp(total, alignment) + size;
            widest = System.Math.Max(widest, alignment);
        }

        return total > 0 ? RoundUp(total, widest) : null;
    }

    /// <summary>What one field of a composite takes up, and what it has to sit on a multiple of.</summary>
    private static (long Size, long Alignment) SizeAndAlignment(TypeAnalysisContext? type)
    {
        var pointerSize = (long)(type?.AppContext.Binary.PointerSize ?? 8);

        if (type is null || !type.IsValueType)
            return (pointerSize, pointerSize);

        if (type.IsEnumType)
            return (4, 4);

        if (type.Namespace == nameof(System))
        {
            return type.Name switch
            {
                "Boolean" or "SByte" or "Byte" => (1, 1),
                "Int16" or "UInt16" or "Char" => (2, 2),
                "Int32" or "UInt32" or "Single" => (4, 4),
                "Int64" or "UInt64" or "Double" or "IntPtr" or "UIntPtr" => (8, 8),
                _ => (0, 1),
            };
        }

        //A struct of floats is four-aligned however many of them it holds; anything else is asked of the
        //metadata, and a size it does not record stops the sum above.
        if (HomogeneousFloatStruct.Count(type) is { } floats && floats > 0)
            return (Aapcs64.SizeOf(type) ?? floats * 4, 4);

        return (CompositeSize(type) ?? 0, 8);
    }

    private static long RoundUp(long value, long to) => (value + to - 1) / to * to;
}
