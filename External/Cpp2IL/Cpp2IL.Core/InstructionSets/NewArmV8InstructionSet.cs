using System;
using System.Collections.Generic;
using System.Linq;
using Disarm;
using Cpp2IL.Core.Api;
using Cpp2IL.Core.Il2CppApiFunctions;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using Disarm.InternalDisassembly;

namespace Cpp2IL.Core.InstructionSets;

public partial class NewArmV8InstructionSet : Cpp2IlInstructionSet
{
    [ThreadStatic]
    private static Dictionary<Arm64Register, ulong> adrpOffsets = new();

    //What the last flag-setting instruction compared. Knowing this lets a b.cond be lifted as the
    //comparison it actually makes instead of an expression over modelled flags. The two sides are
    //copied into these pseudo-registers at the compare rather than referred to where they live, so a
    //branch that is not the very next instruction still reads the values as they were when the flags
    //were set - and ssa gives them phis where the compare and the branch are in different blocks.
    private const string ComparisonLeft = "CMPL";
    private const string ComparisonRight = "CMPR";

    [ThreadStatic]
    private static bool haveComparison;

    public override BinarySlice GetRawBytesForMethod(MethodAnalysisContext context, bool isAttributeGenerator)
    {
        var binary = context.AppContext.Binary;

        if (context is not ConcreteGenericMethodAnalysisContext)
        {
            //Managed method or attr gen => grab raw byte range between a and b
            var startOfNextFunction = (int)MiscUtils.GetAddressOfNextFunctionStart(context.UnderlyingPointer, binary);
            var ptrAsInt = (int)context.UnderlyingPointer;
            var count = startOfNextFunction - ptrAsInt;

            if (startOfNextFunction > 0)
                return new BinarySlice(binary, ptrAsInt, count);
        }

        var result = NewArm64Utils.GetArm64MethodBodyAtVirtualAddress(binary, context.UnderlyingPointer);
        var lastInsn = result.LastValid();

        var start = (int)binary.MapVirtualAddressToRaw(context.UnderlyingPointer);
        // Map the last instruction (always within segment) and add 4 (ARM64 instruction size).
        // This avoids mapping endVa which may land exactly at a segment boundary gap.
        var end = (int)binary.MapVirtualAddressToRaw(lastInsn.Address) + 4;

        //Sanity check
        if (start < 0 || end < 0 || start >= binary.RawLength || end >= binary.RawLength)
            throw new Exception($"Failed to map virtual address 0x{context.UnderlyingPointer:X} to raw address for method {context!.DeclaringType?.FullName}/{context.Name} - start: 0x{start:X}, end: 0x{end:X} are out of bounds for length {binary.RawLength}.");

        return new BinarySlice(binary, start, end - start);
    }

    public override List<object> GetParameterOperandsFromMethod(MethodAnalysisContext context)
    {
        // Is this correct (?)
        var operands = GetArgumentOperandsForCall(context);
        Cpp2IL.Core.Analysis.ParametersOnTheStack.Place(operands, context);
        AddRuntimeMethodOperand(operands, context);
        return operands;
    }

    public override List<Instruction> GetIsilFromMethod(MethodAnalysisContext context)
    {
        var insns = NewArm64Utils.GetArm64MethodBodyAtVirtualAddress(context.AppContext.Binary, context.UnderlyingPointer);

        if (adrpOffsets == null!) // initializers for ThreadStatic fields only run on the first thread
            adrpOffsets = new();
        else
            adrpOffsets.Clear();

        haveComparison = false;
        ConditionalCompare.Reset();
        vectorLanes.Reset();

        var instructions = new List<Instruction>();
        var addresses = new List<ulong>();

        foreach (var instruction in insns)
            ConvertInstructionStatement(instruction, instructions, addresses, context);

        // fix branches
        for (var i = 0; i < instructions.Count; i++)
        {
            var instruction = instructions[i];

            if (instruction.OpCode != OpCode.Jump && instruction.OpCode != OpCode.ConditionalJump)
                continue;

            var targetAddress = (ulong)instruction.Operands[0];
            var targetIndex = addresses.FindIndex(addr => addr == targetAddress);

            if (targetIndex == -1)
            {
                instruction.OpCode = OpCode.Invalid;
                instruction.Operands = [$"Jump target not found in method: 0x{targetAddress:X4}"];
                continue;
            }

            var targetInstruction = instructions[targetIndex];

            instruction.Operands[0] = targetInstruction;
        }

        adrpOffsets.Clear();
        return instructions;
    }

    private void ConvertInstructionStatement(Arm64Instruction instruction, List<Instruction> instructions, List<ulong> addresses, MethodAnalysisContext context)
    {
        var address = instruction.Address;
        currentMethod = context;

        Instruction Add(ulong address, OpCode opCode, params object[] operands)
        {
            addresses.Add(address);
            var newInstruction = new Instruction(instructions.Count, opCode, operands);
            instructions.Add(newInstruction);
            return newInstruction;
        }
        
        //Copy both sides of a comparison into the pseudo-registers a later b.cond reads, so the branch
        //sees the values as they were when the flags were set.
        void RecordComparison(object left, object right)
        {
            Add(address, OpCode.Move, new Register(null, ComparisonLeft), left);
            Add(address, OpCode.Move, new Register(null, ComparisonRight), right);
            //A condition that reads the carry asks an unsigned question - see ComparisonSide in the fork.
            //Seeded with the same pair, which is what a subtraction's carry means, and overwritten by
            //RecordCarry where an addition makes it mean something else.
            Add(address, OpCode.Move, new Register(null, CarryLeft), left);
            Add(address, OpCode.Move, new Register(null, CarryRight), right);
            haveComparison = true;
            ConditionalCompare.Clear();
        }

        //The last register operand of a data processing instruction can carry a shift - `bic w1, w20, w20, asr
        //#31` is how `Math.Max(0, x)` is written without a branch, and `add x0, x1, x2, lsl #3` is how an array
        //element is addressed. Ignoring the shift turned the first into `x & ~x`, which is nothing at all, so the
        //shift is done first and what comes out is used in its place.
        object ShiftedOperand(int operand)
        {
            var value = ConvertOperand(instruction, operand);

            //A narrowing conversion the architecture folds into the operand - see ExtendedTo in the fork. It
            //narrows what goes in and then widens straight back, because the instruction's *result* is a full
            //word: leaving the operand at the narrow type made the whole sum that type, and a hash
            //accumulated in `ushort` truncates to nothing.
            if (ExtendedTo(context, instruction) is { } narrowed)
            {
                var extended = new Register(null, "EXTENDED");
                Add(address, OpCode.Move, extended, value, new ConversionTarget(narrowed));

                var widened = new Register(null, "EXTENDED");
                Add(address, OpCode.Move, widened, extended, new ConversionTarget(context.AppContext.SystemTypes.SystemInt32Type));
                value = widened;
            }

            //Disarm reports how far the operand is shifted but not which way, so the two bits the architecture
            //keeps that in are read off the instruction itself.
            if (instruction.Op3Kind != Arm64OperandKind.Immediate || instruction.Op3Imm == 0)
                return value;

            var direction = ShiftDirection(context, instruction);

            if (direction is not { } shiftDirection)
                return value;

            //A folded shift is done at the instruction's own width, and arm64's two right shifts differ on
            //every negative value - see FoldedShift in the fork.
            return FoldedShift(context, instruction, value, shiftDirection, (opCode, operands) => Add(address, opCode, operands));
        }

        void AddCall(MethodAnalysisContext context, object? returnRegister2, ulong address, ulong target)
        {
            var call = returnRegister2 == null ? 
                Add(address, OpCode.CallVoid, target) : 
                Add(address, OpCode.Call, target, returnRegister2);

            call.Operands.AddRange(GetArgumentOperandsForCall(context, target));

            //A struct of floats does not arrive in the register the result names - it arrives one field to a
            //vector register - and nothing said so. See VectorReturnFields in the fork.
            foreach ((OpCode landedOpCode, object[] landedOperands) in VectorReturnFields(context.AppContext, target, returnRegister2))
                Add(address, landedOpCode, landedOperands);
        }

        //A call to something that is not a managed method, so nothing is known about its signature. Under aapcs64 the
        //first eight arguments are in x0 to x7 and the result comes back in x0, so hand all of those over and leave it
        //to the steps that know a particular runtime function to pick out the ones it actually uses. Without this a
        //call to a runtime helper carries no operands at all, and none of them can be recovered.
        void AddUnmanagedCall(ulong address, ulong target)
        {
            var call = Add(address, OpCode.Call, target, RegisterFor(Arm64Register.X0));

            for (var argument = 0; argument < 8; argument++)
            {
                call.Operands.Add(RegisterFor(Arm64Register.X0 + argument));
            }
        }

        //The same, and then the vector run behind it, for a callee that *will* be named - one whose address
        //several instantiations of a generic method share. A genuinely unknown callee still gets the integer
        //run alone: eight more operands nothing will ever claim would keep eight more values alive, which is
        //what cost branches the last time an unresolved call was given registers it did not use.
        void AddSharedCall(ulong address, ulong target)
        {
            var call = Add(address, OpCode.Call, target, RegisterFor(Arm64Register.X0));

            for (var run = 0; run < 2; run++)
            {
                var first = run == 0 ? Arm64Register.X0 : Arm64Register.V0;

                for (var argument = 0; argument < Analysis.Aapcs64.RegistersPerRun; argument++)
                {
                    call.Operands.Add(RegisterFor(first + argument));
                }
            }
        }

        //Several values in one register - see VectorLanes.cs. What it takes apart entirely it says so for;
        //where it only adds the lanes above the first, the ordinary lifting below still handles that first.
        if (vectorLanes.TryConvert(context, instruction, (opCode, operands) => Add(address, opCode, operands)))
            return;

        switch (instruction.Mnemonic)
        {
            case Arm64Mnemonic.FMOV when instruction.Op1Kind == Arm64OperandKind.Immediate:
                //Eight bits that expand into a float rather than eight bits of one - see FloatImmediate.
                if (FloatImmediate(context, instruction) is not { } expanded)
                    goto default;

                Add(address, OpCode.Move, ConvertOperand(instruction, 0), expanded);
                break;

            case Arm64Mnemonic.MOV:
            case Arm64Mnemonic.MOVZ:
            case Arm64Mnemonic.FMOV:
            case Arm64Mnemonic.SXTW: // move and sign extend Wn to Xd
            case Arm64Mnemonic.LDR:
            case Arm64Mnemonic.LDRB:
            //The remaining loads differ from LDR only in the width they read and whether the offset is
            //scaled. ISIL has no notion of either, so the value they produce is the same: what is at
            //the address. Leaving them unhandled cost a placeholder in the middle of every statement
            //that read a short, a byte with an unscaled offset, or a sign-extended word.
            case Arm64Mnemonic.LDRH:
            case Arm64Mnemonic.LDRSH:
            case Arm64Mnemonic.LDRSB:
            case Arm64Mnemonic.LDRSW:
            case Arm64Mnemonic.LDUR:
            case Arm64Mnemonic.LDURB:
            case Arm64Mnemonic.LDURH:
            case Arm64Mnemonic.LDURSW:
                //Load and move are (dest, src)

                if (instruction.MemIsPreIndexed) //  such as  X8, [X19,#0x30]! 
                {
                    //Regardless of anything else, we're trashing any possible ADRP offsets in the dest here, so let's clear that
                    if (instruction.Op0Kind == Arm64OperandKind.Register)
                        adrpOffsets.Remove(instruction.Op0Reg);

                    var operate = ConvertOperand(instruction, 1);
                    if (operate is MemoryOperand operand)
                    {
                        var register = (Register)operand.Base!;
                        // X19= X19, #0x30
                        Add(address, OpCode.Add, register, register, operand.Addend);
                        //X8 = [X19]
                        Add(address, OpCode.Move, ConvertOperand(instruction, 0), new MemoryOperand(register));
                        break;
                    }
                }

                //The other way round: use the base, then move it. See WritebackAfterUse.
                if (instruction.Op1Kind == Arm64OperandKind.Memory
                    && ConvertOperand(instruction, 1) is MemoryOperand { Base: Register walked }
                    && WritebackAfterUse(context, instruction) is { } step)
                {
                    if (instruction.Op0Kind == Arm64OperandKind.Register)
                        adrpOffsets.Remove(instruction.Op0Reg);

                    Add(address, OpCode.Move, ConvertOperand(instruction, 0), new MemoryOperand(walked));
                    Add(address, OpCode.Add, walked, walked, step);
                    break;
                }

                if (instruction.Op1Kind == Arm64OperandKind.Memory && adrpOffsets.TryGetValue(instruction.MemBase, out var page) && instruction.MemAddendReg == Arm64Register.INVALID)
                {
                    //A displacement of nought is as much a paged load as any other: `adrpOffsets` is only
                    //written by a real ADRP and cleared the moment the register is rewritten, so `ldr x8, [x8]`
                    //straight after one is the same shape with the offset already in the page. Refusing it left
                    //323 metadata usages in this game as arithmetic on a raw address, and every call through one
                    //came out as `Method not found`.
                    //Maybe this is a bit hacky? But I really don't want to write paged load handling into ISIL itself, it's an Arm64 quirk
                    //LDR X0, [X1, #0x1000], where X1 was previously loaded with a page address via an ADRP instruction
                    //We just return the final address, it makes ISIL happier.
                    //TODO check if this is correct
                    //Scaled, because the immediate of a 128 bit access counts sixteen byte units and the
                    //disassembler does not multiply it back out. This fold never reaches MemoryOperandFor,
                    //where that is done for every other addressing form, so it has to be done here too.
                    var offset = Scaled(instruction, instruction.MemOffset) + (long)page;

                    //We're also trashing any possible ADRP offsets in the dest here, so let's clear that now we've possibly grabbed the value if we need it (it's common to store the page and final address in the same register)
                    if (instruction.Op0Kind == Arm64OperandKind.Register)
                        adrpOffsets.Remove(instruction.Op0Reg);

                    //A float read from a fixed address is a literal from the constant pool: il2cpp keeps anything
                    //that can change in a type's static storage, reached through its class, never at an address
                    //baked into the instruction. Reading it out of the binary gives back the number the source
                    //wrote - `PlayBgm(name, 1f, 0.14f, true)` had lost its 0.14 to an unreadable load.
                    if (ReadFloatConstant(context, instruction.Op0Reg, (ulong)offset) is { } literal)
                    {
                        Add(address, OpCode.Move, ConvertOperand(instruction, 0), literal);
                        break;
                    }

                    Add(address, OpCode.Move, ConvertOperand(instruction, 0), Analysis.ConstantBlobStore.Note(instruction, offset));
                    break;
                }

                //And again here we're trashing any possible ADRP offsets in the dest here, so let's clear that
                if (instruction.Op0Kind == Arm64OperandKind.Register)
                    adrpOffsets.Remove(instruction.Op0Reg);

                Add(address, OpCode.Move, ConvertOperand(instruction, 0), ConvertOperand(instruction, 1));
                Add(address, OpCode.CheckEqual, new Register(null, "Z"), ConvertOperand(instruction, 0), 0);
                break;
            case Arm64Mnemonic.MOVN:
                {
                    // dest = ~src

                    //See above re: ADRP offsets
                    if (instruction.Op0Kind == Arm64OperandKind.Register)
                        adrpOffsets.Remove(instruction.Op0Reg);

                    //movn is how a small negative constant is written: `mov w8, #-1` assembles as
                    //`movn w8, #0`. Folding it here keeps the constant a constant, and keeps it at the
                    //width the register has - a -1 arriving as a 64-bit value cannot be stored into an
                    //int field, and the decompiler writes the mismatch out instead of the assignment.
                    if (instruction.Op1Kind == Arm64OperandKind.Immediate)
                    {
                        var inverted = ~instruction.Op1Imm;
                        var narrow = instruction.Op0Kind == Arm64OperandKind.Register
                            && instruction.Op0Reg is >= Arm64Register.W0 and <= Arm64Register.W31;

                        Add(address, OpCode.Move, ConvertOperand(instruction, 0), narrow ? (int)inverted : inverted);
                        break;
                    }

                    var temp2 = new Register(null, "TEMP");
                    Add(address, OpCode.Move, temp2, ConvertOperand(instruction, 1));
                    Add(address, OpCode.Not, temp2, temp2);
                    Add(address, OpCode.Move, ConvertOperand(instruction, 0), temp2);
                    break;
                }
            case Arm64Mnemonic.STR:
            case Arm64Mnemonic.STUR: // unscaled
            case Arm64Mnemonic.STRB:
            case Arm64Mnemonic.STRH:
            case Arm64Mnemonic.STURB:
            case Arm64Mnemonic.STURH:
                //Store is (src, dest)

                //A pre-indexed store moves the base register before writing through it, and the writeback is not
                //optional bookkeeping: the instructions after it address the frame from the new value. Losing it
                //made every following store land at an offset nothing could resolve, so the store was dropped -
                //which is what left the state machine of an iterator missing the fields it sets. The load side
                //has always handled this; the store side never did.
                if (instruction.MemIsPreIndexed
                    && instruction.Op1Kind == Arm64OperandKind.Memory
                    && instruction.MemBase != Arm64Register.INVALID
                    && instruction.MemAddendReg == Arm64Register.INVALID
                    && ConvertOperand(instruction, 1) is MemoryOperand { Base: Register writeback } preIndexed)
                {
                    Add(address, OpCode.Add, writeback, writeback, preIndexed.Addend);
                    Add(address, OpCode.Move, new MemoryOperand(writeback), ConvertStoredValue(instruction, 0));
                    break;
                }

                //And the form that writes at the base and moves it afterwards - see WritebackAfterUse.
                if (instruction.Op1Kind == Arm64OperandKind.Memory
                    && ConvertOperand(instruction, 1) is MemoryOperand { Base: Register stepped }
                    && WritebackAfterUse(context, instruction) is { } stride)
                {
                    Add(address, OpCode.Move, new MemoryOperand(stepped), ConvertStoredValue(instruction, 0));
                    Add(address, OpCode.Add, stepped, stepped, stride);
                    break;
                }

                //A store through a register an ADRP put a page address in is a store to a fixed address, the same as
                //the load case below. It has to be recognised as one, because a store to a fixed address is how il2cpp
                //records that a method's metadata has been initialised, and the step that removes that guard looks for
                //exactly this shape.
                if (instruction.Op1Kind == Arm64OperandKind.Memory
                    && adrpOffsets.TryGetValue(instruction.MemBase, out var storePage)
                    && instruction.MemAddendReg == Arm64Register.INVALID)
                {
                    Add(address, OpCode.Move, new MemoryOperand(addend: instruction.MemOffset + (long)storePage), ConvertStoredValue(instruction, 0));
                    break;
                }

                Add(address, OpCode.Move, ConvertOperand(instruction, 1), ConvertStoredValue(instruction, 0));
                break;
            case Arm64Mnemonic.STP:
                // store pair of registers (reg1, reg2, dest)
                {
                    var dest3 = ConvertOperand(instruction, 2);
                    if (dest3 is StackOffset stackSlot) // a pair of registers saved into the frame
                    {
                        var firstOfPair = ConvertStoredValue(instruction, 0);
                        long slotSize = PairElementSize(instruction.Op0Reg);
                        Add(address, OpCode.Move, stackSlot, firstOfPair);
                        Add(address, OpCode.Move, new StackOffset((int)(stackSlot.Offset + slotSize)), ConvertStoredValue(instruction, 1));
                    }
                    else if (dest3 is Register { Name: "X31" }) // if stack
                    {
                        Add(address, OpCode.Move, dest3, ConvertStoredValue(instruction, 0));
                        Add(address, OpCode.Move, dest3, ConvertStoredValue(instruction, 1));
                    }
                    else if (dest3 is MemoryOperand memory)
                    {
                        var firstRegister = ConvertStoredValue(instruction, 0);
                        long size = PairElementSize(instruction.Op0Reg);
                        Add(address, OpCode.Move, dest3, firstRegister); // [REG + offset] = REG1
                        memory = new MemoryOperand((Register)memory.Base!, addend: memory.Addend + size);
                        dest3 = memory;
                        Add(address, OpCode.Move, dest3, ConvertStoredValue(instruction, 1)); // [REG + offset + size] = REG2
                    }
                    else // reg pointer
                    {
                        var firstRegister = ConvertStoredValue(instruction, 0);
                        long size = PairElementSize(instruction.Op0Reg);
                        Add(address, OpCode.Move, dest3, firstRegister);
                        Add(address, OpCode.Add, dest3, dest3, size);
                        Add(address, OpCode.Move, dest3, ConvertStoredValue(instruction, 1));
                    }
                }
                break;
            case Arm64Mnemonic.ADR:
                //The address of something a fixed distance from this instruction, which unlike `adrp` is
                //exact rather than a page. Nothing else has to be done with it: it is the address.
                Add(address, OpCode.Move, ConvertOperand(instruction, 0), (long)(address + (ulong)instruction.Op1Imm));
                break;

            case Arm64Mnemonic.ADRP:
                //The address of a page, which the disassembler reports as the distance to it rather than as
                //the page - so the register was being given a number that is not an address at all, and
                //negative wherever the page is behind the instruction. It only ever showed where the fold
                //below did not apply, because that reads the table rather than the moved value.
                var pageAddress = address & ~0xFFFUL;
                var addressed = (ulong)((long)pageAddress + instruction.Op1Imm);

                Add(address, OpCode.Move, ConvertOperand(instruction, 0), (long)addressed);
                adrpOffsets[instruction.Op0Reg] = addressed;
                break;
            case Arm64Mnemonic.LDP when instruction.Op2Kind == Arm64OperandKind.Memory:
                //LDP (dest1, dest2, [mem]) - basically just treat as two loads, with the second offset by the length of the first
                var destRegSize = instruction.Op0Reg switch
                {
                    //vector (128 bit)
                    >= Arm64Register.V0 and <= Arm64Register.V31 => 16, //TODO check if this is accurate
                    //double
                    >= Arm64Register.D0 and <= Arm64Register.D31 => 8,
                    //single
                    >= Arm64Register.S0 and <= Arm64Register.S31 => 4,
                    //half
                    >= Arm64Register.H0 and <= Arm64Register.H31 => 2,
                    //word
                    >= Arm64Register.W0 and <= Arm64Register.W31 => 4,
                    //x
                    >= Arm64Register.X0 and <= Arm64Register.X31 => 8,
                    _ => throw new($"Unknown register size for LDP: {instruction.Op0Reg}")
                };

                var dest1 = ConvertOperand(instruction, 0);
                var dest2 = ConvertOperand(instruction, 1);
                var mem = ConvertOperand(instruction, 2);

                if (mem is StackOffset loadSlot)
                {
                    Add(address, OpCode.Move, dest1, loadSlot);
                    Add(address, OpCode.Move, dest2, new StackOffset(loadSlot.Offset + destRegSize));
                    break;
                }

                //TODO clean this mess up
                var memInternal = mem as MemoryOperand?;
                var mem2 = new MemoryOperand((Register)memInternal!.Value.Base!, addend: memInternal.Value.Addend + destRegSize);

                Add(address, OpCode.Move, dest1, mem);
                Add(address, OpCode.Move, dest2, mem2);
                break;
            case Arm64Mnemonic.BL:
                if (context.AppContext.MethodsByAddress.TryGetValue(instruction.BranchTarget, out var possibleMethods))
                {
                    if (possibleMethods.Count == 1)
                    {
                        AddCall(context, GetCallResultOperand(possibleMethods[0]), address, instruction.BranchTarget);
                    }
                    else
                    {
                        //Several methods share this address because they share a body: every instantiation of a
                        //generic method compiles to one. Nothing here says which one is being called - the runtime
                        //method argument does, and a later step reads it - so the call is given every register the
                        //convention could have used, exactly as for a callee that is not known at all. Taking the
                        //first candidate's signature instead dropped that argument and left the result unconnected,
                        //and taking the *caller's* return register said the call produced nothing at all.
                        AddSharedCall(address, instruction.BranchTarget);
                    }
                }
                else if (ImportedCall(context, instruction.BranchTarget) is { } imported)
                {
                    foreach ((OpCode importedOpCode, object[] importedOperands) in imported)
                        Add(address, importedOpCode, importedOperands);
                }
                //An array store the compiler put in a function of its own - see Analysis.OutlinedArrayStore.
                else if (Analysis.OutlinedArrayStore.Rewrite(context, instruction.BranchTarget, RegisterFor) is { } stored)
                {
                    foreach ((OpCode storedOpCode, object[] storedOperands) in stored)
                        Add(address, storedOpCode, storedOperands);
                }
                else
                {
                    AddUnmanagedCall(address, instruction.BranchTarget);
                }
                break;
            case Arm64Mnemonic.RET:
                var returnRegister = GetReturnRegisterForContext(context);
                if (returnRegister == null)
                    Add(address, OpCode.Return);
                else
                    Add(address, OpCode.Return, returnRegister);
                break;
            case Arm64Mnemonic.B when instruction.MnemonicConditionCode is not (Arm64ConditionCode.NONE or Arm64ConditionCode.AL):
                //b.cond. Disarm reports it as a plain B carrying a condition code, so without this it fell
                //through to the unconditional case below and the branch was lifted as a goto: the
                //fall-through path vanished and every if/else in the method collapsed into one arm.
                {
                    var conditionalTarget = instruction.BranchTarget;
                    var condition = new Register(null, "Z");

                    if (haveComparison && TryGetRelationalOpCode(instruction.MnemonicConditionCode, out var relational))
                    {
                        //The flag-setting instruction that produced the condition is still in hand, so the
                        //branch can be given the comparison it actually makes rather than an expression over
                        //modelled flags. The compare's own flag arithmetic is then dead and gets collected.
                        Add(address, relational, condition, ComparisonSide(instruction.MnemonicConditionCode, true), ComparisonSide(instruction.MnemonicConditionCode, false));

                        if (!ConditionalCompare.Apply(instruction.MnemonicConditionCode, condition, (opCode, operands) => Add(address, opCode, operands)))
                            goto default;

                        Add(address, OpCode.ConditionalJump, conditionalTarget, condition);
                        break;
                    }

                    //No compare in hand. Z still holds "the last result was zero", which is exactly eq/ne.
                    if (instruction.MnemonicConditionCode == Arm64ConditionCode.EQ)
                    {
                        Add(address, OpCode.ConditionalJump, conditionalTarget, condition);
                    }
                    else if (instruction.MnemonicConditionCode == Arm64ConditionCode.NE)
                    {
                        var negated = new Register(null, "TEMP");
                        Add(address, OpCode.Not, negated, condition);
                        Add(address, OpCode.ConditionalJump, conditionalTarget, negated);
                    }
                    else
                    {
                        Add(address, OpCode.NotImplemented, $"Instruction B.{instruction.MnemonicConditionCode} with no comparison in scope.");
                    }
                }
                break;
            case Arm64Mnemonic.B:
                var target = instruction.BranchTarget;

                //The body is the half-open range: an address one past the last instruction is the next
                //function, not this one. Asking `>` rather than `>=` let a tail call to the function
                //immediately following look like a branch within the body, and since no instruction is
                //there it became `Jump target not found` - which takes the whole statement with it. It is
                //the commonest single defect in the export: 106 methods, and in 96 of them the only one.
                if (target < context.UnderlyingPointer || target >= context.UnderlyingPointer + (ulong)context.RawBytes.Length)
                {
                    //Unconditional branch to outside the method, treat as call (tail-call, specifically) followed by return
                    //A tail call is still a call, and the same thing that decides its operands for a bl decides
                    //them here: one method at the address means its signature is known, and several means the
                    //body is shared between generic instantiations and only the runtime method argument says
                    //which one - so every register the convention could have used has to be handed over.
                    var returnRegister2 = GetReturnRegisterForContext(context);
                    if (context.AppContext.MethodsByAddress.TryGetValue(target, out var tailCallMethods) && tailCallMethods.Count == 1)
                        AddCall(context, GetCallResultOperand(tailCallMethods[0]), address, target);
                    //An import is a tail call as often as it is an ordinary one - `return Mathf.Atan2(y, x)`
                    //compiles to a branch, not a call - and only the `bl` path asked. Six bodies were left
                    //saying `Method not found` for a function the hook beside it already names.
                    else if (ImportedCall(context, target) is { } tailImported)
                    {
                        foreach ((OpCode tailOpCode, object[] tailOperands) in tailImported)
                            Add(address, tailOpCode, tailOperands);
                    }
                    else
                        AddUnmanagedCall(address, target);

                    if (returnRegister2 == null)
                        Add(address, OpCode.Return);
                    else
                        Add(address, OpCode.Return, returnRegister2);
                }
                else
                {
                    Add(address, OpCode.Jump, instruction.BranchTarget);
                }

                break;
            case Arm64Mnemonic.BR:
                // Branches unconditionally to an address in a register, with a hint that this is not a
                // subroutine return - so it is a tail call, and what the callee returns is what this method
                // returns. Nothing here says what the callee takes, exactly as for a call through a register,
                // so aapcs64 decides: the arguments are in x0 to x7 and the result comes back in x0. Handing
                // those over is what lets the call be recognised later; with nothing to work on it stayed
                // indirect and the statement around it was lost.
                //
                // A branch does not fall through, so the return the callee's own ret would perform is written
                // here. Without it the block ran on into whatever followed - usually the throw belonging to a
                // null check that had already been passed.
                {
                    Add(address, OpCode.IndirectCall, IndirectCallOperands(ConvertOperand(instruction, 0)));

                    var tailCallReturnRegister = GetReturnRegisterForContext(context);

                    if (tailCallReturnRegister == null)
                        Add(address, OpCode.Return);
                    else
                        Add(address, OpCode.Return, tailCallReturnRegister);
                }
                break;
            case Arm64Mnemonic.CBNZ:
            case Arm64Mnemonic.CBZ:
                {
                    //Compare and branch if (non-)zero
                    var targetAddr = (ulong)((long)instruction.Address + instruction.Op1Imm);

                    //Compare to zero...
                    Add(address, OpCode.CheckEqual, new Register(null, "Z"), ConvertOperand(instruction, 0), 0);

                    //And jump if (not) equal
                    if (instruction.Mnemonic == Arm64Mnemonic.CBZ)
                    {
                        Add(address, OpCode.ConditionalJump, targetAddr, new Register(null, "Z"));
                    }
                    else
                    {
                        Add(address, OpCode.Not, new Register(null, "TEMP"), new Register(null, "Z"));
                        Add(address, OpCode.ConditionalJump, targetAddr, new Register(null, "TEMP"));
                    }
                }
                break;

            case Arm64Mnemonic.CMP:
            case Arm64Mnemonic.FCMP:
                var op1 = ConvertOperand(instruction, 0);
                var op2 = ConvertOperand(instruction, 1);
                var temp = new Register(null, "TEMP");

                Add(address, OpCode.Subtract, temp, op1, op2);
                Add(address, OpCode.CheckEqual, new Register(null, "Z"), temp, 0);
                RecordComparison(op1, op2);
                break;

            case Arm64Mnemonic.TBNZ:
            // TBNZ R<t>, #imm, label
            // test bit and branch if NonZero
            case Arm64Mnemonic.TBZ:
                // TBZ R<t>, #imm, label
                // test bit and branch if Zero
                {
                    //Test one bit and branch on it, which is how a bool is tested: bit 0 of the byte holding it.
                    //What was here masked nothing and then branched on the zero flag, which belongs to whatever
                    //set it last - so the branch went the wrong way whenever that was not the same question, and
                    //the block structure around it was wrong with it.
                    var targetAddr = (ulong)((long)instruction.Address + instruction.Op2Imm);
                    var bit = 1L << (int)instruction.Op1Imm;
                    var masked = new Register(null, "TEMP");
                    var tested = new Register(null, "Z");

                    Add(address, OpCode.And, masked, ConvertOperand(instruction, 0), bit);
                    Add(address, instruction.Mnemonic == Arm64Mnemonic.TBZ ? OpCode.CheckEqual : OpCode.CheckNotEqual, tested, masked, 0);
                    Add(address, OpCode.ConditionalJump, targetAddr, tested);
                }
                break;
            case Arm64Mnemonic.UBFM:
                //A bitfield move is a shift in whichever direction its two immediates put it - see
                //BitfieldMove.cs. The translation below is only the extract half, and masks the wrong width.
                if (BitfieldMove.Apply(instruction, ConvertOperand(instruction, 0), ConvertOperand(instruction, 1), (opCode, operands) => Add(address, opCode, operands)))
                    break;

                // UBFM dest, src, #<immr>, #<imms>
                // dest = (src >> #<immr>) & ((1 << #<imms>) - 1)
                {
                    var dest3 = ConvertOperand(instruction, 0);
                    Add(address, OpCode.Move, dest3, ConvertOperand(instruction, 1)); // dest = src
                    Add(address, OpCode.ShiftRight, dest3, dest3, ConvertOperand(instruction, 2)); // dest = dest >> #<immr>
                    var imms = (int)instruction.Op3Imm;
                    Add(address, OpCode.And, dest3, dest3, (1 << imms) - 1); // dest = dest & constexpr { ((1 << #<imms>) - 1) }
                }
                break;

            case Arm64Mnemonic.MUL:
            case Arm64Mnemonic.FMUL:
                //Multiply is (dest, src1, src2)
                Add(address, OpCode.Multiply, ConvertOperand(instruction, 0), ConvertOperand(instruction, 1), ConvertOperand(instruction, 2));
                break;

            case Arm64Mnemonic.ADD:
            case Arm64Mnemonic.FADD:
                //Moving the stack pointer is not a value computation, it is the frame growing or shrinking.
                //It has to be recorded as such or two slots addressed from different stack pointer values
                //end up sharing a name: a method that spills at [sp+20] and then drops sp by 0x80 for a
                //local addresses that local at [sp+20] too, and the two are not the same memory.
                if (IsStackPointerAdjustment(instruction))
                {
                    Add(address, OpCode.ShiftStack, (int)instruction.Op2Imm);
                    break;
                }

                //Taking the address of a stack slot, which is how a value type is handed to something that
                //reads it through a pointer - boxing, and every by-reference argument. Register 31 is the
                //stack pointer in this encoding, not the zero register, so it must not be read as a zero:
                //that turned `&local` into the number 24 and lost the value entirely.
                if (IsStackSlotAddress(instruction))
                {
                    Add(address, OpCode.Move, ConvertOperand(instruction, 0), new StackSlotAddress((int)instruction.Op2Imm));
                    break;
                }

                //Add is (dest, src1, src2)
                Add(address, OpCode.Add, ConvertOperand(instruction, 0), ConvertOperand(instruction, 1), ShiftedOperand(2));
                break;

            case Arm64Mnemonic.SUB:
            case Arm64Mnemonic.FSUB:
                if (IsStackPointerAdjustment(instruction))
                {
                    Add(address, OpCode.ShiftStack, (int)-instruction.Op2Imm);
                    break;
                }

                //Sub is (dest, src1, src2)
                Add(address, OpCode.Subtract, ConvertOperand(instruction, 0), ConvertOperand(instruction, 1), ShiftedOperand(2));
                break;

            case Arm64Mnemonic.AND:
                //And is (dest, src1, src2)
                Add(address, OpCode.And, ConvertOperand(instruction, 0), ConvertOperand(instruction, 1), BitwiseOperand(context, instruction, ShiftedOperand(2)));
                break;

            case Arm64Mnemonic.ADDS:
            case Arm64Mnemonic.SUBS:
            case Arm64Mnemonic.ANDS:
                var dest = ConvertOperand(instruction, 0);
                var src1 = ConvertOperand(instruction, 1);
                var src2 = BitwiseOperand(context, instruction, ConvertOperand(instruction, 2));

                var opCode = instruction.Mnemonic switch
                {
                    Arm64Mnemonic.ADDS => OpCode.Add,
                    Arm64Mnemonic.SUBS => OpCode.Subtract,
                    Arm64Mnemonic.ANDS => OpCode.And,
                    _ => OpCode.Invalid
                };

                //`subs w8, w8, #1` compares the values going in and then overwrites the register it just
                //compared, so a comparison recorded afterwards has a left side already one too small: `len < 1`
                //came out as `len - 1 < 1`, an off-by-one in every loop bound written this way. Where the
                //destination is the left operand, the comparison has to be taken before the subtraction.
                var aliased = instruction.Mnemonic == Arm64Mnemonic.SUBS && instruction.Op0Reg == instruction.Op1Reg;

                if (aliased)
                    RecordComparison(src1, src2);

                Add(address, opCode, dest, src1, src2);
                Add(address, OpCode.CheckEqual, new Register(null, "Z"), dest, 0);

                //subs is a compare that also keeps the difference, and is what the compiler emits when the
                //result is wanted too. adds and ands set the flags against zero, not against each other.
                if (aliased)
                    { }
                else if (instruction.Mnemonic == Arm64Mnemonic.SUBS)
                    RecordComparison(src1, src2);
                else
                {
                    RecordComparison(dest, 0);
                    RecordCarry(instruction, src1, (o, ops) => Add(address, o, ops));
                }
                break;

            case Arm64Mnemonic.ORR:
                //Orr is (dest, src1, src2)
                Add(address, OpCode.Or, ConvertOperand(instruction, 0), ConvertOperand(instruction, 1), BitwiseOperand(context, instruction, ShiftedOperand(2)));
                break;

            case Arm64Mnemonic.EOR:
                //Eor (aka xor) is (dest, src1, src2)
                Add(address, OpCode.Xor, ConvertOperand(instruction, 0), ConvertOperand(instruction, 1), BitwiseOperand(context, instruction, ShiftedOperand(2)));
                break;

            case Arm64Mnemonic.BLR:
                //Call through a register: a virtual, interface or delegate call. Like a call to a function
                //whose signature is unknown, aapcs64 says the arguments are in x0 to x7 and the result comes
                //back in x0, so hand those over rather than leaving the call with nothing to work on.
                Add(address, OpCode.IndirectCall, IndirectCallOperands(ConvertOperand(instruction, 0)));
                break;

            case Arm64Mnemonic.MOVK:
                //Move-keep writes one 16 bit field of a register and leaves the rest alone, which is how a
                //constant too wide for one instruction is built - most often the bit pattern of a float. Which
                //field it writes is read off the instruction, because the disassembler does not shift it
                //correctly - see MoveKeepField.
                if (instruction.Op1Kind == Arm64OperandKind.Immediate
                    && MoveKeepField(context, instruction) is var (written, mask))
                {
                    var kept = new Register(null, "TEMP");
                    var destination = ConvertOperand(instruction, 0);

                    Add(address, OpCode.And, kept, destination, ~mask);
                    Add(address, OpCode.Or, destination, kept, written);
                    break;
                }

                Add(address, OpCode.NotImplemented, $"Instruction {instruction.Mnemonic} not yet implemented.");
                break;

            case Arm64Mnemonic.SBFM:
                //The signed bitfield move is how an arithmetic shift right is encoded, and that is what a
                //branchless `Math.Max(0, x)` is built from: `x & ~(x >> 31)`. Losing the shift left the mask
                //reading as `~x`, so the whole expression stopped making sense and was commented out.
                {
                    var wide = instruction.Op0Kind == Arm64OperandKind.Register
                        && instruction.Op0Reg is >= Arm64Register.X0 and <= Arm64Register.X31;

                    if (instruction.Op3Kind == Arm64OperandKind.Immediate && instruction.Op3Imm == (wide ? 63 : 31))
                    {
                        Add(address, OpCode.ShiftRight, ConvertOperand(instruction, 0), ConvertOperand(instruction, 1), ConvertOperand(instruction, 2));
                        break;
                    }
                }

                //Every other arrangement of the same instruction: `sbfx`, `sxtb`, `sbfiz`. Only the
                //arithmetic-shift-right form above was translated, so the rest were `Not implemented` -
                //`BoardLogic::GenerateSmartBlock` lost eight statements to two of them. The shared
                //translation already tells signed from unsigned; it was simply never reached from here.
                if (BitfieldMove.Apply(instruction, ConvertOperand(instruction, 0), ConvertOperand(instruction, 1), (opCode, operands) => Add(address, opCode, operands)))
                    break;

                Add(address, OpCode.NotImplemented, $"Instruction {instruction.Mnemonic} not yet implemented.");
                break;

            case Arm64Mnemonic.MOVI:
                //Vector immediate. In compiled managed code this is almost always zeroing a float register.
                Add(address, OpCode.Move, ConvertOperand(instruction, 0), ConvertOperand(instruction, 1));
                break;

            //Conversions between widths and between integer and floating point. ISIL carries no width, and
            //the managed types either side of the conversion are what decides the cast in the emitted IL,
            //so the value simply moves.
            case Arm64Mnemonic.SCVTF:
            case Arm64Mnemonic.UCVTF:
            case Arm64Mnemonic.FCVT:
            case Arm64Mnemonic.FCVTZS:
            case Arm64Mnemonic.FCVTZU:
            case Arm64Mnemonic.SXTB:
            case Arm64Mnemonic.SXTH:
            case Arm64Mnemonic.UXTB:
            case Arm64Mnemonic.UXTH:
                //Carrying the type it produces, because a move otherwise says the two sides are the same
                //thing and the whole point of a conversion is that they are not. ConversionTarget.cs.
                Add(address, OpCode.Move,
                    [ConvertOperand(instruction, 0), ConvertOperand(instruction, 1), .. ConvertedTo(context, instruction)]);
                break;

            case Arm64Mnemonic.FDIV:
            case Arm64Mnemonic.SDIV:
            case Arm64Mnemonic.UDIV:
                Add(address, OpCode.Divide, ConvertOperand(instruction, 0), ConvertOperand(instruction, 1), ConvertOperand(instruction, 2));
                break;

            case Arm64Mnemonic.FNEG:
            case Arm64Mnemonic.NEG:
                Add(address, OpCode.Negate, ConvertOperand(instruction, 0), ConvertOperand(instruction, 1));
                break;

            //A library method small enough that il2cpp inlined it down to the one instruction the
            //architecture has for it. Named back, it is a call again.
            case Arm64Mnemonic.FABS:
            case Arm64Mnemonic.FSQRT:
            case Arm64Mnemonic.FMAXNM:
            case Arm64Mnemonic.FMINNM:
            case Arm64Mnemonic.FRINTM:
            case Arm64Mnemonic.FRINTP:
                if (MathCallOperands(context, instruction) is not { } mathCall)
                    goto default;

                Add(address, OpCode.Call, mathCall);
                break;

            case Arm64Mnemonic.CCMP:
            case Arm64Mnemonic.CCMN:
            case Arm64Mnemonic.FCCMP:
                //A comparison the architecture only makes when an earlier one came out a particular way,
                //which is what `&&` and `||` between two comparisons compile to when neither side branches.
                //ConditionalCompare.cs works out the guard and folds it into whatever reads the flags next.
                if (!haveComparison
                    || ConditionalCompare.Guard(instruction, new Register(null, ComparisonLeft), new Register(null, ComparisonRight),
                        ConvertOperand(instruction, 0), ConvertOperand(instruction, 1),
                        (opCode, operands) => Add(address, opCode, operands)) is not var (guardedLeft, guardedRight))
                    goto default;

                RecordComparison(guardedLeft, guardedRight);
                ConditionalCompare.Pend(instruction);
                break;

            case Arm64Mnemonic.FABD:
                //`Math.Abs(a - b)`, which the architecture has one instruction for - a tolerance test, and
                //the whole statement went with it.
                {
                    var difference = new Register(null, "TEMP");

                    if (AbsoluteDifferenceOperands(context, instruction, difference) is not { } absoluteCall
                        || SubtractedOperand(context, instruction) is not { } subtracted)
                        goto default;

                    Add(address, OpCode.Subtract, difference, ConvertOperand(instruction, 1), subtracted);
                    Add(address, OpCode.Call, absoluteCall);
                }
                break;

            case Arm64Mnemonic.FCVTMS:
            case Arm64Mnemonic.FCVTPS:
                //Round, then convert - the architecture does both in one instruction, and doing only the
                //second of them would be a wrong value rather than a missing one.
                {
                    var rounded = new Register(null, "TEMP");

                    if (RoundingConversionOperands(context, instruction, rounded) is not { } roundingCall)
                        goto default;

                    Add(address, OpCode.Call, roundingCall);
                    Add(address, OpCode.Move, [ConvertOperand(instruction, 0), rounded, .. ConvertedTo(context, instruction)]);
                }
                break;

            case Arm64Mnemonic.FNMUL:
                //Negated multiply: the result is -(a * b).
                {
                    var product = new Register(null, "TEMP");
                    Add(address, OpCode.Multiply, product,
                        WidenedSource(context, instruction, ConvertOperand(instruction, 1), 1, (o, ops) => Add(address, o, ops)),
                        WidenedSource(context, instruction, ConvertOperand(instruction, 2), 2, (o, ops) => Add(address, o, ops)));
                    Add(address, OpCode.Negate, ConvertOperand(instruction, 0), product);
                }
                break;

            case Arm64Mnemonic.LSLV:
                Add(address, OpCode.ShiftLeft, ConvertOperand(instruction, 0), ConvertOperand(instruction, 1), ConvertOperand(instruction, 2));
                break;

            case Arm64Mnemonic.LSRV:
                //Logical: zeroes come in at the top, which is `ulong >> n` rather than `long >> n`.
                Cpp2IL.Core.Analysis.LogicalShift.Mark(
                    Add(address, OpCode.ShiftRight, ConvertOperand(instruction, 0), ConvertOperand(instruction, 1), ConvertOperand(instruction, 2)));
                break;

            case Arm64Mnemonic.ASRV:
                Add(address, OpCode.ShiftRight, ConvertOperand(instruction, 0), ConvertOperand(instruction, 1), ConvertOperand(instruction, 2));
                break;

            //The bit-clear family: the second operand is inverted before the logical operation.
            case Arm64Mnemonic.BIC:
            case Arm64Mnemonic.ORN:
            case Arm64Mnemonic.EON:
                {
                    var inverted = new Register(null, "TEMP");
                    Add(address, OpCode.Not, inverted, ShiftedOperand(2));

                    var logical = instruction.Mnemonic switch
                    {
                        Arm64Mnemonic.BIC => OpCode.And,
                        Arm64Mnemonic.ORN => OpCode.Or,
                        _ => OpCode.Xor,
                    };

                    Add(address, logical, ConvertOperand(instruction, 0), ConvertOperand(instruction, 1), inverted);
                }
                break;

            //Multiply-accumulate. The long forms multiply the two **words**, extended to 64 bits by sign or
            //by zero - see WidenedSource in the fork, without which a magic-division constant is read as a
            //large positive number and every remainder by a constant comes out wrong.
            case Arm64Mnemonic.MADD:
            case Arm64Mnemonic.MSUB:
            case Arm64Mnemonic.SMADDL:
            case Arm64Mnemonic.UMADDL:
            case Arm64Mnemonic.SMSUBL:
            case Arm64Mnemonic.UMSUBL:
                {
                    var product = new Register(null, "TEMP");
                    Add(address, OpCode.Multiply, product,
                        WidenedSource(context, instruction, ConvertOperand(instruction, 1), 1, (o, ops) => Add(address, o, ops)),
                        WidenedSource(context, instruction, ConvertOperand(instruction, 2), 2, (o, ops) => Add(address, o, ops)));

                    var accumulator = ConvertOperand(instruction, 3);
                    if (instruction.Mnemonic is Arm64Mnemonic.MSUB or Arm64Mnemonic.SMSUBL or Arm64Mnemonic.UMSUBL)
                        Add(address, OpCode.Subtract, ConvertOperand(instruction, 0), accumulator, product);
                    else
                        Add(address, OpCode.Add, ConvertOperand(instruction, 0), accumulator, product);
                }
                break;

            case Arm64Mnemonic.SMULL:
            case Arm64Mnemonic.UMULL:
                Add(address, OpCode.Multiply, ConvertOperand(instruction, 0),
                    WidenedSource(context, instruction, ConvertOperand(instruction, 1), 1, (o, ops) => Add(address, o, ops)),
                    WidenedSource(context, instruction, ConvertOperand(instruction, 2), 2, (o, ops) => Add(address, o, ops)));
                break;

            case Arm64Mnemonic.CSET:
            case Arm64Mnemonic.CSETM:
                //cset writes the condition itself as a 0/1 value, which is what a managed bool is.
                {
                    var destination = ConvertOperand(instruction, 0);

                    if (haveComparison && TryGetRelationalOpCode(instruction.FinalOpConditionCode, out var setRelational))
                    {
                        Add(address, setRelational, destination, ComparisonSide(instruction.FinalOpConditionCode, true), ComparisonSide(instruction.FinalOpConditionCode, false));

                        if (!ConditionalCompare.Apply(instruction.FinalOpConditionCode, destination, (opCode, operands) => Add(address, opCode, operands)))
                            goto default;
                    }
                    else if (instruction.FinalOpConditionCode == Arm64ConditionCode.EQ)
                    {
                        Add(address, OpCode.Move, destination, new Register(null, "Z"));
                    }
                    else if (instruction.FinalOpConditionCode == Arm64ConditionCode.NE)
                    {
                        Add(address, OpCode.Not, destination, new Register(null, "Z"));
                    }
                    else
                    {
                        Add(address, OpCode.NotImplemented, $"Instruction CSET.{instruction.FinalOpConditionCode} with no comparison in scope.");
                    }
                }
                break;

            case Arm64Mnemonic.CINC:
            case Arm64Mnemonic.CINV:
            case Arm64Mnemonic.CNEG:
                //The same select written the short way, where both arms are the one register: `cinc wd, wn, c`
                //is `c ? n + 1 : n`. Twice before this was lifted by adding it to the `csinc` case below, and
                //twice it was worse - in the file it appears in most it took the statements that could not be
                //written from 100 to 225 - which was put down to the condition being trusted rather than
                //proven. It was not that. **The alias has two operands where `csinc` has three**, so reading a
                //third read whatever was left in it, and the arm it adjusts is the one the condition takes
                //rather than the one it does not. Both of those are wrong values, and a wrong value costs the
                //method rather than the statement.
                if (!haveComparison || !TryGetRelationalOpCode(instruction.FinalOpConditionCode, out var shortRelational))
                    goto default;
                {
                    var shortCondition = new Register(null, "Z");
                    Add(address, shortRelational, shortCondition, ComparisonSide(instruction.FinalOpConditionCode, true), ComparisonSide(instruction.FinalOpConditionCode, false));

                    if (!ConditionalCompare.Apply(instruction.FinalOpConditionCode, shortCondition, (opCode, operands) => Add(address, opCode, operands)))
                        goto default;

                    var unchanged = ConvertOperand(instruction, 1);
                    var adjusted = new Register(null, "TEMP");

                    Add(address, NotTakenArm(instruction.Mnemonic)!.Value.OpCode,
                        [adjusted, unchanged, .. NotTakenArm(instruction.Mnemonic)!.Value.Operands]);

                    Add(address, OpCode.Select, ConvertOperand(instruction, 0), shortCondition, adjusted, unchanged);
                }
                break;

            case Arm64Mnemonic.CSEL:
            case Arm64Mnemonic.FCSEL:
            case Arm64Mnemonic.CSINC:
            //The same select with the other two things the architecture can do to the arm not taken: invert it
            //or negate it. `csinv wd, wn, wzr, c` is `c ? n : -1`, which is how a search that found nothing
            //returns its answer, and `csneg wd, wn, wn, c` is `c ? n : -n`, which is an absolute value.
            case Arm64Mnemonic.CSINV:
            case Arm64Mnemonic.CSNEG:
                //Conditional select: the branchless form of a conditional expression, which is what the
                //compiler turns `x = c ? a : b` into whenever both arms are cheap.
                if (haveComparison && TryGetRelationalOpCode(instruction.FinalOpConditionCode, out var selectRelational))
                {
                    var selectCondition = new Register(null, "Z");
                    Add(address, selectRelational, selectCondition, ComparisonSide(instruction.FinalOpConditionCode, true), ComparisonSide(instruction.FinalOpConditionCode, false));

                    if (!ConditionalCompare.Apply(instruction.FinalOpConditionCode, selectCondition, (opCode, operands) => Add(address, opCode, operands)))
                        goto default;

                    var ifFalse = ConvertOperand(instruction, 2);

                    //Each of these does one thing to the value taken when the condition does not hold.
                    if (NotTakenArm(instruction.Mnemonic) is { } notTaken)
                    {
                        var adjusted = new Register(null, "TEMP");
                        Add(address, notTaken.OpCode, [adjusted, ifFalse, .. notTaken.Operands]);
                        ifFalse = adjusted;
                    }

                    Add(address, OpCode.Select, ConvertOperand(instruction, 0), selectCondition, ConvertOperand(instruction, 1), ifFalse);
                }
                else
                {
                    Add(address, OpCode.NotImplemented, $"Instruction {instruction.Mnemonic}.{instruction.FinalOpConditionCode} with no comparison in scope.");
                }
                break;

            case Arm64Mnemonic.NOP:
            case Arm64Mnemonic.MRS:
            case Arm64Mnemonic.MSR:
            case Arm64Mnemonic.DMB:
            case Arm64Mnemonic.DSB:
            case Arm64Mnemonic.ISB:
                //A system register access or a barrier has no managed meaning at all, and neither has an
                //instruction that does nothing. Dropping it says nothing, which is right; a placeholder in
                //its place would sit in the middle of whatever statement it landed in and take that
                //statement down with it - and `nop` was the only marker in six methods.
                Add(address, OpCode.Nop);
                break;

            default:
                Add(address, OpCode.NotImplemented, $"Instruction {instruction.Mnemonic} not yet implemented.");
                break;
        }
    }

    private object ConvertOperand(Arm64Instruction instruction, int operand)
    {
        var kind = operand switch
        {
            0 => instruction.Op0Kind,
            1 => instruction.Op1Kind,
            2 => instruction.Op2Kind,
            3 => instruction.Op3Kind,
            _ => throw new ArgumentOutOfRangeException(nameof(operand), $"Operand must be between 0 and 3, inclusive. Got {operand}")
        };

        if (kind is Arm64OperandKind.Immediate or Arm64OperandKind.ImmediatePcRelative)
        {
            var imm = operand switch
            {
                0 => instruction.Op0Imm,
                1 => instruction.Op1Imm,
                2 => instruction.Op2Imm,
                3 => instruction.Op3Imm,
                _ => throw new ArgumentOutOfRangeException(nameof(operand), $"Operand must be between 0 and 3, inclusive. Got {operand}")
            };

            if (kind == Arm64OperandKind.ImmediatePcRelative)
                imm += (long)instruction.Address + 4; //Add 4 to the address to get the address of the next instruction (PC-relative addressing is relative to the address of the next instruction, not the current one

            return imm;
        }

        if (kind == Arm64OperandKind.FloatingPointImmediate)
        {
            var imm = operand switch
            {
                0 => instruction.Op0FpImm,
                1 => instruction.Op1FpImm,
                2 => instruction.Op2FpImm,
                3 => instruction.Op3FpImm,
                _ => throw new ArgumentOutOfRangeException(nameof(operand), $"Operand must be between 0 and 3, inclusive. Got {operand}")
            };

            //The disassembler hands every floating point immediate over as a double, but the register being
            //written says how wide it really is - a float given as a double is a type the method does not take.
            if (instruction.Op0Kind == Arm64OperandKind.Register && instruction.Op0Reg is >= Arm64Register.S0 and <= Arm64Register.S31)
                return (float)imm;

            return imm;
        }

        if (kind == Arm64OperandKind.Register)
        {
            var reg = operand switch
            {
                0 => instruction.Op0Reg,
                1 => instruction.Op1Reg,
                2 => instruction.Op2Reg,
                3 => instruction.Op3Reg,
                _ => throw new ArgumentOutOfRangeException(nameof(operand), $"Operand must be between 0 and 3, inclusive. Got {operand}")
            };

            //Register 31 in a data-processing operand is the zero register, not the stack pointer, and
            //reading it always yields zero. Left as a register it became a variable nothing ever
            //assigns, so setting a field to 0, false or null read as an unexplained local instead of
            //the constant the code actually stored. Operand 0 is the destination everywhere below, and
            //writing to the zero register discards the result rather than producing one.
            if (operand > 0 && reg is Arm64Register.X31 or Arm64Register.W31)
                return 0L;

            return RegisterFor(reg);
        }

        if (kind == Arm64OperandKind.Memory)
        {
            var reg = instruction.MemBase;
            var offset = instruction.MemOffset;
            var isPreIndexed = instruction.MemIsPreIndexed;

            if (reg == Arm64Register.INVALID)
                //Offset only
                return new MemoryOperand(addend: offset);

            //Register 31 as a memory base is the stack pointer. A stack slot behaves like a variable
            //rather than like an address: modelling it as one turns a spill and its reload into a copy,
            //which type propagation follows and copy propagation then removes outright. Left as an
            //address it is a load from memory nothing can say anything about, and the statement that
            //read it is lost - which is what was happening to every spilled local and every saved
            //register in the prologue.
            if (reg == Arm64Register.X31 && instruction.MemAddendReg == Arm64Register.INVALID)
                //Through Scaled (fork): a 128-bit immediate is reported unscaled, so the slot was named at a
                //sixteenth of its offset and a `q` spill and its reload disagreed about which one they meant.
                return new StackOffset((int)Scaled(instruction, offset));

            //TODO Handle more stuff here
            return MemoryOperandFor(instruction, reg, offset);
        }

        if (kind == Arm64OperandKind.VectorRegisterElement)
        {
            var reg = operand switch
            {
                0 => instruction.Op0Reg,
                1 => instruction.Op1Reg,
                2 => instruction.Op2Reg,
                3 => instruction.Op3Reg,
                _ => throw new ArgumentOutOfRangeException(nameof(operand), $"Operand must be between 0 and 3, inclusive. Got {operand}")
            };

            var vectorElement = operand switch
            {
                0 => instruction.Op0VectorElement,
                1 => instruction.Op1VectorElement,
                2 => instruction.Op2VectorElement,
                3 => instruction.Op3VectorElement,
                _ => throw new ArgumentOutOfRangeException(nameof(operand), $"Operand must be between 0 and 3, inclusive. Got {operand}")
            };

            var width = vectorElement.Width switch
            {
                Arm64VectorElementWidth.B => "B",
                Arm64VectorElementWidth.H => "H",
                Arm64VectorElementWidth.S => "S",
                Arm64VectorElementWidth.D => "D",
                _ => throw new ArgumentOutOfRangeException(nameof(vectorElement.Width), $"Unknown vector element width {vectorElement.Width}")
            };

            var name = $"{reg.ToString().ToUpperInvariant()}.{width}{vectorElement.Index}";
            return new Register(null, name);
        }

        return $"<UNIMPLEMENTED OPERAND TYPE {kind}>";
    }

    public override BaseKeyFunctionAddresses CreateKeyFunctionAddressesInstance() => new NewArm64KeyFunctionAddresses();

    public override string PrintAssembly(MethodAnalysisContext context) => context.RawBytes.Length <= 0 ? "" : string.Join("\n", Disassembler.Disassemble(context.RawBytes.AsSpan(), context.UnderlyingPointer, new Disassembler.Options(true, true, false)).ToList());

    private object? GetReturnRegisterForContext(MethodAnalysisContext context)
    {
        var returnType = context.ReturnType;
        if (returnType.Namespace == nameof(System))
        {
            //A float comes back in the low half of v0 and a double in the low half of the same register,
            //which is why both name the same canonical register here.
            return returnType.Name switch
            {
                "Void" => null, //Void is no return
                "Double" => RegisterFor(Arm64Register.D0),
                "Single" => RegisterFor(Arm64Register.S0),
                _ => RegisterFor(Arm64Register.X0), //All other system types are x0 like any other pointer
            };
        }

        //TODO Do certain value types have different return registers?

        //Any user type is returned in x0
        return RegisterFor(Arm64Register.X0);
    }

    private List<object> GetArgumentOperandsForCall(MethodAnalysisContext contextBeingCalled)
    {
        var vectorCount = 0;
        var nonVectorCount = 0;

        var ret = new List<object>();

        //Every vector register beyond the first that a struct of floats occupies. Handed over at the end of
        //the operand list rather than beside the parameter, so that everything counting operands off the
        //parameter list - the generator included - sees exactly what it saw before.
        var alsoOccupied = new List<object>();

        //Handle 'this' if it's an instance method
        if (!contextBeingCalled.IsStatic)
        {
            ret.Add(RegisterFor(Arm64Register.X0));
            nonVectorCount++;
        }

        foreach (var parameter in contextBeingCalled.Parameters)
        {
            var paramType = parameter.ParameterType;
            if (paramType.Namespace == nameof(System))
            {
                switch (paramType.Name)
                {
                    case "Single":
                        ret.Add(RegisterFor(Arm64Register.S0 + vectorCount++));
                        break;
                    case "Double":
                        ret.Add(RegisterFor(Arm64Register.D0 + vectorCount++));
                        break;
                    default:
                        ret.Add(RegisterFor(Arm64Register.X0 + nonVectorCount++));
                        break;
                }
            }
            else if (Cpp2IL.Core.Analysis.HomogeneousFloatStruct.Count(paramType) is { } floats)
            {
                //A struct whose every field is a float travels in that many vector registers, one field to
                //each - Vector3 in s0/s1/s2, Quaternion in s0..s3. The value is named by the first of them,
                //and the rest are handed over too - see BeyondTheFirstRegister in the fork.
                ret.Add(RegisterFor(Arm64Register.S0 + vectorCount));

                for (var beyond = 1; beyond < floats; beyond++)
                    alsoOccupied.Add(RegisterFor(Arm64Register.S0 + vectorCount + beyond));

                vectorCount += floats;
            }
            else
            {
                ret.Add(RegisterFor(Arm64Register.X0 + nonVectorCount++));
            }
        }

        //After every parameter, so that nothing which counts operands off the parameter list sees them.
        ret.AddRange(alsoOccupied);

        return ret;
    }
    
    private List<object> GetArgumentOperandsForCall(MethodAnalysisContext contextBeingAnalyzed, ulong callAddr)
    {
        if (!contextBeingAnalyzed.AppContext.MethodsByAddress.TryGetValue(callAddr, out var methodsAtAddress))
            //TODO
            return [];

        //Not First(): a generic definition shares its address with an instantiation. See InstantiationAmong.
        return GetArgumentOperandsForCall(InstantiationAmong(methodsAtAddress));
    }
}
