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
    /// Whether two references name the same field. A field of a generic type is named through the arguments
    /// the type has where it is read, and that naming is worked out fresh at each place - so two references
    /// to one field are never the same object, and comparing objects would let a read be carried past the
    /// write that makes it stale.
    /// </summary>
    private static bool SameField(FieldAnalysisContext left, FieldAnalysisContext right)
        => ReferenceEquals(Underlying(left), Underlying(right));

    private static FieldAnalysisContext Underlying(FieldAnalysisContext field)
        => (field as ConcreteGenericFieldAnalysisContext)?.BaseFieldContext ?? field;

    /// <summary>
    /// Whether the instruction makes the replacement stale, so that carrying it any further would change what
    /// the code says. A constant is never stale; anything read out of memory has a lifetime.
    /// </summary>
    private static bool Invalidates(Instruction instruction, object replacement)
    {
        var isCall = instruction.OpCode is OpCode.Call or OpCode.CallVoid or OpCode.IndirectCall;

        switch (replacement)
        {
            case FieldReference field:
                if (isCall)
                    return true;

                // The same field written on any object, since nothing here proves the objects are different -
                // and the object this one is read from being reassigned makes it name a different field.
                return instruction.Destination switch
                {
                    FieldReference written => SameField(written.Field, field.Field),
                    LocalVariable destination => destination == field.Local,
                    _ => false,
                };

            case MemoryOperand memory when !memory.IsConstant:
                return isCall || instruction.Destination is MemoryOperand or FieldReference;

            default:
                return false;
        }
    }
}
