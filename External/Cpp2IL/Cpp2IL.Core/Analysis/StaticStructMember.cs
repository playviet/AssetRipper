using System.Collections.Generic;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Reads a member of a struct held in another type's static storage, by saying the property over it.
/// </summary>
/// <remarks>
/// <para>
/// <c>Vector3.zero</c> is a public property over a <c>private static readonly Vector3 zeroVector</c>, and
/// il2cpp reaches the field directly - so <c>a - Vector3.zero</c> compiles to three reads of that type's
/// static storage at 0, 4 and 8. The one at nought matches the field exactly and is already recovered, by
/// <see cref="StaticPropertyRecovery"/>, as a call to the property. The other two land <i>inside</i> the
/// struct, match no field at all, and are written out as unmanaged memory:
/// </para>
/// <code>
/// float num2 = direction.x - zero.x;                                              // offset 0, recovered
/// _ = "Unmanaged memory load: [v78 @ X8_v6 (Il2CppStaticFields&lt;Vector3&gt;)+4]";  // .y
/// _ = "Unmanaged memory load: [v78 @ X8_v6 (Il2CppStaticFields&lt;Vector3&gt;)+8]";  // .z
/// </code>
/// <para>
/// Both halves of the answer already exist and were not joined up.
/// <see cref="MetadataResolver.PathToNestedField"/> walks the storage's layout and gives back
/// <c>[zeroVector, z]</c>; <see cref="StaticPropertyRecovery.FindGetter"/> finds the public property that
/// reads that same offset. The pass calls the property into a local and reads the member off it, which is
/// what the source said - <c>Vector3.zero.z</c> rather than a field the project cannot name.
/// </para>
/// <para>
/// <b>Only where the property exists.</b> The field itself is private to another assembly, so without a
/// property there is nothing writable to fall back to and the read is left as it was.
/// <c>FindGetter</c> also declines an aliased property, where two of them read the same offset and nothing
/// says which one the code meant. Reads only: assigning one of these would be assigning through a getter.
/// </para>
/// </remarks>
public static class StaticStructMember
{
    public static bool Run(MethodAnalysisContext method)
    {
        if (method.ControlFlowGraph is not { } graph)
            return false;

        var header = method.AppContext.Binary.is32Bit ? 8 : 0x10;
        var storageOffset = method.AppContext.Binary.is32Bit
            ? LocalVariables.StaticFieldsOffset32
            : LocalVariables.StaticFieldsOffset64;

        var changed = false;

        foreach (var block in graph.Blocks)
        {
            //One call per static per block. These are readonly values a property hands back unchanged, so
            //asking twice in the same block says the same thing twice - `IngameMenu..ctor` was calling
            //`get_one` once for the whole vector and once more for the member read off it.
            var called = new Dictionary<FieldAnalysisContext, LocalVariable>();

            for (var i = 0; i < block.Instructions.Count; i++)
            {
                var instruction = block.Instructions[i];

                //From one where the instruction writes its answer to operand nought: a store into static
                //storage is a different shape, and would have to go through a setter that may not exist.
                for (var operand = instruction.IsAssignment ? 1 : 0; operand < instruction.Operands.Count; operand++)
                {
                    if (instruction.Operands[operand] is not MemoryOperand { Index: null, Scale: 0 } memory
                        || memory.Base is not LocalVariable { Type: StaticFieldStorageTypeAnalysisContext storage })
                        continue;

                    if (MetadataResolver.PathToNestedField(storage.OwnerType, memory.Addend, statics: true, header)
                        is not { Length: > 1 } path)
                        continue;

                    if (path[0].BackingData?.FieldOffset is not { } start
                        || StaticPropertyRecovery.FindGetter(storage.OwnerType, path[0].FieldType, start, storageOffset)
                            is not { } getter)
                        continue;

                    //A property is not something an operand can be, so the call has to go in front of the
                    //instruction that wanted the value - but at the **top of the block**, not immediately
                    //before it. Putting it immediately before splits a contiguous run of stores, and a
                    //constructor's field initialisers are exactly such a run: `IngameMenu..ctor` lost three
                    //of its four. The value is a readonly static, so where in the block it is read makes no
                    //difference to what it holds.
                    if (!called.TryGetValue(path[0], out var value))
                    {
                        value = new LocalVariable($"static{method.Locals.Count}", new Register(null, "STATIC"), path[0].FieldType);
                        method.Locals.Add(value);
                        called[path[0]] = value;

                        block.Instructions.Insert(0, new Instruction(instruction.Index, OpCode.Call, getter, value));
                        i++;
                    }

                    FieldAnalysisContext[] rest = path[1..];

                    instruction.Operands[operand] = rest.Length == 1
                        ? new FieldReference(rest[0], value, 0)
                        : new NestedFieldReference(rest, value, 0);

                    changed = true;
                }
            }
        }

        return changed;
    }
}
