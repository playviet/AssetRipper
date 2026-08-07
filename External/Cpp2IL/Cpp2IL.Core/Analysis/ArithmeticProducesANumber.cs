using System.Collections.Generic;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using LibCpp2IL.BinaryStructures;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Says that what arithmetic over numbers produces is a number, where the destination was called a reference.
/// </summary>
/// <remarks>
/// <para>
/// A register holds whatever is in it and the analysis has to guess what that is; guessing "a reference" for
/// one that holds a number is common enough that <c>IlGenerator</c> declines to read a zero as null because
/// of it. What that guess costs is the whole statement, because <c>object num = num2 - 8L;</c> is not C#:
/// </para>
/// <code>
/// //AssetRipper: commented out, this could not be kept as code.
/// //object obj3 = obj4 - 4L;
/// </code>
/// <para>
/// Grouped by the shape of the first statement in each commented block, that is 99 of them in the game, and
/// they cascade - every later statement reading the local goes too, which is most of the 255 blocks whose
/// first line is a plain <c>a = b;</c>.
/// </para>
/// <para>
/// <b>The rule needs no inference.</b> ISIL has no arithmetic over references: if every value going in is a
/// number then what comes out is one, whatever the register was called. So the destination is retyped and
/// nothing else is touched - no operand is rewritten, no instruction is added or removed.
/// </para>
/// <para>
/// <b>Addition is the exception, and it is why this asks about every source rather than just one.</b> An
/// <c>Add</c> is also how an address is computed - <c>this + 100L</c> is the address of a field - and calling
/// that result a number turns a field access into a cast of <c>this</c> to <c>long</c>, which is worse than
/// the commented line it replaced. Requiring *all* of the sources to be numbers excludes it, because
/// <c>this</c> is not one.
/// </para>
/// </remarks>
public static class ArithmeticProducesANumber
{
    public static bool Run(MethodAnalysisContext method)
    {
        if (method.ControlFlowGraph is not { } graph)
            return false;

        //Every local something is read through. Calling one of these a number stops the resolver ever
        //recognising it as the base of a field or an element, and that is not a trade worth making: typing
        //them all cost `unmanaged` 128 -> 163, thirty-five reads that had been resolved and no longer were.
        var addressed = new HashSet<LocalVariable>();

        foreach (var instruction in graph.Instructions)
            foreach (var operand in instruction.Operands)
                switch (operand)
                {
                    case MemoryOperand { Base: LocalVariable through }:
                        addressed.Add(through);
                        break;
                    case MemoryOperand { Index: LocalVariable subscript }:
                        addressed.Add(subscript);
                        break;
                    case FieldReference { Local: { } holder }:
                        addressed.Add(holder);
                        break;
                }

        var changed = false;

        foreach (var instruction in graph.Instructions)
        {
            if (!IsArithmetic(instruction.OpCode) || instruction.Operands.Count < 2)
                continue;

            //A destination the analysis could not type, or typed as a reference. **Untyped is the whole
            //population**: the `object` in `object obj3 = obj4 - 4L;` is not a local typed `System.Object`,
            //it is the generator's fallback for a local with no type at all. Requiring a non-null reference
            //type made the pass fire **once in the entire game**, which is how that was found.
            //
            //A value type is left alone: it is either already a number or a struct, and a struct in
            //arithmetic is `StructInArithmetic`'s question, not this one.
            if (instruction.Operands[0] is not LocalVariable destination
                || destination.Type is { IsValueType: true }
                || addressed.Contains(destination))
                continue;

            //Addition is the one that has to prove itself, because it is also how an address is computed:
            //`this + 100L` is the address of a field, and calling that a number turns a field access into a
            //cast of `this` to `long`. Everywhere else there is nothing to prove - ISIL has no subtraction,
            //multiplication, shift or bitwise operation over references, so whatever the registers were
            //called the answer is a number. Asking every source to be one first was measured and is inert:
            //in this population the *sources* are mistyped too, which is the whole shape of the defect.
            //Three answers, not two. A source that is *known* to be something other than a number - a struct,
            //a reference, `this` - refuses outright: calling the answer a number then writes
            //`long num4 = (long)Vector3.zero;`, which is no more C# than the line it replaced, and this pass
            //produced a handful of exactly those before the distinction was drawn. A source that is merely
            //**untyped** says nothing, and where every source says nothing the width of the arithmetic is
            //what is left - except for an addition, which may be an address.
            if (!SourcesAreNumbers(instruction, out var widest))
                continue;

            //Where a source says the width, that is the answer. Where none of them does, the width of the
            //arithmetic is what is left - except for an addition, which may be an address. Dropping that
            //fallback entirely was measured: it holds `decisions` at 1063, so the correctness half comes from
            //the typed sources alone, but it costs `full` 308 -> 307, `compare2` 2329 -> 2325 and ninety-six
            //more commented blocks. What made the fallback safe is refusing a **struct** source above, which
            //is what had been turning `1.6f` into `1L`.
            var produced = widest
                ?? (instruction.OpCode == OpCode.Add ? null : method.AppContext.SystemTypes.SystemInt64Type);

            if (produced == null)
                continue;

            destination.Type = produced;
            changed = true;

            //And the sources, where they were called references too. `long obj3 = obj4 - 4L;` still does not
            //compile while `obj4` is an `object`, and in this population it always is - the register the
            //value came out of was guessed at exactly as the destination was. Only where addition is not
            //involved, so an address never gets called a number.
            if (instruction.OpCode == OpCode.Add)
                continue;

            for (var operand = 1; operand < instruction.Operands.Count; operand++)
                if (instruction.Operands[operand] is LocalVariable source
                    && source.Type is not { IsValueType: true }
                    && !addressed.Contains(source))
                    source.Type = produced;
        }

        return changed;
    }

    /// <summary>
    /// The type of what the instruction produces, where every value it reads is a number.
    /// </summary>
    /// <remarks>
    /// A float anywhere in it makes the answer a float; otherwise it is as wide as the arithmetic, which on
    /// this architecture is sixty-four bits. A constant says nothing about width on its own - the same
    /// <c>4</c> is a float offset and an int - so it is accepted but does not decide.
    /// </remarks>
    private static bool SourcesAreNumbers(Instruction instruction, out TypeAnalysisContext? widest)
    {
        widest = null;

        for (var operand = 1; operand < instruction.Operands.Count; operand++)
        {
            switch (instruction.Operands[operand])
            {
                //A constant is a number, but the same `4` is a float offset and an int, so it is accepted
                //without deciding the width.
                case long or ulong or int or uint or short or ushort or byte or sbyte or char or bool
                    or float or double:
                    continue;

                //Untyped, so it says nothing either way - and saying nothing is what lets the width of the
                //arithmetic decide.
                case LocalVariable { Type: null }:
                    continue;

                //A **struct** refuses outright. It is a value in its own right and calling the answer a
                //number writes `long num4 = (long)Vector3.zero;`, which is no more C# than the line it
                //replaced; what to do with a struct here is `StructInArithmetic`'s question, not this one.
                //A **reference** does not refuse: a local the analysis called a reference and then subtracted
                //is mistyped in exactly the way an untyped one is, and refusing those cost
                //`SubCellVisual::ResetEyeScales` six of its eleven instructions.
                case LocalVariable { Type: { } held }:
                    if (held.IsValueType && !IsNumber(held))
                        return false;

                    if (IsNumber(held))
                        widest = Wider(widest, held);

                    continue;

                case FieldReference { Field.FieldType: { } read }:
                    if (read.IsValueType && !IsNumber(read))
                        return false;

                    if (IsNumber(read))
                        widest = Wider(widest, read);

                    continue;

                //Anything else - a memory operand, a method, a type, a string - is not a number.
                default:
                    return false;
            }
        }

        return true;
    }

    /// <summary>A float wins outright; between integers the widest is what the arithmetic was done at.</summary>
    private static TypeAnalysisContext Wider(TypeAnalysisContext? found, TypeAnalysisContext type)
        => found == null || IsFloat(type) || (!IsFloat(found) && Width(type) > Width(found)) ? type : found;

    private static bool IsFloat(TypeAnalysisContext type)
        => type.Type is Il2CppTypeEnum.IL2CPP_TYPE_R4 or Il2CppTypeEnum.IL2CPP_TYPE_R8;

    private static int Width(TypeAnalysisContext type) => type.Type switch
    {
        Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN or Il2CppTypeEnum.IL2CPP_TYPE_I1 or Il2CppTypeEnum.IL2CPP_TYPE_U1 => 1,
        Il2CppTypeEnum.IL2CPP_TYPE_CHAR or Il2CppTypeEnum.IL2CPP_TYPE_I2 or Il2CppTypeEnum.IL2CPP_TYPE_U2 => 2,
        Il2CppTypeEnum.IL2CPP_TYPE_I4 or Il2CppTypeEnum.IL2CPP_TYPE_U4 => 4,
        _ => 8,
    };

    private static bool IsNumber(TypeAnalysisContext type) => StructInArithmetic.IsNumber(type);

    /// <summary>
    /// The instructions whose answer is a number. A comparison is not one of them: it produces a boolean,
    /// which the analysis already types, and <c>Add</c> is left out where it stands alone because it is also
    /// how an address is worked out.
    /// </summary>
    private static bool IsArithmetic(OpCode opCode) => opCode is
        OpCode.Add or OpCode.Subtract or OpCode.Multiply or OpCode.Divide
        or OpCode.ShiftLeft or OpCode.ShiftRight or OpCode.And or OpCode.Or or OpCode.Xor
        or OpCode.Not or OpCode.Negate;
}
