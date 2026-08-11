using Cpp2IL.Core.Extensions;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Names the runtime helpers a cast and a box are compiled into, from the shape of the call rather than its
/// address.
///
/// Casting a reference is not an instruction on this architecture: it is a call into the runtime taking the
/// value and the class to check it against, and giving back the value or nothing. The helper is reached
/// through a thunk that no method table names, so the call stayed an address and every statement built on it
/// went with it - which in this game is every event accessor, every <c>as</c>, and every cast out of a
/// collection.
///
/// What identifies a cast is that its second argument is a type and its result is used: no other runtime
/// helper is handed a value and a class in that order and gives something back. Boxing is the same call with
/// the two the other way round and the type a value type, which nothing else is either - only a value type is
/// ever boxed. The name is all the generator needs; it already knows how to write each of them as the one CIL
/// instruction it stands for.
/// </summary>
public static class CastHelperRecovery
{
    private const string ObjectIsInstance = "il2cpp_vm_object_is_inst";
    private const string Box = "il2cpp_codegen_box";

    public static void Run(MethodAnalysisContext method)
    {
        foreach (var instruction in method.ControlFlowGraph!.Instructions)
        {
            if (instruction.OpCode != OpCode.Call || instruction.Operands.Count < 4)
                continue;

            //Still an address: a call that resolved to a method says what it is already.
            if (!instruction.Operands[0].IsNumeric())
                continue;

            if (instruction.Operands[1] is not LocalVariable)
                continue;

            //Boxing names the type first and only ever names a value type.
            if (ClassOf(instruction.Operands[2]) is { IsValueType: true } boxed
                && instruction.Operands[3] is LocalVariable or FieldReference)
            {
                instruction.Operands = [Box, instruction.Operands[1], instruction.Operands[3], boxed];
                continue;
            }

            //The value being cast, which has to be a value rather than a type or a constant.
            if (instruction.Operands[2] is not (LocalVariable or FieldReference))
                continue;

            if (ClassOf(instruction.Operands[3]) is not { } target)
                continue;

            instruction.Operands = [ObjectIsInstance, instruction.Operands[1], instruction.Operands[2], target];

            //And what it answers with is one of those. The helper hands its answer back in a general register
            //like any other call, so a value that nothing else pinned down came out as a number, and the
            //`isinst` the generator writes is then stored into a `long` - `long num11 = (long)(text as
            //ECellColor[])`, which does not compile, and every statement that used it goes with it. Only where
            //nothing has decided otherwise: a result already known to be a reference is not overruled, since
            //the class here is what the check is *against* and the value may be declared as something below
            //it.
            if (instruction.Operands[1] is LocalVariable { Type: null or { IsValueType: true } } answer)
                answer.Type = target;

            //And the value being asked about, where what it is declared as is something the question could
            //not be about at all.
            if (instruction.Operands[2] is LocalVariable asked && AsksSomethingImpossible(asked.Type, target))
                asked.Type = method.AppContext.SystemTypes.SystemObjectType;
        }
    }

    /// <summary>
    /// Whether the value a cast is asking about is declared as something the cast could not possibly be about,
    /// in which case the declaration is wrong and <c>object</c> is what the code actually has in hand.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A register holds whatever it last held, and a value the analysis could not place keeps that. In
    /// <c>BoardController::PowerUp_Shuffle</c> the result of <c>Array.Clone</c> - which returns
    /// <c>object</c> - came out declared <c>System.String</c>, so the cast after it read
    /// <c>text as ECellColor[]</c>: valid IL, and not something C# will say, because no reference conversion
    /// exists between the two. The statement and the three built on it were commented away.
    /// </para>
    /// <para>
    /// Asking whether a value is of a type says nothing about what it is declared as - that is the whole
    /// point of asking - so where the two are unrelated the declaration is the thing that is wrong. Only
    /// where they are genuinely unrelated: <c>x as Derived</c> on a <c>Base</c> is the ordinary case and
    /// says the declaration was right.
    /// </para>
    /// </remarks>
    private static bool AsksSomethingImpossible(TypeAnalysisContext? declared, TypeAnalysisContext target)
    {
        if (declared is null || declared.IsValueType || target.IsValueType)
            return false;

        if (declared.IsInterface || target.IsInterface)
            return false;

        return !Reaches(declared, target) && !Reaches(target, declared);
    }

    /// <summary>Whether one type is the other or is derived from it.</summary>
    private static bool Reaches(TypeAnalysisContext from, TypeAnalysisContext to)
    {
        //Everything reaches object, so a value declared as anything can be asked about as one.
        if (to.FullName == "System.Object")
            return true;

        for (var step = from; step is not null; step = step.BaseType)
        {
            if (step.FullName == to.FullName)
                return true;
        }

        return false;
    }

    /// <summary>The type a runtime class argument stands for, whether it is folded in or carried in a value.</summary>
    private static TypeAnalysisContext? ClassOf(object operand) => operand switch
    {
        LocalVariable { Type: RuntimeClassTypeAnalysisContext runtimeClass } => runtimeClass.RepresentedType,
        RuntimeClassTypeAnalysisContext runtimeClass => runtimeClass.RepresentedType,
        //These stand for a runtime structure rather than for a type the code can name.
        StaticFieldStorageTypeAnalysisContext or RgctxTableTypeAnalysisContext
            or MethodRgctxTableTypeAnalysisContext or RuntimeMethodInfoAnalysisContext => null,
        TypeAnalysisContext type => type,
        _ => null,
    };
}
