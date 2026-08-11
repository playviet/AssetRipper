using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// A composite too big for two registers is handed over as a pointer to one, so the argument is the struct
/// and not the member the address happened to be named after.
/// </summary>
/// <remarks>
/// <para>
/// AAPCS64 passes a value larger than sixteen bytes - and not a homogeneous float aggregate - by copying it
/// somewhere and handing the callee that address. The register therefore holds an address, and everything
/// downstream that names a distance from an address named the field at nought:
/// </para>
/// <code>
/// CallVoid MinMaxGradient..ctor,      v466 @ stackaddr_-90 (MinMaxGradient), colour, ...
/// CallVoid ParticleImage.set_startColor, v471 (ParticleImage), v466.m_Mode (ParticleSystemGradientMode)
/// </code>
/// <para>
/// which the generator writes as
/// <c>particleImage.startColor = (ParticleSystem.MinMaxGradient)minMaxGradient.mode;</c> - a cast from an
/// enum to a struct, which is not something C# will say, and the statement and everything built on it went.
/// <c>MergeEffect::Play</c>'s lambda is that, and the original is
/// <c>mergeParticle.startColor = new ParticleSystem.MinMaxGradient(...)</c>.
/// </para>
/// <para>
/// <see cref="Aapcs64.ReturnsIndirectly"/> already models this for the answer coming back; this is the same
/// rule on the way in. Only where the field is at distance nought and belongs to the very type the parameter
/// is declared as - a member further in is a real read of a member, and one of some other type says the
/// address was never this struct's.
/// </para>
/// </remarks>
public static class IndirectArgument
{
    public static void Run(MethodAnalysisContext method)
    {
        if (method.ControlFlowGraph is not { } graph)
            return;

        foreach (var instruction in graph.Instructions)
        {
            if (!instruction.IsCall || instruction.Operands.Count == 0
                || instruction.Operands[0] is not MethodAnalysisContext callee)
                continue;

            var receiver = instruction.OpCode == OpCode.Call ? 2 : 1;
            var first = receiver + (callee.IsStatic ? 0 : 1);

            for (var i = 0; i < callee.Parameters.Count && first + i < instruction.Operands.Count; i++)
            {
                var declared = callee.Parameters[i].ParameterType;

                if (!Aapcs64.PassedIndirectly(declared))
                    continue;

                if (instruction.Operands[first + i] is not FieldReference { Offset: 0 } named
                    || named.Field.DeclaringType?.FullName != declared.FullName)
                {
                    continue;
                }

                instruction.Operands[first + i] = named.Local;
            }
        }
    }
}
