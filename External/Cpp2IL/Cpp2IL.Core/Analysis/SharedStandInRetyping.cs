using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Replaces the stand-in a shared body was written against with the type the value really has.
/// </summary>
/// <remarks>
/// il2cpp compiles one body for every instantiation of a generic that is laid out alike, and records it
/// against a stand-in: <c>System.Object</c> for a reference, and for an enum one of a set of types named after
/// the integer behind it - <c>System.Int32Enum</c> and its siblings. Those are real types in il2cpp's own
/// metadata, so a value typed as one reads as perfectly ordinary; but they exist in **no assembly Unity
/// ships**, so a recovered file that names one does not compile, and every statement mentioning it is dropped.
///
/// The value's real type is usually right beside it. A shared body is reached with the arguments the caller
/// had, so the copies that carry a value in and out of it put a local said to hold a
/// <c>HashSet&lt;System.Int32Enum&gt;</c> next to the parameter that holds the <c>HashSet&lt;ECellColor&gt;</c>
/// it actually is. Where a copy connects the two, the one that cannot be written down takes the type of the
/// one that can - which is not a guess: a stand-in is only ever a stand-in, so anything it stands next to is
/// better information than it is.
/// </remarks>
public static class SharedStandInRetyping
{
    /// <summary>Enough rounds for a chain of copies to carry a type along it, and few enough to always stop.</summary>
    private const int Rounds = 8;

    public static bool Run(MethodAnalysisContext method)
    {
        var changed = false;
        bool again;
        var round = 0;

        do
        {
            again = false;

            foreach (var instruction in method.ControlFlowGraph!.Instructions)
            {
                if (instruction.OpCode is OpCode.Call or OpCode.CallVoid)
                {
                    again |= FromParameters(instruction);
                    continue;
                }

                if (instruction.OpCode != OpCode.Move || instruction.Operands.Count != 2)
                    continue;

                //A copy says the two hold the same value, so whichever end can be named names both.
                again |= Adopt(instruction.Operands[0], TypeOf(instruction.Operands[1]));
                again |= Adopt(instruction.Operands[1], TypeOf(instruction.Operands[0]));
            }

            changed |= again;
        } while (again && ++round < Rounds);

        return changed;
    }

    /// <summary>
    /// Names a call's arguments after what the method declares it takes.
    /// </summary>
    /// <remarks>
    /// This is where the stand-in is most often left. The call itself is usually recovered at the real
    /// instantiation - the receiver says which one - so the method reads as
    /// <c>List&lt;ECellColor&gt;.Add</c>, while the value being handed to it is still whatever the shared
    /// body called it, an <c>Int32Enum</c>. The declaration is the better answer, and it is right there in
    /// the call.
    /// </remarks>
    private static bool FromParameters(Instruction call)
    {
        if (call.Operands.Count == 0 || call.Operands[0] is not MethodAnalysisContext callee)
            return false;

        //Past the callee, the register a result comes back in, and the receiver where there is one.
        var first = (call.OpCode == OpCode.Call ? 2 : 1) + (callee.IsStatic ? 0 : 1);
        var changed = false;

        for (var i = 0; i < callee.Parameters.Count && first + i < call.Operands.Count; i++)
            changed |= Adopt(call.Operands[first + i], callee.Parameters[i].ParameterType);

        //And the result, after what the method declares it gives back - the seed the copies above then carry
        //along the whole chain the value travels. Without it a `Dictionary<string, int>.GetEnumerator` lands
        //in a place still typed `Dictionary<object, int>.Enumerator`, the two disagree, and the generator
        //refuses the store: the `foreach` walks an enumerator nothing ever assigned. A struct too large for a
        //register comes back through a pointer, so the place is the local under the memory operand.
        if (call.OpCode == OpCode.Call && call.Operands.Count > 1)
            changed |= Adopt(call.Operands[1] is MemoryOperand { Base: { } through } ? through : call.Operands[1],
                callee.ReturnType);

        return changed;
    }

    /// <summary>Gives a local the real type, where what it is said to hold is only a stand-in for that.</summary>
    private static bool Adopt(object operand, TypeAnalysisContext? real)
    {
        if (operand is not LocalVariable { Type: { } held } local || real == null)
            return false;

        if (!GenericSharingRecovery.IsStandInFor(held, real))
            return false;

        local.Type = real;
        return true;
    }

    /// <summary>What a value is, where that is known.</summary>
    private static TypeAnalysisContext? TypeOf(object operand) => operand switch
    {
        LocalVariable local => local.Type,
        FieldReference field => field.Field.FieldType,
        _ => null,
    };
}
