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

        //Where the registers beyond the first begin. The lifter hands them over after every parameter, in the
        //order the parameters occupy them - see BeyondTheFirstRegister. They are what the walk below used to
        //have to go looking for, and where they are present nothing has to be inferred at all.
        var beyond = first + callee.Parameters.Count;

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
            var handed = beyond;

            vector += floats;
            beyond += floats - 1;

            if (floats < 2 || first + i >= instruction.Operands.Count)
                continue;

            if (FloatConstructor(type, floats) is not { } constructor)
                continue;

            var whole = instruction.Operands[first + i];

            //Where the first register already holds the struct itself - the untouched result of a call that
            //returned one - there is nothing to assemble, and assembling anyway puts a `Vector3` where the
            //constructor wants its `x`. Without this guard the same design cost 49 whole bodies.
            //
            //**But only where it is the same struct.** A `Rect` parameter occupies v0..v3, and a call in the
            //same method whose first vector argument is also v0 is handed a `Vector3` - so the register holds
            //a struct, and not this one. `MultiCutoutOverlay::AddQuad` calls `vh.AddVert(new Vector3(x, y, 0),
            //…)` four times and two of them came out passing `rect` whole. The first lane of the argument is
            //then the member at the front of whatever the register does hold.
            if (whole is LocalVariable { Type: { } already } && HomogeneousFloatStruct.Count(already) is > 1)
            {
                if (already.FullName == type.FullName)
                    continue;

                if (StructInArithmetic.Front(already) is not { } lane)
                    continue;

                whole = new FieldReference(lane, (LocalVariable)instruction.Operands[first + i], 0);
            }

            var parts = new List<object> { whole };

            for (var field = 1; field < floats && parts.Count == field; field++)
            {
                //What the call was handed, where the lifter recorded it, and otherwise the walk back. The two
                //answer the same question; the first is what the convention says rather than what the
                //instructions before this one happen to show, and it is right in the cases the walk gave up
                //on - a value carried in from a caller, or one that copy propagation moved to another
                //register and left no definition of this one behind.
                if (handed + field - 1 < instruction.Operands.Count
                    && instruction.Operands[handed + field - 1] is { } given && IsAFloat(given))
                {
                    parts.Add(given);
                    continue;
                }

                if (Reaching(block, at, start, field) is { } held)
                    parts.Add(held);
            }


            //And every part has to be a float. A register the walk reached may hold anything - a long, an
            //address, a read through one - and a constructor taking four floats handed those comes out as
            //`new Vector3(num * 1067366482L, num, ((Vector3*)num)->z)`, which is worse than the cast it
            //replaced. Three of the 96 files lost a method to exactly that.
            if (parts.Count == floats && parts.TrueForAll(IsAFloat))
            {
                //An integral zero that got here is a cleared lane; the constructor takes floats, and an int
                //reaching it would be written as `ldc.i4.0` where `ldc.r4` belongs.
                for (var part = 0; part < parts.Count; part++)
                    if (parts[part] is int or long or uint or ulong)
                        parts[part] = 0f;

                instruction.Operands[first + i] = new FloatStructAssembly(constructor, parts);
            }
        }
    }

    /// <summary>Whether an operand is a single-precision value, which is all a field of one of these is.</summary>
    /// <remarks>
    /// A value with no type at all is taken where it sits in a vector register. The guard exists to keep a
    /// long, an address or a read through one out of a constructor that wants floats - and none of those is
    /// ever in v0..v31, because an address lives in an x register. What is left there is a float by the same
    /// convention that says which registers the argument occupies, and refusing it lost the whole assembly
    /// over one lane the type fixpoint never reached: `tf.localScale = Vector3.one * 0.85f` had two of its
    /// three fields.
    /// </remarks>
    private static bool IsAFloat(object part) => part switch
    {
        float or double => true,
        FieldReference field => field.Field.FieldType.FullName == "System.Single",
        LocalVariable { Type: { } type } => type.FullName == "System.Single",
        LocalVariable { Type: null, Register.Name: { Length: > 1 } name } => name[0] is 'V' or 'S' or 'D',
        //A lane cleared with `movi d0, #0` arrives as an integral zero, and zero bits are 0f whichever way
        //they are read - the same premise as a broadcast immediate. Only zero: any other integral constant in
        //a vector register would be a bit pattern this has no reason to reinterpret.
        int or long or uint or ulong => System.Convert.ToInt64(part) == 0,
        _ => false,
    };

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
    private static object? Reaching(Block block, int at, int first, int field)
    {
        var register = first + field;

        //A field is in the next register along - or, where one vector register held the whole struct and the
        //lane splitter took it apart, in a lane of the first. `set_pivot` is handed `q0` with both floats in
        //it, so what holds `y` is `V0#4` and there is no `V1` in the body at all.
        var name = "V" + (first + field);
        var lane = "V" + first + "#" + (field * 4);

        for (var depth = 0; depth < Depth; depth++)
        {
            for (var i = at - 1; i >= 0; i--)
            {
                var instruction = block.Instructions[i];

                if (Defined(instruction) is { } written && (Names(written, name) || Names(written, lane)))
                    return written;

                //Every vector register is caller-saved, so a call destroys whatever was in this one - unless
                //the call is what put it there. A struct of floats comes back one field to a register, so
                //after such a call this register holds that field and nothing else does.
                //
                //The lifter names a struct return `x0`, so the **first** field's answer comes from the value
                //flow while the rest come from here. Where the caller had overwritten none of them that is a
                //struct built half from one place and half from another - which is what the guard in
                //`Assemble` refuses, and without that guard this cost 49 whole bodies.
                if (instruction.IsCall)
                    return Returned(instruction, register);
            }

            if (block.Predecessors.Count != 1)
                return null;

            at = block.Predecessors[0].Instructions.Count;
            block = block.Predecessors[0];
        }

        return null;
    }

    /// <summary>
    /// The field of a call's result left in a vector register, where the call returns a struct of floats
    /// that reaches this far. Null for any other call, which merely destroyed the register.
    /// </summary>
    private static object? Returned(Instruction call, int register)
    {
        if (call.OpCode != OpCode.Call || call.Operands.Count < 2
            || call.Operands[0] is not MethodAnalysisContext callee
            || call.Operands[1] is not LocalVariable result)
            return null;

        var returned = callee.ReturnType;

        if (HomogeneousFloatStruct.Count(returned) is not { } floats || register >= floats
            || HomogeneousFloatStruct.Fields(returned) is not { } fields || fields.Count != floats)
            return null;

        return new FieldReference(fields[register], result, register * 4);
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

    /// <summary>Which vector register a local is, ignoring any lane within it.</summary>
    private static int? VectorRegister(LocalVariable local)
        => local.Register is { Name: { Length: > 1 } name } && name[0] is 'V' or 'S' or 'D'
            && int.TryParse(name[1..].Split('#')[0], out var number)
            ? number
            : null;
}

/// <summary>
/// A struct of floats named by the registers its fields arrived in, and the constructor that puts them back
/// together. Only the generator reads this; see <see cref="HomogeneousFloatArguments"/>.
/// </summary>
public sealed class FloatStructAssembly(MethodAnalysisContext constructor, List<object> parts)
{
    public MethodAnalysisContext Constructor { get; } = constructor;

    public List<object> Parts { get; } = parts;

    /// <summary>
    /// Every local the assembly reads, for the passes that count uses. They are inside the operand rather
    /// than beside it, so nothing that walks `Instruction.Operands` sees them without asking.
    /// </summary>
    public IEnumerable<LocalVariable> Reads()
    {
        foreach (var part in Parts)
        {
            switch (part)
            {
                case LocalVariable local:
                    yield return local;
                    break;
                case FieldReference { Local: { } through }:
                    yield return through;
                    break;
            }
        }
    }

    public override string ToString() => $"new {Constructor.DeclaringType?.Name}({string.Join(", ", Parts)})";
}
