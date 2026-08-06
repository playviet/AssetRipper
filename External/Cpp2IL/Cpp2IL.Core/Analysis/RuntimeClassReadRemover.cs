using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Takes out the two reads of a class that only the runtime has any use for.
/// </summary>
/// <remarks>
/// Where a method touches a type it did not declare, the code generator emits a test of the bit saying that
/// type has been prepared and a pointer to where its static values are kept. Neither is anything the
/// language has: a static field is simply there, and it is there because it was prepared before anything
/// could look. Left alone they come out as reads of unmanaged memory, one statement each, in every method
/// that mentions a type.
///
/// The bit is not guessed at. Managed code cannot run before the type it is in has been prepared, so at
/// every point one of these tests is reached the answer is already yes - which makes the test a constant,
/// and the branch that would have prepared the type unreachable, which the collection afterwards removes.
///
/// The pointer is dropped rather than answered, because there is no answer: what reads through it have
/// already been named as the static fields they are, and those name the field directly and no longer ask
/// where it lives. Only a read at an offset nothing accounted for still refers to it, and that read was
/// already going to be a statement saying so.
/// </remarks>
public static class RuntimeClassReadRemover
{
    public static bool Run(MethodAnalysisContext method)
    {
        var staticFieldsOffset = method.AppContext.Binary.is32Bit
            ? LocalVariables.StaticFieldsOffset32
            : LocalVariables.StaticFieldsOffset64;

        var changed = false;

        foreach (var instruction in method.ControlFlowGraph!.Instructions)
        {
            //The pointer to the static values, kept in a local that says that is what it is.
            if (instruction.OpCode == OpCode.Move && instruction.Operands.Count == 2
                && instruction.Operands[0] is LocalVariable { Type: StaticFieldStorageTypeAnalysisContext }
                && instruction.Operands[1] is MemoryOperand { Index: null, Scale: 0 } storage
                && storage.Addend == staticFieldsOffset
                && IsRuntimeClass(storage.Base))
            {
                instruction.OpCode = OpCode.Nop;
                instruction.Operands = [];
                changed = true;
                continue;
            }

            //The cast check a value type's unboxing is guarded by: `obj->klass->element_class` against the
            //target's, and the branch throws InvalidCastException where they differ. Both sides read the same
            //field of a runtime class, which managed code never touches, and in code that runs they are equal
            //- so both become the same value and the throw stops being reachable. Without this, `(int)o` on a
            //boxed int threw, because a read that resolved to nothing compares unequal to one that did.
            if (instruction.OpCode is OpCode.CheckEqual or OpCode.CheckNotEqual
                && instruction.Operands.Count > 2
                && instruction.Operands[1] is MemoryOperand { Index: null, Scale: 0, Addend: Il2CppClassLayout.ElementClass } left
                && instruction.Operands[2] is MemoryOperand { Index: null, Scale: 0, Addend: Il2CppClassLayout.ElementClass } right
                && (IsRuntimeClass(left.Base) || IsRuntimeClass(right.Base)))
            {
                instruction.Operands[1] = 1;
                instruction.Operands[2] = 1;
                ConstantBranchFolding.HasSettledAnswer(instruction);
                changed = true;
                continue;
            }

            //The bit saying the type is ready, wherever it is read - on its own or inside the comparison
            //that tests it. Both become the answer it always has by the time this code runs.
            for (var i = 0; i < instruction.Operands.Count; i++)
            {
                if (instruction.Operands[i] is not MemoryOperand { Index: null, Scale: 0 } flag
                    || (flag.Addend != InitialisedFlagOffset && !Il2CppClassLayout.IsInitialisationFlag(flag.Addend))
                    || !IsRuntimeClass(flag.Base))
                    continue;

                instruction.Operands[i] = 1;

                //Said out loud, because only the pass that decided the answer knows it was decided rather
                //than found: ConstantBranchFolding acts on this and on nothing else it happens to see.
                ConstantBranchFolding.HasSettledAnswer(instruction);
                changed = true;
            }
        }

        return changed;
    }

    private static bool IsRuntimeClass(object? operand)
        => operand is LocalVariable { Type: RuntimeClassTypeAnalysisContext };

    /// <summary>
    /// The bit a class carries saying its static state has been prepared. This one was found by chasing what
    /// the code reads; <see cref="Il2CppClassLayout"/> adds the rest of them, computed from the structure
    /// Unity ships - the byte at 0x135 whose lowest bit is `initialized_and_no_error`, and the two counters
    /// beside it. Every one is the runtime's business rather than the program's, and by the time the recovered
    /// code could run they are all settled.
    /// </summary>
    private const long InitialisedFlagOffset = 0xE4;
}
