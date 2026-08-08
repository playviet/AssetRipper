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
    /// The same recovery where the loads have already been resolved and folded away.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Run"/> finds the delegate by looking for the load of <c>invoke_impl</c> - a read at three
    /// pointers into the object - and following it back to what it was read from. By the time it runs, the
    /// resolver has often named that read as the field it is and constant folding has put it straight into
    /// the call, leaving the three loads as <c>Nop</c> and nothing whose definition can be followed:
    /// </para>
    /// <code>
    /// IndirectCall v8 (IntPtr), v10, v0.method_code, v0.method, v0.invoke_impl, v11, …
    /// </code>
    /// <para>
    /// The answer is in the operand list rather than behind it: a call handed a delegate's
    /// <c>invoke_impl</c> is that delegate's <c>Invoke</c>, and the field says which delegate. 39 of the 94
    /// indirect calls left in the game are this.
    /// </para>
    /// </remarks>
    public static void RunOnFoldedLoads(MethodAnalysisContext method)
    {
        if (method.ControlFlowGraph is not { } graph)
            return;

        foreach (var instruction in graph.Instructions.ToList())
        {
            if (instruction.OpCode != OpCode.IndirectCall)
                continue;

            //Either the field is already an operand of the call, or the register it was loaded into still
            //is and the load beside it names the field. Which of the two depends only on whether the
            //propagation that folds a definition into its use has run yet.
            var handed = instruction.Operands.OfType<FieldReference>().FirstOrDefault(IsInvokeImpl)
                ?? (instruction.Operands.Count > 0 && instruction.Operands[0] is LocalVariable target
                    ? graph.Instructions.FirstOrDefault(i => i is { OpCode: OpCode.Move, Operands: [_, FieldReference read] }
                        && ReferenceEquals(i.Destination, target) && IsInvokeImpl(read))?.Operands[1] as FieldReference
                    : null);

            if (handed?.Local is not { } delegateLocal)
            {
                Report("noInvokeImpl " + instruction);
                continue;
            }

            if (InvokeOf(delegateLocal.Type) is not { } invoke)
            {
                Report("noInvoke on " + delegateLocal.Type);
                continue;
            }

            RewriteAsInvokeForArchitecture(instruction, delegateLocal, invoke, method);

            if (instruction.OpCode == OpCode.IndirectCall)
                Report("rewriteRefused " + invoke.FullName + " params=" + invoke.Parameters.Count + " void=" + invoke.IsVoid);
        }
    }

    /// <summary>The third pointer into a delegate, which is the stub every invocation goes through.</summary>
    private static bool IsInvokeImpl(FieldReference reference) => reference.Field.Name == "invoke_impl";

    private static void Report(string why)
    {
        if (System.Environment.GetEnvironmentVariable("DELEGATE_TRACE") == "1")
            System.Console.Error.WriteLine("DELEGATE " + why);
    }

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
