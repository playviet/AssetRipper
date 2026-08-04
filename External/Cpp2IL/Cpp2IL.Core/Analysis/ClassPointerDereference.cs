using System.Collections.Generic;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Types the value a method gets by reading a class pointer out of the slot that holds it.
/// </summary>
/// <remarks>
/// A type mentioned in a method is reached through a slot filled in at load time, so the class arrives in
/// two steps: the slot's address is a constant, and the class is what is in it. The first step is already
/// understood - the local holding the slot is typed as the class it will hold - but the read through it is
/// not, so the class itself comes out as a value of no type at all, and everything the method does with it
/// afterwards is a read of unnamed memory: static fields most of all, which live at a fixed distance from a
/// class and nowhere else.
///
/// The distance is what makes this safe to assume rather than guess. A read at that offset is only
/// meaningful on a class, so a value used that way is a class, and a value read at offset zero from
/// something already known to be one can only be the same class again. Requiring the second read before
/// believing the first keeps this from typing every incidental dereference of a class-shaped local.
/// </remarks>
public static class ClassPointerDereference
{
    public static bool Run(MethodAnalysisContext method)
    {
        var staticFieldsOffset = method.AppContext.Binary.is32Bit
            ? LocalVariables.StaticFieldsOffset32
            : LocalVariables.StaticFieldsOffset64;

        var definitions = new Dictionary<LocalVariable, Instruction>();

        foreach (var instruction in method.ControlFlowGraph!.Instructions)
            if (instruction.OpCode == OpCode.Move && instruction.Destination is LocalVariable destination)
                definitions[destination] = instruction;

        var changed = false;

        foreach (var instruction in method.ControlFlowGraph.Instructions)
        {
            //The read that says the base is a class. Two offsets say it: where the static fields are kept,
            //and the flag saying they have been prepared. Neither means anything except on a class.
            foreach (var operand in instruction.Operands)
            {
                if (operand is not MemoryOperand { Index: null, Scale: 0 } storage
                    || (storage.Addend != staticFieldsOffset && storage.Addend != InitialisedFlagOffset)
                    || storage.Base is not LocalVariable { Type: null } held)
                    continue;

                //And the base is a value read straight out of a slot already known to hold that class.
                if (!definitions.TryGetValue(held, out var definition)
                    || definition.Operands.Count < 2
                    || definition.Operands[1] is not MemoryOperand { Index: null, Scale: 0, Addend: 0 } slot
                    || slot.Base is not LocalVariable { Type: RuntimeClassTypeAnalysisContext klass })
                    continue;

                held.Type = klass;
                changed = true;
            }
        }

        return changed;
    }

    /// <summary>The bit a class carries saying its static state has been prepared.</summary>
    private const long InitialisedFlagOffset = 0xE4;
}
