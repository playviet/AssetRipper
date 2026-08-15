using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using LibCpp2IL;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// The <c>MethodInfo</c> half of a vtable entry names the method in that slot, exactly as the code pointer
/// beside it does.
/// </summary>
/// <remarks>
/// <para>
/// A vtable entry is two pointers - the method's code at <c>+0</c> and its <c>Il2CppMethodInfo*</c> at
/// <c>+8</c>. <see cref="VirtualCallRecovery"/> reads the first and calls through it;
/// <see cref="VirtualMethodPointer"/> reads the first as a value. **Nothing read the second**, and a virtual
/// call in a *shared generic* body is compiled entirely in terms of it, because such a call has to go through
/// the invoker thunk rather than straight to the code:
/// </para>
/// <code>
/// 346 Move X10, [X20]           ; obj-&gt;klass, the object being EqualityComparer`1&lt;W&gt;
/// 350 Move X1,  [X10 + 0x1C0]   ; klass-&gt;vtable[8].method   - the MethodInfo, this pass
/// 352 Move X0,  [X1 + 8]        ; MethodInfo-&gt;virtualMethodPointer
/// 353 Move X8,  [X1 + 0x10]     ; MethodInfo-&gt;invoker_method - the thunk
/// 358 IndirectCall X8, ...
/// </code>
/// <para>
/// <see cref="RuntimeMethodCallRecovery"/> already knows how to finish this - it accepts an entry point at
/// <c>0x10</c> and hands the frame to <see cref="InvokerThunk"/> - but it gates on the local holding the
/// <c>MethodInfo</c> being typed <see cref="RuntimeMethodInfoAnalysisContext"/>, and only
/// <see cref="RgctxResolver"/> ever types one, from a runtime generic context entry. A <c>MethodInfo</c> that
/// came out of a vtable is just as named, and by an easier route: the slot number and the receiver's own type
/// say which method it is. So this pass does nothing but put the name on the local, and everything downstream
/// is machinery that already exists.
/// </para>
/// <para>
/// <c>IDictionaryExtension::TryGetKeyByValue</c> and <c>GetKeysByValue</c> are 63 of this file's commented
/// statements between them, and both are lost to one such call -
/// <c>EqualityComparer&lt;W&gt;.Default.Equals(pair.Value, value)</c>, the condition the whole loop exists to
/// test.
/// </para>
/// <para>
/// <b>Only the second pointer of the entry.</b> <c>MiscUtils.GetSlotNum</c> normalises a read of it back onto
/// the slot, so it cannot be used to tell the two halves apart; the distance into the entry is computed here
/// instead, and a read of the code pointer is left to the two passes that already own it. And only where the
/// local is not already named, so the fixpoint this runs inside settles.
/// </para>
/// </remarks>
public static class VirtualMethodInfoSlot
{
    public static bool Run(MethodAnalysisContext method)
    {
        if (method.ControlFlowGraph is not { } graph)
            return false;

        var is32Bit = method.AppContext.Binary.is32Bit;
        var vtable = Il2CppClassUsefulOffsets.GetVtableOffset(method.AppContext.MetadataVersion, is32Bit);
        var entry = is32Bit ? 0x8 : 0x10;
        var pointer = is32Bit ? 0x4 : 0x8;

        var definitions = new Dictionary<LocalVariable, Instruction>();

        foreach (var instruction in graph.Instructions)
            if (instruction.Destination is LocalVariable destination)
                definitions[destination] = instruction;

        var changed = false;

        foreach (var instruction in graph.Instructions)
        {
            if (instruction is not { OpCode: OpCode.Move, Operands: [LocalVariable held, MemoryOperand { Index: null, Scale: 0 } slotRead] }
                || held.Type is RuntimeMethodInfoAnalysisContext
                || slotRead.Base is not LocalVariable runtimeClass)
            {
                continue;
            }

            //The second pointer of the entry, and nothing else. The first is a code pointer and belongs to
            //`VirtualCallRecovery` and `VirtualMethodPointer`; taking it here would name the same thing twice
            //and leave a call with two answers.
            var into = slotRead.Addend - vtable;

            if (into < entry || into % entry != pointer)
                continue;

            var slot = (int)(into / entry);

            //The class comes out of the object's header, at offset zero - the same shape both sibling passes
            //require, and what separates a vtable from any other run of pointers at a distance.
            if (Header(runtimeClass, definitions) is not { } receiver
                || receiver.Type is not { } owner || owner.DeclaringAssembly is not { } assembly)
            {
                continue;
            }

            //**A width is not a type, and this pass is where that costs the most.** Only a reference the
            //program declared has a vtable reached this way. This runs inside the type fixpoint, so on its
            //first turns a call whose address is not yet resolved has left its answer typed by the register
            //it came back in - `EqualityComparer<W>.Default` is `System.Int64` until `SharpenFromReturn`
            //corrects it - and slot 8 of `System.Int64` is `IConvertible.ToSByte`. Named without this guard,
            //`TryGetKeyByValue`'s comparison came out as
            //`num23.System_002EIConvertible_002EToSByte(provider)`: resolved, compiling, and completely
            //wrong, which is worse than the marker it replaced. A type is never revised once set, so an
            //early wrong answer is permanent - the guard has to be here rather than downstream.
            if (owner.IsValueType || owner is not (GenericInstanceTypeAnalysisContext or { Definition: not null }))
            {
                Trace($"notADeclaredReference {owner.FullName}  |  {instruction}");
                continue;
            }

            if (FindSlot(owner, slot) is not { } named || named.IsStatic)
                continue;

            Trace($"candidate slot {slot} on {owner.FullName} -> {named.FullName}  |  {instruction}");

            held.Type = new RuntimeMethodInfoAnalysisContext(named, assembly);
            changed = true;
        }

        return changed;
    }

    private static void Trace(string why)
    {
        if (System.Environment.GetEnvironmentVariable("VMIS_TRACE") == "1")
            System.Console.Error.WriteLine("VMIS " + why);
    }

    /// <summary>
    /// The object a local was loaded from the header of, where that is where it came from.
    /// </summary>
    /// <remarks>
    /// Either spelling of the read counts. Offset zero of a reference is the runtime class and never a field,
    /// but the field resolver does not know that while the receiver is still mistyped as a value type, and it
    /// names <c>[v100 + 0]</c> as <c>v100.m_value</c> - <c>System.Int64</c>'s own field - which is then stale
    /// once `SharpenFromReturn` corrects the receiver to `EqualityComparer&lt;W&gt;`. Reading only the memory
    /// operand form loses the site on exactly the turn of the fixpoint where it finally has a type worth
    /// asking about.
    /// </remarks>
    private static LocalVariable? Header(LocalVariable local, Dictionary<LocalVariable, Instruction> definitions)
    {
        if (!definitions.TryGetValue(local, out var definition) || definition.OpCode != OpCode.Move
            || definition.Operands.Count < 2)
        {
            return null;
        }

        return definition.Operands[1] switch
        {
            MemoryOperand { Index: null, Scale: 0, Addend: 0, Base: LocalVariable through } => through,
            FieldReference { Offset: 0, Local: { } named } => named,
            _ => null,
        };
    }

    /// <summary>
    /// The method occupying a vtable slot of a type, from the receiver's own type upward - the same walk
    /// <see cref="VirtualCallRecovery"/> and <see cref="VirtualMethodPointer"/> make, mirrored rather than
    /// shared so that neither has to change.
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
