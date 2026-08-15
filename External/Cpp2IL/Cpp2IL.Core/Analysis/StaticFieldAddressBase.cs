using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Gives an address worked out from a type's static storage a local to rest on.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="FieldAddressSinking"/> reads an address as <c>Add made, &lt;local of known type&gt;, constant</c>.
/// Where the base is a type's <b>static</b> storage the addition rests on a memory read instead -
/// <c>Add v121, [v117 (Il2CppClass&lt;Vector2&gt;) + 0xB8], 4</c> is <c>&amp;Vector2.zero.y</c> - and that pass
/// does not know the shape. One producer it cannot name refuses the whole chain, so the branches that choose
/// between a static and a field of <c>this</c> lose both arms:
/// </para>
/// <code>
/// //object obj = this + 268L;   //obj2 = this + 272L;    (the four floats of a Color field)
/// //object obj5 = num + 12L;    //obj6 = num + 16L;      (the four of a static Color)
/// </code>
/// <para>
/// <c>BaseButton::DoStateTransition</c> is eight such statements and <c>Joystick::HandleInput</c> two. So the
/// read is undone rather than taught to that pass, exactly as <see cref="FieldAddressBase"/> undoes a folded
/// field: the static struct is called into a local of its own and the addition rests on that, leaving a shape
/// the sinking already understands.
/// </para>
/// <para>
/// Only a distance that lands <b>inside</b> a static struct. A distance landing exactly on a static field is
/// <see cref="FieldAddressRecovery"/>'s, which writes <c>ldsflda</c> for it, and this leaves those alone.
/// </para>
/// </remarks>
public static class StaticFieldAddressBase
{
    public static void Run(MethodAnalysisContext method)
    {
        if (method.ControlFlowGraph is not { } graph)
            return;

        var header = method.AppContext.Binary.is32Bit ? 8 : 0x10;
        var storageOffset = method.AppContext.Binary.is32Bit
            ? LocalVariables.StaticFieldsOffset32
            : LocalVariables.StaticFieldsOffset64;

        foreach (var block in graph.Blocks)
        {
            //One local per static per block, as `StaticStructMember` does: these are readonly values a
            //getter hands back unchanged, so asking twice in one block says the same thing twice.
            var called = new Dictionary<FieldAnalysisContext, LocalVariable>();

            for (var at = 0; at < block.Instructions.Count; at++)
            {
                if (block.Instructions[at] is not
                    { OpCode: OpCode.Add, Operands: [LocalVariable made, MemoryOperand read, { } distance] })
                {
                    continue;
                }

                if (read is not { Index: null, Scale: 0, Base: LocalVariable { Type: RuntimeClassTypeAnalysisContext klass } }
                    || read.Addend != storageOffset
                    || Offset(distance) is not { } offset || offset <= 0)
                {
                    continue;
                }

                var owner = (klass.RepresentedType as GenericInstanceTypeAnalysisContext)?.GenericType
                            ?? klass.RepresentedType;

                //One step is the static field's own front - `BaseButton`'s static `Color` is at 12 and its
                //first component is read at exactly 12 - and two is a member inside it. Deeper than that
                //would need a nested reference, which an addition's operand cannot carry, and no site here
                //has one. Only a **struct** static: a reference one's address is `ldsflda`, which
                //`FieldAddressRecovery` already writes, and resting an addition on it would name a field of
                //whatever it points at instead.
                if (MetadataResolver.PathToNestedField(owner, offset, statics: true, header) is not { Length: 1 or 2 } path
                    || path[0].BackingData?.FieldOffset is not { } start
                    || path[0].FieldType is not { IsValueType: true } structure
                    || structure.IsEnumType
                    || !structure.Fields.Any(f => !f.IsStatic))
                {
                    continue;
                }

                //Only where the address is a value nothing wants as one. Where it is wanted - a call on the
                //struct, a by-reference argument - `FieldAddressRecovery` writes `ldsflda` for it, and
                //resting the addition on the struct first would turn that receiver into its first member.
                if (!OnlyCopiedAndRead(graph, made))
                    continue;

                //The field has to be sayable: another assembly's private static is only reachable through the
                //property over it, which is what `Vector2.zero` is.
                var getter = StaticPropertyRecovery.FindGetter(owner, path[0].FieldType, start, storageOffset);

                if (getter == null && !MetadataResolver.ReachableFrom(path[0], method))
                    continue;

                if (!called.TryGetValue(path[0], out var value))
                {
                    value = new LocalVariable($"static{method.Locals.Count}", new Register(null, "STATIC"), path[0].FieldType);
                    method.Locals.Add(value);
                    called[path[0]] = value;

                    //At the top of the block rather than beside the addition, for the reason
                    //`StaticStructMember` records: inserting into the middle splits a contiguous run of
                    //stores, and a constructor's field initialisers are such a run.
                    block.Instructions.Insert(0, getter != null
                        ? new Instruction(block.Instructions[at].Index, OpCode.Call, getter, value)
                        //A static reference carries no owner - the generator emits `ldsfld` and never looks
                        //at one - so the holder stands in for it.
                        : new Instruction(block.Instructions[at].Index, OpCode.Move, value, new FieldReference(path[0], value, 0)));

                    at++;
                }

                //Measured from the struct's own front, which is what the addition now rests on.
                block.Instructions[at].Operands[1] = value;
                block.Instructions[at].Operands[2] = offset - start;
            }
        }
    }

    /// <summary>
    /// Whether the address is only ever carried about and read through at no further distance.
    /// </summary>
    /// <remarks>
    /// The same requirement <c>FieldAddressSinking.Chain</c> makes of the chains it accepts, asked here so
    /// that an address something wants <b>as</b> an address is left in the form the recovery beside this one
    /// already writes.
    /// </remarks>
    private static bool OnlyCopiedAndRead(Graphs.ISILControlFlowGraph graph, LocalVariable made)
    {
        foreach (var instruction in graph.Instructions)
        {
            for (var operand = 0; operand < instruction.Operands.Count; operand++)
            {
                switch (instruction.Operands[operand])
                {
                    //Operand nought of a computation is where the answer goes, not a use of it.
                    case LocalVariable when operand == 0 && instruction.IsAssignment:
                        break;

                    case LocalVariable other when ReferenceEquals(other, made):
                        if (instruction.OpCode is not (OpCode.Move or OpCode.Select))
                            return false;

                        break;

                    case MemoryOperand { Index: null, Scale: 0, Addend: 0, Base: { } through }
                        when ReferenceEquals(through, made):

                        if (operand == 0 && instruction.IsAssignment)
                            return false;

                        break;

                    case MemoryOperand memory when ReferenceEquals(memory.Base, made) || ReferenceEquals(memory.Index, made):
                        return false;
                }
            }
        }

        return true;
    }

    private static long? Offset(object operand) => operand switch
    {
        int i => i,
        uint u => u,
        long l => l,
        ulong ul => (long)ul,
        _ => null,
    };
}
