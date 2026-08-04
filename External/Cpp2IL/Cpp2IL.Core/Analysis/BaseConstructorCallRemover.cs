using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Drops the call to a base constructor that takes nothing, because C# writes it back.
///
/// Every constructor calls its base one, and the language puts that call in for you - which is why no source
/// file contains it. il2cpp is free to put it anywhere in the compiled constructor, and does: after the field
/// initialisers rather than before them. The decompiler can only fold a base call back into the constructor's
/// own header when it comes first, so anywhere else it is written out as <c>base..ctor()</c>, which is not
/// something C# lets you say - the statement does not compile and is commented out, leaving a line in a
/// constructor whose source had nothing there at all.
///
/// Only a base constructor taking no arguments is dropped. One with arguments decides what is passed up and
/// has to survive as <c>: base(...)</c>.
///
/// The closure a lambda captures into is the exception, and has to keep its call. Its constructor is never
/// written out - the whole class is folded away, back into the locals the lambda captured - but the decompiler
/// will only fold it after reading that constructor and finding it does nothing except call
/// <see cref="object"/>'s. A closure whose constructor has had that call taken out of it therefore stays a
/// class, and every captured local stays a field of it, which is not how any of it was written.
/// </summary>
public static class BaseConstructorCallRemover
{
    public static void Run(MethodAnalysisContext method)
    {
        if (method.Name != ".ctor" || method.DeclaringType is not { } declaringType)
            return;

        //A name the language cannot produce is one the compiler generated - the closures among them.
        var compilerGenerated = declaringType.Name.StartsWith('<');

        foreach (var instruction in method.ControlFlowGraph!.Instructions)
        {
            if (instruction.OpCode is not (OpCode.Call or OpCode.CallVoid))
                continue;

            if (instruction.Operands is not [MethodAnalysisContext { Name: ".ctor", Parameters.Count: 0 } callee, ..])
                continue;

            //The receiver has to be this object, and the callee has to belong to something this type is - a
            //constructor of some other object being called here is an allocation, not the base call.
            var receiver = instruction.OpCode == OpCode.Call ? 2 : 1;

            if (instruction.Operands.Count <= receiver || instruction.Operands[receiver] is not LocalVariable { IsThis: true })
                continue;

            //A constructor of some other type called on this object can only be the base one - a call to
            //another constructor of this same type is a `this(...)` chain, and that one has to stay.
            if (callee.DeclaringType is not { } calleeType || Name(calleeType) == Name(declaringType))
                continue;

            //The one the decompiler reads before it will fold a closure away. Nothing writes it out, so it
            //costs nothing to keep, and without it the closure is written out instead.
            if (compilerGenerated && Name(calleeType) == "System.Object")
                continue;

            instruction.OpCode = OpCode.Nop;
            instruction.Operands = [];
        }
    }

    /// <summary>The type's name without its arguments, so that a type and its instantiation are one name.</summary>
    private static string Name(TypeAnalysisContext type)
        => ((type as GenericInstanceTypeAnalysisContext)?.GenericType ?? type).FullName;
}
