using System.Linq;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Gives <c>Type.GetTypeFromHandle</c> back the type its handle names.
/// </summary>
/// <remarks>
/// <para>
/// <c>typeof(X)</c> is two things in IL - a token and a call - and both survive the lifting: the call is
/// resolved and the handle is a runtime class pointer the analysis has already worked out. Where the class
/// came from a constant the generator writes the token; where it came from the runtime generic context it
/// arrives as a <em>local</em> that is merely <b>typed</b> <c>Il2CppClass&lt;T&gt;</c>, and a local is not a
/// token:
/// </para>
/// <code>
/// Call Type.GetTypeFromHandle, v150 (System.Type), v129 @ X20_v3 (Il2CppClass&lt;T&gt;)
/// </code>
/// <para>
/// came out as <c>Type.GetTypeFromHandle((RuntimeTypeHandle)(nint)intPtr)</c>, which does not compile - so
/// the statement goes, and with it whatever it fed: <c>Enum.GetValues(typeof(T))</c> in three of
/// <c>EnumHelper</c>'s methods, <c>typeof(T).Name</c> in two singletons, <c>typeof(T).ToString()</c> in two
/// configuration loaders.
/// </para>
/// <para>
/// The local's type says exactly which class it is. Saying so on the call is all that is needed, and the
/// value it was carrying is then read by nobody, which is what takes the read of the context away with it.
/// </para>
/// </remarks>
public static class TypeHandleIsTheClassItNames
{
    public static void Run(MethodAnalysisContext method)
    {
        if (method.ControlFlowGraph is not { } graph)
            return;

        foreach (var instruction in graph.Blocks.SelectMany(b => b.Instructions))
        {
            if (instruction is not { OpCode: OpCode.Call, Operands: [MethodAnalysisContext callee, _, ..] })
                continue;

            if (callee.Name != "GetTypeFromHandle" || callee.DeclaringType?.FullName != "System.Type")
                continue;

            if (instruction.Operands.Count < 3
                || instruction.Operands[2] is not LocalVariable { Type: RuntimeClassTypeAnalysisContext { RepresentedType: { } named } })
            {
                continue;
            }

            instruction.Operands[2] = named;
        }
    }
}
