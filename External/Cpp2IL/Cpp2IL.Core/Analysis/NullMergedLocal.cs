using System.Collections.Generic;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// A value that is only ever a reference or a nought is that reference, whatever width the register had.
/// </summary>
/// <remarks>
/// <para>
/// A type test compiles to the answer on one arm and a nought on the other, and the two meet at a join. Taking
/// single assignment form apart writes that join as one copy per edge, into a local whose type came from
/// whatever the register was carrying beforehand:
/// </para>
/// <code>
/// Call "il2cpp_vm_object_is_inst", v520 (CF.ECellColor[]), v153, typeof(CF.ECellColor[])
/// Move v230 @ X1_v8 (System.Int64), v520 (CF.ECellColor[])
/// Move v230 @ X1_v8 (System.Int64), 0
/// Move [grids + 0x20 + w*8], v230 @ X1_v8 (System.Int64)
/// </code>
/// <para>
/// Nothing revises a type once it is set, so the store came out as
/// <c>array2[num8] = (ECellColor[])num11;</c> - which does not compile, and the three statements that built
/// <c>num11</c> went with it. <c>BoardController::PowerUp_Shuffle</c>'s last four are exactly that, and its
/// source is <c>grids[w] = (ECellColor[])_boardData[i].grid.Clone();</c>.
/// </para>
/// <para>
/// <b>A number and a reference share exactly one value, and it is null.</b> So where every place that writes
/// a local writes either one reference type or a nought, that reference type is what it holds and the number
/// was the register's previous tenant. Two different reference types, a write this cannot read, or no
/// reference at all, and it says nothing - which is why the rule is stated over <i>every</i> definition
/// rather than over the one that happens to come first.
/// </para>
/// <para>
/// Late, because it needs the arms already resolved: <c>v520</c> is only known to be an <c>ECellColor[]</c>
/// once <see cref="CastHelperRecovery"/> has named the cast helper and said what it answers with.
/// </para>
/// </remarks>
public static class NullMergedLocal
{
    public static void Run(MethodAnalysisContext method)
    {
        if (method.ControlFlowGraph is not { } graph)
            return;

        //What each local is written by, and whether any of those writes is something this cannot read.
        var written = new Dictionary<LocalVariable, TypeAnalysisContext?>();
        var refused = new HashSet<LocalVariable>();

        foreach (var instruction in graph.Instructions)
        {
            if (instruction.Operands.Count == 0 || instruction.Operands[0] is not LocalVariable destination)
                continue;

            if (!IsOnlyAWidth(destination.Type))
                continue;

            if (instruction is not { OpCode: OpCode.Move, Operands: [_, { } source] })
            {
                refused.Add(destination);
                continue;
            }

            switch (Held(source))
            {
                //A reference, and every other definition has to agree with it.
                case { } reference:
                    if (written.TryGetValue(destination, out var already) && already != null
                        && already.FullName != reference.FullName)
                    {
                        refused.Add(destination);
                        break;
                    }

                    written[destination] = reference;
                    break;

                //A nought is the one value the two kinds share, and says nothing on its own.
                case null when IsNought(source):
                    written.TryAdd(destination, null);
                    break;

                default:
                    refused.Add(destination);
                    break;
            }
        }

        foreach (var (local, reference) in written)
        {
            if (reference != null && !refused.Contains(local))
                local.Type = reference;
        }
    }

    /// <summary>The reference type an operand holds, where it is one the program can name.</summary>
    private static TypeAnalysisContext? Held(object operand)
    {
        var type = operand switch
        {
            LocalVariable local => local.Type,
            FieldReference field => field.Field.FieldType,
            _ => null,
        };

        if (type is not { IsValueType: false }
            || type is ByRefTypeAnalysisContext or PointerTypeAnalysisContext
            || LocalVariables.IsRuntimeStandIn(type))
        {
            return null;
        }

        return type;
    }

    /// <summary>
    /// Whether a type is no more than the width of the register the value was in. Only these are overruled,
    /// so a type that says something is never thrown away.
    /// </summary>
    private static bool IsOnlyAWidth(TypeAnalysisContext? type)
        => type?.FullName is "System.Int64" or "System.UInt64" or "System.IntPtr" or "System.UIntPtr";

    private static bool IsNought(object operand)
    {
        try
        {
            return operand is not (string or LocalVariable or Register or MemoryOperand or FieldReference)
                && System.Convert.ToInt64(operand) == 0;
        }
        catch
        {
            return false;
        }
    }
}
