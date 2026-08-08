using System.Linq;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Names the constructor a delegate is built with, so the allocation beside it becomes a real <c>newobj</c>.
/// </summary>
/// <remarks>
/// <para>
/// A lambda is a delegate built from a target and a method, and il2cpp generates one constructor per
/// delegate type - which no method table names. Everything else about the shape survives:
/// </para>
/// <code>
/// Newobj v88 (DOGetter`1&lt;Color&gt;), typeof(DOGetter`1&lt;Color&gt;)
/// Call   328C638, v111, v88, v43 (&lt;&gt;c__DisplayClass34_0), methodof(&lt;DOFadeChar&gt;b__0), ...
/// </code>
/// <para>
/// The type is on the allocation, the target and the method are arguments two and three, and the generator
/// already knows what to do with both: <c>Newobj</c> fuses with a constructor call that follows it, and a
/// runtime method operand is written as <c>ldftn</c>. Only the callee was missing, and without it the whole
/// statement came out as <c>DOGetter&lt;Color&gt; getter = null;</c> beside a lost call.
/// </para>
/// <para>
/// <b>Recognised by shape rather than by address</b>: the object being constructed was allocated as a
/// delegate, and the argument after the target is a method. Nothing else has that shape, and it holds for
/// every delegate type in the game rather than the two whose addresses were counted.
/// </para>
/// </remarks>
public static class DelegateConstruction
{
    public static bool Run(MethodAnalysisContext method)
    {
        if (method.ControlFlowGraph is not { } graph)
            return false;

        var changed = false;

        foreach (var instruction in graph.Instructions)
        {
            //An unresolved call. Anything the tables already name is not this.
            if (instruction.OpCode != OpCode.Call || instruction.Operands.Count < 5
                || instruction.Operands[0] is not ulong)
                continue;


            if (instruction.Operands[2] is not LocalVariable { Type: { } built } created)
                continue;


            if (!IsDelegate(built))
                continue;


            if (instruction.Operands[4] is not RuntimeMethodInfoAnalysisContext)
                continue;


            if (ConstructorOf(built) is not { } constructor)
                continue;


            //`[constructor, theObject, target, method]` - what the generator's own fusing expects, and it
            //gives nothing back, so the result register goes with the opcode.
            instruction.OpCode = OpCode.CallVoid;
            instruction.Operands = [constructor, created, instruction.Operands[3], instruction.Operands[4]];
            changed = true;
        }

        return changed;
    }

    /// <summary>Whether the type is one whose instances hold a target and a method.</summary>
    private static bool IsDelegate(TypeAnalysisContext type)
    {
        //Through the definition at every step. An instantiation carries no base of its own, so walking from
        //`DOGetter<Color>` finds nothing at all - which is why the first version recognised exactly one
        //delegate in the whole game. Bounded rather than trusting the chain to end, as elsewhere in this fork.
        var walked = (type as GenericInstanceTypeAnalysisContext)?.GenericType ?? type;

        for (var steps = 0; walked != null && steps < 16; steps++)
        {
            if (walked.FullName is "System.MulticastDelegate" or "System.Delegate")
                return true;

            var next = walked.BaseType;
            walked = (next as GenericInstanceTypeAnalysisContext)?.GenericType ?? next;
        }

        return false;
    }

    /// <summary>
    /// The two-argument constructor every delegate has, named through the instantiation where there is one.
    /// </summary>
    /// <remarks>
    /// An instantiation carries no methods of its own, so the constructor is declared by the type it
    /// instantiates and has to be named back through its arguments - the same step
    /// <see cref="InaccessibleFieldRecovery"/> needs for an accessor, and without it
    /// <c>DOGetter&lt;Color&gt;</c> looks like it has no constructor at all.
    /// </remarks>
    private static MethodAnalysisContext? ConstructorOf(TypeAnalysisContext type)
    {
        var instance = type as GenericInstanceTypeAnalysisContext;
        var owner = instance?.GenericType ?? type;

        if (owner.Methods.FirstOrDefault(m => m is { IsStatic: false, Name: ".ctor" } && m.Parameters.Count == 2)
            is not { } constructor)
            return null;

        return instance == null
            ? constructor
            : new ConcreteGenericMethodAnalysisContext(constructor, instance.GenericArguments, []);
    }
}
