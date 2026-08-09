using System;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Clearing a struct to nought is an assignment of its default, not a call.
/// </summary>
/// <remarks>
/// <para>
/// <c>CharTransform t = default</c> compiles to <c>memset(&amp;t, 0, sizeof(CharTransform))</c>, and the
/// import goes to a stub in <c>.plt</c> that no method table names - so the statement came out as
/// <c>_ = "Method not found @4AFA580"</c>. <b>46 of them</b> across the export, and every one of them is
/// redundant with the declaration the generator already writes.
/// </para>
/// <para>
/// It is not deleted, because a struct cleared again inside a loop is cleared for a reason. It is written
/// down instead: a zero put where a struct is declared is that struct's default, which the
/// generator already understands on the <see cref="OpCode.Move"/> path - see the memory note
/// <c>il2cpp-a-zero-is-an-empty-struct</c>. So the call
/// becomes the assignment it stands for, exactly, and the marker goes with it.
/// </para>
/// <para>
/// The size has to be the destination type's own, which is what rejects the other uses of the same import -
/// clearing part of a buffer, or a run of elements - where a whole-value assignment would say something the
/// program does not.
/// </para>
/// </remarks>
public static class ClearedStruct
{
    public static void Run(MethodAnalysisContext method)
    {
        if (method.ControlFlowGraph is not { } graph)
            return;

        if (method.AppContext.Binary is not LibCpp2IL.Elf.ElfFile binary)
            return;

        foreach (var instruction in graph.Instructions)
        {
            if (instruction is not { OpCode: OpCode.Call, Operands: [ulong address, { } result, LocalVariable { Type: { IsValueType: true } held } cleared, { } fill, { } size] })
                continue;

            if (binary.ImportedFunctionAt(address) != "memset")
                continue;

            //Nought, and the whole of it. A partial clear is not an assignment of the value.
            if (Constant(fill) != 0 || Constant(size) is not { } bytes || Aapcs64.SizeOf(held) != bytes)
                continue;

            //`memset` answers with the pointer it was given; where something reads that, the call has to stay.
            if (result is LocalVariable returned && ReadAnywhere(graph, returned, instruction))
                continue;

            instruction.OpCode = OpCode.Move;
            instruction.Operands = [cleared, 0];
        }
    }

    private static bool ReadAnywhere(Graphs.ISILControlFlowGraph graph, LocalVariable value, Instruction ignoring)
    {
        foreach (var instruction in graph.Instructions)
        {
            if (ReferenceEquals(instruction, ignoring))
                continue;

            //Operand zero is where a result goes; everything after it is read.
            for (var i = 1; i < instruction.Operands.Count; i++)
                if (ReferenceEquals(instruction.Operands[i], value))
                    return true;
        }

        return false;
    }

    private static long? Constant(object operand)
    {
        try
        {
            return operand is string or LocalVariable or Register or MemoryOperand ? null : Convert.ToInt64(operand);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
