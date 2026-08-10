using System.Collections.Generic;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Drops a read out of a runtime generic context that nothing wants any more.
/// </summary>
/// <remarks>
/// <para>
/// A shared generic body cannot name its own instantiation, so the <c>MethodInfo*</c> a call needs is read
/// out of the runtime generic context hanging off the class or the method:
/// </para>
/// <code>
/// Move     v340 (Il2CppMethodInfo), [v338 (Il2CppRgctx&lt;List`1&lt;SubCellRef&gt;&gt;)+70]
/// CallVoid List`1&lt;SubCellRef&gt;.Add, subCells, subCellRef
/// </code>
/// <para>
/// By the time the call has been resolved and its runtime argument trimmed away, that load feeds nothing -
/// but it is a memory read, which the collection will not take on its own, so it stayed as
/// <c>_ = "Unmanaged memory load: [...]"</c> beside a call that had already been recovered perfectly. Nineteen
/// of them in the game, eight in the ninety-six originals, and every one of those eight the last thing
/// standing between its method and being whole.
/// </para>
/// <para>
/// Only where nothing reads the value. A body whose call did not resolve still needs the pointer, and there
/// the read is the honest statement that something was not recovered.
/// </para>
/// </remarks>
public static class DeadRgctxRead
{
    public static void Run(MethodAnalysisContext method)
    {
        if (method.ControlFlowGraph is not { } graph)
            return;

        //A copy is not a use of its own accord: the lifter carries a register across a block edge with a plain
        //move, and `ProcessSeed` kept every one of these alive through exactly that -
        //`Move v342 @ X2_v4 (System.Int32), v340 @ X2_v3 (Il2CppMethodInfo)`, which nothing then reads either.
        //So reads through copies are settled to a fixpoint before anything is called live.
        HashSet<LocalVariable> read = [];
        List<(LocalVariable Into, LocalVariable From)> copies = [];

        foreach (var instruction in graph.Instructions)
        {
            if (instruction is { OpCode: OpCode.Move, Operands: [LocalVariable into, LocalVariable from] })
            {
                copies.Add((into, from));
                continue;
            }

            //Operand zero is where a value goes; everything after it is a use of one. A memory operand's own
            //base is a use, wherever it sits.
            for (var i = 0; i < instruction.Operands.Count; i++)
            {
                switch (instruction.Operands[i])
                {
                    case LocalVariable used when i > 0:
                        read.Add(used);
                        break;
                    case MemoryOperand { Base: LocalVariable through }:
                        read.Add(through);
                        break;
                    case FieldReference { Local: { } owner }:
                        read.Add(owner);
                        break;
                }
            }
        }

        for (var settling = true; settling;)
        {
            settling = false;

            foreach (var (into, from) in copies)
                if (read.Contains(into) && read.Add(from))
                    settling = true;
        }

        foreach (var instruction in graph.Instructions)
        {
            if (instruction is not { OpCode: OpCode.Move, Operands: [LocalVariable loaded, MemoryOperand { Index: null, Scale: 0 } entry] })
                continue;

            if (entry.Base is not LocalVariable { Type: RgctxTableTypeAnalysisContext or MethodRgctxTableTypeAnalysisContext })
                continue;

            if (read.Contains(loaded))
            {
                Refused(method, loaded, graph);
                continue;
            }

            instruction.OpCode = OpCode.Nop;
            instruction.Operands = [];
        }
    }

    /// <summary>
    /// Says who still reads a context entry, for one named method. Guessing at this was inert once already -
    /// see the memory note <c>il2cpp-instrument-the-lane-not-the-mnemonic</c> for why this is the cheap way.
    /// </summary>
    private static void Refused(MethodAnalysisContext method, LocalVariable loaded, Graphs.ISILControlFlowGraph graph)
    {
        if (System.Environment.GetEnvironmentVariable("RGCTX_TRACE") != "1")
            return;

        var wanted = System.Environment.GetEnvironmentVariable("RGCTX_METHOD");

        if (wanted != null && !method.Name.Contains(wanted))
            return;

        foreach (var instruction in graph.Instructions)
        {
            for (var i = 0; i < instruction.Operands.Count; i++)
            {
                var reads = instruction.Operands[i] switch
                {
                    LocalVariable used => i > 0 && ReferenceEquals(used, loaded),
                    MemoryOperand { Base: LocalVariable through } => ReferenceEquals(through, loaded),
                    FieldReference { Local: { } owner } => ReferenceEquals(owner, loaded),
                    _ => false,
                };

                if (reads)
                    System.Console.WriteLine($"RGCTX {method.Name}: {loaded.Name} read at operand {i} of {instruction.OpCode} {instruction}");
            }
        }
    }
}
