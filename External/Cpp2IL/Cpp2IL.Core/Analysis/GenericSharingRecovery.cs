using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Gives a call back the instantiation it was written with, where the code only records the shared one.
///
/// il2cpp compiles one body for every instantiation of a generic whose arguments are references, with
/// <c>object</c> standing in for each of them - so <c>Dictionary&lt;string, Entry&gt;.TryGetValue</c> and
/// <c>Dictionary&lt;int, IState&lt;T&gt;&gt;.set_Item</c> are the same code at the same address, recorded as
/// <c>Dictionary&lt;object, object&gt;</c> and <c>Dictionary&lt;int, object&gt;</c>. A call resolved by
/// address therefore names that one, and the recovered source casts the receiver to it and every argument to
/// <c>object</c> - which says nothing the source said and does not always compile.
///
/// The object being called on still carries the real instantiation in its own type, and that is what this
/// puts back. Only an argument the shared form left as <c>object</c> is replaced, so a call is never given a
/// type the code did not have: an argument both agree on has to match.
/// </summary>
public static class GenericSharingRecovery
{
    public static bool Run(MethodAnalysisContext method)
    {
        var changed = false;
        var definitions = new Dictionary<LocalVariable, Instruction>();

        foreach (var instruction in method.ControlFlowGraph!.Instructions)
            if (instruction.Destination is LocalVariable destination)
                definitions[destination] = instruction;

        foreach (var instruction in method.ControlFlowGraph.Instructions)
        {
            if (!instruction.IsCall || instruction.Operands.Count == 0)
                continue;

            if (instruction.Operands[0] is not MethodAnalysisContext { IsStatic: false } callee)
                continue;

            if (callee.DeclaringType is not GenericInstanceTypeAnalysisContext shared)
                continue;

            var receiver = instruction.OpCode == OpCode.Call ? 2 : 1;

            if (instruction.Operands.Count <= receiver || TypeOf(instruction.Operands[receiver], definitions) is not { } held)
                continue;

            if (InstantiationOf(held, shared.GenericType) is not { } actual)
                continue;

            if (!IsSharedFormOf(shared.GenericArguments, actual.GenericArguments))
                continue;

            var declared = callee as ConcreteGenericMethodAnalysisContext;

            //The method has to be named on the definition, not on the shared instance: instantiating one
            //instance with another instance's arguments is not a thing, and gives back what it started with.
            if (OnDefinition(declared?.BaseMethodContext ?? callee, shared.GenericType) is not { } definition)
                continue;

            var replacement = new ConcreteGenericMethodAnalysisContext(
                definition,
                actual.GenericArguments,
                declared?.MethodGenericParameters ?? []);

            if (replacement.FullNameWithSignature == callee.FullNameWithSignature)
                continue;

            instruction.Operands[0] = replacement;
            changed = true;

            //The value being called on was typed from the shared call it was passed to, so it says object
            //where the instantiation says something else - and the recovered source then casts it back and
            //forth on every line. What it was read from is the better answer here too.
            if (instruction.Operands[receiver] is LocalVariable holder
                && holder.Type is GenericInstanceTypeAnalysisContext sharedHolder
                && sharedHolder.FullName != actual.FullName
                && IsSharedFormOf(sharedHolder.GenericArguments, actual.GenericArguments))
                holder.Type = actual;
        }

        return changed;
    }

    /// <summary>
    /// The method as the generic definition declares it, given one named on an instance of that definition.
    /// </summary>
    private static MethodAnalysisContext? OnDefinition(MethodAnalysisContext method, TypeAnalysisContext definition)
    {
        if (method.DeclaringType is not GenericInstanceTypeAnalysisContext)
            return method;

        return definition.Methods.FirstOrDefault(m => m.Name == method.Name && m.Parameters.Count == method.Parameters.Count);
    }

    /// <summary>
    /// The type a call's receiver operand holds. Where the value was copied out of a field, what the field
    /// is declared as is preferred: a local takes its type from the call it is passed to, so a receiver of a
    /// shared body ends up typed as the shared form and says nothing about which instantiation it is.
    /// </summary>
    private static TypeAnalysisContext? TypeOf(object operand, Dictionary<LocalVariable, Instruction> definitions)
    {
        switch (operand)
        {
            case FieldReference { Field.FieldType: { } fieldType }:
                return fieldType;

            case LocalVariable local:
                if (definitions.TryGetValue(local, out var definition)
                    && definition is { OpCode: OpCode.Move, Operands: [_, FieldReference { Field.FieldType: { } source }] })
                    return source;

                return local.Type;

            default:
                return null;
        }
    }

    /// <summary>
    /// How the given type instantiates the definition - itself if it is one of its instances, otherwise the
    /// one it inherits. A type that only implements the definition as an interface gives nothing: which of
    /// several instantiations a call went through is not decided by the receiver there.
    /// </summary>
    private static GenericInstanceTypeAnalysisContext? InstantiationOf(TypeAnalysisContext type, TypeAnalysisContext definition)
    {
        var seen = 0;

        for (TypeAnalysisContext? current = type; current != null && seen++ < 32; current = BaseOf(current))
        {
            if (current is GenericInstanceTypeAnalysisContext instance && instance.GenericType.FullName == definition.FullName)
                return instance;
        }

        return null;
    }

    private static TypeAnalysisContext? BaseOf(TypeAnalysisContext type)
        => type.BaseType ?? (type as GenericInstanceTypeAnalysisContext)?.GenericType.BaseType;

    /// <summary>
    /// Whether the first argument list is what the second looks like once every reference in it has been
    /// shared away. An argument the two disagree on that is not <c>object</c> means these are different
    /// instantiations rather than one and its shared form, and nothing is changed.
    /// </summary>
    private static bool IsSharedFormOf(List<TypeAnalysisContext> shared, List<TypeAnalysisContext> actual)
    {
        if (shared.Count != actual.Count || shared.Count == 0)
            return false;

        var replaced = false;

        for (var i = 0; i < shared.Count; i++)
        {
            if (shared[i].FullName == actual[i].FullName)
                continue;

            if (!StandsIn(shared[i], actual[i]))
                return false;

            replaced = true;
        }

        return replaced;
    }

    /// <summary>
    /// Whether one type is the stand-in il2cpp recorded a shared body against for another.
    /// </summary>
    /// <remarks>
    /// Bodies are shared between arguments that are laid out and passed alike. References all are, and
    /// <see cref="object"/> stands for every one of them. Enums are too, by the integer behind them, and there
    /// the stand-in is a type the runtime library keeps to itself - <c>System.Int32Enum</c> for an enum
    /// counted in ints, and one apiece for the other widths. They are types in il2cpp's own metadata, so a
    /// call against one reads as a real call against a real type, and nothing about it looks wrong: a
    /// <c>HashSet&lt;ECellColor&gt;</c> simply reads as a <c>HashSet&lt;System.Int32Enum&gt;</c> whose
    /// contents, and everything reached through them, are of a type the project cannot name.
    /// </remarks>
    private static bool StandsIn(TypeAnalysisContext shared, TypeAnalysisContext actual)
        => shared.FullName switch
        {
            "System.Object" => !actual.IsValueType,
            "System.SByteEnum" or "System.ByteEnum" or "System.Int16Enum" or "System.UInt16Enum"
                or "System.Int32Enum" or "System.UInt32Enum" or "System.Int64Enum" or "System.UInt64Enum"
                => IsEnum(actual),
            _ => false,
        };

    /// <summary>Whether a type is an enum, which is what the stand-ins above stand for.</summary>
    private static bool IsEnum(TypeAnalysisContext type)
        => type.IsValueType && type.BaseType?.FullName == "System.Enum";

    /// <summary>
    /// Whether the first type is what the second looks like written against a shared body's stand-ins, so
    /// that a value said to be the first is really one of the second.
    /// </summary>
    internal static bool IsStandInFor(TypeAnalysisContext held, TypeAnalysisContext real)
    {
        if (StandsIn(held, real))
            return true;

        //An array of a stand-in stands for an array of what it stands for. This is how the stand-in reaches
        //furthest: a list keeps its contents in an array, so a `List<T>` named against one hands out an
        //`Int32Enum[]` to everything that reads its storage.
        if (held is SzArrayTypeAnalysisContext { ElementType: { } standIn } && real is SzArrayTypeAnalysisContext { ElementType: { } element })
            return IsStandInFor(standIn, element);

        return held is GenericInstanceTypeAnalysisContext shared
            && real is GenericInstanceTypeAnalysisContext actual
            && shared.GenericType.FullName == actual.GenericType.FullName
            && IsSharedFormOf(shared.GenericArguments, actual.GenericArguments);
    }
}
