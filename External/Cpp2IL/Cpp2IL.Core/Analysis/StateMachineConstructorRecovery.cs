using System.Collections.Generic;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Puts the initial state of an iterator or async state machine back where the compiler wrote it.
///
/// A factory method builds its state machine with <c>new &lt;M&gt;d__N(0)</c> and then stores the captured
/// parameters into it. Il2cpp inlines that one-line constructor, so what is left is an allocation followed by a
/// store of the state - and the constructor is reconstructed with a default argument, leaving the store as well.
/// Both together say the state is set twice, and the second of them stores a constant where the decompiler
/// requires a parameter, which is what stops it writing the method back as an iterator at all.
///
/// The store is the one that knows the real value, so it becomes the constructor's argument and stops being a
/// store. <c>&lt;&gt;1__state</c> only exists on a compiler-generated state machine, so nothing else can be caught
/// by this.
/// </summary>
public static class StateMachineConstructorRecovery
{
    private const string StateField = "<>1__state";

    public static void Run(MethodAnalysisContext method)
    {
        //A call ends a block, and il2cpp calls the base constructor between the allocation and the stores, so the
        //store is not in the same block as the allocation it belongs to. In single assignment form the allocated
        //value is its own local, so it identifies its own state store wherever that ended up.
        var stores = new Dictionary<LocalVariable, (Instruction Store, object Value)>();
        var definitions = new Dictionary<LocalVariable, Instruction>();

        foreach (var instruction in method.ControlFlowGraph!.Instructions)
            if (instruction.Destination is LocalVariable destination)
                definitions[destination] = instruction;

        foreach (var instruction in method.ControlFlowGraph.Instructions)
        {
            if (instruction.OpCode != OpCode.Move || instruction.Operands.Count < 2)
                continue;

            if (instruction.Operands[0] is not FieldReference { Field.Name: StateField } field)
                continue;

            //The constant is still in a register at this point - it only becomes a literal later, when copies
            //are propagated - so it has to be followed back to where it was put there. It has to be a literal in
            //the end, because the argument is read at the allocation, which is before the register was set.
            if (Constant(instruction.Operands[1], definitions) is not { } value)
                continue;

            //The store is made through whichever copy of the allocated object was in hand at the time, so it
            //has to be traced back to the allocation itself before the two can be matched up.
            var target = Root(field.Local, definitions);

            //Only the first one initialises; a later write to the state is the machine running.
            if (!stores.ContainsKey(target))
                stores[target] = (instruction, value);
        }

        if (stores.Count == 0)
            return;

        foreach (var instruction in method.ControlFlowGraph.Instructions)
        {
            if (instruction is not { OpCode: OpCode.Newobj, Operands: [LocalVariable allocated, _] })
                continue;

            if (!stores.TryGetValue(allocated, out var store))
                continue;

            instruction.Operands.Add(store.Value);

            store.Store.OpCode = OpCode.Nop;
            store.Store.Operands = [];
        }
    }

    // The value a local was ultimately copied from, which for the object a field is stored through is the
    // allocation that produced it. A phi is where two different objects meet, so the trail stops there.
    private static LocalVariable Root(LocalVariable local, Dictionary<LocalVariable, Instruction> definitions)
    {
        const int maxDepth = 8;

        for (var depth = 0; depth < maxDepth; depth++)
        {
            if (!definitions.TryGetValue(local, out var definition)
                || definition.OpCode != OpCode.Move
                || definition.Operands.Count < 2
                || definition.Operands[1] is not LocalVariable source)
                return local;

            local = source;
        }

        return local;
    }

    // The literal a value ultimately came from, following however many copies were made of it on the way.
    private static object? Constant(object operand, Dictionary<LocalVariable, Instruction> definitions)
    {
        const int maxDepth = 8;

        for (var depth = 0; depth < maxDepth; depth++)
        {
            if (Instruction.IsConstantValue(operand) && operand is not MemoryOperand)
                return operand;

            if (operand is not LocalVariable local || !definitions.TryGetValue(local, out var definition))
                return null;

            if (definition.OpCode != OpCode.Move || definition.Operands.Count < 2)
                return null;

            operand = definition.Operands[1];
        }

        return null;
    }
}
