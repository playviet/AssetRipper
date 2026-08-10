using System.Collections.Generic;
using System.Globalization;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// A constant distance from a stack slot's address is the address of the field that far into the struct the
/// slot holds.
/// </summary>
/// <remarks>
/// <para>
/// Calling a method on a member of a struct the frame keeps means working the member's address out from the
/// struct's, and where the frame is aligned the compiler uses <c>orr</c> rather than <c>add</c>:
/// <c>add x21, sp, #0x20</c> then <c>orr x0, x21, #8</c> is <c>&amp;stateMachine.&lt;&gt;t__builder</c>.
/// <see cref="SlotAddressRead"/> reads a load *through* such an address; nothing read the address itself, so
/// the <c>Or</c> survived to the generator and the receiver came out as a number -
/// <c>((AsyncTaskMethodBuilder)(num | 8L)).Start(ref stateMachine)</c>, the largest single root the game's
/// commented statements have.
/// </para>
/// <para>
/// What the slot holds is not guessed: the callee says it. <c>AsyncTaskMethodBuilder.Start</c> takes
/// <c>ref TStateMachine</c>, so its one by-reference parameter names the struct, and the distance has to land
/// on a field of that struct whose type is the callee's own before anything is rewritten. Two independent
/// facts have to agree, and where they do the answer is metadata rather than inference.
/// </para>
/// <para>
/// The same call is what the by-reference argument belongs to, and it has been lost by the time this runs.
/// <see cref="OutParameterWriteback"/> makes a slot and its address one variable, so the store of the initial
/// state - <c>str w8, [sp, #0x20]</c>, which is <c>stateMachine.&lt;&gt;1__state = -1</c> - assigns the very
/// name the struct's address travels under, and the call reads <c>-1</c> where the state machine belongs.
/// That is right for a scalar out parameter and wrong for a struct whose first four bytes are a field of it;
/// naming the slot here puts the argument back without disturbing the shape that pass exists for.
/// </para>
/// </remarks>
public static class SlotFieldAddress
{
    private static readonly bool Trace =
        System.Environment.GetEnvironmentVariable("SLOTFIELD_TRACE") is not null;

    public static bool Run(MethodAnalysisContext method)
    {
        if (method.ControlFlowGraph is not { } graph)
            return false;

        var definitions = new Dictionary<LocalVariable, Instruction>();

        foreach (var instruction in graph.Instructions)
            if (instruction.Destination is LocalVariable destination)
                definitions[destination] = instruction;

        //What each slot holds, first and on its own: only a call that takes the struct by reference can say,
        //and a property getter on the same member cannot - so the getters are rewritten afterwards, on what
        //the one call that could say established.
        foreach (var call in graph.Instructions)
        {
            if (!call.IsCall || Callee(call) is not { } callee)
                continue;

            if (Made(call, definitions) is not { } made)
                continue;

            if (Trace)
                System.Console.Error.WriteLine($"SLOTFIELD {method.Name}: {callee.DeclaringType?.Name}.{callee.Name} "
                    + $"slot={made.Slot.Register.Name} type={made.Slot.Type?.FullName ?? "?"} d={made.Distance} "
                    + $"byref={ByReference(callee)?.FullName ?? "none"} "
                    + $"field={(ByReference(callee) is { } b ? FieldAt(b, made.Distance, method)?.Name ?? "none" : "-")}");

            if (ByReference(callee) is not { IsValueType: true } structure)
                continue;

            if (FieldAt(structure, made.Distance, method) is not { } field
                || callee.DeclaringType is not { } member || !SameType(field.FieldType, member))
                continue;

            //A slot nothing else named is typed by whatever last went through the register - `System.Int64`,
            //from the arithmetic below it - so the callee's word overrules it. Only a number: anything else
            //is something a pass with more to go on already settled.
            if (made.Slot.Type is null || IsNumber(made.Slot.Type))
                made.Slot.Type = structure;
        }

        var changed = false;

        foreach (var call in graph.Instructions)
        {
            if (!call.IsCall || Callee(call) is not { } callee || callee.DeclaringType is not { } member)
                continue;

            if (Made(call, definitions) is not { } made || made.Slot.Type is not { IsValueType: true } structure)
                continue;

            if (FieldAt(structure, made.Distance, method) is not { } field || !SameType(field.FieldType, member))
                continue;

            var receiver = ReceiverIndex(call);

            call.Operands[receiver] = new FieldReference(field, made.Slot, (int)made.Distance);

            //And the struct itself wherever the callee takes it by reference, which is the argument the
            //renaming above turned into the number that was last stored in the slot's first word.
            for (var i = 0; i < callee.Parameters.Count; i++)
                if (callee.Parameters[i].ParameterType is ByRefTypeAnalysisContext byReference
                    && SameType(byReference.ElementType, structure)
                    && receiver + 1 + i < call.Operands.Count)
                    call.Operands[receiver + 1 + i] = made.Slot;

            //Only once nothing else reads its answer: the reference above says it now, and left standing the
            //arithmetic is `stateMachine | 8L`, which is no more writable than what it replaced.
            if (!ReadElsewhere(graph, made.Arithmetic))
            {
                made.Arithmetic.OpCode = OpCode.Nop;
                made.Arithmetic.Operands = [];
            }

            changed = true;
        }

        return changed;
    }

    /// <summary>The method a call goes to, where the resolver has named one.</summary>
    private static MethodAnalysisContext? Callee(Instruction call)
        => call.Operands.Count > 0 ? call.Operands[0] as MethodAnalysisContext : null;

    /// <summary>Where a call's receiver sits: one that answers keeps its destination in front of it.</summary>
    private static int ReceiverIndex(Instruction call) => call.OpCode == OpCode.Call ? 2 : 1;

    /// <summary>The slot and the distance a call's receiver was worked out from, where it was worked out.</summary>
    private static (Instruction Arithmetic, LocalVariable Slot, long Distance)? Made(Instruction call,
        Dictionary<LocalVariable, Instruction> definitions)
    {
        var index = ReceiverIndex(call);

        if (call.Operands.Count <= index || call.Operands[index] is not LocalVariable receiver
            || !definitions.TryGetValue(receiver, out var arithmetic)
            || arithmetic.OpCode is not (OpCode.Or or OpCode.Add) || arithmetic.Operands.Count != 3
            || arithmetic.Operands[1] is not LocalVariable slot
            || Constant(arithmetic.Operands[2]) is not { } distance || distance <= 0
            || Offset(slot) is not { } start)
            return null;

        //An `orr` is only an `add` where the bits do not meet. The frame is sixteen-byte aligned, so the low
        //bits of a slot's address are the low bits of its offset, and the two have to be disjoint.
        if (arithmetic.OpCode == OpCode.Or && (start & distance) != 0)
            return null;

        return (arithmetic, slot, distance);
    }

    /// <summary>Where in the frame a local sits, if it is a slot's address rather than a register.</summary>
    private static long? Offset(LocalVariable local)
    {
        if (local.Register.Name is not { } name || !name.StartsWith(StackSlots.AddressPrefix))
            return null;

        var written = name[StackSlots.AddressPrefix.Length..];
        var negative = written.StartsWith('-');

        return long.TryParse(negative ? written[1..] : written, NumberStyles.HexNumber,
            CultureInfo.InvariantCulture, out var value) ? negative ? -value : value : null;
    }

    private static long? Constant(object operand) => operand switch
    {
        int i => i,
        uint u => u,
        long l => l,
        ulong ul => (long)ul,
        _ => null,
    };

    /// <summary>The one type a method takes by reference, where it takes exactly one.</summary>
    private static TypeAnalysisContext? ByReference(MethodAnalysisContext callee)
    {
        TypeAnalysisContext? only = null;

        foreach (var parameter in callee.Parameters)
            if (parameter.ParameterType is ByRefTypeAnalysisContext byReference)
            {
                if (only != null)
                    return null;

                only = byReference.ElementType;
            }

        return only;
    }

    /// <summary>The field of a struct at a distance from its front.</summary>
    /// <remarks>
    /// Both conventions occur, as <see cref="PackedStructFieldRead"/> records: il2cpp writes a struct's fields
    /// at the offsets they have in a boxed one, which is what <c>FieldOfStructValue</c> asks for, and a type
    /// laid out explicitly keeps the offsets it was given. A state machine is the second -
    /// <c>&lt;&gt;1__state@0, &lt;&gt;t__builder@8</c> - and the boxed question finds nothing at either.
    /// </remarks>
    private static FieldAnalysisContext? FieldAt(TypeAnalysisContext structure, long distance,
        MethodAnalysisContext method)
    {
        if (MetadataResolver.FieldOfStructValue(structure, distance, method) is { } boxed)
            return boxed;

        var definition = (structure as GenericInstanceTypeAnalysisContext)?.GenericType ?? structure;

        foreach (var field in definition.Fields)
            if (!field.IsStatic && field.Offset == distance)
                return structure is GenericInstanceTypeAnalysisContext instance
                    ? new ConcreteGenericFieldAnalysisContext(field, instance)
                    : field;

        //And a generic one records no layout at all - every field of `<LoadAsync>d__2`1<T>` reads as offset
        //zero, so neither question above can land. `GenericFieldLayout` works the offsets out from the field
        //types, but it needs the size of *every* field, and the size of `AsyncTaskMethodBuilder`1<T>` is
        //exactly what a generic instance does not record - so it gives up on the state machines this pass is
        //for. Only the fields *in front of* the one being asked about need a size, and walking to the answer
        //rather than past it is what makes the question answerable: `<>1__state` is four bytes at nothing,
        //aligned up to eight, and the builder is what starts there.
        var position = 0L;

        foreach (var field in definition.Fields)
        {
            if (field.IsStatic)
                continue;

            if (position == distance)
                return structure is GenericInstanceTypeAnalysisContext at
                    ? new ConcreteGenericFieldAnalysisContext(field, at)
                    : field;

            //Past it without landing on it: the distance is inside a field rather than at one, which this
            //pass has nothing to say about.
            if (position > distance || Aapcs64.SizeOf(field.FieldType) is not { } size || size <= 0)
                return null;

            //Each field starts at a multiple of its own size, which is what the generated struct does.
            position += size;
            position += (8 - position % 8) % 8;
        }

        return null;
    }

    /// <summary>The same type, disregarding which instantiation of it.</summary>
    /// <remarks>
    /// A shared body names its own type by stand-ins, so the builder a call is on arrives as
    /// <c>AsyncTaskMethodBuilder&lt;object&gt;</c> where the state machine declares
    /// <c>AsyncTaskMethodBuilder&lt;GameObject&gt;</c>. The open definitions agreeing is enough to know the
    /// distance landed on the right field, and <see cref="GenericSharingRecovery"/> - which runs after this -
    /// is what then reads the real instantiation off the field reference this leaves behind.
    /// </remarks>
    private static bool SameType(TypeAnalysisContext a, TypeAnalysisContext b)
        => Definition(a).FullName == Definition(b).FullName;

    private static TypeAnalysisContext Definition(TypeAnalysisContext type)
        => (type as GenericInstanceTypeAnalysisContext)?.GenericType ?? type;

    private static bool IsNumber(TypeAnalysisContext type) => type.FullName
        is "System.SByte" or "System.Byte" or "System.Int16" or "System.UInt16" or "System.Int32"
        or "System.UInt32" or "System.Int64" or "System.UInt64" or "System.IntPtr" or "System.UIntPtr";

    /// <summary>Whether anything but the references just written still reads the arithmetic's answer.</summary>
    private static bool ReadElsewhere(Graphs.ISILControlFlowGraph graph, Instruction arithmetic)
    {
        if (arithmetic.Destination is not LocalVariable made)
            return true;

        foreach (var instruction in graph.Instructions)
        {
            if (ReferenceEquals(instruction, arithmetic))
                continue;

            for (var i = instruction.IsAssignment ? 1 : 0; i < instruction.Operands.Count; i++)
                if (ReferenceEquals(instruction.Operands[i], made))
                    return true;
        }

        return false;
    }
}
