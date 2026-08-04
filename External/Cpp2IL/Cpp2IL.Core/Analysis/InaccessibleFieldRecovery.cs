using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Reads and writes a field the project cannot name through the property that stands for it.
///
/// il2cpp inlines a property accessor of another assembly into its caller, so what is left is a direct
/// access to the field behind it - and that field is private there. The statement is true but cannot be
/// written down: `button.m_OnClick.AddListener(...)` does not compile, and the line is lost even though
/// `button.onClick` is the same thing and is public.
///
/// A property is only used where it is unambiguous: it has to be declared on the same type, hold the same
/// type as the field, and be named the field's name without the `m_` or `_` that marks it as backing. That
/// is the convention every Unity assembly follows, and the type has to agree as well, so a property that
/// merely has a similar name is not taken.
/// </summary>
public static class InaccessibleFieldRecovery
{
    public static void Run(MethodAnalysisContext method)
    {
        foreach (var block in method.ControlFlowGraph!.Blocks)
        {
            for (var i = 0; i < block.Instructions.Count; i++)
            {
                var instruction = block.Instructions[i];
                var assigns = WritesOperandZero(instruction.OpCode);

                //A write becomes the setter, which takes the object and the value and gives nothing back.
                if (assigns && instruction.Operands.Count == 2
                    && instruction.Operands[0] is FieldReference { Field: { IsStatic: false } written } target
                    && !IsVisible(written, method) && Accessor(written, written: true) is { } setter)
                {
                    instruction.OpCode = OpCode.CallVoid;
                    instruction.Operands = [setter, target.Local, instruction.Operands[1]];
                    continue;
                }

                for (var operand = assigns ? 1 : 0; operand < instruction.Operands.Count; operand++)
                {
                    if (instruction.Operands[operand] is not FieldReference { Field: { IsStatic: false } field } reference)
                        continue;

                    if (IsVisible(field, method) || Accessor(field, written: false) is not { } getter)
                        continue;

                    //A property is not something an operand can be, so the call goes in front of the
                    //instruction that wanted the value and the value takes its place.
                    var value = new LocalVariable($"property{method.Locals.Count}", new Register(null, "PROPERTY"), field.FieldType);
                    method.Locals.Add(value);

                    block.Instructions.Insert(i, new Instruction(instruction.Index, OpCode.Call, getter, value, reference.Local));
                    instruction.Operands[operand] = value;
                    i++;
                }
            }
        }
    }

    /// <summary>The property accessor that stands for the field, if one plainly does.</summary>
    private static MethodAnalysisContext? Accessor(FieldAnalysisContext field, bool written)
    {
        if (field.DeclaringType is not { } declaring || Named(field.Name) is not { } name)
            return null;

        var wanted = (written ? "set_" : "get_") + name;

        return declaring.Methods.FirstOrDefault(m => !m.IsStatic && m.Name == wanted
            && (written
                ? m.Parameters.Count == 1 && m.Parameters[0].ParameterType.FullName == field.FieldType.FullName
                : m.Parameters.Count == 0 && m.ReturnType.FullName == field.FieldType.FullName));
    }

    /// <summary>The property name a backing field's name is made from, or null if it is not one.</summary>
    private static string? Named(string? field)
    {
        if (field == null)
            return null;

        //The name a compiler gives an auto-property's field is already handled where the property is found
        //by its own accessors; this is the hand-written convention.
        var bare = field.StartsWith("m_") ? field[2..] : field.StartsWith('_') ? field[1..] : null;

        return string.IsNullOrEmpty(bare) ? null : char.ToLowerInvariant(bare[0]) + bare[1..];
    }

    private static bool IsVisible(FieldAnalysisContext field, MethodAnalysisContext method)
    {
        var declaringAssembly = field.DeclaringType?.DeclaringAssembly;

        if (declaringAssembly is null || ReferenceEquals(declaringAssembly, method.DeclaringType?.DeclaringAssembly))
            return true;

        return (field.Attributes & FieldAttributes.FieldAccessMask) == FieldAttributes.Public;
    }

    private static bool WritesOperandZero(OpCode opCode)
        => opCode is OpCode.Move or OpCode.Phi or OpCode.Add or OpCode.Subtract or OpCode.Multiply
            or OpCode.Divide or OpCode.ShiftLeft or OpCode.ShiftRight or OpCode.And or OpCode.Or
            or OpCode.Xor or OpCode.Not or OpCode.Negate or OpCode.Newobj or OpCode.Select
            or (>= OpCode.CheckEqual and <= OpCode.CheckLessOrEqual);
}
