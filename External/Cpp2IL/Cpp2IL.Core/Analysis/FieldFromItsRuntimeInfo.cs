using System.Collections.Generic;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// A field reached through the runtime's own <c>FieldInfo</c> is still that field.
/// </summary>
/// <remarks>
/// <para>
/// Where a body is shared between instantiations il2cpp cannot inline a field's offset, so it looks the field
/// up at run time: read <c>Il2CppClass&lt;X&gt;::fields</c>, step to the one it wants - each <c>FieldInfo</c>
/// being thirty-two bytes - and hand the object and that entry to a helper. <see cref="RuntimeFieldHelper"/>
/// reads off which helper it is; one answers <c>&amp;object.field</c> and the other stores through it.
/// </para>
/// <para>
/// The class is already named by the time this runs and the step is a constant, so <b>which field it is is
/// stated outright</b>: the list is the type's own fields in metadata order, and the step counts entries.
/// Left alone the whole thing is a read of unmanaged memory, an addition nothing can name and a call to an
/// address, and the statement that wanted the field goes with them.
/// </para>
/// <para>
/// <b>The index is the answer, not the distance.</b> The first version of this wrote the addition the offset
/// would have been - <c>object + fieldInfo->offset</c> - and a generic type records no offsets at all, which
/// is the whole reason the compiler looked the field up at run time. So it fired on nothing: all fifteen
/// sites in the game are in one generic iterator, and every one of them reads <c>offset=0</c>. Naming the
/// field itself needs no offset, and <see cref="FieldReference"/> is what the generator already turns into
/// <c>ldfld</c>, <c>ldflda</c> and <c>stfld</c>.
/// </para>
/// <para>
/// Recognised by the <em>chain</em> rather than by any helper's address, so nothing here is pinned to this
/// binary. A call handed an object and a <c>klass-&gt;fields</c> entry is one of those helpers; there is
/// nothing else with both.
/// </para>
/// </remarks>
public static class FieldFromItsRuntimeInfo
{
    /// <summary>What one <c>FieldInfo</c> takes up, which is what a step between two of them counts in.</summary>
    private const long FieldInfoWidth = 0x20;

    public static void Run(MethodAnalysisContext method)
    {
        if (method.ControlFlowGraph is not { } graph)
            return;

        var definitions = new Dictionary<LocalVariable, Instruction>();
        var assigned = new HashSet<LocalVariable>();

        foreach (var instruction in graph.Instructions)
        {
            if (instruction.Destination is not LocalVariable destination)
                continue;

            if (!assigned.Add(destination))
                definitions.Remove(destination);
            else
                definitions[destination] = instruction;
        }

        foreach (var call in graph.Instructions)
        {
            //Only a call nothing has resolved: one that names a method says what it does already.
            if (call.OpCode is not (OpCode.Call or OpCode.CallVoid) || call.Operands.Count < 2
                || call.Operands[0] is not ulong target)
            {
                continue;
            }

            var first = call.OpCode == OpCode.Call ? 2 : 1;

            if (call.Operands.Count < first + 2 || call.Operands[first] is not LocalVariable held)
                continue;

            if (Named(call.Operands[first + 1], definitions) is not { } field)
                continue;

            switch (RuntimeFieldHelper.Of(method.AppContext, target))
            {
                case RuntimeFieldHelper.Kind.Address:
                    //An unresolved call always carries every integer register, so the answer is there whether
                    //anything wanted it or not; a `CallVoid` in this position is a shape this cannot speak for.
                    if (first != 2 || call.Operands[1] is not LocalVariable answer
                        || Uses(method, graph, call, answer) is not { Count: > 0 } uses)
                    {
                        continue;
                    }

                    foreach (var (instruction, operand) in uses)
                        instruction.Operands[operand] = new FieldReference(field, held, 0);

                    call.OpCode = OpCode.Nop;
                    call.Operands = [];
                    break;

                case RuntimeFieldHelper.Kind.Store:
                    if (call.Operands.Count < first + 3)
                        continue;

                    //The address it computed on the way is its own business and nothing here reads it - but
                    //if something did, dropping the call would drop that too.
                    if (first == 2 && call.Operands[1] is LocalVariable made && Reads(graph, call, made))
                        continue;

                    call.OpCode = OpCode.Move;
                    call.Operands = [new FieldReference(field, held, 0), call.Operands[first + 2]];
                    break;
            }
        }
    }

    /// <summary>The field a <c>FieldInfo</c> is, where it was stepped to from a class's own list.</summary>
    private static FieldAnalysisContext? Named(object operand, Dictionary<LocalVariable, Instruction> definitions)
    {
        if (operand is not LocalVariable info || !definitions.TryGetValue(info, out var made))
            return null;

        long step = 0;

        if (made is { OpCode: OpCode.Add, Operands: [_, { } stepped, { } by] })
        {
            if (Constant(by) is not { } away || away < 0 || away % FieldInfoWidth != 0)
                return null;

            step = away;

            //The list is either still a local of its own, or - where copy propagation folded the load into
            //the addition, which is the shape it usually leaves - the read itself. Only the first was matched,
            //and the whole of `JsonExtension`'s iterator family is the second.
            if (stepped is MemoryOperand folded)
                made = new Instruction(made.Index, OpCode.Move, made.Operands[0], folded);
            else if (stepped is not LocalVariable from || !definitions.TryGetValue(from, out made))
                return null;
        }

        if (made is not { OpCode: OpCode.Move, Operands: [_, MemoryOperand { Index: null, Scale: 0, Base: LocalVariable klass } read] }
            || read.Addend != Il2CppClassLayout.Fields
            || klass.Type is not RuntimeClassTypeAnalysisContext { RepresentedType: { } named })
        {
            return null;
        }

        var declaring = named is GenericInstanceTypeAnalysisContext instance ? instance.GenericType : named;
        var index = (int)(step / FieldInfoWidth);

        //The list is the type's own fields in metadata order, which is the order the runtime lays them out
        //in - statics and constants included, since each of those has a `FieldInfo` of its own and so takes a
        //place in the step. That is what makes the index safe where the offset is not.
        if (index < 0 || index >= declaring.Fields.Count)
            return null;

        var field = declaring.Fields[index];

        //Named against the arguments the class was instantiated with, or - where it stands for itself in its
        //own shared body - against the type's own parameters. Importing the definition instead names an
        //unbound generic, `((JsonExtension.<FromNewlineDelimitedJson>d__2<>)this).reader`, which is not a
        //type C# can write, and the statement is lost one step further on. Same shape as `GenericFieldLayout`.
        if (named is GenericInstanceTypeAnalysisContext arguments)
            return new ConcreteGenericFieldAnalysisContext(field, arguments);

        return declaring.GenericParameters.Count > 0
            ? new ConcreteGenericFieldAnalysisContext(field, declaring.GenericParameters)
            : field;
    }

    /// <summary>
    /// Every place the field's address is used, or null where one of them is not a use this can name.
    /// </summary>
    /// <remarks>
    /// The same three positions <see cref="FieldAddressRecovery"/> allows, asked here rather than shared so
    /// that neither pass has to move for the other: a read through the address at no further distance, the
    /// receiver of a call on a value type, and a by-reference argument.
    /// </remarks>
    private static List<(Instruction Instruction, int Operand)>? Uses(MethodAnalysisContext method,
        ISILControlFlowGraph graph, Instruction call, LocalVariable address)
    {
        var found = new List<(Instruction, int)>();

        foreach (var instruction in graph.Instructions)
        {
            if (ReferenceEquals(instruction, call))
                continue;

            var wantsAnAddress = AddressPositions(method, instruction);

            for (var operand = 0; operand < instruction.Operands.Count; operand++)
            {
                switch (instruction.Operands[operand])
                {
                    case MemoryOperand { Index: null, Scale: 0, Addend: 0, Base: { } through }
                        when ReferenceEquals(through, address):

                        //Written through, which is a store to the field - and the generator says `stfld` for
                        //exactly that operand. The store helper above is the usual way a shared body writes
                        //one, so this arm is the rarer half of the same thing.
                        found.Add((instruction, operand));
                        continue;

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

    /// <summary>Whether anything reads a local at all, in any position.</summary>
    private static bool Reads(ISILControlFlowGraph graph, Instruction call, LocalVariable made)
    {
        foreach (var instruction in graph.Instructions)
        {
            if (ReferenceEquals(instruction, call))
                continue;

            for (var operand = instruction.IsAssignment ? 1 : 0; operand < instruction.Operands.Count; operand++)
                switch (instruction.Operands[operand])
                {
                    case LocalVariable other when ReferenceEquals(other, made):
                    case MemoryOperand memory when ReferenceEquals(memory.Base, made) || ReferenceEquals(memory.Index, made):
                    case FieldReference reference when ReferenceEquals(reference.Local, made):
                        return true;
                }
        }

        return false;
    }

    /// <summary>Which operands of a call are places an address belongs.</summary>
    private static HashSet<int> AddressPositions(MethodAnalysisContext method, Instruction instruction)
    {
        var positions = new HashSet<int>();

        if (!instruction.IsCall || instruction.Operands.Count == 0)
            return positions;

        //A copy the width of a `T` is how a shared body reads a field it cannot name the offset of, and the
        //second argument of `memcpy` is an address by definition. Only the **source**: the destination of a
        //copy is a store, and <see cref="ClearingASizedByT"/> reads the destination as the buffer being
        //filled - handing it a field there would take a write away with nothing said in its place.
        if (instruction.Operands[0] is ulong imported)
        {
            if (method.AppContext.Binary is LibCpp2IL.Elf.ElfFile binary
                && binary.ImportedFunctionAt(imported) is "memcpy" or "memmove")
            {
                positions.Add((instruction.OpCode == OpCode.Call ? 2 : 1) + 1);
            }

            return positions;
        }

        if (instruction.Operands[0] is not MethodAnalysisContext callee)
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
