using System.Linq;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Reads a compiler-generated object's own fields off <c>this</c>, wherever the compiled code kept it.
/// </summary>
/// <remarks>
/// An iterator's state machine and a lambda's closure are classes nothing else can reach: the compiler makes
/// them, names them something the language cannot say, and hands them to no one. Inside one of their own
/// methods there is exactly one of them - <c>this</c> - unless the method makes another, which none of them
/// does.
///
/// That matters because the compiled code does not keep <c>this</c> in one place. It is put in a register that
/// survives a call, read back after each one, and where the register is wanted for something else it goes
/// somewhere else again; every one of those is a value of the state machine's type that is not the parameter,
/// and a register reused for a loop counter can even merge the two, leaving a value that reads as both. What
/// the fields are read off then depends on which copy the code happened to be holding.
///
/// A decompiler will only put <c>yield return</c> back when the state and the current value are read off
/// <c>this</c> itself: <c>copy.current = x</c> is, as far as it can tell, some other object's field, and one
/// such write costs the whole method - it is written out as the state machine instead of as what was written.
/// Since the type has only one instance here, saying so costs nothing and is not a guess.
///
/// Only what a field is read from is redirected. A copy used as a number is a copy of something else that
/// happened to share the register, and nothing here can say what it should have been.
/// </remarks>
public static class CompilerGeneratedSelfRecovery
{
    public static void Run(MethodAnalysisContext method)
    {
        if (method.DeclaringType is not { } declaringType || !declaringType.Name.StartsWith('<'))
            return;

        if (method.ParameterLocals.FirstOrDefault(p => p.IsThis) is not { } self)
            return;

        //Another one of these could be reached through an argument, or made here - neither of which any
        //compiler-generated class actually does, but both of which would make "there is only one" untrue.
        if (method.ParameterLocals.Any(p => !p.IsThis && p.Type is { } given && Same(given, declaringType)))
            return;

        foreach (var instruction in method.ControlFlowGraph!.Instructions)
            if (instruction.OpCode == OpCode.Newobj
                && instruction.Destination is LocalVariable { Type: { } allocated }
                && Same(allocated, declaringType))
                return;

        foreach (var instruction in method.ControlFlowGraph.Instructions)
            foreach (var operand in instruction.Operands)
                if (operand is FieldReference { Local: { } local } field
                    && !ReferenceEquals(local, self)
                    && local.Type is { } type
                    && Same(type, declaringType))
                    field.Local = self;
    }

    /// <summary>
    /// Whether two contexts name the same type. Compared by name: an instantiation is worked out fresh each
    /// time it is asked for, so two standing for one type are never the same object.
    /// </summary>
    private static bool Same(TypeAnalysisContext left, TypeAnalysisContext right)
        => ReferenceEquals(left, right) || left.FullName == right.FullName;
}
