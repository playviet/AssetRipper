using System.Collections.Generic;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// What this fork adds: refusing to carry a value read from memory past anything that could have changed
/// what is there.
///
/// Kept apart from the file it belongs to so that the file stays as close to upstream as it can,
/// and a later version of Cpp2IL can be merged without the two sets of changes meeting.
/// </summary>
public static partial class Simplifier
{
    // A value the code read from somewhere, as opposed to one it computed and holds.
    private static bool IsReadFromMemory(object replacement) =>
        replacement is FieldReference || replacement is MemoryOperand { IsConstant: false };

    /// <summary>
    /// Whether the instruction makes the replacement stale, so that carrying it any further would change what
    /// the code says. A constant is never stale; anything read out of memory has a lifetime.
    /// </summary>
    /// <remarks>
    /// Where the instruction writes is asked of <see cref="StoreTarget"/> rather than of
    /// <c>Instruction.Destination</c>, which is null for a store into a field or into memory - that is, for
    /// exactly the writes this is here to notice. See <see cref="StoreTarget"/> for what that cost.
    /// </remarks>
    private static bool Invalidates(Instruction instruction, object replacement)
    {
        var isCall = instruction.OpCode is OpCode.Call or OpCode.CallVoid or OpCode.IndirectCall;
        var written = StoreTarget.Of(instruction);

        switch (replacement)
        {
            case FieldReference field:
                if (isCall)
                    return true;

                // The same field written on any object, since nothing here proves the objects are different -
                // and the object this one is read from being reassigned makes it name a different field.
                // A store through a raw memory operand may be that very field: field recovery does not name
                // every write, and the same slot appears as `[x0 + 0x10]` on one line and as
                // `this.<>1__state` on the next.
                return written switch
                {
                    FieldReference stored => StoreTarget.IsTheSameField(stored.Field, field.Field),
                    LocalVariable destination => destination == field.Local,
                    MemoryOperand => true,
                    _ => false,
                };

            case MemoryOperand memory when !memory.IsConstant:
                return isCall || written is MemoryOperand or FieldReference;

            default:
                return false;
        }
    }
}
