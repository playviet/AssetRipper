using System.Collections.Generic;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Which register each argument of a call travelled in, under the calling convention arm64 uses.
/// </summary>
/// <remarks>
/// A call through a register says nothing about what the callee takes, so the lifter hands over every register
/// an argument could have arrived in and leaves it to whichever pass works out the signature to pick the ones
/// that were used. Doing that needs the convention written down in one place: arguments are laid into
/// <b>two independent runs</b> of eight - x0 to x7 for integers and pointers, v0 to v7 for floating point - so
/// the register a parameter is in depends on how many parameters <i>of its own kind</i> came before it, not on
/// its position in the list. A method taking <c>(int, float, int)</c> reads them from x0, v0 and x1.
///
/// Getting this wrong is not a missed recovery but a wrong one: the arguments would be handed over shifted
/// along by one, and the call would be written with the wrong values in it and still compile.
/// </remarks>
public static class Aapcs64
{
    /// <summary>How many arguments of one kind travel in registers before the rest go on the stack.</summary>
    public const int RegistersPerRun = 8;

    /// <summary>
    /// Where the argument registers begin in the operands of a call through a register, which are the target
    /// being called and the register the result comes back in, then the two runs one after the other.
    /// </summary>
    private const int FirstArgument = 2;

    /// <summary>
    /// The operands holding the callee's parameters, in the order the callee declares them.
    ///
    /// Null when the call cannot be rebuilt from what is there: a parameter past the eighth of its kind
    /// travelled on the stack, which these operands do not carry, and handing over the eighth register in its
    /// place would put a value in the call that was never an argument to it.
    /// </summary>
    public static List<object>? ParametersOf(MethodAnalysisContext callee, List<object> callOperands)
    {
        //An instance method is handed its receiver in the first integer register, so its parameters start
        //after it. The receiver itself is not taken from here - a pass that recovers such a call knows the
        //object from what it read the runtime class out of, which survives where the register may not.
        var integer = callee.IsStatic ? 0 : 1;
        var vector = 0;

        var parameters = new List<object>(callee.Parameters.Count);

        foreach (var parameter in callee.Parameters)
        {
            var type = parameter.ParameterType;

            int index;

            if (type is { Namespace: nameof(System), Name: "Single" or "Double" })
            {
                index = VectorRegister(vector++);
            }
            else if (HomogeneousFloatStruct.Count(type) is { } floats)
            {
                //A struct whose every field is a float travels in one vector register per field, and is named
                //by the first of them - the same shape the lifter gives a call whose target it did know.
                index = VectorRegister(vector);
                vector += floats;
            }
            else
            {
                index = IntegerRegister(integer++);
            }

            if (integer > RegistersPerRun || vector > RegistersPerRun || index >= callOperands.Count)
                return null;

            parameters.Add(callOperands[index]);
        }

        return parameters;
    }

    /// <summary>
    /// The operands a call's arguments arrived in: the receiver first where the callee takes one, then the
    /// parameters. For a pass that has recovered the receiver from elsewhere, <see cref="ParametersOf"/> gives
    /// the rest on its own.
    /// </summary>
    public static List<object>? ArgumentsOf(MethodAnalysisContext callee, List<object> callOperands)
    {
        if (ParametersOf(callee, callOperands) is not { } parameters)
            return null;

        if (callee.IsStatic)
            return parameters;

        return callOperands.Count > FirstArgument ? [callOperands[FirstArgument], .. parameters] : null;
    }

    private static int IntegerRegister(int index) => FirstArgument + index;

    private static int VectorRegister(int index) => FirstArgument + RegistersPerRun + index;
}
