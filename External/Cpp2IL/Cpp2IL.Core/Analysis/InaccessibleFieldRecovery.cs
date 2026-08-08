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
                    //A chain of fields cannot be reached with one accessor: the getter of the field at the
                    //end takes the field before it, not the object at the start. `enumerator._current.key`
                    //has to become `enumerator.Current.Key`, one step at a time, or the property is called
                    //on the wrong thing and the statement is lost with it.
                    if (instruction.Operands[operand] is NestedFieldReference { Path.Length: > 1 } chain)
                    {
                        object source = chain.Local;
                        var inserted = 0;

                        foreach (var step in chain.Path)
                        {
                            var holder = new LocalVariable($"step{method.Locals.Count}", new Register(null, "STEP"), step.FieldType);
                            method.Locals.Add(holder);

                            var reached = !IsVisible(step, method) && Accessor(step, written: false) is { } stepGetter
                                ? new Instruction(instruction.Index, OpCode.Call, stepGetter, holder, source)
                                : new Instruction(instruction.Index, OpCode.Move, holder,
                                    source is LocalVariable holding ? new FieldReference(step, holding, 0) : source);

                            block.Instructions.Insert(i + inserted, reached);
                            inserted++;
                            source = holder;
                        }

                        instruction.Operands[operand] = source;
                        i += inserted;
                        continue;
                    }

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

        //An instantiation carries no methods of its own, so the accessor is declared by the type it
        //instantiates and has to be named through the arguments again - otherwise `List<int>.Enumerator`
        //looks like it has no `get_Current` at all, and the field stays private and unwritable.
        var instance = declaring as GenericInstanceTypeAnalysisContext;
        var owner = instance?.GenericType ?? declaring;

        foreach (var candidate in Names(name))
        {
            var wanted = (written ? "set_" : "get_") + candidate;

            if (owner.Methods.FirstOrDefault(m => !m.IsStatic && m.Name == wanted
                    && (written
                        ? m.Parameters.Count == 1
                        : m.Parameters.Count == 0)) is not { } accessor)
                continue;

            var named = instance == null
                ? accessor
                : new ConcreteGenericMethodAnalysisContext(accessor, instance.GenericArguments, []);

            //The type still has to agree, and for an instantiation that is only true once the arguments are
            //in - which is why the check is here rather than in the search above.
            var matches = written
                ? named.Parameters.Count == 1 && named.Parameters[0].ParameterType.FullName == field.FieldType.FullName
                : named.ReturnType.FullName == field.FieldType.FullName;

            if (matches)
                return named;
        }

        return written ? null : ForwardingGetter(field, declaring);
    }

    /// <summary>
    /// The plain method that stands for the field, where a type forwards to it by hand rather than through a
    /// property.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>LevelManager</c> keeps <c>private ELevelState _currentState</c> and hands it out through
    /// <c>public ELevelState GetCurrentState()</c>. il2cpp inlines that into every caller, so what arrives
    /// here is a read of the private field from another type - true, and unwritable. The property search
    /// above finds nothing, because there is no property: the accessor is an ordinary method.
    /// </para>
    /// <para>
    /// <b>Reads only.</b> A <c>SetX</c> is not the mirror of this: <c>LevelManager.SetState</c> assigns the
    /// field <i>and</i> raises an event, and calling it where the code assigned a field would put back
    /// something the program never did. A getter that did more than return the field would have been inlined
    /// as more than a field read, and then this is not the shape that reached us.
    /// </para>
    /// </remarks>
    private static MethodAnalysisContext? ForwardingGetter(FieldAnalysisContext field, TypeAnalysisContext declaring)
    {
        if (Named(field.Name) is not { } name)
            return null;

        foreach (var candidate in Names(name))
        {
            var wanted = "Get" + char.ToUpperInvariant(candidate[0]) + candidate[1..];

            if (declaring.Methods.FirstOrDefault(m => !m.IsStatic && m.Name == wanted
                    && m.Parameters.Count == 0
                    && m.ReturnType.FullName == field.FieldType.FullName
                    && (m.Attributes & MethodAttributes.MemberAccessMask) is MethodAttributes.Public
                        or MethodAttributes.Assembly or MethodAttributes.FamORAssem) is { } getter)
                return getter;
        }

        return null;
    }

    /// <summary>
    /// The names a property standing for this field could go by, best first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two conventions, and the runtime uses one while Unity uses the other. Unity names the property exactly
    /// as the field without its prefix and keeps the leading letter small - <c>m_OnClick</c> is
    /// <c>onClick</c> - so that is tried first and everything that worked before still does. The class
    /// libraries capitalise, which is why <c>System.String</c> was still being named by
    /// <c>_stringLength</c>: the property is <c>Length</c>, and <c>get_length</c> matches nothing.
    /// </para>
    /// <para>
    /// And the field often carries a word the property does not, because a field says what it is inside the
    /// type while a property says what it is to a caller - <c>_stringLength</c> against <c>Length</c>. So each
    /// camelCase word-suffix is tried too. Only at a word boundary, so <c>Length</c> is a candidate for
    /// <c>_stringLength</c> and <c>ength</c> is not; and the type of the property still has to match the type
    /// of the field, which is what keeps a shorter name from matching something unrelated.
    /// </para>
    /// </remarks>
    private static IEnumerable<string> Names(string bare)
    {
        yield return char.ToLowerInvariant(bare[0]) + bare[1..];

        if (char.IsLower(bare[0]))
            yield return char.ToUpperInvariant(bare[0]) + bare[1..];

        for (var i = 1; i < bare.Length; i++)
        {
            if (!char.IsUpper(bare[i]) || char.IsUpper(bare[i - 1]))
                continue;

            yield return bare[i..];
            yield return char.ToLowerInvariant(bare[i]) + bare[(i + 1)..];
        }
    }

    /// <summary>The property name a backing field's name is made from, or null if it is not one.</summary>
    private static string? Named(string? field)
    {
        if (field == null)
            return null;

        //The name a compiler gives an auto-property's field is already handled where the property is found
        //by its own accessors; this is the hand-written convention.
        //A field written with no prefix at all - `KeyValuePair<K, V>.key`, `.value` - still has a property
        //with the same name in a different case, and where the field is unreachable that property is the
        //only way to say it. The compiler's own backing fields are left out: they are found by their
        //accessors instead, and their names are not a convention anybody wrote.
        var bare = field.StartsWith("m_") ? field[2..]
            : field.StartsWith('_') ? field[1..]
            : field.Contains("_003C") || field.Contains('<') ? null
            : field;

        return string.IsNullOrEmpty(bare) ? null : bare;
    }

    /// <summary>Whether the method could name this field if it were written out as source.</summary>
    /// <remarks>
    /// This used to answer by assembly alone - anything in the same assembly was taken to be reachable - and
    /// that is not what the language says. <c>private</c> is per <b>type</b>, so
    /// <c>TimersManager</c> cannot name <c>LevelManager._currentState</c> even though the two are compiled
    /// together. il2cpp had inlined <c>LevelManager.GetCurrentState()</c> into the caller, so that is exactly
    /// the shape that arrives here, and the pass was declining to act on all of it.
    /// </remarks>
    private static bool IsVisible(FieldAnalysisContext field, MethodAnalysisContext method)
    {
        if (field.DeclaringType is not { } declaring || method.DeclaringType is not { } from)
            return true;

        //Everything of its own is reachable, and a type nested in another may name that other's privates and
        //the other way round - so it is enough that either encloses the other.
        if (Encloses(declaring, from) || Encloses(from, declaring))
            return true;

        var sameAssembly = ReferenceEquals(declaring.DeclaringAssembly, from.DeclaringAssembly);

        return (field.Attributes & FieldAttributes.FieldAccessMask) switch
        {
            FieldAttributes.Public => true,
            FieldAttributes.Assembly => sameAssembly,
            FieldAttributes.Family => Inherits(from, declaring),
            FieldAttributes.FamORAssem => sameAssembly || Inherits(from, declaring),
            FieldAttributes.FamANDAssem => sameAssembly && Inherits(from, declaring),
            _ => false,
        };
    }

    /// <summary>Whether <paramref name="inner"/> is <paramref name="outer"/> or is nested inside it.</summary>
    private static bool Encloses(TypeAnalysisContext outer, TypeAnalysisContext? inner)
    {
        for (var type = inner; type != null; type = type.DeclaringType)
            if (ReferenceEquals(type, outer))
                return true;

        return false;
    }

    private static bool Inherits(TypeAnalysisContext type, TypeAnalysisContext ancestor)
    {
        //Bounded rather than trusting the chain to end: a base pointer that resolved oddly must not hang the
        //whole export, and no real hierarchy in a game is anywhere near this deep.
        var walked = type;

        for (var steps = 0; walked != null && steps < 64; steps++, walked = walked.BaseType)
            if (ReferenceEquals(walked, ancestor))
                return true;

        return false;
    }

    private static bool WritesOperandZero(OpCode opCode)
        => opCode is OpCode.Move or OpCode.Phi or OpCode.Add or OpCode.Subtract or OpCode.Multiply
            or OpCode.Divide or OpCode.ShiftLeft or OpCode.ShiftRight or OpCode.And or OpCode.Or
            or OpCode.Xor or OpCode.Not or OpCode.Negate or OpCode.Newobj or OpCode.Select
            or (>= OpCode.CheckEqual and <= OpCode.CheckLessOrEqual);
}
