using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// A vtable slot read as a value, not called through, is the method in it.
/// </summary>
/// <remarks>
/// <para>
/// <c>onClick.AddListener(ClickEffect)</c> where <c>ClickEffect</c> is <c>virtual</c> is <c>ldvirtftn</c>,
/// and il2cpp writes it as the same two reads a virtual call makes - the class out of the object's header,
/// then the slot out of the class - except that the answer goes into a delegate's constructor rather than
/// being jumped to:
/// </para>
/// <code>
/// Newobj   v53 (UnityAction), typeof(UnityAction)
/// Move     v54, [this]                    ; klass
/// Move     v62 (IntPtr), [v54 + 0x3F0]    ; klass-&gt;vtable[43].methodPtr
/// CallVoid UnityAction..ctor, v53, this, v62
/// </code>
/// <para>
/// <see cref="VirtualCallRecovery"/> owns exactly this arithmetic but only looks at an
/// <see cref="OpCode.IndirectCall"/>, and <see cref="DelegateConstruction"/> only fuses a constructor whose
/// method operand is already a runtime <c>MethodInfo</c> - so between them the read fell through and the
/// delegate came out holding an unnamed pointer.
/// </para>
/// <para>
/// Naming it gives back a <c>ldftn</c> rather than a <c>ldvirtftn</c>, which is the same source here: what
/// the slot holds at this point in the hierarchy is what the source named. Before
/// <see cref="DelegateConstruction"/>, which is what then folds it into the constructor, and before anything
/// that could turn <c>[klass + N]</c> into a field read - the trap that cost the delegate family once
/// already.
/// </para>
/// </remarks>
public static class VirtualMethodPointer
{
    public static bool Run(MethodAnalysisContext method)
    {
        if (method.ControlFlowGraph is not { } graph)
            return false;

        var metadataVersion = method.AppContext.MetadataVersion;
        var is32Bit = method.AppContext.Binary.is32Bit;

        var definitions = new Dictionary<LocalVariable, Instruction>();

        foreach (var instruction in graph.Instructions)
            if (instruction.Destination is LocalVariable destination)
                definitions[destination] = instruction;

        var changed = false;

        foreach (var instruction in graph.Instructions)
        {
            //A plain read into a local, not a call through one - that is the other pass's shape.
            if (instruction is not { OpCode: OpCode.Move, Operands: [LocalVariable held, MemoryOperand { Index: null, Scale: 0 } slotRead] })
                continue;

            if (slotRead.Base is not LocalVariable runtimeClass)
                continue;

            Trace($"candidate {instruction}");

            var slot = MiscUtils.GetSlotNum((int)slotRead.Addend, metadataVersion, is32Bit);

            if (slot < 0)
            {
                Trace($"badSlot {slotRead.Addend:X}");
                continue;
            }

            //The class comes out of the object's header, at offset zero. Anything else is not this shape.
            if (ReadOf(runtimeClass, definitions) is not { Addend: 0, Index: null, Scale: 0, Base: LocalVariable receiver }
                || receiver.Type is not { } holder)
            {
                Trace($"notAClassRead {slotRead.Base}");
                continue;
            }

            if (FindSlot(holder, slot) is not { } named || named.IsStatic)
            {
                Trace($"noSuchSlot {slot} on {holder.FullName}");
                continue;
            }

            //And it has to be used as a value. A pointer that is called through is a virtual call, and
            //rewriting it here would take the call away from the pass that knows what to do with it.
            if (!OnlyHandedOn(graph, held, instruction))
            {
                Trace($"notHandedOn {held}");
                continue;
            }

            if (holder.DeclaringAssembly is not { } assembly)
            {
                Trace("noAssembly");
                continue;
            }

            Trace($"named {named.FullName}");

            var pointer = new RuntimeMethodInfoAnalysisContext(named, assembly);
            instruction.Operands[1] = pointer;

            //And at the places that read it. `DelegateConstruction` fuses a constructor whose method operand
            //*is* a runtime MethodInfo, not one that holds a local that holds one - and propagation does not
            //fold this, because the local is versioned. Without this the delegate came out as
            //`new UnityAction(this, method)`, which is not something C# can say.
            foreach (var reader in graph.Instructions)
            {
                if (ReferenceEquals(reader, instruction))
                    continue;

                for (var i = 1; i < reader.Operands.Count; i++)
                    if (ReferenceEquals(reader.Operands[i], held))
                        reader.Operands[i] = pointer;
            }

            changed = true;
        }

        return changed;
    }

    private static void Trace(string why)
    {
        if (System.Environment.GetEnvironmentVariable("VMP_TRACE") == "1")
            System.Console.Error.WriteLine("VMP " + why);
    }

    /// <summary>Whether the value read is only ever passed on, never called through.</summary>
    private static bool OnlyHandedOn(Graphs.ISILControlFlowGraph graph, LocalVariable value, Instruction definition)
    {
        var used = false;

        foreach (var instruction in graph.Instructions)
        {
            if (ReferenceEquals(instruction, definition))
                continue;

            //Operand zero of an indirect call is the address being called - the case this must not take.
            if (instruction.OpCode == OpCode.IndirectCall && instruction.Operands.Count > 0
                && ReferenceEquals(instruction.Operands[0], value))
                return false;

            for (var i = 1; i < instruction.Operands.Count; i++)
                if (ReferenceEquals(instruction.Operands[i], value))
                    used = true;
        }

        return used;
    }

    private static MemoryOperand? ReadOf(LocalVariable local, Dictionary<LocalVariable, Instruction> definitions)
    {
        if (!definitions.TryGetValue(local, out var definition) || definition.OpCode != OpCode.Move || definition.Operands.Count < 2)
            return null;

        return definition.Operands[1] is MemoryOperand { Index: null, Scale: 0 } memory ? memory : null;
    }

    /// <summary>
    /// The method occupying a vtable slot of a type, from the receiver's own type upward - the same walk
    /// <see cref="VirtualCallRecovery"/> makes, mirrored rather than shared so that neither has to change.
    /// </summary>
    private static MethodAnalysisContext? FindSlot(TypeAnalysisContext type, int slot)
    {
        for (var current = type; current is not null; current = BaseOf(current))
        {
            var declaring = current is GenericInstanceTypeAnalysisContext generic ? generic.GenericType : current;
            var found = declaring.Methods.FirstOrDefault(m => m.Definition?.slot == slot);

            if (found is null)
                continue;

            return current is GenericInstanceTypeAnalysisContext genericInstance
                ? new ConcreteGenericMethodAnalysisContext(found, genericInstance.GenericArguments, [])
                : found;
        }

        return null;
    }

    private static TypeAnalysisContext? BaseOf(TypeAnalysisContext type)
        => type is GenericInstanceTypeAnalysisContext generic ? generic.GenericType.BaseType : type.BaseType;
}
