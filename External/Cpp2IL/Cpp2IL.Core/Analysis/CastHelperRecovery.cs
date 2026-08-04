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
        }
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
