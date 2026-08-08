using System.Collections.Generic;
using System.Reflection;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Puts back the <c>is</c> that a single comparison of class pointers was compiled from.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="InlinedTypeTestRecovery"/> answers for the general test, which walks the chain of base
/// classes. Against a type nothing can derive from there is nothing to walk: being of the type and being
/// exactly it are the same question, so il2cpp compares the object's class against the one it wants and
/// stops.
/// </para>
/// <code>
/// Move       table,  [0x4DCBAF0]                  // the runtime's table of built-in classes
/// CheckEqual answer, [obj], [table + 0x90]        // obj->klass == string_class
/// Select     result, answer, obj, 0               // ...which is `obj as string`
/// </code>
/// <para>
/// Neither read names anything, so both came out as unmanaged memory and took the statement with them - and
/// since the answer feeds a branch or a choice, everything that depended on it went too. 43 of these in
/// <c>Assembly-CSharp</c>, against 3 of the shape <see cref="Il2CppDefaultsTable"/> can already see: that
/// pass wants the class in a local of its own, and here the read sits straight inside the comparison.
/// </para>
/// <para>
/// Only where nothing can derive from the class. Otherwise the comparison is asking whether the object is
/// <em>exactly</em> of that type - <c>GetType() == typeof(T)</c> - and an <c>isinst</c> would say yes to a
/// subclass the original said no to. Every built-in the table names is either a value type or
/// <c>string</c>, so the ones this fires for are the ones where the two questions agree.
/// </para>
/// </remarks>
public static class ExactTypeTestRecovery
{
    private const string ObjectIsInstance = "il2cpp_vm_object_is_inst";

    public static void Run(MethodAnalysisContext method)
    {
        if (method.ControlFlowGraph is not { } graph)
            return;

        var tables = Il2CppDefaultsTable.Tables(graph);

        if (tables.Count == 0)
            return;

        foreach (var block in graph.Blocks)
            for (var i = 0; i < block.Instructions.Count; i++)
            {
                if (Match(block.Instructions[i], tables, method.AppContext) is not var (answer, tested, target, equal))
                    continue;

                var value = new LocalVariable($"instance{method.Locals.Count}", new Register(null, "ISINST"), target);
                method.Locals.Add(value);

                //The same call an un-inlined cast leaves behind, so the generator writes a real isinst and
                //the two reads are left for the collector. An equality means the object is one.
                block.Instructions[i] = new Instruction(block.Instructions[i].Index, OpCode.Call,
                    ObjectIsInstance, value, tested, target);

                block.Instructions.Insert(i + 1, new Instruction(block.Instructions[i].Index,
                    equal ? OpCode.CheckNotEqual : OpCode.CheckEqual, answer, value, 0));

                i++;
            }
    }

    /// <summary>The comparison of an object's class against a built-in one, and what it is asking.</summary>
    private static (LocalVariable Answer, LocalVariable Tested, TypeAnalysisContext Target, bool Equal)? Match(
        Instruction instruction, HashSet<LocalVariable> tables, ApplicationAnalysisContext appContext)
    {
        if (instruction.OpCode is not (OpCode.CheckEqual or OpCode.CheckNotEqual)
            || instruction.Operands.Count != 3
            || instruction.Operands[0] is not LocalVariable answer)
            return null;

        //Either way round: which side the compiler put the class on is not something to depend on.
        var tested = Klass(instruction.Operands[1]) ?? Klass(instruction.Operands[2]);
        var target = FromTable(instruction.Operands[1], tables, appContext) ?? FromTable(instruction.Operands[2], tables, appContext);

        if (tested is null || target is null)
            return null;

        //Nothing may derive from it, or being of the type and being exactly it are different questions.
        if (!target.Attributes.HasFlag(TypeAttributes.Sealed))
            return null;

        return (answer, tested, target, instruction.OpCode == OpCode.CheckEqual);
    }

    /// <summary>The object whose class this reads, if that is what it is.</summary>
    private static LocalVariable? Klass(object operand)
        => operand is MemoryOperand { Index: null, Scale: 0, Addend: 0, Base: LocalVariable held } ? held : null;

    /// <summary>The built-in class this read names, if it is a read of the table at all.</summary>
    private static TypeAnalysisContext? FromTable(object operand, HashSet<LocalVariable> tables, ApplicationAnalysisContext appContext)
        => operand is MemoryOperand { Index: null, Scale: 0, Base: LocalVariable table } read && tables.Contains(table)
            ? Il2CppDefaultsTable.SlotType(read.Addend, appContext)
            : null;
}
