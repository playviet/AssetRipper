using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Names the field whose address the compiler worked out into a register.
/// </summary>
/// <remarks>
/// <para>
/// A field read is usually one instruction and the resolver names it from the offset in the addressing mode.
/// Where the compiler wanted the address itself - to call a method on a struct field, or to hand it to a
/// by-reference parameter - it computes the address into a register instead, and then nothing names the
/// field at all:
/// </para>
/// <code>
/// Add   v12, this, 0x30
/// Call  AsyncOperationHandle`1.get_Task, v13, v12
/// </code>
/// <para>
/// What the generator writes for that is address arithmetic on a managed reference,
/// <c>Unsafe.AddByteOffset(ref this, 48L)</c>, which does not compile - and the call beside it is given a
/// fresh <c>default(T)</c> instead of the field. There are <b>499</b> additions of <c>this</c> and a
/// constant in the game.
/// </para>
/// <para>
/// <b>Every use has to be one this can speak for</b>, and there are three: a read through the address at
/// distance nought, the receiver of a call on a value type, and a by-reference argument. The first becomes
/// the field's value and the other two its address - which is what
/// <c>IlGenerator.Fork.TryLoadFieldAddress</c> then writes as <c>ldflda</c>. Any other use and the address
/// is a value in its own right, so the addition is left exactly as it was.
/// </para>
/// </remarks>
public static class FieldAddressRecovery
{
    public static bool Run(MethodAnalysisContext method)
    {
        if (method.ControlFlowGraph is not { } graph)
            return false;

        var header = method.AppContext.Binary.is32Bit ? 8 : 0x10;
        var changed = false;

        foreach (var addition in graph.Instructions.ToList())
        {
            if (addition.OpCode != OpCode.Add || addition.Operands.Count != 3)
                continue;

            if (addition.Operands[0] is not LocalVariable address
                || addition.Operands[2] is not (long or int or ulong or uint))
                continue;

            var offset = System.Convert.ToInt64(addition.Operands[2]);

            //Taking the address of a **static** field: the base is the read of the class's static storage,
            //which is where `Interlocked.CompareExchange(ref SomeEvent, ...)` gets the place it swaps. Every
            //event accessor in the game is that call, so the call was already named and its first argument
            //was `0 + 8L`. Written as a field reference the generator says `ldsflda`, which it can already do.
            if (StaticFieldAt(addition.Operands[1], offset, method) is { } statics)
            {
                if (Uses(graph, addition, address) is not { Count: > 0 } staticUses)
                    continue;

                foreach (var (instruction, operand) in staticUses)
                    instruction.Operands[operand] = new FieldReference(statics, address, (int)offset);

                addition.OpCode = OpCode.Nop;
                addition.Operands = [];
                changed = true;
                continue;
            }

            if (addition.Operands[1] is not LocalVariable { Type: { IsValueType: false } owner } held)
                continue;

            if (FieldAt(owner, offset, header) is not { } field)
                continue;

            if (Uses(graph, addition, address) is not { Count: > 0 } uses)
                continue;

            foreach (var (instruction, operand) in uses)
                instruction.Operands[operand] = new FieldReference(field, held, (int)offset);

            addition.OpCode = OpCode.Nop;
            addition.Operands = [];
            changed = true;
        }

        return changed;
    }

    /// <summary>
    /// The static field at that distance into a class's static storage, where the base is a read of it.
    /// </summary>
    /// <remarks>
    /// <c>Add v96, [v88 (Il2CppClass&lt;T&gt;) + 0xB8], 8</c> is the address of <c>T</c>'s second static
    /// field. The read half of this - <c>[storage + 8]</c> as a value - `MetadataResolver` already resolves;
    /// only the address half was missing, and it is the one every event accessor needs.
    /// </remarks>
    private static FieldAnalysisContext? StaticFieldAt(object under, long offset, MethodAnalysisContext method)
    {
        var storage = method.AppContext.Binary.is32Bit ? 0x5C : 0xB8;

        if (under is not MemoryOperand { Index: null, Scale: 0, Base: LocalVariable klass } read
            || read.Addend != storage
            || klass.Type is not RuntimeClassTypeAnalysisContext { RepresentedType: { } owner })
            return null;

        var definition = (owner as GenericInstanceTypeAnalysisContext)?.GenericType ?? owner;

        return definition.Fields.FirstOrDefault(f => f.IsStatic && f.BackingData?.FieldOffset == offset);
    }

    /// <summary>The instance field lying at that distance into the type, if one does.</summary>
    /// <remarks>
    /// il2cpp records a field's offset as the type is laid out boxed, so a reference type's fields already
    /// carry the object header and match the addend directly.
    /// </remarks>
    private static FieldAnalysisContext? FieldAt(TypeAnalysisContext owner, long offset, int header)
    {
        var wanted = owner.IsValueType ? offset + header : offset;

        return owner.Fields.FirstOrDefault(f => !f.IsStatic && f.BackingData?.FieldOffset == wanted);
    }

    /// <summary>Every place the address is used, or null where one of them is not a use this can name.</summary>
    private static List<(Instruction Instruction, int Operand)>? Uses(Graphs.ISILControlFlowGraph graph,
        Instruction addition, LocalVariable address)
    {
        var found = new List<(Instruction, int)>();

        foreach (var instruction in graph.Instructions)
        {
            if (ReferenceEquals(instruction, addition))
                continue;

            var wantsAnAddress = AddressPositions(instruction);

            for (var operand = 0; operand < instruction.Operands.Count; operand++)
            {
                switch (instruction.Operands[operand])
                {
                    //Read through it, at no further distance. Writing through it is a store to the field,
                    //which needs the address rather than the value and is a different shape.
                    case MemoryOperand { Index: null, Scale: 0, Addend: 0, Base: { } through }
                        when ReferenceEquals(through, address):

                        if (operand == 0 && instruction.IsAssignment)
                            return null;

                        found.Add((instruction, operand));
                        continue;

                    //The register itself. Only where the position wants an address anyway - a receiver of a
                    //call on a value type, or a by-reference parameter - because there the field's address is
                    //exactly what was meant. Anywhere else the address is a value and this cannot speak for it.
                    case LocalVariable other when ReferenceEquals(other, address):
                        if (!wantsAnAddress.Contains(operand))
                            return null;

                        found.Add((instruction, operand));
                        continue;

                    case MemoryOperand memory when ReferenceEquals(memory.Base, address)
                        || ReferenceEquals(memory.Index, address):
                        return null;

                    case FieldReference reference when ReferenceEquals(reference.Local, address):
                        return null;
                }
            }
        }

        return found;
    }

    /// <summary>Which operands of a call are places an address belongs.</summary>
    private static HashSet<int> AddressPositions(Instruction instruction)
    {
        var positions = new HashSet<int>();

        if (!instruction.IsCall || instruction.Operands.Count == 0
            || instruction.Operands[0] is not MethodAnalysisContext callee)
            return positions;

        var receiver = instruction.OpCode == OpCode.Call ? 2 : 1;

        //A method on a value type is called on the address of one.
        if (!callee.IsStatic && callee.DeclaringType is { IsValueType: true })
            positions.Add(receiver);

        var first = receiver + (callee.IsStatic ? 0 : 1);

        for (var i = 0; i < callee.Parameters.Count && first + i < instruction.Operands.Count; i++)
            if (callee.Parameters[i].ParameterType is ByRefTypeAnalysisContext)
                positions.Add(first + i);

        return positions;
    }
}
