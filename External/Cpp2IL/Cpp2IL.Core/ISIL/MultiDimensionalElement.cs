using System.Linq;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.ISIL;

/// <summary>
/// One element of an array of more than one dimension, named by an index per dimension.
/// </summary>
/// <remarks>
/// An array of two dimensions is stored flat, so the compiler works out a single distance into it -
/// <c>length1 * i + j</c> - and reads there. That arithmetic is not something the language will say: C# indexes
/// such an array by its dimensions, and the flattened index has no name in it. So the two indices have to be
/// carried separately, which is what this is for; a <see cref="MemoryOperand"/> has room for one.
/// </remarks>
public class MultiDimensionalElement(object array, object[] indices, ArrayTypeAnalysisContext arrayType)
{
    public object Array = array;

    /// <summary>One per dimension, outermost first.</summary>
    public object[] Indices = indices;

    public ArrayTypeAnalysisContext ArrayType = arrayType;

    public override string ToString() => $"{Array}[{string.Join(", ", Indices.Select(index => index.ToString()))}]";
}
