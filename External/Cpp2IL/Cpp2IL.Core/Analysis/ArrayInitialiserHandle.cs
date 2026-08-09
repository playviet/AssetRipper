using System.Collections.Generic;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using LibCpp2IL;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Gives back the handle an array initialiser is copied through.
/// </summary>
/// <remarks>
/// <para>
/// <c>int[] t = { 11, 22, 33, 44 }</c> is not a run of stores. The compiler puts the bytes in the binary as a
/// hidden field and emits <c>newarr</c> followed by
/// <c>RuntimeHelpers.InitializeArray(t, fieldHandle)</c>, where the handle is a metadata usage read out of a
/// global:
/// </para>
/// <code>
/// Move handle, [0xDD4180]          ; a field usage - resolved to nothing
/// Call RuntimeHelpers.InitializeArray, t, handle
/// </code>
/// <para>
/// Unresolved, the read becomes the constant zero and the call is handed it. What that costs depends on where
/// it happens, and neither outcome says anything went wrong:
/// </para>
/// <list type="bullet">
/// <item>in a method, the array is the right length and full of zeros;</item>
/// <item>in a type's own initialiser, unboxing that zero throws, and every method on the class then throws
/// <c>TypeInitializationException</c> - one lost handle takes the whole type with it.</item>
/// </list>
/// <para>
/// The field is real, its bytes are already carried on the field context, and the output format already writes
/// them out as an RVA - so all that is missing is naming it. It is named rather than read, which is what
/// <see cref="FieldToken"/> is for.
/// </para>
/// </remarks>
public static class ArrayInitialiserHandle
{
    public static void Run(MethodAnalysisContext method)
    {
        if (method.ControlFlowGraph is not { } graph)
            return;

        Dictionary<LocalVariable, FieldAnalysisContext>? handles = null;

        foreach (var instruction in graph.Instructions)
        {
            if (instruction.OpCode != OpCode.Move
                || instruction.Operands.Count != 2
                || instruction.Operands[0] is not LocalVariable destination
                || instruction.Operands[1] is not MemoryOperand { Base: null, Index: null, Scale: 0 } global)
                continue;

            if (Handle(method, (ulong)global.Addend) is { } field)
                (handles ??= [])[destination] = field;
        }

        if (handles == null)
            return;

        //The handle arrives through two loads, not one: the global holds the field's runtime info, and the
        //handle is read out of that in turn - `Move v15, [0xDD4180]` then `Move v39, [v15]`. Both name the
        //same field, so what is known about the first has to reach the second, or the call is still holding
        //a local nothing has been said about. Repeated because the two loads need not be adjacent.
        for (var carried = true; carried;)
        {
            carried = false;

            foreach (var instruction in graph.Instructions)
            {
                if (instruction.OpCode != OpCode.Move
                    || instruction.Operands.Count != 2
                    || instruction.Operands[0] is not LocalVariable copy
                    || handles.ContainsKey(copy))
                    continue;

                var from = instruction.Operands[1] switch
                {
                    LocalVariable source => source,
                    MemoryOperand { Index: null, Scale: 0, Addend: 0, Base: LocalVariable through } => through,
                    _ => null,
                };

                if (from != null && handles.TryGetValue(from, out var carriedField))
                {
                    handles[copy] = carriedField;
                    carried = true;
                }
            }
        }

        //The token has to be loaded where it is used, not stored first. A `ldtoken` put into a local is IL
        //that ILSpy declines to decompile - `OpCode not supported: LdMemberToken` - and the whole initialiser
        //is then commented out, which costs exactly as much as never having resolved it. Handed straight to
        //the call it is the sequence a C# array initialiser compiles to, and it reads back as one.
        foreach (var instruction in graph.Instructions)
        {
            //InitializeArray returns nothing, so CallVoid is the one that carries it; the others are here
            //because a handle is a handle whatever is called with it.
            if (instruction.OpCode is not (OpCode.Call or OpCode.CallVoid or OpCode.IndirectCall))
                continue;

            for (var i = 0; i < instruction.Operands.Count; i++)
            {
                if (instruction.Operands[i] is LocalVariable argument && handles.TryGetValue(argument, out var field))
                    instruction.Operands[i] = new FieldToken(field);
            }
        }

        //Nothing reads the global any more, so the load of it goes when unused locals are dropped, and the
        //"unmanaged memory load" it would have printed goes with it.
    }

    /// <summary>The field an initialiser global names, where that is what it is.</summary>
    private static FieldAnalysisContext? Handle(MethodAnalysisContext method, ulong address)
    {
        var context = method.AppContext.LibCpp2IlContext;
        var usage = context.GetAnyGlobalByAddress(MetadataResolver.ThroughGlobalOffsetTable(method, address));

        //FieldInfo is the usage a handle is normally recorded as; FieldRva is the same field named by the data
        //it carries. Both index the field references, and AsField refuses the second, so the reference is
        //taken directly rather than through it.
        if (usage is not { Type: MetadataUsageType.FieldInfo or MetadataUsageType.FieldRva })
            return null;

        var references = context.Metadata.fieldRefs;

        if (usage.RawValue >= references.Length)
            return null;

        //Resolving asks the field for its declaring type, and a `<PrivateImplementationDetails>` storage field
        //is not always in the cache that answers that - it throws rather than returning nothing, which loses
        //the whole body over a read this pass would have declined anyway.
        FieldAnalysisContext? field;

        try
        {
            field = method.AppContext.ResolveContextForField(references[usage.RawValue].FieldDefinition);
        }
        catch (KeyNotFoundException)
        {
            return null;
        }

        if (field is null)
            return null;

        //The guard that makes taking the reference directly safe: an initialiser field is one that carries
        //bytes. A usage decoded out of the wrong table almost never lands on one, and fails closed if it does
        //not - leaving the read exactly as unresolved as it was.
        return field.StaticArrayInitialValue.Length > 0 ? field : null;
    }
}
