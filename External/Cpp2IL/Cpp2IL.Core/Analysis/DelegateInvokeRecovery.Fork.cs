using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// What this fork adds: finding a delegate's <c>Invoke</c> when the delegate is a generic one.
///
/// Kept apart from the file it belongs to so that the file stays as close to upstream as it can, and a later
/// version of Cpp2IL can be merged without the two sets of changes meeting.
/// </summary>
public static partial class DelegateInvokeRecovery
{
    /// <summary>
    /// Writes the call back as the delegate's <c>Invoke</c>, by the convention the build actually uses.
    /// </summary>
    /// <remarks>
    /// Upstream resolves the arguments against the x64 convention whatever the binary is, and refuses any
    /// signature that puts one in a vector register - which on x64 costs the floating point delegates and on
    /// arm64 would be resolving against the wrong convention entirely. Taking the arm64 path here leaves that
    /// as it was for the architecture it was written for.
    ///
    /// A delegate is invoked through a stub whose first argument is not the delegate but the target it holds,
    /// so the receiver is replaced rather than read out of a register; the parameters after it are the
    /// delegate's own, and <see cref="Aapcs64"/> says which register each arrived in.
    /// </remarks>
    private static void RewriteAsInvokeForArchitecture(
        Instruction call, LocalVariable delegateLocal, MethodAnalysisContext invoke, MethodAnalysisContext method)
    {
        if (method.AppContext.InstructionSet is not NewArmV8InstructionSet)
        {
            RewriteAsInvoke(call, delegateLocal, invoke);
            return;
        }

        if (Aapcs64.ParametersOf(invoke, call.Operands) is not { } parameters)
            return;

        //Where the result goes has to be somewhere, or the emitted IL pushes a value nothing takes off again.
        if (!invoke.IsVoid && (call.Operands.Count < 2 || call.Operands[1] is not LocalVariable))
            return;

        var operands = new List<object> { invoke };

        if (!invoke.IsVoid)
            operands.Add(call.Operands[1]);

        operands.Add(delegateLocal);
        operands.AddRange(parameters);

        call.OpCode = invoke.IsVoid ? OpCode.CallVoid : OpCode.Call;
        call.Operands = operands;
    }

    /// <summary>
    /// The <c>Invoke</c> of the delegate a value holds, at the instantiation the value has.
    ///
    /// An instance of a generic type is a wrapper around the type it instantiates, and only that type carries
    /// the metadata saying what it derives from - so a value of type <c>Action&lt;string, int&gt;</c> reports
    /// no base type at all and does not read as a delegate. Every <c>Action&lt;...&gt;</c> and
    /// <c>Func&lt;...&gt;</c> in a game is one of those, which is to say almost every delegate call there is.
    /// </summary>
    private static MethodAnalysisContext? InvokeOf(TypeAnalysisContext? type)
    {
        var generic = type as GenericInstanceTypeAnalysisContext;
        var definition = generic?.GenericType ?? type;

        if (definition is not { IsDelegate: true })
            return null;

        if (definition.Methods.FirstOrDefault(m => m.Name == "Invoke") is not { } invoke)
            return null;

        //Named at the instantiation, or its parameters are the type's own and say nothing about what it takes.
        return generic == null ? invoke : new ConcreteGenericMethodAnalysisContext(invoke, generic.GenericArguments, []);
    }
}
