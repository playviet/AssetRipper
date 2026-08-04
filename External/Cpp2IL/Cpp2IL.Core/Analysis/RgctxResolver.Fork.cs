using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using LibCpp2IL.BinaryStructures;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// What this fork adds: the runtime generic context that belongs to a method rather than to a type, and
/// the substitution of the method's own type arguments into what that context names.
///
/// Kept apart from the file it belongs to so that the file stays as close to upstream as it can,
/// and a later version of Cpp2IL can be merged without the two sets of changes meeting.
/// </summary>
public static partial class RgctxResolver
{
    /// <summary>
    /// Puts the type an allocation makes in place of the table entry it was read from.
    ///
    /// A generic body cannot name the type it allocates by address either - <c>new Dictionary&lt;int,
    /// IState&lt;T&gt;&gt;()</c> is a different type for every T - so the allocation reads the class out of
    /// the runtime generic context. Unlike everything else read from there, the entry is not carried in a
    /// value that can be typed: it is the operand saying what to allocate, so it has to be replaced.
    ///
    /// Leaving it also kept the whole chain that led to it alive - the runtime method, its class, its table -
    /// and each of those was written out as a read of unmanaged memory.
    /// </summary>
    private static bool NameAllocatedTypes(MethodAnalysisContext method, long rgctxOffset, int pointerSize)
    {
        var changed = false;

        foreach (var instruction in method.ControlFlowGraph!.Instructions)
        {
            if (instruction.OpCode != OpCode.Newobj || instruction.Operands.Count < 2)
                continue;

            var allocated = instruction.Operands[1] switch
            {
                MemoryOperand { Index: null, Scale: 0, Base: LocalVariable { Type: RgctxTableTypeAnalysisContext table } } entry
                    when entry.Addend % pointerSize == 0
                    => ResolveEntry(table.OwnerType, (int)(entry.Addend / pointerSize)),

                LocalVariable { Type: RuntimeClassTypeAnalysisContext held } => held,

                _ => null,
            };

            if (allocated is not RuntimeClassTypeAnalysisContext { RepresentedType: { } type })
                continue;

            instruction.Operands[1] = type;
            changed = true;

            //The value the allocation produces is of that type, and the entry says it more precisely than the
            //shared body does - there every reference argument is object.
            if (instruction.Operands[0] is LocalVariable destination && !SameType(destination.Type ?? type, type))
                destination.Type = type;
        }

        return changed;
    }

    /// <summary>
    /// Whether two contexts name the same type. An inflated entry is worked out fresh each time it is
    /// asked for, so two that stand for the same type are never the same object - and a resolution that
    /// compared objects would report a change on every pass, leaving the fixpoint above never settling.
    /// </summary>
    private static bool SameType(TypeAnalysisContext left, TypeAnalysisContext right)
        => ReferenceEquals(left, right) || left.FullName == right.FullName;

    /// <summary>
    /// Whether two contexts name the same method. An instantiation resolved from an entry is built fresh
    /// each pass, for the same reason and with the same consequence as a type: comparing objects would
    /// report a change every time round and the fixpoint above would never settle.
    /// </summary>
    private static bool SameMethod(MethodAnalysisContext left, MethodAnalysisContext right)
        => ReferenceEquals(left, right) || left.FullNameWithSignature == right.FullNameWithSignature;

    /// <summary>
    /// The type an entry of a method's runtime generic context holds. The entries are recorded once, on the
    /// method definition, and written in terms of its parameters - so the instantiation the value was loaded
    /// from is what says which types they stand for.
    /// </summary>
    private static TypeAnalysisContext? ResolveMethodEntry(MethodAnalysisContext method, int index)
    {
        var concrete = method as ConcreteGenericMethodAnalysisContext;

        if ((concrete?.BaseMethodContext ?? method).Definition is not { } definition)
            return null;

        var entries = definition.RgctXs;

        if (index < 0 || index >= entries.Length)
            return null;

        var entry = entries[index];

        if (entry.type is Il2CppRGCTXDataType.IL2CPP_RGCTX_DATA_METHOD)
            return ResolveMethodOfEntry(entry, method.DeclaringType);

        if (entry.type is not (Il2CppRGCTXDataType.IL2CPP_RGCTX_DATA_CLASS or Il2CppRGCTXDataType.IL2CPP_RGCTX_DATA_TYPE))
            return null;

        var entryType = method.AppContext.ResolveIl2CppType(entry.Type);

        if (Substitute(entryType, concrete) is not { } substituted)
            return null;

        return new RuntimeClassTypeAnalysisContext(substituted, substituted.DeclaringAssembly);
    }

    /// <summary>
    /// The runtime method an entry holds, named at the instantiation it was read from.
    ///
    /// A generic type's body cannot call a method the type arguments decide by address, because one body is
    /// shared between every instantiation - the base constructor of <c>Foo&lt;T&gt;</c> is a different method
    /// for every T. It is reached through the runtime generic context instead, and what is left in the code
    /// is a load from a table and a call through the pointer at the front of what it found. The entry says
    /// which method that is, and the parameters it is written against stand for the arguments the type has
    /// here.
    /// </summary>
    private static TypeAnalysisContext? ResolveMethodOfEntry(Il2CppRGCTXDefinition entry, TypeAnalysisContext? instance)
    {
        if (instance?.DeclaringAssembly is not { } assembly)
            return null;

        var appContext = instance.AppContext;

        if (entry.MethodSpec is not { MethodDefinition: { } definition } spec)
            return null;

        if (appContext.ResolveContextForMethod(definition) is not { } declared)
            return null;

        var typeArguments = Inflate(spec.GenericClassParams, instance, appContext);
        var methodArguments = Inflate(spec.GenericMethodParams, instance, appContext);

        if (typeArguments == null || methodArguments == null)
            return null;

        var method = typeArguments.Count == 0 && methodArguments.Count == 0
            ? declared
            : new ConcreteGenericMethodAnalysisContext(declared, typeArguments, methodArguments);

        return new RuntimeMethodInfoAnalysisContext(method, assembly);
    }

    /// <summary>
    /// The types an entry's argument list stands for here, or null if any of them cannot be worked out. An
    /// entry belongs to the definition, so its arguments are written in terms of the definition's parameters.
    /// </summary>
    private static List<TypeAnalysisContext>? Inflate(Il2CppType[] arguments, TypeAnalysisContext instance, ApplicationAnalysisContext appContext)
    {
        var resolved = new List<TypeAnalysisContext>(arguments.Length);

        foreach (var argument in arguments)
        {
            if (Inflate(appContext.ResolveIl2CppType(argument), instance) is not { } value)
                return null;

            resolved.Add(value);
        }

        return resolved;
    }

    /// <summary>
    /// Puts a method instantiation's type arguments in place of the parameters an entry is written against.
    /// An entry can name a parameter of the declaring type or one of the method's own, and which list it
    /// belongs to is what the parameter's own type enum says.
    /// </summary>
    private static TypeAnalysisContext? Substitute(TypeAnalysisContext? type, ConcreteGenericMethodAnalysisContext? concrete)
    {
        switch (type)
        {
            case null:
                return null;

            case GenericParameterTypeAnalysisContext { Index: var index } parameter:
                var arguments = parameter.Type == Il2CppTypeEnum.IL2CPP_TYPE_MVAR
                    ? concrete?.MethodGenericParameters
                    : concrete?.TypeGenericParameters;

                //There is no instantiation when the body being analysed is the open one - the single body every
                //instantiation shares, and the one the recovered source is written from. Refusing the entry
                //there loses everything the context holds, which for a generic method is the interface it calls
                //through, the class it allocates and the method it invokes. The parameter standing for itself is
                //both true and exactly what that source has to say: in `GetRandom<T>`, an entry written
                //`IEnumerable<!!0>` is `IEnumerable<T>`.
                if (arguments is null)
                    return parameter;

                return index >= 0 && index < arguments.Count ? arguments[index] : null;

            case GenericInstanceTypeAnalysisContext generic:
                var resolved = new List<TypeAnalysisContext>(generic.GenericArguments.Count);

                foreach (var argument in generic.GenericArguments)
                {
                    if (Substitute(argument, concrete) is not { } value)
                        return null;

                    resolved.Add(value);
                }

                return new GenericInstanceTypeAnalysisContext(generic.GenericType, resolved);

            default:
                //A closed type stands for itself.
                return type;
        }
    }
}
