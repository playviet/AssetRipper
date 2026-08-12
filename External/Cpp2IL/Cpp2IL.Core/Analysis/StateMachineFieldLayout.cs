using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Works out where a generic state machine keeps its fields, and checks the answer against the code that
/// reads them.
/// </summary>
/// <remarks>
/// <para>
/// A generic type is laid out once per set of arguments, so the metadata records no offsets at all - every
/// field of <c>AssetLoader+&lt;LoadAsync&gt;d__2&lt;T&gt;</c> says nought.
/// <see cref="GenericFieldLayout"/> can compute a layout and only trusts it where a concrete type derives
/// from the generic one and its first recorded field agrees with where the base ends. A compiler-generated
/// state machine has no derived type, so nothing could confirm it and every access stayed a read of unmanaged
/// memory through <c>this</c>: twenty-three of them in <c>AssetLoader</c>'s two.
/// </para>
/// <para>
/// There is another thing to check it against: <b>the code</b>. Every distance the body adds to <c>this</c>
/// is a field it means to reach, so if the computed layout puts a field at every one of them the layout is
/// the one the compiler used, and if it puts a field in the middle of one it is not. A wrong layout cannot
/// pass - it would have to land on every boundary by accident - and a layout that passes has been checked
/// against as many facts as the method has accesses.
/// </para>
/// <para>
/// A shared body only ever runs for reference instantiations, so a field of the machine's own <c>T</c> is a
/// pointer. That is what makes the layout computable at all.
/// </para>
/// </remarks>
public static class StateMachineFieldLayout
{
    /// <summary>What sits in front of an object's fields: its class and its monitor.</summary>
    private const long Header = 0x10;

    private static readonly HashSet<TypeAnalysisContext> Settled = [];

    public static bool Run(MethodAnalysisContext method)
    {
        if (method.ControlFlowGraph is not { } graph || method.IsStatic
            || method.DeclaringType is not { } owner || !IsCompilerGenerated(owner)
            || owner.Fields.Count == 0)
        {
            return false;
        }

        lock (Settled)
        {
            if (Settled.Contains(owner))
                return false;
        }

        //Nothing to do where the metadata already says: a non-generic state machine has real offsets.
        if (owner.Fields.Any(f => !f.IsStatic && f.Offset > 0))
        {
            if (System.Environment.GetEnvironmentVariable("LAYOUT_TRACE") is not null)
                System.Console.Error.WriteLine($"LAYOUT {owner.Name}::{method.Name} already has offsets");

            return false;
        }

        if (Laid(owner) is not { } layout || layout.Count == 0)
        {
            if (System.Environment.GetEnvironmentVariable("LAYOUT_TRACE") is not null)
                System.Console.Error.WriteLine($"LAYOUT {owner.Name}::{method.Name} could not be laid out");

            return false;
        }

        var wanted = Reached(graph, method);

        if (System.Environment.GetEnvironmentVariable("LAYOUT_TRACE") is not null)
            System.Console.Error.WriteLine($"LAYOUT {owner.Name}::{method.Name} fields={owner.Fields.Count} "
                + $"laid=[{string.Join(",", layout.Keys.OrderBy(k => k).Select(k => k.ToString("X")))}] "
                + $"wanted=[{string.Join(",", wanted.OrderBy(k => k).Select(k => k.ToString("X")))}] "
                + $"base={owner.BaseType?.FullName} :: "
                + string.Join(" | ", owner.Fields.Where(f => !f.IsStatic).Select(f => $"{f.Name}:{f.FieldType?.FullName}")));

        //Nothing observed says nothing either way, and a distance that is not a field start says the layout
        //is not the one the compiler used.
        if (wanted.Count == 0 || wanted.Any(at => !layout.ContainsKey(at)))
            return false;

        lock (Settled)
        {
            //Recorded on the fields as well, so that everything downstream that asks a field where it is gets
            //the same answer - but the accesses in this body are rewritten here, because what resolves them
            //ran long before there was an offset to find.
            if (Settled.Add(owner))
                foreach (var (where, held) in layout)
                    held.OverrideOffset = (int)where;
        }

        var named = false;

        foreach (var instruction in graph.Instructions)
        {
            for (var i = 0; i < instruction.Operands.Count; i++)
            {
                if (instruction.Operands[i] is not MemoryOperand { Index: null, Scale: 0, Base: LocalVariable { IsThis: true } self } read
                    || !layout.TryGetValue(read.Addend, out var field))
                {
                    continue;
                }

                instruction.Operands[i] = new FieldReference(field, self, (int)read.Addend);
                named = true;
            }
        }

        return named;
    }

    /// <summary>Whether the type is one the compiler wrote, which is where this shape lives.</summary>
    private static bool IsCompilerGenerated(TypeAnalysisContext type)
        => type.Name is { } name && name.StartsWith('<') && name.Contains(">d__");

    /// <summary>Every distance from <c>this</c> the body reaches for.</summary>
    private static HashSet<long> Reached(Graphs.ISILControlFlowGraph graph, MethodAnalysisContext method)
    {
        var found = new HashSet<long>();

        //By what it is rather than by which object it is: the receiver is copied about, and this runs early
        //enough that the copies have not been folded away.
        foreach (var instruction in graph.Instructions)
        {
            foreach (var operand in instruction.Operands)
            {
                if (operand is MemoryOperand { Index: null, Scale: 0, Base: LocalVariable { IsThis: true } } read
                    && read.Addend >= Header)
                {
                    found.Add(read.Addend);
                }
            }

            //And the address of one, taken to pass it on or to read through later.
            if (instruction is { OpCode: OpCode.Add, Operands: [_, LocalVariable { IsThis: true }, { } by] }
                && Constant(by) is { } away && away >= Header)
            {
                found.Add(away);
            }
        }

        return found;
    }

    /// <summary>
    /// Where each field would begin: the header, then every one in declaration order at its natural
    /// alignment.
    /// </summary>
    private static Dictionary<long, FieldAnalysisContext>? Laid(TypeAnalysisContext owner)
    {
        var layout = new Dictionary<long, FieldAnalysisContext>();
        var at = Header;

        foreach (var field in owner.Fields)
        {
            if (field.IsStatic)
                continue;

            if (Sized(field.FieldType) is not var (size, alignment) || size <= 0)
                return null;

            at = (at + alignment - 1) / alignment * alignment;
            layout[at] = field;
            at += size;
        }

        return layout;
    }

    /// <summary>How much room a field takes and what it has to start on.</summary>
    /// <remarks>
    /// A reference is a pointer, and so is the machine's own <c>T</c>: a body shared between instantiations
    /// only ever runs for reference ones, which is what makes any of this answerable.
    /// </remarks>
    private static (long Size, long Alignment)? Sized(TypeAnalysisContext? type)
    {
        if (type is null)
            return null;

        if (type is GenericParameterTypeAnalysisContext || !type.IsValueType)
            return (8, 8);

        //A struct built on the machine's own `T` records no layout either, and every one that turns up here -
        //the builder and the awaiter - is one reference wide. `Aapcs64.SizeOf` adds the declared fields up and
        //gets twenty-eight for `AsyncTaskMethodBuilder<T>`, which put every field after it in the wrong place.
        if (MentionsAGenericParameter(type, 0))
            return (8, 8);

        if (Aapcs64.SizeOf(type) is not { } size || size <= 0)
            return null;

        //A struct starts on whatever its widest member starts on, and nothing on this target needs more
        //than eight.
        return (size, System.Math.Min(Widest(type, 0), 8));
    }

    /// <summary>Whether a type is a shared generic parameter, or is built out of one.</summary>
    private static bool MentionsAGenericParameter(TypeAnalysisContext type, int depth) => depth <= 4 && type switch
    {
        GenericParameterTypeAnalysisContext => true,
        GenericInstanceTypeAnalysisContext instance => instance.GenericArguments.Any(a => MentionsAGenericParameter(a, depth + 1)),
        WrappedTypeAnalysisContext wrapped => wrapped.ElementType is { } element && MentionsAGenericParameter(element, depth + 1),
        _ => false,
    };

    private static long Widest(TypeAnalysisContext type, int depth)
    {
        if (depth > 4)
            return 8;

        long widest = 1;

        foreach (var field in type.Fields)
        {
            if (field.IsStatic || field.FieldType is not { } held)
                continue;

            var member = held is GenericParameterTypeAnalysisContext || !held.IsValueType ? 8
                : held.Fields.Any(f => !f.IsStatic && f.FieldType?.IsValueType == true && f.FieldType.Namespace != nameof(System))
                    ? Widest(held, depth + 1)
                    : Aapcs64.SizeOf(held) ?? 8;

            widest = System.Math.Max(widest, System.Math.Min(member, 8));
        }

        return widest;
    }

    private static long? Constant(object operand)
        => operand switch
        {
            int i => i,
            uint u => u,
            long l => l,
            ulong ul => (long)ul,
            _ => null,
        };
}
