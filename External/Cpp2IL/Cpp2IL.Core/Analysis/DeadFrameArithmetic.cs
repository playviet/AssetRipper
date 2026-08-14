using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Takes away the working-out that nothing is left to read.
/// </summary>
/// <remarks>
/// <para>
/// Every pass that recovers a call replaces a run of machinery with the one statement it stood for, and what
/// it replaced does not go anywhere: the runtime generic context is still read, the class is still read, the
/// size of a <c>T</c> is still worked out, the frame the invoker thunk was handed is still built. None of it
/// is anything the recovery can name, so each surviving instruction is a marker at the front of a body that
/// is otherwise whole:
/// </para>
/// <code>
/// _ = "Unmanaged memory load: [v29 (Il2CppMethodRgctx&lt;RandomItem&gt;)+20]";
/// _ = "Unmanaged memory load: [v44 (Il2CppClass&lt;T&gt;)+FC]";
/// long num = num2 - (0xFL &amp; 0x1FFFFFFF0L);          // the alloca
/// _ = obj - 12L;                                     // the frame slot the thunk read
/// </code>
/// <para>
/// All four are dead - the values reach nothing - and the only reason they survive is that dead code
/// elimination works on <b>values</b>, so a store into the frame counts as a use of whatever it stores and
/// keeps the whole chain behind it alive. A store into this method's own frame that nothing reads back is as
/// dead as an unread value, and taking the two away together is what lets the chain unwind.
/// </para>
/// <para>
/// Only the frame. A store through anything else could be a field of an object somebody else can see, and a
/// frame whose address is handed to a call could be read by the callee - both are left alone.
/// </para>
/// </remarks>
public static class DeadFrameArithmetic
{
    /// <summary>An instruction whose whole effect is the value it produces.</summary>
    private static bool ComputesOnly(OpCode opCode) => opCode
        is OpCode.Move or OpCode.Add or OpCode.Subtract or OpCode.Multiply or OpCode.Divide
        or OpCode.ShiftLeft or OpCode.ShiftRight or OpCode.And or OpCode.Or or OpCode.Xor
        or OpCode.Not or OpCode.Negate or OpCode.Select
        or OpCode.CheckEqual or OpCode.CheckNotEqual or OpCode.CheckGreater or OpCode.CheckLess
        or OpCode.CheckGreaterOrEqual or OpCode.CheckLessOrEqual;

    public static void Run(MethodAnalysisContext method)
    {
        if (method.ControlFlowGraph is not { } graph)
            return;

        var escaped = FramesHandedOut(graph);

        for (var removedAny = true; removedAny;)
        {
            removedAny = false;

            var live = graph.Blocks.SelectMany(b => b.Instructions).Where(i => i.OpCode != OpCode.Nop).ToList();
            var read = Reads(live);

            foreach (var instruction in live)
            {
                if (!ComputesOnly(instruction.OpCode) || instruction.Operands.Count == 0)
                    continue;

                var wanted = instruction.Operands[0] switch
                {
                    LocalVariable value => read.Values.Contains(value),
                    MemoryOperand into when Frame(into) is { } place => escaped.Contains(place.Base)
                        || read.Places.Contains(place),
                    //Anywhere else - a field of an object, a static, an address worked out at run time - is
                    //somebody else's memory and a store into it is not this pass's to judge.
                    _ => true,
                };

                if (wanted)
                    continue;

                instruction.OpCode = OpCode.Nop;
                instruction.Operands = [];
                removedAny = true;
            }
        }
    }

    /// <summary>Every value and every place the method still reads.</summary>
    private static (HashSet<LocalVariable> Values, HashSet<(string Base, long Offset)> Places) Reads(List<Instruction> live)
    {
        var values = new HashSet<LocalVariable>();
        var places = new HashSet<(string, long)>();

        foreach (var instruction in live)
        {
            for (var i = 0; i < instruction.Operands.Count; i++)
            {
                var operand = instruction.Operands[i];

                //Operand zero of a computation is where the answer goes, so the *place* is not read.
                //Everything else is, including operand zero of anything that is not a computation - a call's
                //callee, a jump's target.
                var written = i == 0 && ComputesOnly(instruction.OpCode);

                //Writing somewhere still needs to know where, so every local a destination names is read even
                //when the destination itself is not. Counting a plain base was not enough: a resolved field
                //write says `receiver.field`, and missing that called the receiver dead the moment it was
                //only written through - `inventory._saveData = saveData;` became
                //`AbilityInventory abilityInventory = default(AbilityInventory);` and a write to that.
                if (!written || operand is not LocalVariable)
                    foreach (var inside in Locals(operand))
                        values.Add(inside);

                if (!written && operand is MemoryOperand memory && Frame(memory) is { } place)
                    places.Add(place);
            }
        }

        return (values, places);
    }

    /// <summary>
    /// Every value an operand names, however it is wrapped.
    /// </summary>
    /// <remarks>
    /// An operand is not always a local: a resolved field access is a <see cref="FieldReference"/> around one,
    /// a rank-two element is an array and its subscripts, and there is no common interface saying so.
    /// Anything unrecognised is taken apart by reflection rather than skipped, because a value missed here is
    /// a value called dead - which is the one way this pass can be wrong that nothing downstream would catch.
    /// </remarks>
    private static IEnumerable<LocalVariable> Locals(object? operand)
    {
        switch (operand)
        {
            case null:
                yield break;

            case LocalVariable local:
                yield return local;
                yield break;

            case MemoryOperand memory:
                foreach (var inside in Locals(memory.Base).Concat(Locals(memory.Index)))
                    yield return inside;

                yield break;

            //Before the case below, which it derives from.
            case NestedFieldReference nested:
                yield return nested.Local;
                yield break;

            case FieldReference field:
                yield return field.Local;
                yield break;

            //A struct of floats assembled for a call keeps its parts *inside* itself rather than beside it -
            //`FloatStructAssembly.Reads` exists to say so - and it lives in this namespace rather than in
            //ISIL, so the reflection fallback below, which is gated on the operand being an ISIL type, never
            //reached it. Every part was therefore invisible to this pass: in `new Vector3(x, y, z)` the
            //second and third components stay alive only because the call names them *again* as trailing
            //operands, while the first, having no such second mention, was called dead and the arithmetic
            //that computed it taken away. That is the whole of the lost-first-component family - **122 of
            //its 124 sites game-wide are argument nought of a vector constructor**, and `SubCellVisual`
            //assigns `_leftEyeScale * k` and gets `new Vector3(default(float), y, z)`.
            case FloatStructAssembly assembly:
                foreach (var part in assembly.Reads())
                    yield return part;

                yield break;

            //Everything else that can hold a value is an operand wrapper and lives beside these; a constant,
            //a type, a method or a block holds nothing this pass cares about.
            default:
                if (operand.GetType().Namespace == typeof(MemoryOperand).Namespace)
                    foreach (var inside in Anywhere(operand))
                        yield return inside;

                yield break;
        }
    }

    /// <summary>Whatever an unrecognised operand holds, found by asking it.</summary>
    private static IEnumerable<LocalVariable> Anywhere(object operand)
    {
        foreach (var field in operand.GetType().GetFields())
            foreach (var inside in Held(field.GetValue(operand)))
                yield return inside;

        foreach (var property in operand.GetType().GetProperties())
        {
            if (property.GetIndexParameters().Length > 0)
                continue;

            object? value;

            try
            {
                value = property.GetValue(operand);
            }
            catch
            {
                continue;
            }

            foreach (var inside in Held(value))
                yield return inside;
        }
    }

    private static IEnumerable<LocalVariable> Held(object? value)
    {
        if (value is LocalVariable local)
        {
            yield return local;
        }
        else if (value is System.Collections.IEnumerable many and not string)
        {
            foreach (var each in many)
                foreach (var inside in Locals(each))
                    yield return inside;
        }
        else if (value is MemoryOperand or FieldReference or NestedFieldReference)
        {
            foreach (var inside in Locals(value))
                yield return inside;
        }
    }

    /// <summary>
    /// The frames whose address the method gives away, and whose contents a callee could therefore read.
    /// </summary>
    private static HashSet<string> FramesHandedOut(Graphs.ISILControlFlowGraph graph)
    {
        var given = new HashSet<string>();

        foreach (var instruction in graph.Blocks.SelectMany(b => b.Instructions))
        {
            if (instruction.OpCode is not (OpCode.Call or OpCode.CallVoid or OpCode.IndirectCall))
                continue;

            foreach (var operand in instruction.Operands)
                if (operand is LocalVariable local && IsFrame(local))
                    given.Add(local.Register.Name);
        }

        return given;
    }

    /// <summary>The place a memory operand names, where that place is on this method's own frame.</summary>
    private static (string Base, long Offset)? Frame(MemoryOperand memory)
        => memory is { Index: null, Scale: 0, Base: LocalVariable held } && IsFrame(held)
            ? (held.Register.Name, memory.Addend)
            : null;

    /// <summary>A local that stands for somewhere on the frame rather than for a value.</summary>
    private static bool IsFrame(LocalVariable local)
        => local.Register.Name is { } name
            && (name.StartsWith(StackSlots.AddressPrefix) || name.StartsWith(StackSlots.ValuePrefix));
}
