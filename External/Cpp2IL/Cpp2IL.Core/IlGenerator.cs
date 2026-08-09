using System.Collections.Generic;
using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using AsmResolver.PE.DotNet.Metadata.Tables;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils.AsmResolver;
using LibCpp2IL.BinaryStructures;

namespace Cpp2IL.Core;

public static partial class IlGenerator
{
    public static void GenerateIl(MethodAnalysisContext context, MethodDefinition definition)
    {
        CurrentContext = context;

        var assembly = context.DeclaringType!.DeclaringAssembly;
        var module = definition.DeclaringModule!;
        var importer = module.DefaultImporter;
        var factory = module.CorLibTypeFactory;

        var writeLine = factory.CorLibScope
            .CreateTypeReference("System", "Console")
            .CreateMemberReference("WriteLine", MethodSignature.CreateStatic(factory.Void, [factory.String]))
            .ImportWith(importer);

        var stringType = factory.CorLibScope.CreateTypeReference("System", "String");
        var stringCtor = stringType
            .CreateMemberReference(".ctor", MethodSignature.CreateStatic(stringType.ToTypeSignature(false), [factory.String]))
            .ImportWith(importer);

        // Change branch targets to instructions
        foreach (var instruction in context.ControlFlowGraph!.Blocks.SelectMany(block => block.Instructions))
        {
            if (instruction.Operands.Count > 0 && instruction.Operands[0] is Block target)
            {
                if (FirstInstructionFrom(target) is { } entry)
                    instruction.Operands[0] = entry;
            }
        }

        var body = new CilMethodBody()
        {
            InitializeLocals = true, // Without this ILSpy does: CompilerServices.Unsafe.SkipInit(out object obj);
            ComputeMaxStackOnBuild = false // There's stack imbalance somewhere, but this works for now
        };

        definition.CilMethodBody = body;

        // Make sure context.Locals actually has all locals (idk why it doesn't sometimes)
        foreach (var operand in context.ControlFlowGraph.Instructions.SelectMany(i => i.Operands))
        {
            LocalVariable? local = null;

            if (operand is FieldReference field)
                local = field.Local;

            if (operand is LocalVariable local2)
                local = local2;

            if (operand is MemoryOperand memory && memory.Base is LocalVariable local3)
                local = local3;

            if (local != null && !context.Locals.Contains(local))
                context.Locals.Add(local);
        }

        // Map ISIL locals to IL
        Dictionary<LocalVariable, CilLocalVariable> locals = [];
        foreach (var local in context.Locals)
        {
            TypeSignature ilType;

            // Use object if type couldn't be determined
            if (local.Type != null)
                ilType = local.Type.ToTypeSignature(module);
            else
                ilType = module.CorLibTypeFactory.Object;

            // A managed reference or a pointer is something nothing here can legally produce - there is no
            // ldloca or ldarga in what this generates - so a local typed as one only ever holds a lie. It
            // also does not compile: C# has no pointer to a managed type, and the decompiler writes the
            // declaration out as one. What it points at is both true and expressible.
            while (ilType is ByReferenceTypeSignature or PointerTypeSignature)
                ilType = ((TypeSpecificationSignature)ilType).BaseType;

            var ilLocal = new CilLocalVariable(ilType);
            body.LocalVariables.Add(ilLocal);
            locals.Add(local, ilLocal);
        }

        /* foreach (var instruction in context.ControlFlowGraph!.Instructions)
        {
            body.Instructions.Add(CilOpCodes.Ldstr, instruction.ToString());
            body.Instructions.Add(CilOpCodes.Call, _importer!.ImportMethod(_writeLine!));
        }
        body.Instructions.Add(CilOpCodes.Ldstr, "-------------------------------------------------------------------------");
        body.Instructions.Add(CilOpCodes.Call, _importer!.ImportMethod(_writeLine!)); */

        // Generate IL
        Dictionary<Instruction, List<CilInstruction>> instructionMap = [];
        Dictionary<Block, CilInstruction> blockEntryMap = [];
        List<(CilInstruction BranchInstruction, Block TargetBlock)> pendingBlockBranchFixups = [];

        foreach (var block in LayoutOrder(context.ControlFlowGraph!))
        {
            if (block == context.ControlFlowGraph.EntryBlock || block == context.ControlFlowGraph.ExitBlock)
                continue;

            if (block.Instructions.Count == 0)
                continue;

            foreach (var instruction in block.Instructions)
            {
                var generated = GenerateInstructions(instruction, context, definition, locals, writeLine, stringCtor);
                EnsureBranchable(generated, definition);
                instructionMap.Add(instruction, generated);

                if (!blockEntryMap.ContainsKey(block) && generated.Count > 0)
                    blockEntryMap[block] = generated[0];
            }

            var lastInstruction = block.Instructions.Last();
            
            if (lastInstruction.OpCode == OpCode.ConditionalJump)
            {
                var trueTarget = TryResolveJumpTargetBlock(lastInstruction, context.ControlFlowGraph);
                var falseSuccessor = block.Successors.FirstOrDefault(s => s != trueTarget && s != context.ControlFlowGraph.ExitBlock);
                if (falseSuccessor == null) continue;
                var bridge = new CilInstruction(CilOpCodes.Br, new CilInstructionLabel());
                definition.CilMethodBody!.Instructions.Add(bridge);
                pendingBlockBranchFixups.Add((bridge, falseSuccessor));
            }

            else if (lastInstruction.OpCode != OpCode.Jump && lastInstruction.OpCode != OpCode.Return && lastInstruction.OpCode != OpCode.IndirectJump)
            {
                var successor = block.Successors.FirstOrDefault(s => s != context.ControlFlowGraph.ExitBlock);
                if (successor == null) continue;
                var bridge = new CilInstruction(CilOpCodes.Br, new CilInstructionLabel());
                definition.CilMethodBody!.Instructions.Add(bridge);
                pendingBlockBranchFixups.Add((bridge, successor));
            }
        }
        // Set IL branch targets
        foreach (var kvp in instructionMap)
        {
            var instruction = kvp.Key;
            var il = kvp.Value;

            if (instruction.OpCode == OpCode.Jump || instruction.OpCode == OpCode.ConditionalJump)
            {
                var ilBranch = il.First(i => i.OpCode == CilOpCodes.Br || i.OpCode == CilOpCodes.Brtrue);

                if (instruction.Operands[0] is Block targetBlock)
                {
                    context.AddWarning($"Branch target block not in cfg: {instruction} ({targetBlock})");
                    StopBranching(ilBranch);
                    continue;
                }

                var target = (Instruction)instruction.Operands[0];

                if (!instructionMap.ContainsKey(target))
                {
                    context.AddWarning($"Branch target not in ISIL to IL map: {instruction} --- {target}");
                    StopBranching(ilBranch);
                    continue;
                }

                ilBranch.Operand = new CilInstructionLabel(instructionMap[target][0]);
            }
        }
        
        foreach (var (branchInstruction, targetBlock) in pendingBlockBranchFixups)
        {
            var target = ResolveBlockEntryInstruction(targetBlock, blockEntryMap);
            if (target == null)
            {
                context.AddWarning($"Unable to resolve branch target block: {targetBlock}");
                branchInstruction.OpCode = CilOpCodes.Nop;
                branchInstruction.Operand = null;
                continue;
            }

            branchInstruction.Operand = new CilInstructionLabel(target);
        }

        RemoveBranchesToTheFollowingInstruction(body, blockEntryMap);
        MoveArgumentLoadsAheadOfTheValueTheyFollow(body, instructionMap);
        KeepSingleUseValuesOnStack(body);
        DiscardValuesNothingReads(body);

        // Add analysis warnings
        var instructions = body.Instructions;
        foreach (var warning in context.AnalysisWarnings)
        {
            instructions.Add(CilOpCodes.Ldstr, "Warning: " + warning);
            instructions.Add(CilOpCodes.Call, importer.ImportMethod(writeLine));
        }

        EnsureTheBodyEnds(context, definition, module, importer);
    }
    

    private static Block? TryResolveJumpTargetBlock(Instruction jumpInstruction, ISILControlFlowGraph cfg)
    {
        if (jumpInstruction.Operands.Count == 0)
            return null;

        if (jumpInstruction.Operands[0] is Block targetBlock)
            return targetBlock;

        if (jumpInstruction.Operands[0] is Instruction targetInstruction)
            return cfg.FindBlockByInstruction(targetInstruction);

        return null;
    }

    private static CilInstruction? ResolveBlockEntryInstruction(Block block,
        Dictionary<Block, CilInstruction> blockEntryMap, HashSet<Block>? visited = null)
    {
        if (blockEntryMap.TryGetValue(block, out var target))
            return target;

        visited ??= [];
        if (!visited.Add(block))
            return null;

        foreach (var successor in block.Successors)
        {
            var resolved = ResolveBlockEntryInstruction(successor, blockEntryMap, visited);
            if (resolved != null)
                return resolved;
        }
        return null;
    }

    private static List<CilInstruction> GenerateInstructions(Instruction instruction, MethodAnalysisContext context,
        MethodDefinition method, Dictionary<LocalVariable, CilLocalVariable> locals, MemberReference writeLine, MemberReference stringCtor)
    {
        var body = method.CilMethodBody!;
        var instructions = body.Instructions;
        var currentCount = instructions.Count;
        var startIndex = instructions.Count;

        var module = method.DeclaringModule!;
        var importer = module.DefaultImporter!;

        switch (instruction.OpCode)
        {
            case OpCode.Invalid:
                instructions.Add(CilOpCodes.Ldstr, $"Invalid instruction: {instruction}");
                instructions.Add(CilOpCodes.Call, importer.ImportMethod(writeLine));
                break;

            case OpCode.NotImplemented:
                instructions.Add(CilOpCodes.Ldstr, $"Not implemented instruction: {instruction.Operands[0]}");
                instructions.Add(CilOpCodes.Call, importer.ImportMethod(writeLine));
                break;

            case OpCode.Interrupt:
            case OpCode.Nop:
                instructions.Add(CilOpCodes.Nop);
                break;

            case OpCode.Move:
                if (instruction.Operands[0] is FieldReference field) // stfld takes instance before value so LoadOperand StoreToOperand doesn't work
                {
                    if (!field.Field.IsStatic)
                        LoadLocal(field.Local, method, locals);

                    LoadOperand(instruction.Operands[1], method, locals, writeLine, stringCtor, field.Field.FieldType);
                    instructions.Add(field.Field.IsStatic ? CilOpCodes.Stsfld : CilOpCodes.Stfld, field.Field.ToFieldDescriptor(module));
                    break;
                }

                LoadOperandInto(instruction.Operands[0], instruction.Operands[1], method, locals, writeLine, stringCtor, ConversionTarget.Of(instruction));
                StoreToOperand(instruction.Operands[0], method, locals, writeLine);
                break;

            case OpCode.Select:
                // A conditional select. CIL has no such instruction, so it becomes the branches the
                // compiler removed - which is also what the decompiler needs to see to write it back as a
                // conditional expression. The two labels are patched once both arms exist.
                {
                    var takeFalse = new CilInstruction(CilOpCodes.Brfalse, new CilInstructionLabel());
                    var skipFalse = new CilInstruction(CilOpCodes.Br, new CilInstructionLabel());

                    LoadOperand(instruction.Operands[1], method, locals, writeLine, stringCtor);
                    instructions.Add(takeFalse);
                    LoadOperand(instruction.Operands[2], method, locals, writeLine, stringCtor);
                    instructions.Add(skipFalse);

                    var falseArm = instructions.Count;
                    LoadOperand(instruction.Operands[3], method, locals, writeLine, stringCtor);

                    // An arm that loaded nothing would leave the stack unbalanced on one path, which the
                    // body would then fail to verify with. Pushing a zero keeps both paths agreeing.
                    if (instructions.Count == falseArm)
                        instructions.Add(CilOpCodes.Ldc_I4_0);

                    takeFalse.Operand = new CilInstructionLabel(instructions[falseArm]);

                    var afterArms = instructions.Count;
                    StoreToOperand(instruction.Operands[0], method, locals, writeLine);

                    if (instructions.Count == afterArms)
                        instructions.Add(CilOpCodes.Pop);

                    skipFalse.Operand = new CilInstructionLabel(instructions[afterArms]);
                }
                break;

            case OpCode.Newobj:
                // Try and fuse our Newobj + the follow up constructor CallVoid into one IL newobj.
                // If we can't, just fall back to an Ldnull.
                var allocatedType = (instruction.Operands[0] as LocalVariable)?.Type;
                var fusedCall = FindConstructorCallAlongPath(context, instruction) is { Operands: [MethodAnalysisContext found, _, ..] } call
                    ? (Call: call, Constructor: found)
                    : default;

                // il2cpp inlines a small constructor into its caller, which leaves the allocation followed by a call
                // to the base constructor, or by nothing at all. Neither says how the object was built, but the type
                // being allocated does: use its own constructor so the allocation reads as one.
                var constructor = fusedCall.Constructor is { } candidate && ConstructsAllocatedType(candidate, allocatedType, fusedCall.Call)
                    ? candidate
                    : ShortestConstructor(allocatedType, context.DeclaringType?.DeclaringAssembly) ?? ConstructorOfAllocatedType(fusedCall.Constructor, allocatedType);

                if (constructor != null)
                {
                    // Operands are [ctor, newObject, arguments..., methodInfo], so take only as many as
                    // the constructor declares (i.e. drop methodInfo)
                    var constructorArgs = TakesTheCallsArguments(constructor, fusedCall.Constructor)
                        ? fusedCall.Call!.Operands.Skip(2).Take(constructor.Parameters.Count).ToList()
                        : [];

                    // A state machine's constructor sets nothing but the state, and the value it was given is on
                    // the allocation, put there by the pass that took it off the separate store il2cpp's inlining
                    // left behind. Both together would set the state twice, and the second of them stores a
                    // constant where a decompiler looking for an iterator requires a captured parameter.
                    var stateArgument = instruction.Operands.Count > 2 && constructor.Parameters.Count == 1
                        ? instruction.Operands[2]
                        : null;

                    // And where the allocation carries one operand per parameter, those are the arguments the
                    // inlined constructor was given - see InlinedConstructorRecovery. The single-parameter
                    // case is the state machine's, and reads identically either way.
                    var carriedArguments = CarriedConstructorArguments(instruction, constructor);

                    for (var i = 0; i < constructor.Parameters.Count; i++)
                    {
                        if (carriedArguments != null)
                            LoadOperand(carriedArguments[i], method, locals, writeLine, stringCtor, constructor.Parameters[i].ParameterType);
                        else if (stateArgument != null)
                            LoadOperand(stateArgument, method, locals, writeLine, stringCtor, constructor.Parameters[i].ParameterType);
                        else if (i < constructorArgs.Count)
                            LoadOperand(constructorArgs[i], method, locals, writeLine, stringCtor, constructor.Parameters[i].ParameterType);
                        else
                            AddDefaultValue(instructions, constructor.Parameters[i].ParameterType, module, importer);
                    }

                    instructions.Add(CilOpCodes.Newobj, importer.ImportMethod(constructor.ToMethodDescriptor(module)));
                    StoreToOperand(instruction.Operands[0], method, locals, writeLine);

                    if (fusedCall.Call != null)
                    {
                        fusedCall.Call.OpCode = OpCode.Nop;
                        fusedCall.Call.Operands = [];
                    }
                }
                else
                {
                    instructions.Add(CilOpCodes.Ldnull);
                    StoreToOperand(instruction.Operands[0], method, locals, writeLine);
                }
                break;

            case OpCode.Throw:
                if (instruction.Operands is [TypeAnalysisContext exceptionType]
                    && exceptionType.Methods.FirstOrDefault(m => m.Name == ".ctor" && m.Parameters.Count == 0) is { } exceptionCtor)
                    instructions.Add(CilOpCodes.Newobj, importer.ImportMethod(exceptionCtor.ToMethodDescriptor(module)));
                else
                    instructions.Add(CilOpCodes.Ldnull);

                instructions.Add(CilOpCodes.Throw);
                break;

            case OpCode.Phi:
                instructions.Add(CilOpCodes.Ldstr, $"Phi opcodes should not exist at this point in decompilation ({instruction})");
                instructions.Add(CilOpCodes.Call, importer.ImportMethod(writeLine));
                break;

            case OpCode.Call:
            case OpCode.CallVoid:
                if (instruction.Operands[0] is not MethodAnalysisContext targetMethod)
                {
                    if (instruction.Operands[0] is string keyFunction && TryEmitKeyFunction(keyFunction, instruction, method, locals, writeLine, stringCtor))
                        break;

                    if (instruction.Operands[0] is ulong targetAddress)
                        instructions.Add(CilOpCodes.Ldstr, $"Method not found @{targetAddress:X}");
                    else // Probably key function
                        instructions.Add(CilOpCodes.Ldstr, $"Unknown call target operand: {instruction}");

                    instructions.Add(CilOpCodes.Call, importer.ImportMethod(writeLine));
                    break;
                }

                var importedMethod = importer.ImportMethod(targetMethod.ToMethodDescriptor(module));

                var thisParamIndex = instruction.OpCode == OpCode.Call ? 2 : 1;

                if (!targetMethod.IsStatic) // Load 'this' param
                {
                    var receiver = (instruction.Operands.Count - 1) >= thisParamIndex ? instruction.Operands[thisParamIndex] : null;

                    //A method on a value type is called on the address of one, not on a copy of it - the same
                    //rule as a by-reference parameter, and broken the same way: a value in that position is not
                    //verifiable IL and reads back as a pointer to a managed type, which C# does not have.
                    if (targetMethod.DeclaringType is { IsValueType: true } valueType)
                    {
                        if (!TryLoadFieldAddress(receiver, method, locals, module))
                        instructions.Add(CilOpCodes.Ldloca, ReceiverLocal(receiver, valueType.ToTypeSignature(module), method, locals));
                    }
                    else if (receiver != null)
                        LoadOperand(receiver, method, locals, writeLine, stringCtor, targetMethod.DeclaringType);
                    else
                    {
                        instructions.Add(CilOpCodes.Ldstr, $"Non static method called without 'this' param ({instruction})");
                        instructions.Add(CilOpCodes.Call, importer.ImportMethod(writeLine));
                        instructions.Add(CilOpCodes.Ldnull);
                    }

                }

                // Load normal params
                var callParamIndex = instruction.OpCode == OpCode.Call ? (targetMethod.IsStatic ? 2 : 3) : (targetMethod.IsStatic ? 1 : 2);
                // A call whose target was only identified after lifting still carries the operands the
                // unknown-callee convention gave it, which may be fewer than the method actually takes.
                // The stack still has to match the signature, so anything missing gets a placeholder.
                var availableArgs = instruction.Operands.Count - callParamIndex;
                for (var i = 0; i < targetMethod.Parameters.Count; i++)
                {
                    var parameterType = targetMethod.Parameters[i].ParameterType;
                    var argument = i < availableArgs ? instruction.Operands[callParamIndex + i] : null;

                    //A by-reference parameter wants the address of something, and nothing here produces one:
                    //loading a value into that position is not verifiable IL, and the decompiler writes it out
                    //as a pointer to a managed type, which C# does not have. The address of a local is both
                    //valid and true - it is a place the callee can write, which is what the parameter is for.
                    if (parameterType is ByRefTypeAnalysisContext byReference)
                    {
                        if (!TryLoadFieldAddress(argument, method, locals, module))
                        instructions.Add(CilOpCodes.Ldloca, ScratchLocal(argument, byReference.ElementType.ToTypeSignature(module), method, locals));
                        continue;
                    }

                    if (argument != null)
                        LoadOperand(argument, method, locals, writeLine, stringCtor, parameterType);
                    else
                        PushDefaultOf(parameterType, instructions);
                }

                instructions.Add(CilOpCodes.Call, importedMethod);

                // Whether the call was lifted as returning something was decided by looking at whether the
                // result register was read afterwards, which is a guess about the caller. The callee's own
                // signature is not a guess, and the stack has to follow it: storing a result a void method
                // never pushed leaves the stack short, and dropping one it did push leaves it long. Either
                // way the body cannot be read afterwards and is discarded whole.
                if (!targetMethod.IsVoid)
                {
                    // A result that would have to be cast into where it is going is one the language will
                    // refuse - see ResultFitsItsDestination in the fork. Dropping it is what happened before
                    // the indirect return existed, and is better than losing the statements around it.
                    if (instruction.OpCode == OpCode.Call && instruction.Operands.Count > 1
                        && ResultFitsItsDestination(instruction.Operands[1], targetMethod))
                        StoreToOperand(instruction.Operands[1], method, locals, writeLine);
                    else
                        instructions.Add(CilOpCodes.Pop);
                }

                break;

            case OpCode.IndirectCall:
                instructions.Add(CilOpCodes.Ldstr, $"Indirect call: {instruction} (should have been resolved before IL gen)");
                instructions.Add(CilOpCodes.Call, importer.ImportMethod(writeLine));
                break;

            case OpCode.Return:
                if (!context.IsVoid)
                {
                    if (instruction.Operands.Count == 1 && ReturnsTheRightThing(instruction.Operands[0], context.ReturnType))
                        LoadOperand(instruction.Operands[0], method, locals, writeLine, stringCtor, context.ReturnType);
                    else
                        //A ret still pops a value even where we lost track of it, and for a value type null is not
                        //one it can hold.
                        AddDefaultValue(instructions, context.ReturnType, module, importer);
                }
                instructions.Add(CilOpCodes.Ret);
                break;

            case OpCode.Jump:
                instructions.Add(CilOpCodes.Br, new CilInstructionLabel());
                break;

            case OpCode.ConditionalJump:
                LoadOperand(instruction.Operands[1], method, locals, writeLine, stringCtor);
                instructions.Add(CilOpCodes.Brtrue, new CilInstructionLabel());
                break;

            case OpCode.IndirectJump:
                instructions.Add(CilOpCodes.Ldstr, $"Indirect jump: {instruction} (should have been resolved before IL gen)");
                instructions.Add(CilOpCodes.Call, importer.ImportMethod(writeLine));
                break;

            case OpCode.ShiftStack:
                instructions.Add(CilOpCodes.Ldstr, $"Stack shift: {instruction} (stack analysis should have removed these)");
                instructions.Add(CilOpCodes.Call, importer.ImportMethod(writeLine));
                break;

            case OpCode.CheckEqual:
            case OpCode.CheckGreater:
            case OpCode.CheckLess:
            case OpCode.CheckNotEqual:
            case OpCode.CheckGreaterOrEqual:
            case OpCode.CheckLessOrEqual:

            case OpCode.Add:
            case OpCode.Subtract:
            case OpCode.Multiply:
            case OpCode.Divide:

            case OpCode.ShiftLeft:
            case OpCode.ShiftRight:

            case OpCode.And:
            case OpCode.Or:
            case OpCode.Xor:
                // A value that only holds one register of a struct is not the struct, so combining or
                // comparing it as one says something the language has no operator for - and what it would
                // have to be written as does not compile. There is nothing here to say instead.
                if (SpansSeveralRegisters(instruction.Operands.Count > 1 ? instruction.Operands[1] : null)
                    || SpansSeveralRegisters(instruction.Operands.Count > 2 ? instruction.Operands[2] : null))
                {
                    instructions.Add(CilOpCodes.Ldstr, $"Part of a struct used as a value: {instruction}");
                    instructions.Add(CilOpCodes.Call, importer.ImportMethod(writeLine));
                    PushDefaultOf(TypeOfOperand(instruction.Operands[0]), instructions);
                    StoreResult(instruction, method, locals, writeLine);
                    break;
                }

                // A value whose type was never worked out lowers to a native integer, and C# has no
                // ordering operators on IntPtr/UIntPtr until it is spelled nuint - so an ordered
                // comparison between two of them decompiles to source the editor rejects. Widening both
                // sides to a 64-bit integer first says exactly the same thing and does compile.
                var ordered = instruction.OpCode is OpCode.CheckGreater or OpCode.CheckLess
                    or OpCode.CheckGreaterOrEqual or OpCode.CheckLessOrEqual;

                //An equality between a native integer and an ordinary one is written out as `int == IntPtr`,
                //which the editor rejects: there is no such operator. Only a comparison of two values needs
                //this - comparing a value against a literal reads as the literal's type and is accepted.
                var mixedEquality = instruction.OpCode is OpCode.CheckEqual or OpCode.CheckNotEqual
                    && instruction.Operands[1] is LocalVariable && instruction.Operands[2] is LocalVariable
                    && LowersToNativeInt(instruction.Operands[1]) != LowersToNativeInt(instruction.Operands[2]);

                var widenToInt64 = (ordered || mixedEquality)
                    && (LowersToNativeInt(instruction.Operands[1]) || LowersToNativeInt(instruction.Operands[2]));

                // Each side is loaded knowing what the other one is, so that a constant lands on the stack
                // at the width the value it is being compared or combined with has. Without that, a
                // comparison against a literal put an int64 next to an int32 - which is what leaves the
                // decompiler writing `state == 1L` and giving up on the method as a state machine.
                //Never as a struct: see ComparableType in the fork.
                var leftType = ComparableType(TypeOfOperand(instruction.Operands[1]));
                var rightType = ComparableType(TypeOfOperand(instruction.Operands[2]));

                LoadOperand(instruction.Operands[1], method, locals, writeLine, stringCtor, rightType);
                if (widenToInt64)
                    instructions.Add(CilOpCodes.Conv_I8);

                LoadOperand(instruction.Operands[2], method, locals, writeLine, stringCtor, leftType);
                if (widenToInt64)
                    instructions.Add(CilOpCodes.Conv_I8);

                switch (instruction.OpCode)
                {
                    case OpCode.CheckEqual: instructions.Add(CilOpCodes.Ceq); break;
                    case OpCode.CheckGreater: instructions.Add(CilOpCodes.Cgt); break;
                    case OpCode.CheckLess: instructions.Add(CilOpCodes.Clt); break;

                    // a != b  ==  (a == b) == 0
                    case OpCode.CheckNotEqual:
                        instructions.Add(CilOpCodes.Ceq);
                        instructions.Add(CilOpCodes.Ldc_I4_0);
                        instructions.Add(CilOpCodes.Ceq);
                        break;
                    // a >= b  ==  !(a < b)
                    case OpCode.CheckGreaterOrEqual:
                        instructions.Add(CilOpCodes.Clt);
                        instructions.Add(CilOpCodes.Ldc_I4_0);
                        instructions.Add(CilOpCodes.Ceq);
                        break;
                    // a <= b  ==  !(a > b)
                    case OpCode.CheckLessOrEqual:
                        instructions.Add(CilOpCodes.Cgt);
                        instructions.Add(CilOpCodes.Ldc_I4_0);
                        instructions.Add(CilOpCodes.Ceq);
                        break;

                    case OpCode.Add: instructions.Add(CilOpCodes.Add); break;
                    case OpCode.Subtract: instructions.Add(CilOpCodes.Sub); break;
                    case OpCode.Multiply: instructions.Add(CilOpCodes.Mul); break;
                    case OpCode.Divide: instructions.Add(CilOpCodes.Div); break;

                    case OpCode.ShiftLeft: instructions.Add(CilOpCodes.Shl); break;
                    case OpCode.ShiftRight: instructions.Add(Analysis.LogicalShift.BringsInZeroes(instruction) ? CilOpCodes.Shr_Un : CilOpCodes.Shr); break;

                    case OpCode.And: instructions.Add(CilOpCodes.And); break;
                    case OpCode.Or: instructions.Add(CilOpCodes.Or); break;
                    case OpCode.Xor: instructions.Add(CilOpCodes.Xor); break;
                }

                StoreToOperand(instruction.Operands[0], method, locals, writeLine);
                break;

            case OpCode.Not:
            case OpCode.Negate:
                LoadOperand(instruction.Operands[1], method, locals, writeLine, stringCtor);

                if (instruction.OpCode == OpCode.Negate)
                    instructions.Add(CilOpCodes.Neg);
                else if (IsBoolean(instruction.Operands[1], context))
                {
                    instructions.Add(CilOpCodes.Ldc_I4_0);
                    instructions.Add(CilOpCodes.Ceq);
                }
                else
                    instructions.Add(CilOpCodes.Not);

                StoreToOperand(instruction.Operands[0], method, locals, writeLine);
                break;

            default:
                instructions.Add(CilOpCodes.Ldstr, $"Unknown instruction: {instruction}");
                instructions.Add(CilOpCodes.Call, importer.ImportMethod(writeLine));
                break;
        }

        return instructions.ToList().GetRange(startIndex, instructions.Count - startIndex); // Return added IL
    }

    // Try find the follow up CallVoid for a constructor, after a Newobj.
    private static Instruction? FindConstructorCall(MethodAnalysisContext context, Instruction newobj)
    {
        var newObject = newobj.Operands[0];

        foreach (var block in context.ControlFlowGraph!.Blocks)
        {
            var index = block.Instructions.IndexOf(newobj);
            if (index < 0)
                continue;

            for (var i = index + 1; i < block.Instructions.Count; i++)
            {
                var candidate = block.Instructions[i];
                if (candidate is { OpCode: OpCode.CallVoid, Operands: [MethodAnalysisContext { Name: ".ctor" }, _, ..] }
                    && ReferenceEquals(candidate.Operands[1], newObject))
                    return candidate;
            }

            return null;
        }

        return null;
    }

    private static void LoadOperand(object operand, MethodDefinition method,
        Dictionary<LocalVariable, CilLocalVariable> locals, MemberReference writeLine, MemberReference stringCtor,
        TypeAnalysisContext? expectedType = null)
    {
        //A struct of floats put together from the registers or the immediate that carried it - see
        //TryBuildFloatStruct. Here, because it is the one place every operand is loaded through.
        if (TryBuildFloatStruct(operand, method, locals, method.DeclaringModule!, writeLine, stringCtor))
            return;

        var instructions = method.CilMethodBody!.Instructions;

        var module = method.DeclaringModule!;
        var importer = module.DefaultImporter!;

        // A null reference reaches us as an integer zero, which would otherwise be emitted as a literal 0
        // and read back as a cast from a number.
        if (expectedType is { IsValueType: false } && IsZeroConstant(operand))
        {
            instructions.Add(CilOpCodes.Ldnull);
            return;
        }

        // And a value type reaches us as a zero in the same way - a cleared register is an empty struct. See
        // ZeroValueOf in the fork.
        if (ZeroValueOf(expectedType, operand) is { } empty)
        {
            AddDefaultValue(instructions, empty, module, importer);
            return;
        }

        switch (operand)
        {
            case int i:
                instructions.Add(CilOpCodes.Ldc_I4, i);
                break;
            case uint ui:
                instructions.Add(CilOpCodes.Ldc_I4, unchecked((int)ui));
                break;
            case short s:
                instructions.Add(CilOpCodes.Ldc_I4, s);
                break;
            case ushort us:
                instructions.Add(CilOpCodes.Ldc_I4, us);
                break;
            case byte b8:
                instructions.Add(CilOpCodes.Ldc_I4, b8);
                break;
            case sbyte sb8:
                instructions.Add(CilOpCodes.Ldc_I4, sb8);
                break;
            case long l:
                //A zero written where a reference is expected is a null, not the number nought: a field
                //set to null compiles to a store of the zero register, and storing an integer into a
                //reference field is not verifiable IL, so the body would be thrown away.
                if (l == 0 && expectedType is { IsValueType: false } && !LowersToNativeInt(expectedType))
                {
                    instructions.Add(CilOpCodes.Ldnull);
                    break;
                }

                //Arm64 immediates arrive here as 64-bit whatever their width. Where the value it is going
                //to meet is a 32-bit one, loading it as 64-bit puts two different widths on the stack, and
                //the decompiler then writes the mismatch out rather than the comparison.
                if (IsThirtyTwoBitInteger(expectedType) && l is >= int.MinValue and <= int.MaxValue)
                {
                    instructions.Add(CilOpCodes.Ldc_I4, (int)l);
                    break;
                }

                instructions.Add(CilOpCodes.Ldc_I8, l);
                break;
            case ulong ul:
                instructions.Add(CilOpCodes.Ldc_I8, unchecked((long)ul));
                break;
            case float f:
                instructions.Add(CilOpCodes.Ldc_R4, f);
                break;
            case double d:
                instructions.Add(CilOpCodes.Ldc_R8, d);
                break;
            case bool b:
                instructions.Add(CilOpCodes.Ldc_I4, b ? 1 : 0);
                break;
            case string s:
                instructions.Add(CilOpCodes.Ldstr, s);
                break;
            case LocalVariable local:
                LoadLocal(local, method, locals);
                break;
            case MultiDimensionalElement multi:
                LoadMultiDimensionalElement(multi, method, locals);
                break;
            case FieldToken token:
                LoadFieldToken(token, instructions, module);
                break;
            case NestedFieldReference nested:
                LoadNestedField(nested, method, locals, module);
                break;
            case FieldReference field:
                if (field.Field.IsStatic)
                {
                    instructions.Add(CilOpCodes.Ldsfld, field.Field.ToFieldDescriptor(module));
                    break;
                }

                LoadLocal(field.Local, method, locals);
                instructions.Add(CilOpCodes.Ldfld, field.Field.ToFieldDescriptor(module));
                break;
            case MemoryOperand memory:
                if (memory.Index == null && memory.Addend == 0 && memory.Scale == 0
                    && memory.Base is LocalVariable local2)
                {
                    LoadLocal(local2, method, locals);

                    break;
                }

                if (TryLoadArrayElement(memory, method, locals))
                    break;

                instructions.Add(CilOpCodes.Ldstr, "Unmanaged memory load: " + operand.ToString());
                instructions.Add(CilOpCodes.Call, importer.ImportMethod(writeLine));
                instructions.Add(CilOpCodes.Ldc_I4_0);
                instructions.Add(CilOpCodes.Conv_I);
                break;
            case RuntimeMethodInfoAnalysisContext runtimeMethod:
                // A delegate constructor takes its target as a native pointer, which is exactly ldftn.
                if (expectedType?.FullName == "System.IntPtr")
                {
                    instructions.Add(CilOpCodes.Ldftn, importer.ImportMethod(runtimeMethod.RepresentedMethod.ToMethodDescriptor(module)));
                    break;
                }

                //Not fully implemented, these basically shouldn't actually ever exist in the final IL.
                instructions.Add(CilOpCodes.Ldc_I4_0);
                instructions.Add(CilOpCodes.Conv_I);
                break;
            case TypeAnalysisContext token2 when TryLoadTypeToken(token2, expectedType, instructions, module, importer):
                break;
            case TypeAnalysisContext:
                //A type used as a value is a runtime class handle, which is a native integer. Building an instance of
                //the type instead, as this used to, says something the code never did, and does not even compile when
                //the constructor it picks is one the project cannot reach: a build strips its class libraries down to
                //what the game used, so what is reachable there and what is reachable in the editor differ.
                instructions.Add(CilOpCodes.Ldc_I4_0);
                instructions.Add(CilOpCodes.Conv_I);
                break;
            default:
                instructions.Add(CilOpCodes.Ldstr, "Unknown operand: " + operand.ToString());
                instructions.Add(CilOpCodes.Call, importer.ImportMethod(writeLine));
                instructions.Add(CilOpCodes.Ldnull);
                break;
        }

        //The value is on the stack now, and an integer that is not the width of the place it is going to
        //makes the IL unverifiable - see ConvertToWidthOf in the fork.
        ConvertToWidthOf(instructions, operand, expectedType);
    }

    /// <summary>
    /// The same question about a type on its own. A runtime handle says it is not a value type, because there
    /// is no managed type behind it to be one - but it lowers to a native integer, which is.
    /// </summary>
    private static bool LowersToNativeInt(TypeAnalysisContext? type) =>
        type is null
            or RuntimeClassTypeAnalysisContext
            or RuntimeMethodInfoAnalysisContext
            or StaticFieldStorageTypeAnalysisContext
            or RgctxTableTypeAnalysisContext
            or MethodRgctxTableTypeAnalysisContext
        || type.FullName is "System.IntPtr" or "System.UIntPtr";

    private static void PushDefaultOf(TypeAnalysisContext type, CilInstructionCollection instructions)
    {
        //TODO Remove this, we should be handling arguments correctly in ISIL resolution, this is a hack to emit balanced stacks.
        //TODO At the *very* least we should emit a console.writeline saying that we did this.
        //A runtime handle says it is not a value type, because there is no managed type behind it to be
        //one - but it lowers to a native integer, and null is not a native integer. Asked first, because
        //a value of no type at all is one of these, and asking it whether it is a value type is a crash.
        if (LowersToNativeInt(type))
        {
            instructions.Add(CilOpCodes.Ldc_I4_0);
            instructions.Add(CilOpCodes.Conv_I);
            return;
        }

        if (!type.IsValueType)
        {
            instructions.Add(CilOpCodes.Ldnull);
            return;
        }

        switch (type.FullName)
        {
            case "System.Single": instructions.Add(CilOpCodes.Ldc_R4, 0f); break;
            case "System.Double": instructions.Add(CilOpCodes.Ldc_R8, 0d); break;
            case "System.Int64" or "System.UInt64": instructions.Add(CilOpCodes.Ldc_I8, 0L); break;
            default: instructions.Add(CilOpCodes.Ldc_I4_0); break;
        }
    }

    //A field says what it holds as plainly as a typed local does, and a bool read straight out of one is the
    //usual shape of a flag test - `!Define.DisableTutorial` reached here as a field and was negated bitwise.
    private static bool IsBoolean(object operand, MethodAnalysisContext context) =>
        operand switch
        {
            LocalVariable { Type: { } type } => type == context.AppContext.SystemTypes.SystemBooleanType,
            FieldReference field => field.Field.FieldType == context.AppContext.SystemTypes.SystemBooleanType,
            _ => false,
        };

    private static bool IsZeroConstant(object operand) =>
        operand switch
        {
            int i => i == 0,
            uint ui => ui == 0,
            long l => l == 0,
            ulong ul => ul == 0,
            short s => s == 0,
            ushort us => us == 0,
            byte b => b == 0,
            sbyte sb => sb == 0,
            _ => false,
        };

    private static void LoadLocal(LocalVariable local, MethodDefinition method, Dictionary<LocalVariable, CilLocalVariable> locals)
    {
        var instructions = method.CilMethodBody!.Instructions;

        if (local.IsThis)
        {
            instructions.Add(CilOpCodes.Ldarg_0);
            return;
        }

        var parameter = method.Parameters.FirstOrDefault(p => p.Name == local.Name);

        if (parameter != null)
            instructions.Add(CilOpCodes.Ldarg, parameter);
        else
            instructions.Add(CilOpCodes.Ldloc, locals[local]);
    }

    private static void StoreToOperand(object operand, MethodDefinition method,
        Dictionary<LocalVariable, CilLocalVariable> locals, MemberReference writeLine)
    {
        var instructions = method.CilMethodBody!.Instructions;

        var module = method.DeclaringModule!;
        var importer = module.DefaultImporter!;

        switch (operand)
        {
            case LocalVariable local:
                instructions.Add(CilOpCodes.Stloc, locals[local]);
                break;

            case FieldReference field:
                var fieldDescriptor = field.Field.ToFieldDescriptor(module);

                if (field.Field.IsStatic)
                {
                    instructions.Add(CilOpCodes.Stsfld, fieldDescriptor);
                    break;
                }

                // stfld wants the object underneath the value, but the value is already on the stack, so
                // park it in a temporary while we load the object.
                var scratch = new CilLocalVariable(fieldDescriptor.Signature!.FieldType);
                method.CilMethodBody!.LocalVariables.Add(scratch);

                instructions.Add(CilOpCodes.Stloc, scratch);
                LoadLocal(field.Local, method, locals);
                instructions.Add(CilOpCodes.Ldloc, scratch);
                instructions.Add(CilOpCodes.Stfld, fieldDescriptor);
                break;

            case MemoryOperand memory:
                if (memory.Index == null && memory.Addend == 0 && memory.Scale == 0
                    && memory.Base is LocalVariable local2)
                {
                    // Can pointer assignments just be ignored because it's C#? (Move [local], 123)
                    instructions.Add(CilOpCodes.Stloc, locals[local2]);
                    break;
                }

                if (TryStoreArrayElement(memory, method, locals))
                    break;

                instructions.Add(CilOpCodes.Pop);
                break;

            default:
                instructions.Add(CilOpCodes.Ldstr, $"Store into unknown operand: {operand}");
                instructions.Add(CilOpCodes.Call, importer.ImportMethod(writeLine));
                instructions.Add(CilOpCodes.Pop);
                break;
        }
    }
}
