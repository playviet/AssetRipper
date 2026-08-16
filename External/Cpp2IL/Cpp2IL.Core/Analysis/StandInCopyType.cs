using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Overrules a runtime stand-in on a local that is copied to or from something the program can name.
/// </summary>
/// <remarks>
/// <para>
/// A register that once held a class pointer keeps that stand-in type for the rest of the method, and where
/// the same register later carries a managed value across a block edge the copies say two things at once:
/// </para>
/// <code>
/// Move v157 @ X19_v4 (CF.CellData[]),           board @ X0 (CF.CellData[])
/// Move v158 @ X19_v5 (Il2CppClass&lt;CF.BoardLogic&gt;), v157 @ X19_v4 (CF.CellData[])
/// Move v157 @ X19_v4 (CF.CellData[]),           v158 @ X19_v5 (Il2CppClass&lt;CF.BoardLogic&gt;)
/// </code>
/// <para>
/// The generator has to write a conversion between the two, and what comes out is
/// <c>array = (CellData[])num17;</c> - which is not C#, so the statement is commented and every later one
/// that read the array goes with it. `BoardLogic::CheckAndMergeAll` lost its merge loop to exactly that.
/// </para>
/// <para>
/// A stand-in is not a type the program has, so where a copy puts a managed reference into one - or takes one
/// out of it - the managed side is what the register holds. The class pointer that named it belongs to the
/// initialisation guard, which is unreachable by the time this code runs.
/// </para>
/// <para>
/// <b>Not inside a generic body</b>, for the reason
/// <see cref="LocalVariables.SetTypeFromParameter"/> gives: there the method's own <c>MethodInfo*</c> is how
/// every runtime generic context entry is reached, so a local holding one is load-bearing however it is also
/// copied. Last of all, so that nothing which resolves against a stand-in has yet to run.
/// </para>
/// </remarks>
public static class StandInCopyType
{
    public static void Run(MethodAnalysisContext method)
    {
        if (method.ControlFlowGraph is not { } graph || method.GenericParameters.Any())
            return;

        //Only a local that never received a class pointer of its own. Overruling every stand-in a copy
        //touches was measured: it took the six bad casts out of `CheckAndMergeAll` and put **forty** commented
        //statements into the same file, because the reads that legitimately go through a class pointer stopped
        //resolving. A stand-in whose every definition is a copy was never a class pointer at all - the type
        //came from a register the guard used earlier - and that one is free to correct.
        var carriedOnly = new Dictionary<LocalVariable, bool>();

        foreach (var instruction in graph.Instructions)
        {
            if (instruction.Destination is not LocalVariable written)
                continue;

            //Reading a field is a copy of something nameable too, and a stronger one: a managed field never
            //holds a class pointer, so a local filled from one was never a class pointer either.
            //`BoardController::BuildLevelPool` opens with `Move v65 (Il2CppClass<BoardSettingSO>),
            //this._shuffleColors (ECellColor[])` and the array's own length then read as
            //`[Il2CppClass<BoardSettingSO>+18]` - an unmanaged-memory statement for what is `.Length`.
            var copy = instruction is { OpCode: OpCode.Move, Operands: [_, LocalVariable or FieldReference] };
            carriedOnly[written] = copy && carriedOnly.GetValueOrDefault(written, true);
        }

        for (var settling = true; settling;)
        {
            settling = false;

            foreach (var instruction in graph.Instructions)
            {
                if (instruction is not { OpCode: OpCode.Move, Operands: [LocalVariable into, { } source] })
                    continue;

                //What the source says it holds, whether it is a local or the field one was read out of.
                var held = source switch
                {
                    LocalVariable local => local.Type,
                    FieldReference { Field.FieldType: { } declared } => declared,
                    _ => null,
                };

                if (LocalVariables.IsRuntimeStandIn(into.Type) && Nameable(held) && carriedOnly.GetValueOrDefault(into))
                {
                    into.Type = held;
                    settling = true;
                }
                else if (!SharedInstanceOff && SharperInstantiation(into.Type, held) is { } sharper
                    && carriedOnly.GetValueOrDefault(into))
                {
                    into.Type = sharper;
                    settling = true;
                }
                else if (source is LocalVariable from
                    && LocalVariables.IsRuntimeStandIn(from.Type) && Nameable(into.Type) && carriedOnly.GetValueOrDefault(from))
                {
                    from.Type = into.Type;
                    settling = true;
                }
            }
        }
    }

    /// <summary>Set <c>SHAREDCOPY_OFF=1</c> to measure the same build without the instantiation rule.</summary>
    internal static readonly bool SharedInstanceOff = System.Environment.GetEnvironmentVariable("SHAREDCOPY_OFF") == "1";

    /// <summary>
    /// The real instantiation behind a generic-sharing stand-in, where a copy says what it is.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The other stand-in, and it needs the same treatment for a different reason. il2cpp shares one body per
    /// reference instantiation, so every call into <c>List&lt;T&gt;</c> names its receiver
    /// <c>List&lt;object&gt;</c> - and that type then settles on the local, because
    /// <c>SetTypeIfUnknown</c> will not revisit one. While copy propagation folded the field read into the
    /// call the receiver kept the field's real type and nothing noticed; once a copy legitimately survives
    /// (see <see cref="StoreTarget"/>) the local keeps <c>List&lt;object&gt;</c>, its element reads as
    /// <c>object</c>, and a field of the element has nothing to resolve against.
    /// </para>
    /// <para>
    /// <c>BoardController::InitBoard</c> is what named it: <c>_targetColors[i] = _targets[i].color</c> came
    /// back as <c>targetColors3[num5] = (ECellColor)obj;</c> with the colour read left as
    /// <c>Unmanaged memory load: [… (System.Object)+10]</c> - so every target colour was written as zero.
    /// </para>
    /// <para>
    /// Exact rather than heuristic: the two must be the <b>same generic definition</b>, argument for
    /// argument, differing only where the stand-in has a stand-in and the copy has something real. Two
    /// locals a copy joins hold one object, so if the instantiations can differ at all they are not the same
    /// thing and nothing is taken. <c>System.Object</c> standing where a <b>value type</b> is refused for
    /// that reason - a value-type instantiation gets a body of its own and is never shared with one.
    /// </para>
    /// </remarks>
    private static TypeAnalysisContext? SharperInstantiation(TypeAnalysisContext? standIn, TypeAnalysisContext? real)
    {
        if (standIn is not GenericInstanceTypeAnalysisContext shared
            || real is not GenericInstanceTypeAnalysisContext concrete
            || shared.GenericType.FullName != concrete.GenericType.FullName
            || shared.GenericArguments.Count != concrete.GenericArguments.Count)
        {
            return null;
        }

        var sharper = false;

        for (var argument = 0; argument < shared.GenericArguments.Count; argument++)
        {
            var stands = shared.GenericArguments[argument];
            var given = concrete.GenericArguments[argument];

            if (stands.FullName == given.FullName)
                continue;

            if (!SharedBody.IsAStandIn(stands) || given.IsValueType || SharedBody.IsAStandIn(given))
                return null;

            sharper = true;
        }

        return sharper ? real : null;
    }

    /// <summary>A reference type the program itself has - which a stand-in never is.</summary>
    /// <remarks>
    /// Not a type parameter. Unconstrained, <c>T</c> is neither a reference nor a value type to the language,
    /// so a value that arrives as one has to be cast through <c>object</c> and cannot be compared to null -
    /// <c>GameObjectExtension::HasInterface&lt;T&gt;</c> holds its candidate in an <c>object</c> exactly for
    /// that reason, and calling it a <c>T</c> costs the two statements that read it.
    /// </remarks>
    private static bool Nameable(TypeAnalysisContext? type)
        => type is { IsValueType: false }
            and not (ByRefTypeAnalysisContext or PointerTypeAnalysisContext
                or GenericParameterTypeAnalysisContext)
            && !LocalVariables.IsRuntimeStandIn(type);
}
