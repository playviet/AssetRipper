using System.Collections.Generic;
using System.Reflection;
using System.Linq;
using Cpp2IL.Core.Extensions;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.Il2CppApiFunctions;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using LibCpp2IL;

namespace Cpp2IL.Core.Analysis;

public static partial class MetadataResolver
{
    public static void ResolveAll(MethodAnalysisContext method)
    {
        ResolveCalls(method);
        ResolveGetter(method);
        ResolveMetadataUsages(method);
    }

    /// <summary>
    /// Resolves <c>Move local, [absoluteAddress]</c> loads of IL2CPP metadata-usage globals into a
    /// strongly-typed operand: a string literal, a <see cref="TypeAnalysisContext"/> (an Il2CppType*/
    /// Il2CppClass* usage) or, for a MethodInfo* usage, a <see cref="RuntimeMethodInfoAnalysisContext"/>
    /// naming the method it refers to (also used to type the local - see <see cref="LocalVariables"/>).
    /// </summary>
    private static void ResolveMetadataUsages(MethodAnalysisContext method)
    {
        var libContext = method.AppContext.LibCpp2IlContext;

        //Locals that were loaded from a global offset table slot, and so now hold the usage rather than its address.
        var resolved = new HashSet<LocalVariable>();

        foreach (var instruction in method.ControlFlowGraph!.Instructions)
        {
            if (instruction.OpCode != OpCode.Move)
                continue;

            // Only an absolute-address load [addr] (no base/index/scale) can be a metadata-usage global.
            if (instruction.Operands[0] is not LocalVariable
                || instruction.Operands[1] is not MemoryOperand { Base: null, Index: null, Scale: 0 } memory)
                continue;

            //Position-independent code names a global through a slot in the global offset table, so the
            //usage is at what the slot holds rather than at the slot. See MetadataResolver.Fork.
            var address = ThroughGlobalOffsetTable(method, (ulong)memory.Addend);

            // String literal.
            var stringLiteral = libContext.GetLiteralByAddress(address);
            if (stringLiteral != null)
            {
                instruction.Operands[1] = stringLiteral;
                resolved.Add((LocalVariable)instruction.Operands[0]);
                continue;
            }

            // Type metadata usage (Il2CppType* / Il2CppClass*).
            if (method.DeclaringType is { } declaringType)
            {
                var typeGlobal = libContext.GetTypeGlobalByAddress(address);
                if (typeGlobal != null)
                {
                    instruction.Operands[1] = declaringType.AppContext.ResolveIl2CppType(typeGlobal);
                    resolved.Add((LocalVariable)instruction.Operands[0]);
                    continue;
                }
            }

            // Method metadata usage (MethodInfo*). On metadata v27+ GetMethodGlobalByAddress can return
            // any global, so confirm it is actually a method before resolving - the resolver's switch
            // throws on other usage kinds.
            var methodUsage = libContext.GetMethodGlobalByAddress(address);
            if (methodUsage?.Type is MetadataUsageType.MethodDef or MetadataUsageType.MethodRef
                && method.AppContext.ResolveContextForMethod(methodUsage) is { DeclaringType: { } methodDeclaringType } methodContext)
            {
                instruction.Operands[1] = new RuntimeMethodInfoAnalysisContext(methodContext, methodDeclaringType.DeclaringAssembly);
                resolved.Add((LocalVariable)instruction.Operands[0]);
            }
        }

        PropagateResolved(method, resolved);
        CollapseUsageDereferences(method, resolved);
    }

    /// <summary>
    /// Replaces every <c>[base + addend]</c> memory operand whose base is a typed local with a
    /// <see cref="FieldReference"/> to the field at that offset. Returns whether any operand was
    /// resolved this pass, so the type/field fixpoint can detect convergence: as more bases become
    /// typed (a field load types its result, which is the base of the next load), more offsets
    /// resolve, so this is re-run until it stops finding new fields.
    /// </summary>
    public static bool ResolveFieldOffsets(MethodAnalysisContext method)
    {
        var changed = false;

        foreach (var instruction in method.ControlFlowGraph!.Instructions)
        {
            for (var i = 0; i < instruction.Operands.Count; i++)
            {
                var operand = instruction.Operands[i];

                if (operand is not MemoryOperand memory)
                    continue;

                // Has to be [base (local) + addend (field offset)]
                if (memory.Index != null || memory.Scale != 0)
                    continue;

                if (memory.Base is not LocalVariable local || local?.Type == null)
                    continue;

                // check if static field access
                var staticOwner = (local.Type as StaticFieldStorageTypeAnalysisContext)?.OwnerType;

                // a generic instance keeps its members on the definition, so look there for the statics.
                var candidates = staticOwner == null
                    ? local.Type.Fields
                    : ((staticOwner as GenericInstanceTypeAnalysisContext)?.GenericType ?? staticOwner).Fields;

                var field = candidates.FirstOrDefault(f => f.IsStatic == (staticOwner != null) && HasStorage(f) && f.BackingData?.FieldOffset == memory.Addend);

                //A generic type's statics have no recorded offsets either, and its storage begins at nothing.
                if (field == null && staticOwner != null)
                    field = FieldOfOpenGeneric(staticOwner, memory.Addend, method, statics: true);

                //A generic type records no offsets of its own, so a body inside one - the property accessors
                //and anything else compiled against the open definition - finds nothing at the offset it reads.
                //Where such a type derives straight from object its fields begin at the object header and are
                //laid out from there, which is the layout rule rather than a guess.
                if (field == null && staticOwner == null)
                    field = FieldOfOpenGeneric(local.Type, memory.Addend, method);

                //A field a type inherits is still one of its fields, and is laid out at an offset the base type
                //decided, so looking only at the type's own declarations misses every one of them. A state machine
                //reading `context` off its base was the case that showed this up.
                if (field == null && staticOwner == null)
                {
                    for (var baseType = local.Type.BaseType; baseType is not null && field == null; baseType = baseType.BaseType)
                    {
                        var declaring = (baseType as GenericInstanceTypeAnalysisContext)?.GenericType ?? baseType;
                        field = declaring.Fields.FirstOrDefault(f => !f.IsStatic && f.BackingData?.FieldOffset == memory.Addend);

                        //A generic base records no offsets either, and the layout of one can be walked - but
                        //only where the walk is right. Measured over the game it named more fields wrongly
                        //than rightly once the chain above the base was itself generic, so the walk is not
                        //used here: what it would take is the size of every type in the chain, and a generic
                        //one has none recorded.
                        if (field == null && declaring.BaseType is null or { FullName: "System.Object" })
                        {
                            var header = method.AppContext.Binary.is32Bit ? 8 : 0x10;
                            field = declaring.Fields.FirstOrDefault(f => !f.IsStatic && f.BackingData?.FieldOffset + header == memory.Addend);
                        }

                        //An open generic base declares the field, but naming it from here has to go through the
                        //instantiation, or the field's type is a type parameter nothing can resolve.
                        if (field != null && baseType is GenericInstanceTypeAnalysisContext genericBase)
                            field = new ConcreteGenericFieldAnalysisContext(field, genericBase);
                    }

                    //A generic base records every offset as zero, so the loop above can only ever match the
                    //first field of one. The rest are found by walking the layout, where it can be closed.
                    field ??= FieldOfGenericBase(local.Type, memory.Addend, method);

                    //And a struct reached through its address. A distance into one is measured from the value,
                    //while most types record their fields from the front of the boxed object - see
                    //FieldOfStructValue in the fork, which answers for both. Last, so nothing that matched
                    //outright is second-guessed.
                    if (field == null && StructBehind(local.Type) is { } pointee)
                        field = FieldOfStructValue(pointee, memory.Addend, method);
                }

                //An offset that matched nothing may name a member of a struct held in a field, which is stored
                //where it lies. Only where the value is being read: writing one back needs the address of each
                //step, which is a different shape than loading through them.
                //Writing one back is the same address, and `obj.field.x = v` is something C# says outright as
                //long as every step is a field rather than a property - so a store is taken too, on an
                //instance only. A static's members would need a setter that may not exist, and the address of
                //one is a different instruction. Until now the store was refused here, the operand stayed an
                //address the generator could not place, and the write was dropped: an inlined
                //`_ped.position = screenPos` writes `m_Position.x` at +0x108 and it simply did not happen.
                if (field == null && (i != 0 || staticOwner == null)
                    && PathToNestedField(staticOwner ?? local.Type, memory.Addend, staticOwner != null,
                        method.AppContext.Binary.is32Bit ? 8 : 0x10) is { Length: > 1 } path
                    && path.All(f => ReachableFrom(f, method)))
                {
                    instruction.Operands[i] = new NestedFieldReference(path, local, (int)memory.Addend);
                    changed = true;
                    continue;
                }

                if (field == null)
                    continue;

                //A static field of a type in another assembly can only be named from here if it is public.
                //Il2cpp reaches the storage directly and does not care, but decompiled source has to say the
                //field's name, and a name the project cannot see does not compile - the framework's own
                //assemblies are also a different build here from the ones the project will be compiled
                //against, so a private field of theirs may not even exist there.
                if (field.IsStatic && !IsVisibleFrom(field, method))
                {
                    //The property over it is the accessible way to the same value, and the getter says which
                    //property that is. Reading a value through its property is what the original source did.
                    if (instruction.OpCode == OpCode.Move && i == 1 && instruction.Operands[0] is LocalVariable destination
                        && staticOwner != null
                        && StaticPropertyRecovery.FindGetter(staticOwner, field.FieldType, memory.Addend, StaticFieldsOffset(method)) is { } getter)
                    {
                        instruction.OpCode = OpCode.Call;
                        instruction.Operands = [getter, destination];
                        changed = true;
                        break;
                    }

                    continue;
                }

                // make sure we have a full GIT for ldsfld. open type is bad.
                if (staticOwner is GenericInstanceTypeAnalysisContext genericOwner)
                    field = new ConcreteGenericFieldAnalysisContext(field, genericOwner);

                //A number stored where a struct field begins is that struct's first member, not the struct.
                //The offset cannot tell them apart and neither can ISIL, which carries no width - but the
                //value can: nothing copies a float into a `Vector2`. `SubCellVisual` writes `_lastOwnerSize.x`
                //and `.y` as two stores, and with only the second recognised the first came out as
                //`_lastOwnerSize = (Vector2)width2`. This is the store side of `IlGenerator.Fork.FrontMember`,
                //which already reasons this way about an array element.
                if (i == 0 && instruction is { OpCode: OpCode.Move, Operands: [_, { } stored] }
                    && field.FieldType is { IsValueType: true, IsEnumType: false } held
                    && held.Namespace != nameof(System)
                    && FrontMemberOf(held) is { } front && !StructInArithmetic.IsNumber(held)
                    && TypeOfStored(stored) is { } value && StructInArithmetic.IsNumber(value)
                    //And the number has to be *that* member. A value the analysis called `Int64` because it
                    //was eight bytes wide may be the whole struct arriving in a register, and naming its
                    //first member would throw the rest away - which cost twelve branches when the rule was
                    //stated over any number at all.
                    && value.FullName == front.FieldType.FullName)
                {
                    instruction.Operands[i] = new NestedFieldReference([field, front], local, (int)memory.Addend);
                    changed = true;
                    continue;
                }

                instruction.Operands[i] = new FieldReference(field, local, (int)memory.Addend);
                changed = true;
            }
        }

        return changed;
    }

    private static void ResolveCalls(MethodAnalysisContext method)
    {
        //Every call, not only the one a block ends with. A call is usually a block's last instruction, but not
        //always - a constructor whose whole body is a call to its base and a return has both in one block, and
        //looking only at the last instruction left that call, and every other call sharing a block, unresolved.
        foreach (var callInstruction in method.ControlFlowGraph!.Instructions)
        {
            if (!callInstruction.IsCall || callInstruction.Operands.Count == 0)
                continue;

            var dest = callInstruction.Operands[0];

            if (!dest.IsNumeric())
                continue;

            var target = (ulong)dest;

            var keyFunctionAddresses = method.AppContext.GetOrCreateKeyFunctionAddresses();

            //A key function is a runtime C entry point, never a managed method. `il2cpp_vm_object_is_inst` is
            //located by taking the last `bl` in Type::IsInstanceOfType, and in this corlib that method is
            //managed - `o != null && IsAssignableFrom(o.GetType())` - so the last call is Object::GetType and
            //every `x.GetType()` in the game was renamed to a helper the generator cannot write. Of the 25 key
            //function addresses this binary resolves, that is the only one `MethodsByAddress` names; where the
            //metadata names the address, the metadata is right.
            if (keyFunctionAddresses.IsKeyFunctionAddress(target)
                && !method.AppContext.MethodsByAddress.ContainsKey(target))
            {
                HandleKeyFunction(method.AppContext, callInstruction, target, keyFunctionAddresses);
                continue;
            }

            //Non-key function call. Try to find a single match
            if (!method.AppContext.MethodsByAddress.TryGetValue(target, out var targetMethods))
            {
                // Not a managed method at all. It may be one of the runtime helpers that exist purely to
                // throw, in which case restore the throw itself
                if (ThrownExceptionAt(method, callInstruction, target) is { } thrown)
                {
                    callInstruction.OpCode = OpCode.Throw;
                    callInstruction.Operands = [thrown];
                }

                continue;
            }

            // Duplicated/Shared method bodies are resolved later in ResolveCallsViaMethodInfo/ResolveAmbiguousCalls.
            if (targetMethods is not [{ } singleTargetMethod])
                continue;

            callInstruction.Operands[0] = singleTargetMethod;
        }

        method.ControlFlowGraph.MergeCallBlocks();
    }

    /// <summary>
    /// Resolves calls whose address maps to more than one method by matching the receiver's known
    /// type against the candidates' declaring types. Runs inside the type/field fixpoint and so
    /// re-fires as receivers become typed - a resolved call types its return value, which can type
    /// the receiver of a further call. Returns whether any call was resolved this pass.
    ///
    /// Conservative by design: it commits only when exactly one non-static candidate's declaring
    /// type matches the receiver's type. Anything still untyped or ambiguous is left for a later
    /// pass, or left unresolved - it never guesses.
    /// </summary>
    public static bool ResolveAmbiguousCalls(MethodAnalysisContext method)
    {
        var changed = false;

        foreach (var instruction in method.ControlFlowGraph!.Instructions)
        {
            if (!instruction.IsCall)
                continue;

            var target = instruction.Operands[0];

            // A resolved call's target is a method/key-function name; only unresolved ones are still numeric.
            if (!target.IsNumeric())
                continue;

            if (!method.AppContext.MethodsByAddress.TryGetValue((ulong)target, out var candidates) || candidates.Count < 2)
                continue;

            if (GetReceiver(instruction) is not { Type: { } receiverType })
                continue;

            MethodAnalysisContext? match = null;
            var ambiguous = false;

            foreach (var candidate in candidates)
            {
                if (candidate.IsStatic || !ReferenceEquals(candidate.DeclaringType, receiverType))
                    continue;

                if (match != null)
                {
                    ambiguous = true;
                    break;
                }

                match = candidate;
            }

            if (ambiguous || match == null)
                continue;

            instruction.Operands[0] = match;
            changed = true;
        }

        return changed;
    }

    // The receiver ('this') of a call is the first integer-slot argument: operand 1 for CallVoid
    // (after the target), operand 2 for Call (after the target and the return value).
    private static LocalVariable? GetReceiver(Instruction call)
    {
        var index = call.OpCode == OpCode.CallVoid ? 1 : 2;
        return index < call.Operands.Count ? call.Operands[index] as LocalVariable : null;
    }

    /// <summary>
    /// Resolves any Call (theoretically should always be a CallVoid) target directly after a Newobj to a constructor call.
    /// </summary>
    public static bool ResolveConstructorCalls(MethodAnalysisContext method)
    {
        var definitions = new Dictionary<LocalVariable, Instruction>();
        foreach (var instruction in method.ControlFlowGraph!.Instructions)
            if (instruction.Destination is LocalVariable definition)
                definitions[definition] = instruction;

        var changed = false;

        foreach (var instruction in method.ControlFlowGraph.Instructions)
        {
            if (!instruction.IsCall || !instruction.Operands[0].IsNumeric())
                continue;

            if (!method.AppContext.MethodsByAddress.TryGetValue((ulong)instruction.Operands[0], out var candidates))
                continue;

            if (GetReceiver(instruction) is not { } receiver || AllocatedType(receiver, definitions) is not { } allocatedType)
                continue;

            var constructor = candidates.FirstOrDefault(c => !c.IsStatic && c.Name == ".ctor" && ReferenceEquals(c.DeclaringType, allocatedType));
            if (constructor == null)
                continue;

            instruction.Operands[0] = constructor;
            changed = true;
        }

        return changed;
    }

    // Follow SSA copies from a local back to the Newobj that produced the value
    private static TypeAnalysisContext? AllocatedType(LocalVariable local, Dictionary<LocalVariable, Instruction> definitions)
    {
        var visited = new HashSet<LocalVariable>();

        while (visited.Add(local) && definitions.TryGetValue(local, out var definition))
        {
            switch (definition.OpCode)
            {
                case OpCode.Newobj:
                    return (definition.Operands[0] as LocalVariable)?.Type;
                case OpCode.Move when definition.Operands[1] is LocalVariable source:
                    local = source;
                    continue;
            }

            break;
        }

        return null;
    }

    /// <summary>
    /// Resolves calls whose address maps to more than one method by reading the runtime
    /// <c>MethodInfo*</c> the caller passes in, if there is one.
    /// </summary>
    public static bool ResolveCallsViaMethodInfo(MethodAnalysisContext method)
    {
        var changed = false;

        foreach (var instruction in method.ControlFlowGraph!.Instructions)
        {
            if (!instruction.IsCall)
                continue;

            var target = instruction.Operands[0];

            if (!target.IsNumeric())
                //Already resolved
                continue;

            if (GetMethodInfoArgument(instruction) is not { RepresentedMethod: { } representedMethod })
                //No MethodInfo to work with
                continue;

            var representedBase = BaseMethodOf(representedMethod);

            if (method.AppContext.MethodsByAddress.TryGetValue((ulong)target, out var candidates) && candidates.Count > 0)
            {
                //Try to actually match on the method name so we don't just replace a call with something else.
                if (!candidates.Any(candidate => ReferenceEquals(BaseMethodOf(candidate), representedBase)))
                    continue;
            }
            else if (!ReferenceEquals(representedMethod, representedBase)
                && IsManagedCode(method.AppContext, (ulong)target))
            {
                //No method is recorded at this address because the body is shared between every instantiation of
                //a generic method, and only the concrete ones carry addresses. Which instantiation is being called
                //is exactly what the runtime method argument is there to say - il2cpp passes it so that the shared
                //body can find its own type arguments - so it is the call target, and nothing else could be.
                //
                //**Only where the address is managed code at all** - see IsManagedCode in the fork. A native
                //runtime helper is also an address no method is recorded at, and the register the hidden
                //argument would occupy is also very often still holding one from the statement before.
            }
            else
            {
                //A runtime method that names no instantiation says nothing about where this call goes. It could be
                //an argument being passed along rather than the hidden one, and guessing from it would put a call
                //to some unrelated method into the output.
                continue;
            }

            instruction.Operands[0] = representedMethod;
            changed = true;
        }

        return changed;
    }

    private static MethodAnalysisContext BaseMethodOf(MethodAnalysisContext method) =>
        method is ConcreteGenericMethodAnalysisContext { BaseMethodContext: { } baseMethod } ? baseMethod : method;

    private static RuntimeMethodInfoAnalysisContext? GetMethodInfoArgument(Instruction call)
    {
        var firstArg = call.OpCode == OpCode.CallVoid ? 1 : 2;

        for (var i = call.Operands.Count - 1; i >= firstArg; i--)
        {
            switch (call.Operands[i])
            {
                case RuntimeMethodInfoAnalysisContext methodInfo:
                    return methodInfo;
                //The method's own hidden argument says which instantiation this body is running as, not
                //where a call inside it goes - and it sits in a later register than the one a call loads
                //the callee's into, so taking it would hide the one that does say.
                case LocalVariable { IsMethodInfo: true }:
                    break;
                case LocalVariable { Type: RuntimeMethodInfoAnalysisContext methodInfoLocal }:
                    return methodInfoLocal;
            }
        }

        return null;
    }

    private static void HandleKeyFunction(ApplicationAnalysisContext appContext, Instruction instruction, ulong target, BaseKeyFunctionAddresses kFA)
    {
        var method = "";
        if (target == kFA.il2cpp_codegen_initialize_method || target == kFA.il2cpp_codegen_initialize_runtime_metadata)
        {
            if (appContext.MetadataVersion < 27)
            {
                method = nameof(kFA.il2cpp_codegen_initialize_method);
            }
            else
            {
                method = nameof(kFA.il2cpp_codegen_initialize_runtime_metadata);
            }
        }
        else
        {
            var pairs = kFA.Pairs.ToList();
            var key = pairs.FirstOrDefault(pair => pair.Value == target).Key;
            if (key == null)
                return;
            method = key;
        }

        if (method != "")
        {
            instruction.Operands[0] = method;
        }
    }

    // Because of il2cpp fields (like cctor_finished_or_no_cctor) [local @ reg+offset] sometimes can't be resolved, but this works for now
    private static void ResolveGetter(MethodAnalysisContext method)
    {
        if (!method.Name.StartsWith("get_"))
            return;

        // Default get: Return [this @ reg+offset]
        var instructions = method.ControlFlowGraph!.Instructions;
        if (instructions.Count == 1)
        {
            var instr = instructions[0];

            if (instr.OpCode != OpCode.Return
                || instr.Operands.Count < 1
                || instr.Operands[0] is not MemoryOperand memory
                || memory.Index != null || memory.Scale != 0
                || memory.Base is not LocalVariable local)
                return;

            var fieldName = $"<{method.Name[4..]}>k__BackingField";

            var field = method.DeclaringType!.Fields.Find(f => f.Name == fieldName);
            if (field == null)
                return;

            instr.Operands[0] = new FieldReference(field, local, (int)memory.Addend);
        }
    }
}
