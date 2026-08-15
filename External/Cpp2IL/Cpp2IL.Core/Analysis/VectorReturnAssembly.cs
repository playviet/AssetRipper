using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// The callee side of a struct of floats: at the <c>ret</c>, the answer is gathered out of the vector
/// registers it was computed into and written into a buffer of the method's own return type.
/// </summary>
/// <remarks>
/// <para>
/// Aapcs64 returns a homogeneous float aggregate one field per vector register and never in <c>x0</c>, and
/// upstream's <c>GetReturnRegisterForContext</c> names <c>x0</c> anyway - above its own
/// <c>//TODO Do certain value types have different return registers?</c>. The <b>caller</b> side of this has
/// been handled since 1.0.746 by <c>VectorReturnFields</c>, which emits one read per returned field after the
/// call. The callee side had no counterpart, and the whole family fell out of the export in silence: of the
/// 74 methods in this game declaring an HFA return type, <b>64 return <c>default(T)</c></b> and every one of
/// them scores <c>full</c>. The execution oracle is what made it visible - <c>Scale</c>, <c>Cross</c> and
/// <c>Blend</c> in the corpus each recover as <c>T result = default(T); return result;</c>, no marker, two
/// statements, and the method does nothing.
/// </para>
/// <para>
/// <b>Three earlier attempts named the register instead of assembling the value</b> and all three measured
/// worse. 1.0.607 and 1.0.793 both replaced <c>x0</c> with <c>v0</c> at the return, which is ABI-correct and
/// names <b>lane zero only</b>: nothing gathered v1 and v2, so every such method returned a third of its
/// answer and the bodies that had compiled whole around an <c>x0</c> holding *something* stopped compiling
/// (compare2 3018 -> 2937 at 1.0.793). 1.1.48 tried carrying the lanes as extra operands of the
/// <c>Return</c> and found that at least two places assume it has exactly one
/// (<c>LocalVariables.cs:71</c>, <c>LocalVariables.Fork.cs:331</c>), so the lanes were never versioned and
/// what came out was a confident wrong answer built from undefined locals.
/// </para>
/// <para>
/// So: <b>one operand, and the lanes go into a store rather than beside the return</b>. The shape emitted is
/// exactly the one the indirect-return path already produces and the generator already writes correctly -
/// <c>Corpus::Spread</c> exports as <c>Wide result = default(Wide); result.Four = seed;</c> - only with a
/// buffer of this fork's own naming instead of the caller's <c>x8</c>:
/// </para>
/// <code>
/// Move [RETVAL+0], V0
/// Move [RETVAL+4], V1
/// Move [RETVAL+8], V2
/// Return RETVAL
/// </code>
/// <para>
/// Everything downstream is machinery that already exists. A store's destination is not a local, so
/// <c>DeadCodeEliminator</c> never removes one and the arithmetic that computed each lane is kept alive by
/// being read here. <c>LocalVariables.PropagateFromReturn</c> stamps the method's return type on the
/// return operand, so <c>RETVAL</c> is typed without a rule of its own. <c>MetadataResolver</c>'s
/// <c>FieldOfStructValue</c> then names <c>[RETVAL+4]</c> as the field at that distance into the value, and
/// the generator writes <c>result.y = ...</c>.
/// </para>
/// <para>
/// <b>Where it runs is the whole of why it works.</b> This is the last hook before the first
/// <c>DeadCodeEliminator</c> inside <c>Analyze</c>, and that run is what deletes the lane chain: nothing
/// reads v0..vn once the return has been named x0, so by the time any later pass could gather them the
/// evidence is gone. Before single assignment form is built, so the lanes are versioned and reach their
/// definitions like any other operand - the same reasoning as <see cref="StackedFloatArgument"/> beside it.
/// </para>
/// </remarks>
public static class VectorReturnAssembly
{
    /// <summary>
    /// The name of the buffer the answer is assembled into. A register of this fork's own invention rather
    /// than <c>x0</c>: in an instance method <c>x0</c> is the receiver and in a loop it is whatever was last
    /// reloaded through it, so writing fields through it would be a store into an unrelated object - which is
    /// exactly the defect <c>ReturnRegisterCarriesSomethingElse</c> exists to refuse. Nothing else ever names
    /// this register, so it has no value of its own and reads as <c>default(T)</c>, which is the right thing
    /// for a struct being filled in field by field.
    /// </summary>
    public const string BufferName = "RETVAL";

    public static void Run(MethodAnalysisContext method)
    {
        if (method.ControlFlowGraph is not { } graph || method.ReturnType is not { } returned)
            return;

        //Two floats at least: a single one already comes back in s0 and the return register names it
        //correctly, and more than four is not an HFA at all.
        if (HomogeneousFloatStruct.Count(returned) is not { } floats || floats is < 2 or > 4)
            return;

        //Every lane has to land on a field this can name, or the store stays a memory operand the generator
        //writes out as `Unmanaged memory store` - a marker where there had been a quiet default. A nested
        //aggregate (two Vector2s, say) counts four floats but declares two fields, so it is left alone.
        if (HomogeneousFloatStruct.Fields(returned) is not { } fields || fields.Count != floats)
            return;

        //Belt and braces: the same type must not also be answered through a buffer in x8.
        if (Aapcs64.ReturnsIndirectly(method))
            return;

        //**Every lane has to be accounted for**, or the assembly puts a confident wrong value where there had
        //been an honest `default`. A lane is accounted for if the body wrote it, or if it is one of this
        //method's *own* vector-argument registers - in which case its entry value is a parameter, and under
        //the convention a parameter left in place is exactly what that lane of the answer is:
        //`ChangeX(this Vector2 parent, float newX)` is one `fmov s0, s2` and nothing else, because v1 already
        //holds `parent.y`. Sixteen bodies in this game are that shape and the first cut of this pass, which
        //asked that every lane be written, refused all of them.
        //
        //What stays refused is the lane nothing can speak for: a call whose callee was not known while
        //lifting - virtual, interface, through a register - writes no lane, and where the method has no float
        //parameters either there is nothing in the register but whatever the caller left. That is the ten
        //survivors of the census, whose value arrives whole and is handed straight back.
        if (!EveryLaneIsAccountedFor(method, graph, floats))
            return;

        var buffer = new Register(null, BufferName);

        foreach (var block in graph.Blocks)
        {
            for (var at = 0; at < block.Instructions.Count; at++)
            {
                var instruction = block.Instructions[at];

                //Only a return the lifter named with a register. One already rewritten - or one carrying a
                //constant - is not this.
                if (instruction.OpCode != OpCode.Return || instruction.Operands.Count != 1
                    || instruction.Operands[0] is not Register)
                    continue;

                for (var lane = 0; lane < floats; lane++)
                {
                    block.Instructions.Insert(at, new Instruction(instruction.Index, OpCode.Move,
                        new MemoryOperand(buffer, addend: lane * 4),
                        new Register(null, "V" + lane)));
                    at++;
                }

                instruction.Operands[0] = buffer;
            }
        }
    }

    /// <summary>
    /// Whether every vector register the convention returns this type in holds something this method can
    /// speak for: a value the body wrote, or the entry value of one of its own vector arguments.
    /// </summary>
    private static bool EveryLaneIsAccountedFor(MethodAnalysisContext method, Graphs.ISILControlFlowGraph graph,
        int floats)
    {
        var written = new System.Collections.Generic.HashSet<string>();

        foreach (var instruction in graph.Instructions)
        {
            //`Destination` rather than `Operands[0]`, because a call's answer is operand *one* - and a call
            //returning a struct of floats is exactly how a lane gets its value.
            if (instruction.Destination is Register register)
                written.Add(register.Name);
        }

        var parameterLanes = VectorArgumentRegisters(method);

        for (var lane = 0; lane < floats; lane++)
            if (lane >= parameterLanes && !written.Contains("V" + lane))
                return false;

        return true;
    }

    /// <summary>
    /// How many vector registers this method's own parameters occupy, counted the way
    /// <see cref="Aapcs64.ParametersOf"/> lays a call's arguments out - the floating point run is independent
    /// of the integer one, so this depends only on the parameters of its own kind.
    /// </summary>
    private static int VectorArgumentRegisters(MethodAnalysisContext method)
    {
        var vector = 0;

        foreach (var parameter in method.Parameters)
        {
            var type = parameter.ParameterType;

            if (type is { Namespace: nameof(System), Name: "Single" or "Double" })
                vector++;
            else if (HomogeneousFloatStruct.Count(type) is { } floats)
                vector += floats;
        }

        return vector > Aapcs64.RegistersPerRun ? Aapcs64.RegistersPerRun : vector;
    }
}
