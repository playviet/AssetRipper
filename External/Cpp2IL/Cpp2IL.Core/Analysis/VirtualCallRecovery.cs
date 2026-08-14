using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Turns il2cpp's virtual dispatch back into the call it came from.
///
/// A virtual call is compiled to three steps: read the runtime class out of the object's header, read a
/// slot out of that class's vtable, and call through it. Nothing in that sequence names a method, so the
/// call is left indirect and the statement around it is lost - and the object's type is right there in
/// the first step, which together with the slot says exactly which method was meant.
/// </summary>
public static class VirtualCallRecovery
{
    public static bool Run(MethodAnalysisContext method)
    {
        var metadataVersion = method.AppContext.MetadataVersion;
        var is32Bit = method.AppContext.Binary.is32Bit;
        var voidType = method.AppContext.SystemTypes.SystemVoidType;

        var definitions = new Dictionary<LocalVariable, Instruction>();

        foreach (var instruction in method.ControlFlowGraph!.Instructions)
            if (instruction.Destination is LocalVariable destination)
                definitions[destination] = instruction;

        var changed = false;

        foreach (var instruction in method.ControlFlowGraph.Instructions)
        {
            if (instruction.OpCode != OpCode.IndirectCall || instruction.Operands.Count == 0)
                continue;

            //The vtable read is either still its own instruction, or copy propagation has already folded
            //it into the call, which is the shape it usually ends up in.
            var vtableRead = instruction.Operands[0] switch
            {
                MemoryOperand { Index: null, Scale: 0 } folded => folded,
                LocalVariable target => ReadOf(target, definitions),
                _ => null,
            };

            if (vtableRead is not { Base: LocalVariable runtimeClass })
                continue;

            var slot = MiscUtils.GetSlotNum((int)vtableRead.Value.Addend, metadataVersion, is32Bit);
            if (slot < 0)
                continue;

            //The class comes out of the object's header, which is at offset zero. Anything else is not
            //this shape - an interface call reaches its method through the interface offset table.
            if (ReadOf(runtimeClass, definitions) is not { Addend: 0, Base: LocalVariable receiver })
                continue;

            if (SlotOwner(receiver, slot, method.AppContext) is not { } owner)
                continue;

            if (FindSlot(owner, slot) is not { } callee || callee.IsStatic)
                continue;

            //The call was handed every register an argument could have come in, because nothing was known
            //about what it took. Now that the callee is named, the convention says which of them it read.
            if (Aapcs64.ParametersOf(callee, instruction.Operands) is not { } parameters)
                continue;

            List<object> arguments = [receiver, .. parameters];

            if (ReferenceEquals(callee.ReturnType, voidType))
            {
                instruction.OpCode = OpCode.CallVoid;
                instruction.Operands = [callee, .. arguments];
            }
            else if (instruction.Operands.Count > 1 && instruction.Operands[1] is LocalVariable result)
            {
                instruction.OpCode = OpCode.Call;
                instruction.Operands = [callee, result, .. arguments];
            }
            else
            {
                //A value comes back but there is nowhere left to put it, so leaving the call as it is
                //keeps the stack honest rather than dropping a value the emitted IL would still push.
                continue;
            }

            changed = true;
        }

        return changed;
    }

    // The memory operand a local was loaded from, if it was loaded from one at all.
    private static MemoryOperand? ReadOf(LocalVariable local, Dictionary<LocalVariable, Instruction> definitions)
    {
        if (!definitions.TryGetValue(local, out var definition) || definition.OpCode != OpCode.Move || definition.Operands.Count < 2)
            return null;

        return definition.Operands[1] is MemoryOperand { Index: null, Scale: 0 } memory ? memory : null;
    }

    /// <summary>
    /// The method occupying a vtable slot of a type. A slot is filled by whichever type in the hierarchy
    /// last overrode it, so the search starts at the receiver's own type and walks up.
    /// </summary>
    /// <summary>
    /// The type whose vtable answers this slot for a given receiver.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two receivers that carry the answer anyway were being refused. A <b>by-reference</b> one is the value
    /// it points at - a method called on <c>ref x</c> reaches the same vtable as one called on <c>x</c> - and
    /// three sites are that shape (<c>InstallReferrerManager::PopulateMeta</c> twice on a <c>JToken&amp;</c>,
    /// <c>TrackingManager::InjectParamCounts</c> on an <c>Object&amp;</c>).
    /// </para>
    /// <para>
    /// And a receiver with <b>no type at all</b> - read back from a frame slot or an array element - still
    /// answers the first four slots, because those are <c>System.Object</c>'s and every class in the program
    /// has them in the same places. <c>+0x168</c> is slot 3, <c>ToString</c>, and five of these sites are it:
    /// <c>AdjustTracking::GetAndroidId</c>, <c>FacebookTracking::ToFbParams</c>,
    /// <c>FirebaseTracking::ToParams</c>, <c>Logger::PrintColorMapping</c>.
    /// </para>
    /// <para>
    /// <b>Naming the base declaration is not a guess.</b> The instruction is a <c>callvirt</c> either way, so
    /// dispatch still finds the override at run time; what is written down is the declaration the call was
    /// made through, which is what the source said. The alternative is no call at all, and the arguments and
    /// the result going with it.
    /// </para>
    /// </remarks>
    private static TypeAnalysisContext? SlotOwner(LocalVariable receiver, int slot, ApplicationAnalysisContext application)
    {
        if (receiver.Type is ByRefTypeAnalysisContext { ElementType: { } pointedAt })
            return pointedAt;

        //Above the fallback, so a receiver that knows its own type always answers with it.
        return receiver.Type ?? (slot < 4 ? application.SystemTypes.SystemObjectType : null);
    }

    private static MethodAnalysisContext? FindSlot(TypeAnalysisContext type, int slot)
    {
        for (var current = type; current is not null; current = BaseOf(current))
        {
            var declaring = current is GenericInstanceTypeAnalysisContext generic ? generic.GenericType : current;
            //The slot is on the definition; the context keeps its own copy private.
            var method = declaring.Methods.FirstOrDefault(m => m.Definition?.slot == slot);

            if (method is null)
                continue;

            return current is GenericInstanceTypeAnalysisContext genericInstance
                ? new ConcreteGenericMethodAnalysisContext(method, genericInstance.GenericArguments, [])
                : method;
        }

        return null;
    }

    /// <summary>
    /// The type the search continues into. A generic instance is a wrapper around the type it instantiates
    /// and has no definition of its own, so it reports no base type at all - and a hierarchy that passes
    /// through a generic base, which is every singleton and every generic view here, ended one type early
    /// with the inherited method never found.
    /// </summary>
    private static TypeAnalysisContext? BaseOf(TypeAnalysisContext type)
        => type is GenericInstanceTypeAnalysisContext generic ? generic.GenericType.BaseType : type.BaseType;
}
