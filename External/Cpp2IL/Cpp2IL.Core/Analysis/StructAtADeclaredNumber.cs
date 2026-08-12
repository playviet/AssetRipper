using System.Collections.Generic;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Follows a struct down to the member a declaration says was being read.
/// </summary>
/// <remarks>
/// <para>
/// The same defect <see cref="StructInArithmetic"/> fixes, in the two other places it turns up. A struct in a
/// field lies where it is rather than being pointed at, so a read of its first member is a read at distance
/// nought - which matches the outer field exactly, and the resolver stops there. <c>input.x</c> comes back as
/// <c>input</c>, while <c>input.y</c>, being at distance four, comes back right. <c>Joystick</c> has one of
/// each and says so plainly:
/// </para>
/// <code>
/// public float Vertical   =&gt; input.y;          // recovered
/// public float Horizontal =&gt; (float)input;      // a Vector2 cast to float - the whole property is lost
/// </code>
/// <para>
/// <b>What disambiguates it here is the declaration.</b> That pass uses "the operand beside it is a plain
/// number", because an addition is also how an address is computed and nothing else tells the two apart. A
/// call and a return need no such inference: the parameter and the return type say outright what has to be
/// there, and a struct is not it. So the rule is narrower than the arithmetic one rather than broader - it
/// asks for an exact match, and declines every read that does not land on the declared type.
/// </para>
/// <para>
/// Reads only, and only where the walk ends on the declared type. A struct passed where a struct is declared
/// means what it says; a struct passed where a float is declared was a float all along.
/// </para>
/// </remarks>
public static class StructAtADeclaredNumber
{
    public static bool Run(MethodAnalysisContext method)
    {
        if (method.ControlFlowGraph is not { } graph)
            return false;

        var changed = false;

        foreach (var instruction in graph.Instructions)
        {
            switch (instruction.OpCode)
            {
                case OpCode.Call:
                case OpCode.CallVoid:
                    if (instruction.Operands.Count == 0 || instruction.Operands[0] is not MethodAnalysisContext callee)
                        continue;

                    //Past the callee, the register a result comes back in, and the receiver where there is
                    //one. The receiver itself is left alone: calling a method on a struct is ordinary.
                    var first = (instruction.OpCode == OpCode.Call ? 2 : 1) + (callee.IsStatic ? 0 : 1);

                    for (var i = 0; i < callee.Parameters.Count && first + i < instruction.Operands.Count; i++)
                        changed |= Narrow(instruction, first + i, callee.Parameters[i].ParameterType);

                    continue;

                case OpCode.Return:
                    if (instruction.Operands.Count == 1)
                        changed |= Narrow(instruction, 0, method.ReturnType);

                    continue;

                //A copy into a place that holds a number. The destination is the declaration here, and it is
                //one that may be believed: a value-type destination is the direction
                //`il2cpp-a-move-has-no-type-of-its-own` sanctions, because a register named as a struct may
                //be anything while a register named as a `Single` is one. This is where the defect actually
                //lives - `Joystick.Horizontal` reaches `SnapFloat` through
                //`Move returnVal1 (Single), this.input (Vector2)`, and by the call itself the argument is
                //already the plain local, so nothing at the call site can see it.
                case OpCode.Move:
                    //A field is a place that declares what it holds just as a local does, and the same read
                    //reaches both: `currentCode = identifier` and `identifier == currentCode` are the two
                    //statements of `ApplyFontBasedOnLocale`, and narrowing only the local half left the other
                    //assigning a `LocaleIdentifier` to a `string`.
                    if (instruction.Operands.Count == 2)
                    {
                        changed |= Narrow(instruction, 1, instruction.Operands[0] switch
                        {
                            LocalVariable into => into.Type,
                            FieldReference place => place.Field.FieldType,
                            _ => null,
                        });
                    }

                    continue;
            }
        }

        return changed;
    }

    /// <summary>Replaces a struct read with the member of it the declaration asks for, where one matches.</summary>
    private static bool Narrow(Instruction instruction, int operand, TypeAnalysisContext? declared)
    {
        //Any declared type, not only a number. The gate was `IsNumber` because the shape was first seen on a
        //float, but nothing about the reasoning is arithmetic: a place declares what it holds, and an
        //eight-byte load landing on a struct field's own offset is the member at its front whatever that
        //member happens to be. `Locale.m_Identifier` is a sixteen-byte `LocaleIdentifier` whose first member
        //is a `string`, so `ApplyFontBasedOnLocale` compared a `LocaleIdentifier` to a `string` and assigned
        //one to the other - both bodies commented away entirely.
        //
        //Not `object`, which is what a place declares when it knows nothing and would make every struct read
        //its own first member. The exactness that keeps this safe is the `FullName` match below, which is
        //untouched.
        if (declared == null || declared.FullName is "System.Object" or "System.Void")
            return false;

        //Exactly the declared type, not merely something narrower than the struct. Following a struct down to
        //whatever happens to sit at its front and hoping is how the arithmetic version of this cost three
        //whole methods before it was narrowed; here the answer is checkable, so it is checked.
        switch (instruction.Operands[operand])
        {
            case FieldReference read when read.Field.FieldType.FullName != declared.FullName:
                if (StructInArithmetic.Inside(read) is not { } member
                    || member.Path[^1].FieldType.FullName != declared.FullName)
                    return false;

                instruction.Operands[operand] = member;
                return true;

            //The struct never reached a field of its own - it was copied into a register and handed straight
            //over, which is what `SnapFloat(input.x, ...)` compiles to. The local *is* the struct, so the
            //member is named on the local at distance nought.
            case LocalVariable { Type: { IsValueType: true } held } local
                when held.FullName != declared.FullName && !StructInArithmetic.IsNumber(held):
                if (PathTo(held, declared) is not { } path)
                    return false;

                instruction.Operands[operand] = path.Count == 1
                    ? new FieldReference(path[0], local, 0)
                    : new NestedFieldReference([.. path], local, 0);

                return true;

            default:
                return false;
        }
    }

    /// <summary>The chain of front members leading from a struct to a value of the declared type, if one does.</summary>
    /// <remarks>
    /// Bounded rather than trusting the chain to end, and it must land <b>on</b> the declared type: a struct
    /// whose front member is another struct is followed, and one that never arrives is left alone.
    /// </remarks>
    private static List<FieldAnalysisContext>? PathTo(TypeAnalysisContext structure, TypeAnalysisContext declared)
    {
        var path = new List<FieldAnalysisContext>();

        for (var type = structure; path.Count < 8; )
        {
            if (StructInArithmetic.Front(type) is not { } front)
                return null;

            path.Add(front);

            if (front.FieldType.FullName == declared.FullName)
                return path;

            type = front.FieldType;
        }

        return null;
    }
}
