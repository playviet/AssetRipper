using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Gives an instance call back the object it was called on when the compiled code stopped passing one.
///
/// An instance method takes its receiver in the first argument register, and normally the value is already
/// sitting there. But when the callee never reads the receiver - <c>IsTutorial()</c> here only asks a
/// singleton a question - the compiler proves the argument dead and stops materialising it. What is left
/// is a load of the object, a null check on it, and the call: the null check has to stay, because C# still
/// promises a <see cref="System.NullReferenceException"/> on a null receiver.
///
/// The register then holds whatever the previous call returned, and reading it as the receiver produced
/// nonsense - a <c>bool</c> cast to the class it was supposedly called on. The value that was null-checked
/// on the way in is the receiver, and that is what this restores.
///
/// It has to run before <see cref="NullCheckRemover"/>, which is what those checks are there for.
/// </summary>
public static class CallReceiverRecovery
{
    /// <summary>How far back to look for the check. il2cpp emits it immediately before the call.</summary>
    private const int MaximumBlocksSearched = 3;

    public static void Run(MethodAnalysisContext method)
    {
        foreach (var block in method.ControlFlowGraph!.Blocks)
        {
            for (var i = 0; i < block.Instructions.Count; i++)
            {
                var instruction = block.Instructions[i];

                if (instruction.OpCode is not (OpCode.Call or OpCode.CallVoid))
                    continue;

                if (instruction.Operands.Count == 0 || instruction.Operands[0] is not MethodAnalysisContext { IsStatic: false } callee)
                    continue;

                if (callee.DeclaringType is not { } declaring)
                    continue;

                var receiverIndex = instruction.OpCode == OpCode.Call ? 2 : 1;

                if (instruction.Operands.Count <= receiverIndex)
                    continue;

                //Only a receiver that cannot be the object is repaired. One whose type is unknown is left
                //alone: nothing has been shown to be wrong with it, and a guess would be no better.
                if (instruction.Operands[receiverIndex] is not LocalVariable { Type: { } held } || CanBe(held, declaring))
                    continue;

                if (NullCheckedValue(block, i, declaring) is { } receiver)
                {
                    instruction.Operands[receiverIndex] = receiver;
                    continue;
                }

                //And where there is no check at all, because the callee dereferences nothing. `SetCoin`
                //writes a singleton's field and raises a static event, so the compiler neither passed the
                //receiver nor checked it - and `AddCoin(v) => SetCoin(GetCoin() + v)` came out calling
                //`SetCoin` on the *number* `GetCoin` had left in x0.
                //
                //Only from an instance method of the very type the callee is declared on. Another instance
                //of that type would still be in the register, properly typed, and never reach here; what is
                //here is a value that cannot be the object at all, and inside such a method there is exactly
                //one object it can be.
                if (method.DeclaringType is { } caller && CanBe(caller, declaring) && Self(method) is { } self)
                    instruction.Operands[receiverIndex] = self;
            }
        }
    }

    /// <summary>
    /// The nearest value null-checked before this point that could be an instance of the declaring type,
    /// searching the block itself and then back along single-predecessor edges. A join is not followed:
    /// with more than one way in there is no single preceding check to speak of.
    /// </summary>
    private static LocalVariable? NullCheckedValue(Block block, int before, TypeAnalysisContext declaring)
    {
        var current = block;
        var start = before - 1;

        for (var depth = 0; depth < MaximumBlocksSearched; depth++)
        {
            for (var i = start; i >= 0; i--)
            {
                if (Subject(current.Instructions[i]) is { Type: { } type } subject && CanBe(type, declaring))
                    return subject;
            }

            if (current.Predecessors.Count != 1)
                return null;

            current = current.Predecessors[0];
            start = current.Instructions.Count - 1;
        }

        return null;
    }

    /// <summary>The method's own <c>this</c>, where it has one.</summary>
    private static LocalVariable? Self(MethodAnalysisContext method)
        => method.IsStatic ? null : method.Locals.FirstOrDefault(local => local.IsThis);

    /// <summary>The reference an instruction tests against null, if that is what it does.</summary>
    private static LocalVariable? Subject(Instruction instruction)
    {
        if (instruction.OpCode is not (OpCode.CheckEqual or OpCode.CheckNotEqual) || instruction.Operands.Count < 3)
            return null;

        if (instruction.Operands[1] is not LocalVariable subject || subject.Type is { IsValueType: true })
            return null;

        return IsZero(instruction.Operands[2]) ? subject : null;
    }

    private static bool IsZero(object operand)
    {
        try
        {
            return operand is not string && System.Convert.ToInt64(operand) == 0;
        }
        catch (System.Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Whether a value of this type could be an instance of the declaring type - the same type, or one
    /// derived from it. An interface is accepted from anywhere, since the implementing types are not
    /// reachable from here.
    /// </summary>
    private static bool CanBe(TypeAnalysisContext held, TypeAnalysisContext declaring)
    {
        if (declaring.IsInterface || held.FullName == "System.Object")
            return true;

        var seen = 0;

        for (TypeAnalysisContext? type = held; type != null && seen++ < 16; type = type.BaseType)
        {
            if (type.FullName == declaring.FullName)
                return true;
        }

        return false;
    }
}
