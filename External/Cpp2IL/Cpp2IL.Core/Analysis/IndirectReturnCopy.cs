using System;
using System.Collections.Generic;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// A big struct copied out of the buffer the call that made it wrote through is that call's answer.
/// </summary>
/// <remarks>
/// <para>
/// A struct larger than sixteen bytes comes back through a pointer the caller passes in <c>x8</c>, so
/// <c>Touch t = Input.GetTouch(i)</c> is a scratch slot, a call that fills it and a <c>memcpy</c> into the
/// variable:
/// </para>
/// <code>
/// Call Input.GetTouch, [v72 @ stackaddr_-D4], i
/// Call memcpy, v81, v78 @ stackaddr_-90 (UnityEngine.Touch), v72 @ stackaddr_-D4, 68
/// </code>
/// <para>
/// Nothing names the import, so what came out was <c>Input.GetTouch(0);</c> with the answer discarded and
/// <c>Touch touch = default(Touch);</c> beside it - a body that compiles whole, scores <c>full</c>, and
/// branches on a <c>phase</c> that is always zero.
/// </para>
/// <para>
/// Two earlier attempts at this family rewrote the copy into a slot-to-slot <see cref="OpCode.Move"/> and
/// both measured worse, because neither slot has a value to move: they are addresses, and the machinery to
/// copy between them does not exist. This does not copy. It sends the call's answer where the copy was going
/// to put it and takes the copy away - which needs nothing the generator cannot already do, since storing an
/// indirect return into its destination is exactly what it does with the buffer today.
/// </para>
/// <para>
/// The size is what makes it safe. <c>n</c> has to be exactly the destination type's own size, which rejects
/// the sites where the same shape carries something else - a single float in a vector register copied into a
/// <c>UIVertex</c> slot, where "both ends are typed" would have written <c>uIVertex = num</c>. And the
/// scratch slot has to be filled by that one call and read by that one copy, or the buffer is shared and
/// taking it away loses a fill.
/// </para>
/// </remarks>
public static class IndirectReturnCopy
{
    public static void Run(MethodAnalysisContext method)
    {
        if (method.ControlFlowGraph is not { } graph)
            return;

        if (method.AppContext.Binary is not LibCpp2IL.Elf.ElfFile binary)
            return;

        //Where each call that answers through a pointer put its answer. Flat rather than per block, because
        //a call ends a block and the copy is never in the same one as the call that filled the buffer.
        var filled = new Dictionary<LocalVariable, Instruction>();

        foreach (var instruction in graph.Instructions)
        {
            if (instruction is { OpCode: OpCode.Call, Operands: [MethodAnalysisContext callee, MemoryOperand { Index: null, Scale: 0, Addend: 0, Base: LocalVariable slot }, ..] }
                && Aapcs64.ReturnsIndirectly(callee))
            {
                //A slot filled twice is not one call's answer.
                filled[slot] = filled.ContainsKey(slot) ? null! : instruction;
            }
        }

        if (filled.Count == 0)
            return;

        foreach (var instruction in graph.Instructions)
        {
            if (instruction is not { OpCode: OpCode.Call, Operands: [ulong address, { } result, LocalVariable { Type: { } held } destination, LocalVariable source, { } size] })
                continue;

            if (binary.ImportedFunctionAt(address) is not ("memcpy" or "memmove"))
                continue;

            if (!filled.TryGetValue(source, out var producer) || producer is null)
                continue;

            if (producer.Operands[0] is not MethodAnalysisContext callee || callee.ReturnType?.FullName != held.FullName)
                continue;

            if (Constant(size) is not { } bytes || Aapcs64.SizeOf(held) != bytes)
                continue;

            //The buffer must be this call's and this copy's, and nothing else's.
            if (!ReadOnlyBy(graph, source, producer, instruction))
                continue;

            //And the copy's own answer - the destination pointer, which C returns - must be going nowhere,
            //or taking the call away would lose it.
            if (result is LocalVariable returned && ReadAnywhere(graph, returned, instruction))
                continue;

            producer.Operands[1] = destination;

            instruction.OpCode = OpCode.Nop;
            instruction.Operands = [];
        }
    }

    /// <summary>Whether the only places naming this slot are the call that filled it and the copy.</summary>
    private static bool ReadOnlyBy(Graphs.ISILControlFlowGraph graph, LocalVariable slot, Instruction producer, Instruction copy)
    {
        foreach (var instruction in graph.Instructions)
        {
            if (ReferenceEquals(instruction, producer) || ReferenceEquals(instruction, copy))
                continue;

            foreach (var operand in instruction.Operands)
            {
                if (ReferenceEquals(operand, slot))
                    return false;

                if (operand is MemoryOperand memory && (ReferenceEquals(memory.Base, slot) || ReferenceEquals(memory.Index, slot)))
                    return false;
            }
        }

        return true;
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
