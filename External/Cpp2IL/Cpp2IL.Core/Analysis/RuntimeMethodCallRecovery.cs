using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Turns a call through a runtime method's own code pointer back into the call it came from.
///
/// A body shared between the instantiations of a generic cannot call a method the type arguments decide by
/// address, because one body serves them all - the base constructor of <c>Foo&lt;T&gt;</c> is a different
/// method for every T. il2cpp reaches it through the runtime generic context instead: read the runtime
/// class out of the <c>MethodInfo*</c> the body was handed, read the entry out of the class's context table,
/// and call through the pointer at the front of the <c>MethodInfo</c> it found.
///
/// Nothing in that names a method, so the whole of a constructor whose only statement was <c>base()</c> came
/// back as three reads of unmanaged memory and an unresolved indirect call. The entry does name it, and
/// <see cref="RgctxResolver"/> has followed the chain by the time this runs.
/// </summary>
public static class RuntimeMethodCallRecovery
{
    public static bool Run(MethodAnalysisContext method)
    {
        var voidType = method.AppContext.SystemTypes.SystemVoidType;
        var definitions = new Dictionary<LocalVariable, Instruction>();

        foreach (var instruction in method.ControlFlowGraph!.Instructions)
            if (instruction.Destination is LocalVariable destination)
                definitions[destination] = instruction;

        var changed = false;

        foreach (var instruction in method.ControlFlowGraph.Instructions)
        {
            if (instruction.OpCode != OpCode.IndirectCall || instruction.Operands.Count < 2)
                continue;

            //The read of the code pointer is either still its own instruction or has been folded into the
            //call, which is the shape copy propagation usually leaves.
            var pointerRead = instruction.Operands[0] switch
            {
                MemoryOperand { Index: null, Scale: 0, Addend: 0 } folded => folded,
                LocalVariable target => ReadOf(target, definitions),
                _ => null,
            };

            //The code pointer is the first thing in an Il2CppMethodInfo, so nothing but offset zero is it.
            //A method's own code is reached through any of three pointers at the front of its `MethodInfo`,
            //and which one the compiler picks depends on how the method is shared - a fully shared generic is
            //entered through the invoker at 0x10, not the plain pointer at 0. Accepting only the first left
            //every such call unresolved, and with it the whole context chain that found the method.
            if (pointerRead is not { Base: LocalVariable holder } read || !Il2CppMethodInfoLayout.IsEntryPoint(read.Addend))
                continue;

            if (holder.Type is not RuntimeMethodInfoAnalysisContext { RepresentedMethod: var callee })
                continue;

            List<object> arguments;

            if (read.Addend == Il2CppMethodInfoLayout.InvokerMethod)
            {
                //The thunk has a convention of its own and it is not the callee's: what it is handed is a
                //uniform frame it unpacks itself. Read as the callee's, X1 - the `MethodInfo` the thunk needs
                //to do the unpacking - is handed over as the first argument, and the arguments that were
                //really passed are never read at all. `keys.Add(pair.Key)` came out as `keys.Add(MethodInfo)`,
                //and it compiles, so nothing but running it said otherwise.
                if (InvokerThunk.Read(callee, instruction, method) is not { } unpacked)
                    continue;

                arguments = callee.IsStatic ? unpacked.Arguments : [unpacked.Receiver, .. unpacked.Arguments];
            }
            //The call was handed every register an argument could have come in, because nothing was known
            //about what it took. Now that the callee is named, the convention says which of them it read.
            else if (Aapcs64.ArgumentsOf(callee, instruction.Operands) is { } direct)
            {
                arguments = direct;
            }
            else
            {
                continue;
            }

            if (ReferenceEquals(callee.ReturnType, voidType))
            {
                instruction.OpCode = OpCode.CallVoid;
                instruction.Operands = [callee, .. arguments];
            }
            else if (instruction.Operands[1] is LocalVariable result)
            {
                instruction.OpCode = OpCode.Call;
                instruction.Operands = [callee, result, .. arguments];
            }
            else
            {
                //A value comes back with nowhere left to put it. Leaving the call alone keeps what is emitted
                //honest rather than dropping a value the IL would still push.
                continue;
            }

            changed = true;
        }

        return changed;
    }

    /// <summary>The memory the local was last read out of, if that is where it came from.</summary>
    private static MemoryOperand? ReadOf(LocalVariable local, Dictionary<LocalVariable, Instruction> definitions)
        => definitions.TryGetValue(local, out var definition)
            && definition is { OpCode: OpCode.Move, Operands: [_, MemoryOperand { Index: null, Scale: 0 } memory] }
            ? memory
            : null;

    /// <summary>Aapcs64 puts a floating point value in a vector register, which is not one of x0 to x7.</summary>
}
