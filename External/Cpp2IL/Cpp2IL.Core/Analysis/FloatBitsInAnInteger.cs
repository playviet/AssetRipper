using System.Linq;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Moving a float into an integer register moves its <em>bits</em>, and the language has a name for that.
/// </summary>
/// <remarks>
/// <para>
/// <c>FMOV Wd, Sn</c> and <c>FMOV Xd, Dn</c> are the architecture's bit reinterpretations, and they are how
/// every test of a floating point value's exponent is compiled - <c>float.IsNaN</c>, <c>IsInfinity</c>, and
/// the sign test the compiler inlines for <c>Mathf.Abs</c>:
/// </para>
/// <code>
/// fmov w8, s0
/// and  w8, w8, #0x7fffffff   ; drop the sign
/// cmp  w8, #0x7f800000       ; the exponent of infinity
/// </code>
/// <para>
/// Lifted as a plain move, that is wrong twice. A move says both sides are the same value, so copy
/// propagation folds it away and the reinterpretation disappears entirely - and what is left is a
/// <c>float</c> as the operand of a bitwise <c>and</c>, which C# cannot say, so the statement is commented
/// out. <c>VectorExtensions::IsValid</c> is the whole of it: every NaN and infinity test is gone and the
/// method returns <c>false</c> for every vector it is given.
/// </para>
/// <para>
/// <b>It is not a conversion.</b> Marking it with a <c>ConversionTarget</c> would make the generator write
/// <c>(int)x</c>, and <c>(int)0.85f</c> is nought where the bits are <c>0x3F59999A</c>. The operation the
/// instruction performs has a name in the library - <c>BitConverter.SingleToInt32Bits</c> - so it is written
/// as the call it is, the way an inlined <c>Mathf.Sqrt</c> is written as a call.
/// </para>
/// <para>
/// Only the direction that crosses <em>into</em> an integer register. The other way round -
/// <c>FMOV Sd, Wn</c> - is how a float literal is materialised, 185 sites against these twelve.
/// </para>
/// <para>
/// That sentence used to end "and it is already handled by the literal recovery", <b>which was wrong</b>.
/// The literal recovery reinterprets a constant that is an operand of the expression; a constant that is
/// one <c>FMOV</c> away, in a general register, is not one, and it stayed an integer -
/// <c>ProceduralImage::EncodeFloats_0_1_16_16</c> divided a float by <c>1199570688L</c> and returned the
/// sum of two longs from a <c>float</c> method. That direction is <see cref="FloatConstantInAnInteger"/>,
/// and it is a literal rather than a call: this class writes the operation the program performs, that one
/// writes the number the source said.
/// </para>
/// </remarks>
public static class FloatBitsInAnInteger
{
    /// <summary>
    /// The library method that names this reinterpretation, or nothing where the move is an ordinary one.
    /// </summary>
    public static MethodAnalysisContext? Naming(ApplicationAnalysisContext appContext, bool isDouble)
    {
        var name = isDouble ? "DoubleToInt64Bits" : "SingleToInt32Bits";
        var from = isDouble ? "System.Double" : "System.Single";

        return appContext.GetAssemblyByName("mscorlib")?.GetTypeByFullName("System.BitConverter")?
            .Methods.FirstOrDefault(m => m.Name == name && m.IsStatic && m.Parameters.Count == 1
                && m.Parameters[0].ParameterType.FullName == from);
    }
}
