using System.Linq;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Writes a read of <c>EmptyArray&lt;T&gt;.Value</c> as the call to <c>Array.Empty&lt;T&gt;()</c> it is.
///
/// Calling a method whose last parameter is <c>params</c> without any arguments for it passes an empty
/// array, and the compiler asks <c>Array.Empty&lt;T&gt;()</c> for one rather than allocating. That method's
/// whole body is <c>return EmptyArray&lt;T&gt;.Value;</c>, so it gets inlined and the call site is left
/// reading the field directly - which is what the recovered code then says.
///
/// The field says the same thing as the call, but only the call can be written down: the type holding it
/// is internal to the runtime library, so a body naming it does not compile and gets commented out - the
/// call it belongs to along with it. Naming the method instead loses nothing, since the method is defined
/// as that field.
/// </summary>
public static class EmptyArrayRecovery
{
    public static void Run(MethodAnalysisContext method)
    {
        MethodAnalysisContext? empty = null;

        foreach (var instruction in method.ControlFlowGraph!.Instructions)
        {
            //Only the plain read is rewritten. A call takes its result in the operand after the callee
            //rather than before it, so the two shapes do not overlap and nothing else has to move.
            if (instruction.OpCode != OpCode.Move || instruction.Operands.Count != 2)
                continue;

            if (instruction.Operands[0] is not LocalVariable destination)
                continue;

            if (instruction.Operands[1] is not FieldReference { Field: { Name: "Value", IsStatic: true } field }
                || ElementOf(field.DeclaringType) is not { } element)
                continue;

            empty ??= ArrayEmpty(method.AppContext);

            if (empty == null)
                return;

            instruction.OpCode = OpCode.Call;
            instruction.Operands = [new ConcreteGenericMethodAnalysisContext(empty, [], [element]), destination];
        }
    }

    /// <summary>The T of an <c>EmptyArray&lt;T&gt;</c>, if that is the type the field belongs to.</summary>
    private static TypeAnalysisContext? ElementOf(TypeAnalysisContext declaring)
        => declaring is GenericInstanceTypeAnalysisContext { GenericArguments: [var element] } generic
            && generic.GenericType.FullName == "System.EmptyArray`1"
            ? element
            : null;

    private static MethodAnalysisContext? ArrayEmpty(ApplicationAnalysisContext appContext)
        => appContext.GetAssemblyByName("mscorlib")?.GetTypeByFullName("System.Array")?.Methods
            .FirstOrDefault(m => m is { Name: "Empty", IsStatic: true, Parameters.Count: 0 } && m.GenericParameters.Count == 1);
}
