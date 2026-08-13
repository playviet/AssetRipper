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
/// Only the callee side. A stacked argument at a <em>call</em> is written into the caller's own frame before
/// the branch, which is a different set of slots entirely and is not what this says.
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
        //`this` occupies the first integer register and the first operand.
        var first = method.IsStatic ? 0 : 1;

        if (operands.Count < first + method.Parameters.Count)
            return;

        var vectors = 0;
        var integers = first;
        var vectorsGone = false;
        var integersGone = false;
        long nsaa = 0;

        //Every vector register past the first that a float struct occupies is handed over after the
        //parameters, in parameter order. One that goes on the stack occupies none of them, so its share of
        //that tail has to go with it or the tail names registers holding something else.
        var beyond = new List<int>();
        var dropped = new List<int>();

        for (var i = 0; i < method.Parameters.Count; i++)
        {
            var type = method.Parameters[i].ParameterType;
            var (registers, isVector, size, alignment) = Occupies(type);

            var extra = beyond.Count;

            if (isVector)
                for (var more = 1; more < registers; more++)
                    beyond.Add(i);

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
            operands[first + i] = new StackOffset((int)nsaa);
            nsaa += size;

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

    /// <summary>Which run a parameter is passed in, how much of it it takes, and how much room it needs.</summary>
    private static (int Registers, bool IsVector, long Size, long Alignment) Occupies(TypeAnalysisContext? type)
    {
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

    private static long RoundUp(long value, long to) => (value + to - 1) / to * to;
}
