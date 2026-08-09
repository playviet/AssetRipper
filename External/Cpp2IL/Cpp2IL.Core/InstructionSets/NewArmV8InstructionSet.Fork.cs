using System;
using System.Collections.Generic;
using System.Linq;
using Disarm;
using Cpp2IL.Core.Api;
using Cpp2IL.Core.Il2CppApiFunctions;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using Disarm.InternalDisassembly;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>
/// What this fork adds to the arm64 lifter: the operand shapes the architecture folds into an instruction -
/// a shifted register, a bitfield move, a stack slot's address - and the register naming the rest of the
/// lifting reads those through.
///
/// Kept apart from the file it belongs to so that the file stays as close to upstream as it can, and a later
/// version of Cpp2IL can be merged without the two sets of changes meeting.
/// </summary>
public partial class NewArmV8InstructionSet
{
    /// <summary>
    /// What is known about the lanes of the vector registers in the method being lifted.
    /// </summary>
    /// <remarks>
    /// One per thread, because methods are lifted in parallel and this is per-method state. Held on the
    /// instruction set it was shared between them, and the dictionaries inside it were being written from
    /// several methods at once - which showed up as 570 bodies failing to convert with an index outside the
    /// bounds of an array, thrown from inside <c>Dictionary.TryInsert</c>. It is cleared where the held
    /// comparison is, at the start of each method.
    /// </remarks>
    /// <summary>
    /// The method being lifted, so that an operand can be read back off the instruction where the
    /// disassembler's account of it is wrong. Per thread, as the lane model is and for the same reason.
    /// </summary>
    [ThreadStatic]
    private static MethodAnalysisContext? currentMethod;

    [ThreadStatic]
    private static VectorLanes? threadVectorLanes;

    private static VectorLanes vectorLanes => threadVectorLanes ??= new VectorLanes();


    /// <summary>
    /// How far apart the two halves of a load or store pair are.
    ///
    /// The width has to come from the instruction rather than from the register the operand converted to:
    /// the zero register is written <c>wzr</c> or <c>xzr</c> for the same register number, and converting it
    /// gives back the 64-bit name whichever was written. A <c>stp wzr, w9, [x8, #0x18]</c> - which is how a
    /// list is cleared, count to zero and version up in one instruction - then wrote its second half eight
    /// bytes along instead of four, landing on the field after the one it meant.
    /// </summary>
    private static int PairElementSize(Arm64Register register)
        => register switch
        {
            >= Arm64Register.V0 and <= Arm64Register.V31 => 16,
            >= Arm64Register.D0 and <= Arm64Register.D31 => 8,
            >= Arm64Register.S0 and <= Arm64Register.S31 => 4,
            >= Arm64Register.H0 and <= Arm64Register.H31 => 2,
            >= Arm64Register.W0 and <= Arm64Register.W31 => 4,
            _ => 8,
        };

    /// <summary>
    /// A call through a register, handed every register an argument could have arrived in.
    /// </summary>
    /// <remarks>
    /// Nothing at the call site says what the callee takes, so aapcs64 decides: the first eight arguments that
    /// are integers or pointers travel in x0 to x7, and the first eight that are floating point travel in v0 to
    /// v7 - two independent runs, not one. Only the general purpose half was handed over, so a call whose
    /// signature turned out to have a <c>float</c> or a <c>double</c> in it could not be rebuilt: the value had
    /// never been mentioned at the call, so nothing carried it there and there was no operand to hand to the
    /// parameter. Every pass that recovers such a call had to refuse it, and the walk it was reached through
    /// stayed in the output.
    ///
    /// Both runs are handed over here, and <see cref="Cpp2IL.Core.Analysis.Aapcs64.ArgumentsOf"/> is what picks
    /// out of them once a pass knows the signature. Naming a register the callee does not read costs nothing:
    /// an operand nothing needs is dropped with the rest of the dead code.
    /// </remarks>
    /// <summary>
    /// Where a call's result lands: the register it comes back in, or the place the caller told it to write.
    /// </summary>
    /// <remarks>
    /// A composite of more than sixteen bytes is not returned in a register at all - the caller passes
    /// somewhere to put it in <c>x8</c> and the callee writes through that pointer. See
    /// <see cref="Analysis.Aapcs64.ReturnsIndirectly"/>. The generator already stores a call result into
    /// <c>[local]</c> by writing the base local, so a memory destination is something it can express.
    /// </remarks>
    private object? GetCallResultOperand(MethodAnalysisContext callee)
        => Analysis.Aapcs64.ReturnsIndirectly(callee)
            ? new MemoryOperand(RegisterFor(Arm64Register.X8))
            : GetReturnRegisterForContext(callee);

    /// <summary>
    /// Emits the call an imported function was, where the branch goes to a stub that jumps to one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A call to <c>sinf</c> or <c>memcpy</c> goes to a four-instruction stub in <c>.plt</c>, which no method
    /// table names - so it resolved to nothing and the statement was written out as
    /// <c>Method not found @4AFA510</c>. <c>ElfFile.ImportedFunctionAt</c> follows the stub to the jump slot
    /// and the slot's relocation to the symbol, which is exactly how the loader decides what the call
    /// reaches.
    /// </para>
    /// <para>
    /// <b>It has to be done here rather than in a later pass</b>, because the fallback for an unknown callee
    /// hands over <c>x0</c> to <c>x7</c> and nothing else - and a floating point argument is in <c>v0</c>. By
    /// the time the ISIL exists the argument is not among the operands at all, so no pass can put it back;
    /// here the convention is still known and the right registers can be named outright.
    /// </para>
    /// <para>
    /// Only the ones that are exactly a managed method of the same shape. <c>modf</c> and <c>sincosf</c> hand
    /// answers back through pointers, <c>exp2f</c> has no counterpart, and <c>memcpy</c> is a struct
    /// assignment rather than a call - each of those is a different problem and is left as it was.
    /// <c>__stack_chk_fail</c> is dropped: it is what the guard branch calls when the stack has been
    /// smashed, and recovered C# has no stack to smash.
    /// </para>
    /// </remarks>
    private static List<(OpCode OpCode, object[] Operands)>? ImportedCall(MethodAnalysisContext context, ulong target)
    {

        //A jump straight back out is a thunk, not a function, and the call goes wherever it points:
        //`0x2183F68` jumps to `il2cpp_runtime_class_init_actual` and `0x2183F70` to `il2cpp_vm_object_box`,
        //both of which `MetadataResolver` already knows by name.
        //
        //**Never where the address is already a key function.** Cpp2IL's key functions *are* thunks -
        //`il2cpp_codegen_write_barrier` is `0x2183D8C`, `raise_exception` `0x2183DC4`, `SzArrayNew`
        //`0x2183ED8` - and the resolver matches a call target against exactly those addresses. Following
        //them unguarded moved every one of those off its own name at once: `full` 308 -> 271, roundtrip
        //1216 -> 897, decisions 96.6% -> 91.8%. It is the worst regression this fork has measured.
        if (!context.AppContext.GetOrCreateKeyFunctionAddresses().IsKeyFunctionAddress(target)
            && FollowThunks(context.AppContext.Binary, target) is var jumped && jumped != target)
        {
            //Two thunks that reach the same place are the same function, and Cpp2IL knows one of them by
            //name: `0x2183DE8` is `il2cpp_codegen_initialize_runtime_metadata`, whose whole body is one
            //call to `0x21E9D18`, which 56 sites reach by another thunk and nothing names.
            //
            //This broke 691 bodies the first time it was tried, because the generator answers that key
            //function by emitting nothing while the lifted call still carries a result nothing then stores
            //- it now stores a default - and a body AssetRipper fills with the generator's exception has
            //one statement and no marker, so every scorer read the regression as a win. See
            //`il2cpp-a-thrown-body-scores-as-a-whole-one`: count generation failures beside the scorers if
            //this is ever touched again.
            var followed = KeyFunctionReaching(context, jumped) ?? jumped;

            var thunked = new List<object> { followed, RegisterFor(Arm64Register.X0) };

            for (var argument = 0; argument < Aapcs64.RegistersPerRun; argument++)
                thunked.Add(RegisterFor(Arm64Register.X0 + argument));

            return [(OpCode.Call, thunked.ToArray())];
        }

        if (CompareExchangeOperands(context, target) is { } exchange)
            return [(OpCode.Call, exchange)];

        if (context.AppContext.Binary is not LibCpp2IL.Elf.ElfFile binary
            || binary.ImportedFunctionAt(target) is not { } import)
            return null;

        if (import == "__stack_chk_fail")
            return [(OpCode.Nop, [])];

        //`modf` is the *double* form and ends in `f` all the same - the only one in libm that does, so the
        //suffix rule made `bare` come out as "mod" and the hook written for it matched nothing.
        var isDouble = import is "modf" || !import.EndsWith("f");
        var bare = import is "modf" ? "modf" : isDouble ? import : import[..^1];

        //An import that hands its answers back through pointers is one call and two statements. A call may
        //write into `[reg]` - that is how a big struct comes back through `x8` - and the register holding a
        //pointer to a local *is* that local here, because taking a slot's address makes the two one variable.
        //So each answer is written straight where it goes and no temporary is needed.
        if (bare is "sincos" && Analysis.MathIntrinsics.Resolve(context.AppContext, ["Sin"], 1, isDouble) is { } sine
            && Analysis.MathIntrinsics.Resolve(context.AppContext, ["Cos"], 1, isDouble) is { } cosine)
        {
            return
            [
                (OpCode.Call, [sine, RegisterFor(Arm64Register.X0), RegisterFor(Arm64Register.V0)]),
                (OpCode.Call, [cosine, RegisterFor(Arm64Register.X1), RegisterFor(Arm64Register.V0)]),
            ];
        }

        //`modf(x, &whole)` writes the whole part where it is told and gives back what is left of it.
        if (bare is "modf" && Analysis.MathIntrinsics.Resolve(context.AppContext, ["Truncate"], 1, isDouble) is { } whole)
        {
            return
            [
                (OpCode.Call, [whole, RegisterFor(Arm64Register.X0), RegisterFor(Arm64Register.V0)]),
                (OpCode.Subtract, [RegisterFor(Arm64Register.V0), RegisterFor(Arm64Register.V0),
                    RegisterFor(Arm64Register.X0)]),
            ];
        }

        //`exp2f(x)` is two raised to `x`, and the language has no name for that on its own - which is why the
        //table below skips it. `Pow` is exactly it once the base is written down, and a constant in an
        //argument position is what every other literal there already is. Six bodies wait on this one call.
        if (bare == "exp2" && Analysis.MathIntrinsics.Resolve(context.AppContext, ["Pow"], 2, isDouble) is { } raise)
        {
            return [(OpCode.Call, [raise, RegisterFor(Arm64Register.V0),
                isDouble ? 2.0 : 2.0f, RegisterFor(Arm64Register.V0)])];
        }

        string[]? names = bare switch
        {
            "sin" => ["Sin"], "cos" => ["Cos"], "tan" => ["Tan"],
            "asin" => ["Asin"], "acos" => ["Acos"], "atan" => ["Atan"],
            "exp" => ["Exp"], "log" => ["Log"], "log10" => ["Log10"],
            "sqrt" => ["Sqrt"], "fabs" => ["Abs"],
            "ceil" => ["Ceil", "Ceiling"], "floor" => ["Floor"], "round" => ["Round"],
            "pow" => ["Pow"], "atan2" => ["Atan2"], "fmin" => ["Min"], "fmax" => ["Max"],
            _ => null,
        };

        if (names == null)
            return null;

        var parameters = bare is "pow" or "atan2" or "fmin" or "fmax" ? 2 : 1;

        if (Analysis.MathIntrinsics.Resolve(context.AppContext, names, parameters, isDouble) is not { } method)
            return null;

        //Single and double precision use the same physical registers, and `RegisterFor` gives them one name -
        //so `v0` here is the same variable a `d0` or `s0` operand elsewhere resolves to.
        var operands = new List<object> { method, RegisterFor(Arm64Register.V0) };

        for (var argument = 0; argument < parameters; argument++)
            operands.Add(RegisterFor(Arm64Register.V0 + argument));

        return [(OpCode.Call, operands.ToArray())];
    }

    /// <summary>
    /// The operands of an atomic compare-and-exchange, where the target is the helper that does one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every <c>event</c> accessor in the game compiles to the same loop - combine the delegate, then
    /// atomically install it and go round again if somebody else got there first - and the install is a call
    /// to a runtime helper no method table names. <b>62 sites</b>, the largest unnamed target left:
    /// </para>
    /// <code>
    /// Delegate obj2 = Delegate.Combine(obj, value);
    /// _ = "Method not found @21BC67C";
    /// bool flag = (object)obj3 != obj;
    /// </code>
    /// <para>
    /// The helper is <c>Interlocked.CompareExchange</c> and its arguments are already in the right order -
    /// <c>f(address, value, comparand)</c>. It returns the comparand where the swap took and whatever was
    /// there otherwise, which is the same thing as returning the original value in both cases.
    /// </para>
    /// <para>
    /// <b>Recognised by shape rather than by address</b>, so nothing here is pinned to this binary: the body
    /// calls a primitive that begins with <c>casal</c> - the architecture's compare-and-swap - and carries a
    /// <c>dmb ish</c> of its own, which is the publish barrier a managed reference needs and an ordinary
    /// arithmetic helper does not have.
    /// </para>
    /// </remarks>
    private static object[]? CompareExchangeOperands(MethodAnalysisContext context, ulong target)
    {
        if (!IsCompareExchange(context.AppContext.Binary, target))
            return null;

        if (context.AppContext.GetAssemblyByName("mscorlib")
                ?.GetTypeByFullName("System.Threading.Interlocked") is not { } interlocked)
            return null;

        MethodAnalysisContext? found = null;

        foreach (var candidate in interlocked.Methods)
        {
            if (candidate is not { IsStatic: true, Name: "CompareExchange" }
                || candidate.Parameters.Count != 3
                || candidate.GenericParameters.Count != 0
                || candidate.Parameters[0].ParameterType is not ByRefTypeAnalysisContext { ElementType.IsValueType: false })
                continue;

            //Two overloads over references would be an alias, and nothing says which one was meant.
            if (found != null)
                return null;

            found = candidate;
        }

        return found == null
            ? null
            : [found, RegisterFor(Arm64Register.X0), RegisterFor(Arm64Register.X0),
                RegisterFor(Arm64Register.X1), RegisterFor(Arm64Register.X2)];
    }

    /// <summary>Whether the function at this address installs a pointer atomically and publishes it.</summary>
    private static bool IsCompareExchange(LibCpp2IL.Il2CppBinary binary, ulong target)
    {
        if (Words(binary, target, 16) is not { } body)
            return false;

        var published = false;
        var swaps = false;

        for (var index = 0; index < body.Length; index++)
        {
            var word = body[index];

            //DMB ISH - the barrier that makes the new value visible before anything reads it.
            if (word == 0xD503_3BBF)
                published = true;

            //BL somewhere; the primitive it reaches begins with the compare-and-swap itself.
            if ((word & 0xFC00_0000) != 0x9400_0000)
                continue;

            var offset = (long)(word & 0x03FF_FFFF);

            if ((offset & 0x0200_0000) != 0)
                offset -= 0x0400_0000;

            //From the branch itself, not from the front of the function - the offset a branch carries is
            //relative to its own address. Reading only the first instruction, as the thunk follower does,
            //hides that: there the two are the same, and here they are 0x18 apart.
            var called = (ulong)((long)target + index * 4 + (offset << 2));

            if (Words(binary, called, 8) is { } primitive)
                foreach (var inner in primitive)
                    //CASAL Xs, Xt, [Xn] at sixty-four bits.
                    if ((inner & 0xFFE0_FC00) == 0xC8E0_FC00)
                        swaps = true;
        }

        return published && swaps;
    }

    /// <summary>The instructions at an address, or null where there are none to read.</summary>
    private static uint[]? Words(LibCpp2IL.Il2CppBinary binary, ulong address, int count)
    {
        try
        {
            return binary.ReadClassArrayAtVirtualAddress<uint>(address, count);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The key function whose own body is a single call to this address, where there is one.</summary>
    /// <remarks>
    /// Only where exactly one call is found before the body returns, so an ordinary function that happens to
    /// begin with a call cannot be mistaken for a wrapper of it.
    /// </remarks>
    private static ulong? KeyFunctionReaching(MethodAnalysisContext context, ulong destination)
    {
        foreach (var pair in context.AppContext.GetOrCreateKeyFunctionAddresses().Pairs)
            if (pair.Value != 0 && SingleCallIn(context.AppContext.Binary, pair.Value) == destination)
                return pair.Value;

        return null;
    }

    /// <summary>The one address a short body calls, or zero where it does not call exactly one.</summary>
    private static ulong SingleCallIn(LibCpp2IL.Il2CppBinary binary, ulong function)
    {
        if (Words(binary, function, 8) is not { } body)
            return 0;

        ulong called = 0;

        for (var index = 0; index < body.Length; index++)
        {
            //RET ends the body; anything past it belongs to whatever comes after.
            if (body[index] == 0xD65F_03C0)
                break;

            if ((body[index] & 0xFC00_0000) != 0x9400_0000)
                continue;

            //A second call, and this is a function in its own right rather than a wrapper for either.
            if (called != 0)
                return 0;

            var offset = (long)(body[index] & 0x03FF_FFFF);

            if ((offset & 0x0200_0000) != 0)
                offset -= 0x0400_0000;

            called = (ulong)((long)function + index * 4 + (offset << 2));
        }

        return called;
    }

    /// <summary>Where a chain of one-instruction jumps ends, or the address itself where it is not one.</summary>
    /// <remarks>
    /// Only an unconditional <c>b</c> as the <b>first</b> instruction of the target counts, which is what a
    /// thunk is: nothing else could sit in front of it. Bounded, so a jump that somehow points at itself
    /// cannot spin.
    /// </remarks>
    private static ulong FollowThunks(LibCpp2IL.Il2CppBinary binary, ulong target)
    {
        for (var step = 0; step < 4; step++)
        {
            uint word;

            try
            {
                word = binary.ReadClassArrayAtVirtualAddress<uint>(target, 1)[0];
            }
            catch
            {
                return target;
            }

            //B label - the offset is signed over 26 bits and counts instructions.
            if ((word & 0xFC00_0000) != 0x1400_0000)
                return target;

            var offset = (long)(word & 0x03FF_FFFF);

            if ((offset & 0x0200_0000) != 0)
                offset -= 0x0400_0000;

            target = (ulong)((long)target + (offset << 2));
        }

        return target;
    }

    private static object[] IndirectCallOperands(object target)
    {
        var operands = new List<object> { target, RegisterFor(Arm64Register.X0) };

        for (var argument = 0; argument < Aapcs64.RegistersPerRun; argument++)
            operands.Add(RegisterFor(Arm64Register.X0 + argument));

        for (var argument = 0; argument < Aapcs64.RegistersPerRun; argument++)
            operands.Add(RegisterFor(Arm64Register.V0 + argument));

        return operands.ToArray();
    }

    /// <summary>
    /// The operands of the call an inlined floating point library method came from, or null where the method
    /// it stands for is not in this game's libraries and there is therefore nothing to name.
    /// </summary>
    /// <remarks>
    /// The architecture has one instruction apiece for absolute value, square root and the two-sided minimum
    /// and maximum, so a call to the library method that does one of those is compiled away to it entirely.
    /// Nothing in the instruction names a method - and an instruction the lifter does not translate takes the
    /// whole statement holding it, so one <c>Mathf.Abs</c> in the middle of an expression cost the expression.
    ///
    /// <c>FMAXNM</c>/<c>FMINNM</c> rather than <c>FMAX</c>/<c>FMIN</c> is what a library minimum and maximum
    /// compile to: the NM pair returns the number when one side is a NaN, which is what the managed methods
    /// promise, and the plain pair returns the NaN, which is not the same method and is not translated here.
    /// </remarks>
    private object[]? MathCallOperands(MethodAnalysisContext context, Arm64Instruction instruction)
    {
        //The architecture has a single-precision and a double-precision form of each of these, told apart by
        //the register the result goes to. Which library method was inlined follows from that and nothing else.
        var isDouble = instruction.Op0Reg is >= Arm64Register.D0 and <= Arm64Register.D31;

        //Both names are offered where the two libraries spell one method differently.
        var (names, parameters) = instruction.Mnemonic switch
        {
            Arm64Mnemonic.FABS => (new[] { "Abs" }, 1),
            Arm64Mnemonic.FSQRT => (new[] { "Sqrt" }, 1),
            Arm64Mnemonic.FMAXNM => (new[] { "Max" }, 2),
            Arm64Mnemonic.FMINNM => (new[] { "Min" }, 2),
            Arm64Mnemonic.FRINTM => (new[] { "Floor" }, 1),
            Arm64Mnemonic.FRINTP => (new[] { "Ceil", "Ceiling" }, 1),
            _ => (null!, 0),
        };

        //Only where the instruction works on one value. A rounding over a vector arrangement rounds every lane,
        //and a single call claiming the register would say the low lane is the whole of it - which is the shape
        //`VectorLanes` exists to take apart, not one to paper over here.
        if (instruction.Op0Arrangement != Arm64ArrangementSpecifier.None)
            return null;

        if (names == null || MathIntrinsics.Resolve(context.AppContext, names, parameters, isDouble) is not { } method)
            return null;

        var operands = new List<object> { method, ConvertOperand(instruction, 0) };

        for (var argument = 1; argument <= parameters; argument++)
            operands.Add(ConvertOperand(instruction, argument));

        return operands.ToArray();
    }

    /// <summary>
    /// The rounding half of <c>fcvtms</c> and <c>fcvtps</c>, which round and then convert in one instruction.
    /// </summary>
    /// <remarks>
    /// <c>fcvtzs</c> truncates, which is what a cast to an integer does, so it needs nothing but the
    /// conversion. These two round down and up first - <c>Mathf.FloorToInt</c> and <c>Mathf.CeilToInt</c> -
    /// and lifting them as though they truncated would be a wrong value in every one of them. Which width the
    /// library method takes follows from the register being read, not the one being written: the result is an
    /// integer either way.
    /// </remarks>
    private object[]? RoundingConversionOperands(MethodAnalysisContext context, Arm64Instruction instruction, Register rounded)
    {
        var names = instruction.Mnemonic switch
        {
            Arm64Mnemonic.FCVTMS => new[] { "Floor" },
            Arm64Mnemonic.FCVTPS => ["Ceil", "Ceiling"],
            _ => null,
        };

        var isDouble = instruction.Op1Reg is >= Arm64Register.D0 and <= Arm64Register.D31;

        return names is null || MathIntrinsics.Resolve(context.AppContext, names, 1, isDouble) is not { } method
            ? null
            : [method, rounded, ConvertOperand(instruction, 1)];
    }

    /// <summary>
    /// The type an arm64 conversion produces, as the operand the move carries to say so.
    /// </summary>
    /// <remarks>
    /// Which of the two widths it is follows from the register the result goes to, exactly as it does for the
    /// inlined library calls: <c>d</c> and <c>x</c> are the wide ones. The signed and unsigned forms produce
    /// the same managed type here because ISIL has no unsigned - what the sign changes is the value, and the
    /// value is what the emitted conversion computes.
    /// </remarks>
    private static object[] ConvertedTo(MethodAnalysisContext context, Arm64Instruction instruction)
    {
        var wide = instruction.Op0Reg is >= Arm64Register.X0 and <= Arm64Register.X31
            or >= Arm64Register.D0 and <= Arm64Register.D31;

        var types = context.AppContext.SystemTypes;

        TypeAnalysisContext? produced = instruction.Mnemonic switch
        {
            Arm64Mnemonic.SCVTF or Arm64Mnemonic.UCVTF or Arm64Mnemonic.FCVT
                => wide ? types.SystemDoubleType : types.SystemSingleType,

            //A sign or zero extension has to say the *narrow* type, not the word it is extended into. Saying
            //the word emits conv.i4, which for a value already in a 32-bit register does nothing, and
            //`sxtb w0, w1` then kept the whole of w1. conv.i1 narrows and still leaves an int on the stack,
            //which is what the instruction does.
            Arm64Mnemonic.SXTB => types.SystemSByteType,
            Arm64Mnemonic.UXTB => types.SystemByteType,
            Arm64Mnemonic.SXTH => types.SystemInt16Type,
            Arm64Mnemonic.UXTH => types.SystemUInt16Type,

            Arm64Mnemonic.FCVTZS or Arm64Mnemonic.FCVTZU
                or Arm64Mnemonic.FCVTMS or Arm64Mnemonic.FCVTPS
                => wide ? types.SystemInt64Type : types.SystemInt32Type,

            _ => null,
        };

        return produced is null ? [] : [new ConversionTarget(produced)];
    }

    /// <summary>
    /// What a conditional select does to the arm it takes when the condition does not hold, for the three
    /// forms that do something to it rather than taking it as it is.
    /// </summary>
    /// <remarks>
    /// The architecture folds a small adjustment into the select so that `c ? n : n + 1`, `c ? n : ~n` and
    /// `c ? n : -n` are one instruction each. Undoing the fold is a single operation in front of the select,
    /// which is why all three lift through the same case.
    /// </remarks>
    private static (OpCode OpCode, object[] Operands)? NotTakenArm(Arm64Mnemonic mnemonic) => mnemonic switch
    {
        Arm64Mnemonic.CSINC or Arm64Mnemonic.CINC => (OpCode.Add, [(object)1]),
        Arm64Mnemonic.CSINV or Arm64Mnemonic.CINV => (OpCode.Not, []),
        Arm64Mnemonic.CSNEG or Arm64Mnemonic.CNEG => (OpCode.Negate, []),
        _ => null,
    };

    /// <summary>
    /// The number an eight bit floating point immediate stands for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>fmov s1, #-1.0</c> does not carry the bits of the float; it carries eight bits that expand into one
    /// - a sign, an exponent built out of a single bit, and six of mantissa. The disassembler hands those
    /// eight bits over as though they were the value, so <c>#-1.0</c> arrives as <c>-1</c> and <c>#1.0</c> as
    /// <c>0</c>. 644 of them in this game's own assembly, every one a constant the recovered source states
    /// wrongly - and it compiles, because a number is a number.
    /// </para>
    /// <para>
    /// The expansion is the architecture's <c>VFPExpandImm</c>, and it is exact: eight bits name one of 256
    /// values and nothing is approximated.
    /// </para>
    /// </remarks>
    private static object? FloatImmediate(MethodAnalysisContext context, Arm64Instruction instruction)
    {
        if (VectorLanes.Word(context, instruction.Address) is not { } word)
            return null;

        //Advanced SIMD modified immediate, the vector form: 0 Q op 0111100000 abc cmode 0 1 defgh Rd.
        //`FMOV Vd.2S, #1.0` is cmode 1111 with op nought, and the disassembler hands back nought for its
        //immediate - a silent wrong value at 191 sites, since a broadcast of nought reads as plausible.
        if ((word >> 19 & 0x3FF) == 0b0111100000 && (word >> 12 & 0xF) == 0b1111
            && (word >> 29 & 1) == 0 && (word >> 10 & 3) == 0b01)
        {
            return Expanded((int)((word >> 16 & 7) << 5 | (word >> 5 & 0x1F)));
        }

        //Floating point immediate, scalar: 00011110 type 1 imm8 100 00000 Rd.
        if (word >> 24 != 0b00011110 || (word >> 21 & 1) != 1 || (word >> 10 & 0x1F) != 0b10000)
            return null;

        var immediate = (int)(word >> 13 & 0xFF);

        return (word >> 22 & 3) switch
        {
            0 => Expanded(immediate),
            1 => ExpandedWide(immediate),
            _ => null,
        };
    }

    /// <summary>The single precision value eight bits expand into.</summary>
    internal static float Expanded(int immediate)
    {
        var exponentIsSmall = (immediate >> 6 & 1) == 1;

        var bits = (uint)(immediate >> 7 & 1) << 31
            | (exponentIsSmall ? 0u : 1u) << 30
            | (exponentIsSmall ? 0x1Fu : 0u) << 25
            | (uint)(immediate & 0x3F) << 19;

        return BitConverter.Int32BitsToSingle(unchecked((int)bits));
    }

    internal static double ExpandedWide(int immediate)
    {
        var exponentIsSmall = (immediate >> 6 & 1) == 1;

        var bits = (ulong)(immediate >> 7 & 1) << 63
            | (exponentIsSmall ? 0ul : 1ul) << 62
            | (exponentIsSmall ? 0xFFul : 0ul) << 54
            | (ulong)(immediate & 0x3F) << 48;

        return BitConverter.Int64BitsToDouble(unchecked((long)bits));
    }

    /// <summary>
    /// How far a load or store moves its base register <i>after</i> using it, where it is one that does.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Post-indexed addressing reads or writes at the base and then advances it, which is how a loop over an
    /// array is compiled once the index has been turned into a walking pointer:
    /// <c>ldr w11, [x8], #4</c>. The pre-indexed form, which advances first, is already handled; this one was
    /// not, so the access was taken for a plain offset - <c>[x8 + 4]</c>, the element after the one meant -
    /// and the pointer never moved at all. `Corpus.CountOf` counts how many cells equal a colour and came
    /// back reading <c>cells[1]</c> every time round.
    /// </para>
    /// <para>
    /// The disassembler reports the offset but nothing that tells the two forms apart, so the two bits that
    /// do are read off the instruction: 01 after the base register means post-indexed, 11 means pre.
    /// </para>
    /// </remarks>
    private static long? WritebackAfterUse(MethodAnalysisContext context, Arm64Instruction instruction)
    {
        if (VectorLanes.Word(context, instruction.Address) is not { } word)
            return null;

        //size 111 V 00 opc 0 imm9 01 Rn Rt
        if ((word >> 27 & 0x7) != 0b111 || (word >> 24 & 3) != 0b00
            || (word >> 21 & 1) != 0 || (word >> 10 & 3) != 0b01)
            return null;

        var immediate = (int)(word >> 12 & 0x1FF);

        return immediate >= 0x100 ? immediate - 0x200 : immediate;
    }

    /// <summary>
    /// How far a register-held index is scaled, read off the instruction.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An index held in a register is scaled by the width the instruction accesses, but only when the
    /// instruction says so - one bit decides it. The disassembler drops that bit on the extended forms, so
    /// <c>ldr x0, [x8, w1, uxtw #3]</c> arrives saying the index is scaled by one. Over the binary it is wrong
    /// 1295 times and right 4566, and what it costs is not a placeholder: an index scaled by one where the
    /// elements are eight bytes apart is a different element, quietly.
    /// </para>
    /// <para>
    /// The width is the access width, which for a vector register is not the size field alone - a 128 bit
    /// access is written with the size field at zero and the opcode saying the rest.
    /// </para>
    /// </remarks>
    private static int? IndexShift(Arm64Instruction instruction)
    {
        if (currentMethod is not { } method || VectorLanes.Word(method, instruction.Address) is not { } word)
            return null;

        //Register offset: size 111 V 00 opc 1 Rm option S 10 Rn Rt.
        if ((word >> 27 & 7) != 0b111 || (word >> 24 & 3) != 0b00
            || (word >> 21 & 1) != 1 || (word >> 10 & 3) != 0b10)
            return null;

        if ((word >> 12 & 1) == 0)
            return 0;

        var size = (int)(word >> 30 & 3);

        return (word >> 26 & 1) == 1 && size == 0 && (word >> 22 & 3) >= 2 ? 4 : size;
    }

    /// <summary>
    /// The offset a load or store really has, for the one width the disassembler does not scale.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The immediate of a load or store is in units of the width it accesses, and the disassembler multiplies
    /// it back out - for a byte, a half, a word, a double word and a <c>d</c> register. For a <b>128 bit</b>
    /// one it does not, so every <c>ldr q</c> and <c>str q</c> arrives with an offset sixteen times too small.
    /// </para>
    /// <para>
    /// What that looks like in the output is a field read at an offset that belongs to no field. The static
    /// constructor of <c>CFramework.ColorExtension</c> fills an array from its hundred and forty static
    /// <c>Color</c> fields, each sixteen bytes apart, and every one of them was read at an offset of one, two,
    /// three - inside the first field rather than at the next - so a hundred and seven reads resolved to
    /// nothing. It is not a vector problem; it is every 128 bit access in the game.
    /// </para>
    /// </remarks>
    private static long Scaled(Arm64Instruction instruction, long offset)
    {
        //Only the scaled immediate form. `ldur`/`stur` carry a byte offset already, an offset held in a
        //register is not scaled at all, and a pre- or post-indexed one is a byte count too.
        if (instruction.Mnemonic is not (Arm64Mnemonic.LDR or Arm64Mnemonic.STR)
            || instruction.MemAddendReg != Arm64Register.INVALID
            || instruction.MemIsPreIndexed
            || instruction.Op0Reg is not (>= Arm64Register.V0 and <= Arm64Register.V31))
            return offset;

        return offset * 16;
    }

    /// <summary>
    /// The call half of <c>fabd</c>, which is <c>Math.Abs</c> over a difference the caller works out first.
    /// </summary>
    /// <remarks>
    /// It is the same inlining <see cref="MathCallOperands"/> undoes, one step further along: the compiler
    /// folded the subtraction into the call as well, so both have to be given back for the statement to
    /// stand. Which of the two libraries' <c>Abs</c> it was follows from the width of the result, exactly as
    /// it does there.
    /// </remarks>
    private object[]? AbsoluteDifferenceOperands(MethodAnalysisContext context, Arm64Instruction instruction, Register difference)
    {
        var isDouble = instruction.Op0Reg is >= Arm64Register.D0 and <= Arm64Register.D31;

        return MathIntrinsics.Resolve(context.AppContext, ["Abs"], 1, isDouble) is not { } method
            ? null
            : [method, ConvertOperand(instruction, 0), difference];
    }

    /// <summary>
    /// The register <c>fabd</c> subtracts, which has to be read off the instruction because the disassembler
    /// reports it as the register being subtracted from.
    /// </summary>
    /// <remarks>
    /// <c>fabd s0, s8, s10</c> comes back as <c>FABD S0, S8, S8</c>, so taking the operands as given makes the
    /// difference between a value and itself - zero, and it compiles, and it is wrong everywhere the
    /// instruction appears. The second source is bits 20 to 16 of the word.
    /// </remarks>
    private static object? SubtractedOperand(MethodAnalysisContext context, Arm64Instruction instruction)
    {
        if (VectorLanes.Word(context, instruction.Address) is not { } word)
            return null;

        var register = (int)(word >> 16 & 0x1F);

        return RegisterFor((instruction.Op0Reg is >= Arm64Register.D0 and <= Arm64Register.D31
            ? Arm64Register.D0
            : Arm64Register.S0) + register);
    }

    /// <summary>
    /// Adds the register a method's own runtime <c>MethodInfo</c> arrives in.
    ///
    /// il2cpp gives every managed method one as a last argument, and for a body shared between the
    /// instantiations of a generic it is the only thing telling them apart: the type arguments, the runtime
    /// class and the generic context all hang off it. Without it in the list the parameter is neither named
    /// nor typed, and every chain that starts there - a static field, a type, the base constructor of a
    /// generic type - reads as unmanaged memory.
    ///
    /// It goes in the first general purpose register the arguments did not take. Past the eighth there is no
    /// register left for it, so nothing is added rather than naming one that means something else.
    /// </summary>
    private void AddRuntimeMethodOperand(List<object> operands, MethodAnalysisContext context)
    {
        var used = context.IsStatic ? 0 : 1;

        foreach (var parameter in context.Parameters)
        {
            //A floating point argument travels in a vector register and takes none of these.
            if (parameter.ParameterType is { Namespace: nameof(System), Name: "Single" or "Double" })
                continue;

            used++;
        }

        if (used < 8)
            operands.Add(RegisterFor(Arm64Register.X0 + used));
    }

    /// <summary>
    /// The comparison an arm64 condition code makes, given the operands of the flag-setting instruction
    /// it follows. The unsigned conditions are mapped onto the signed comparisons, matching how the x86
    /// side already recovers them: ISIL has one set of relational opcodes, and treating cs/cc/hi/ls as
    /// their signed counterparts is the existing inaccuracy rather than a new one. Conditions that test
    /// the overflow flag have no relational meaning and are refused.
    /// </summary>
    //A condition that reads the carry flag asks an **unsigned** question, and ISIL has no unsigned
    //comparison - so `HI` and `CS` are translated as `>` and `>=`, which is only right while both sides are
    //non-negative. After an **addition** it is worse than approximate: the carry is not a fact about the
    //result at all, and rendering `csel w8, w9, w8, hi` after `cmn w8, #32` as `(w8 + 32) > 0` inverts the
    //answer. `Corpus::Bits` returned 0x55C00000055 where the source says 0x70000055, and 250 sites in the
    //game are the same shape. These carry the other reading of the same flags, chosen at the consumer.
    private const string CarryLeft = "CARL";
    private const string CarryRight = "CARR";
    private const string CarryValue = "CARV";

    /// <summary>A source of a widening multiply, made to hold what the instruction actually multiplies.</summary>
    /// <remarks>
    /// <c>smaddl x8, w1, w8, xzr</c> multiplies the two **words**, sign-extended; <c>umaddl</c> zero-extends
    /// them. ISIL carries no width, so upstream lifts both as a plain multiply of whatever the 64-bit locals
    /// happen to hold - and a magic-division constant assembled by <c>movz</c>/<c>movk</c> holds
    /// <c>0x84210843</c>, which as a signed word is -2078209981, not 2216757315. Every remainder by a
    /// constant compiled this way then computed the wrong quotient: <c>Corpus::Bits</c> rotated by 15 where
    /// the source says 9, and nothing marked it.
    ///
    /// Written as a shift pair because ISIL has no conversion. Where the source is a constant - which is the
    /// whole point of these instructions - <c>ConstantFolding</c> collapses it straight back to one number.
    /// </remarks>
    internal static object WidenedSource(MethodAnalysisContext context, Arm64Instruction instruction,
        object source, int operand, Action<OpCode, object[]> emit)
    {
        var signed = instruction.Mnemonic is Arm64Mnemonic.SMADDL or Arm64Mnemonic.SMSUBL or Arm64Mnemonic.SMULL;
        var unsigned = instruction.Mnemonic is Arm64Mnemonic.UMADDL or Arm64Mnemonic.UMSUBL or Arm64Mnemonic.UMULL;

        if (!signed && !unsigned)
            return source;

        //Two conversions and not a shift pair. A shift pair says the right thing about the value and nothing
        //about the width, and `x << 32 >> 32` on a 32-bit local is **a no-op in C#** - the language takes the
        //count modulo the operand's width. So the sign came out right and the product stayed 32 bits, and the
        //`asr x8, x8, #32` that follows a magic multiply then read the high word of a number that had none.
        var types = context.AppContext.SystemTypes;
        var narrow = new Register(null, "NARROW" + operand);
        var widened = new Register(null, "WIDE" + operand);

        emit(OpCode.Move, [narrow, source, new ConversionTarget(unsigned ? types.SystemUInt32Type : types.SystemInt32Type)]);
        emit(OpCode.Move, [widened, narrow, new ConversionTarget(unsigned ? types.SystemUInt64Type : types.SystemInt64Type)]);

        return widened;
    }

    /// <summary>Whether the condition asks about the carry rather than about the sign of a result.</summary>
    internal static bool ReadsCarry(Arm64ConditionCode condition) =>
        condition is Arm64ConditionCode.CS or Arm64ConditionCode.CC
            or Arm64ConditionCode.HI or Arm64ConditionCode.LS;

    /// <summary>Which pair of pseudo-registers a condition should be given.</summary>
    internal static Register ComparisonSide(Arm64ConditionCode condition, bool left) =>
        new(null, ReadsCarry(condition)
            ? (left ? CarryLeft : CarryRight)
            : (left ? ComparisonLeft : ComparisonRight));

    /// <summary>
    /// Records what the carry out of an addition actually asks, where it can be written down exactly.
    /// </summary>
    /// <remarks>
    /// <c>adds wd, wn, #imm</c> carries when <c>(uint)wn + imm</c> reaches 2^32, so the question is
    /// <c>(uint)wn >= 2^32 - imm</c> - a comparison of two **non-negative** numbers, which signed ISIL can
    /// state exactly once the left side is masked to its unsigned value. Every one of the four carry
    /// conditions then comes out right, including <c>HI</c> and <c>LS</c>, whose extra dependence on Z falls
    /// where the strict and non-strict comparisons already differ.
    ///
    /// Only a 32-bit addition of an unshifted immediate. A 64-bit one would need a value wider than the
    /// arithmetic can hold, and a register operand would need its own mask; both keep the ordinary pair,
    /// which is what a subtraction's carry means anyway.
    /// </remarks>
    internal static void RecordCarry(Arm64Instruction instruction, object source, Action<OpCode, object[]> emit)
    {
        if (instruction.Mnemonic != Arm64Mnemonic.ADDS
            || instruction.Op0Reg is not (>= Arm64Register.W0 and <= Arm64Register.W31)
            || instruction.Op2Kind != Arm64OperandKind.Immediate
            || instruction.MemExtendOrShiftAmount != 0)
            return;

        var unsigned = new Register(null, CarryValue);

        emit(OpCode.And, [unsigned, source, 0xFFFFFFFFL]);
        emit(OpCode.Move, [new Register(null, CarryLeft), unsigned]);
        emit(OpCode.Move, [new Register(null, CarryRight), 0x1_0000_0000L - instruction.Op2Imm]);
    }

    internal static bool TryGetRelationalOpCode(Arm64ConditionCode condition, out OpCode opCode)
    {
        opCode = condition switch
        {
            Arm64ConditionCode.EQ => OpCode.CheckEqual,
            Arm64ConditionCode.NE => OpCode.CheckNotEqual,
            Arm64ConditionCode.GE or Arm64ConditionCode.CS or Arm64ConditionCode.PL => OpCode.CheckGreaterOrEqual,
            Arm64ConditionCode.LT or Arm64ConditionCode.CC or Arm64ConditionCode.MI => OpCode.CheckLess,
            Arm64ConditionCode.GT or Arm64ConditionCode.HI => OpCode.CheckGreater,
            Arm64ConditionCode.LE or Arm64ConditionCode.LS => OpCode.CheckLessOrEqual,
            _ => OpCode.Invalid,
        };

        return opCode != OpCode.Invalid;
    }

    /// <summary>
    /// Says where a struct of floats actually lands, for a call that returns one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="GetReturnRegisterForContext"/> names every non-primitive return <c>x0</c>, and for a struct
    /// whose every field is a float that is a register the callee never touched: aapcs64 returns it one field
    /// per vector register, exactly as it passes one. Nothing recorded that, so single assignment form never
    /// gave <c>v0..v7</c> a new value at a call - and the *caller's own* parameters, which arrive in those
    /// same registers, went on reaching every read below it. <c>worldPos - _camTransform.position</c> came out
    /// as <c>worldPos.x - worldPos.x</c>, which is zero.
    /// </para>
    /// <para>
    /// Each register is named as the field of the answer that is in it. Written as a <b>read of the answer</b>
    /// rather than a copy of it, and that is the whole reason this works: a copy is folded away by the
    /// propagation in single assignment form, which puts the caller's parameter back where it was and loses
    /// the distinction all over again. A read is not folded, and field recovery already turns
    /// <c>[x0 + 4]</c> on a <c>Vector3</c> into <c>x0.y</c> - so this needs no naming of its own.
    /// </para>
    /// <para>
    /// Only where the callee is known and returns more than one float. A single <c>float</c> already comes
    /// back in <c>s0</c>, which the return register names correctly.
    /// </para>
    /// </remarks>
    private static IEnumerable<(OpCode, object[])> VectorReturnFields(ApplicationAnalysisContext application,
        ulong target, object? result)
    {
        if (result is not Register
            || !application.MethodsByAddress.TryGetValue(target, out var called) || called.Count != 1
            || Analysis.HomogeneousFloatStruct.Count(called[0].ReturnType) is not { } floats || floats < 2)
        {
            yield break;
        }

        for (var field = 0; field < floats; field++)
        {
            yield return (OpCode.Move,
                [RegisterFor(Arm64Register.V0 + field), new MemoryOperand(result, addend: field * 4)]);
        }
    }

    /// <summary>
    /// The name a register is known by. Arm64 gives one physical register several names according to
    /// the width in use: w1 is the low half of x1, and s0, d0 and q0 are all v0. Naming them apart made
    /// each width a variable of its own, so a value written as a 32-bit integer and read as a pointer -
    /// or an int parameter passed in w1 and looked for in x1 - was never connected to itself. Since ISIL
    /// carries no width, one name per physical register is both simpler and what the analysis needs.
    /// </summary>
    private static Register RegisterFor(Arm64Register register)
    {
        var number = register switch
        {
            >= Arm64Register.X0 and <= Arm64Register.X31 => register - Arm64Register.X0,
            >= Arm64Register.W0 and <= Arm64Register.W31 => register - Arm64Register.W0,
            _ => -1,
        };

        if (number >= 0)
            return new Register(null, "X" + number);

        var vector = register switch
        {
            >= Arm64Register.V0 and <= Arm64Register.V31 => register - Arm64Register.V0,
            >= Arm64Register.D0 and <= Arm64Register.D31 => register - Arm64Register.D0,
            >= Arm64Register.S0 and <= Arm64Register.S31 => register - Arm64Register.S0,
            >= Arm64Register.H0 and <= Arm64Register.H31 => register - Arm64Register.H0,
            >= Arm64Register.B0 and <= Arm64Register.B31 => register - Arm64Register.B0,
            _ => -1,
        };

        return vector >= 0 ? new Register(null, "V" + vector) : new Register(null, register.ToString().ToUpperInvariant());
    }

    /// <summary>
    /// The value a move-keep writes and the 16 bit field of the register it writes it into.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both are read off the instruction, because the disassembler shifts the immediate with a <b>32 bit</b>
    /// shift count: <c>movk x8, #0xcccd, lsl #32</c> comes back as <c>0xcccd</c> (the count wrapping to zero)
    /// and <c>movk x8, #0x3f4c, lsl #48</c> as <c>0x3f4c0000</c> (wrapping to sixteen). Every field above the
    /// low half therefore arrived in the wrong place.
    /// </para>
    /// <para>
    /// What stood here inferred the position back from the value, taking the lowest aligned field it fitted
    /// in. That is right for the half of the constant that ends up in the low word and wrong for the rest, so
    /// a constant built from more than two fields came out as its own top half - <c>0x3F4CCCCD0000000C</c>,
    /// which is <c>12</c> and <c>0.8f</c> side by side, as <c>0x3F4CCCCD</c>. The two fields it was about to
    /// be stored into then got one wrong number between them.
    /// </para>
    /// </remarks>
    private static (long Value, long Mask) MoveKeepField(MethodAnalysisContext context, Arm64Instruction instruction)
    {
        if (VectorLanes.Word(context, instruction.Address) is { } word)
        {
            var shift = (int)(word >> 21 & 3) * 16;

            return ((long)(word >> 5 & 0xFFFF) << shift, 0xFFFFL << shift);
        }

        //Nothing to read the instruction back from, so the disassembler's value is all there is: put it in the
        //lowest aligned field it fits in, which is where it belongs whenever it is one of the low two.
        for (var shift = 0; shift < 64; shift += 16)
        {
            var field = (long)((ulong)instruction.Op1Imm >> shift);

            if ((field & ~0xFFFFL) == 0 && field << shift == instruction.Op1Imm)
                return (instruction.Op1Imm, 0xFFFFL << shift);
        }

        return (instruction.Op1Imm, ~0L);
    }

    /// <summary>
    /// The floating point literal at an address, where the register being loaded says how wide it is. Returns
    /// null for anything that is not a floating point load, or for an address the binary does not cover.
    /// </summary>
    private static object? ReadFloatConstant(MethodAnalysisContext context, Arm64Register destination, ulong address)
    {
        var isSingle = destination is >= Arm64Register.S0 and <= Arm64Register.S31;
        var isDouble = destination is >= Arm64Register.D0 and <= Arm64Register.D31;

        if (!isSingle && !isDouble)
            return null;

        var binary = context.AppContext.Binary;

        if (!binary.TryMapVirtualAddressToRaw(address, out var raw) || raw <= 0)
            return null;

        var content = binary.GetRawBinaryContent();
        var width = isSingle ? 4 : 8;

        if (raw + width > content.Length)
            return null;

        var bytes = content.Slice((int)raw, width);

        //Boxed apart, because a conditional whose arms are float and double is a double either way, and a float
        //argument handed over as a double is a type the method does not take.
        if (isSingle)
            return BitConverter.ToSingle(bytes);

        return BitConverter.ToDouble(bytes);
    }

    /// <summary>
    /// Which way a shifted register operand is shifted. The shifted register forms of add, subtract and the
    /// logical operations all keep it in bits 23 and 22, and the disassembler does not hand it over, so it is
    /// read from the encoding. A rotate has no counterpart here and is refused rather than approximated.
    /// </summary>
    /// <summary>
    /// The immediate of a bitwise instruction, decoded from the instruction rather than taken as given.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Arm64 does not carry a bitmask literally; it encodes it as a repeating run of ones - N, immr and imms -
    /// and the disassembler decodes that wrongly for the widest run. `and x9, x9, #0xffffffff`, which is how a
    /// 32-bit length is widened to a 64-bit loop bound, arrives as `and x9, x9, #0` - so the loop it guards is
    /// entered never, the method returns its initial value, and nothing anywhere says so. 34 such masks are
    /// live in the game's own assembly, five of them the condition of a loop, and another seven arrive as
    /// `0x1ffffffff`, which is not an encodable mask at all.
    /// </para>
    /// <para>
    /// The fault is a shift by 32 of a 32-bit one, which on this architecture is a shift by nothing:
    /// <c>(1 &lt;&lt; 32) - 1</c> is 0 rather than <c>0xffffffff</c>. Disarm is a package, so it is decoded here
    /// instead, following DecodeBitMasks from the architecture manual.
    /// </para>
    /// </remarks>
    private static long? LogicalImmediate(MethodAnalysisContext context, Arm64Instruction instruction)
    {
        var binary = context.AppContext.Binary;

        if (!binary.TryMapVirtualAddressToRaw(instruction.Address, out var raw) || raw <= 0)
            return null;

        var content = binary.GetRawBinaryContent();

        if (raw + 4 > content.Length)
            return null;

        var word = BitConverter.ToUInt32(content.Slice((int)raw, 4));

        //Only the logical-immediate encodings lay their operand out as N:immr:imms. Add and subtract with an
        //immediate reuse the same bits as sh:imm12, and reading those as a bitmask parses to something
        //plausible and wholly wrong - `subs x9, x9, #1`, which is every loop counter that counts down, came
        //out as `x9 - 0x300000003`. The opcode field is what tells them apart.
        if ((word >> 23 & 0x3F) != 0b100100)
            return null;

        var sixtyFourBit = (word >> 31 & 1) == 1;
        var n = (int)(word >> 22 & 1);
        var immr = (int)(word >> 16 & 0x3F);
        var imms = (int)(word >> 10 & 0x3F);

        //The size of the repeating run is the highest set bit of N followed by the complement of imms.
        var pattern = (n << 6) | (~imms & 0x3F);
        var length = 6;
        while (length >= 0 && (pattern >> length & 1) == 0)
            length--;

        if (length < 1 || (!sixtyFourBit && n == 1))
            return null;

        var size = 1 << length;
        var levels = size - 1;
        var ones = (imms & levels) + 1;

        //All ones is the encoding's one reserved case, not a mask.
        if (ones > levels)
            return null;

        //Shifting by the whole width is what goes wrong upstream, so the run is built without ever doing it.
        var element = ones >= 64 ? ulong.MaxValue : (1UL << ones) - 1;
        var rotate = immr & levels;

        if (rotate != 0 && size < 64)
            element = (element >> rotate | element << (size - rotate)) & (size >= 64 ? ulong.MaxValue : (1UL << size) - 1);
        else if (rotate != 0)
            element = element >> rotate | element << (64 - rotate);

        var mask = element;
        for (var filled = size; filled < 64; filled *= 2)
            mask |= mask << filled;

        //A 32-bit operation writes the low half and clears the high one, so the mask widens with zeroes.
        return sixtyFourBit ? unchecked((long)mask) : unchecked((long)(uint)mask);
    }

    /// <summary>
    /// The second operand of a bitwise instruction, with an immediate decoded properly.
    /// </summary>
    private static object BitwiseOperand(MethodAnalysisContext context, Arm64Instruction instruction, object given)
    {
        if (instruction.Op2Kind != Arm64OperandKind.Immediate)
            return given;

        return LogicalImmediate(context, instruction) ?? given;
    }

    /// <summary>
    /// The narrow type an extended-register operand is taken as, where it is one.
    /// </summary>
    /// <remarks>
    /// arm64 folds a narrowing conversion into the operand rather than spending an instruction on it:
    /// <c>add w8, w8, w0, uxtb</c> adds the low byte of w0, not the whole word. Read as a plain register the
    /// addition is of the wrong value and nothing says so - `(byte)v + (short)v` came back as `v + v`.
    /// Only the four that actually narrow are taken. <c>uxtw</c> and <c>sxtw</c> are left alone because they
    /// are how an index is widened in an address, and that path is recovered elsewhere; <c>uxtx</c> and
    /// <c>sxtx</c> narrow nothing.
    /// </remarks>
    internal static TypeAnalysisContext? ExtendedTo(MethodAnalysisContext context, Arm64Instruction instruction)
    {
        var types = context.AppContext.SystemTypes;

        return instruction.FinalOpExtendType switch
        {
            Arm64ExtendType.UXTB => types.SystemByteType,
            Arm64ExtendType.SXTB => types.SystemSByteType,
            Arm64ExtendType.UXTH => types.SystemUInt16Type,
            Arm64ExtendType.SXTH => types.SystemInt16Type,
            _ => null,
        };
    }

    private static OpCode? ShiftDirection(MethodAnalysisContext context, Arm64Instruction instruction)
    {
        if (instruction.FinalOpShiftType != Arm64ShiftType.NONE)
        {
            return instruction.FinalOpShiftType switch
            {
                Arm64ShiftType.LSL => OpCode.ShiftLeft,
                Arm64ShiftType.LSR or Arm64ShiftType.ASR => OpCode.ShiftRight,
                _ => null,
            };
        }

        var binary = context.AppContext.Binary;

        if (!binary.TryMapVirtualAddressToRaw(instruction.Address, out var raw) || raw <= 0)
            return null;

        var content = binary.GetRawBinaryContent();

        if (raw + 4 > content.Length)
            return null;

        return (BitConverter.ToUInt32(content.Slice((int)raw, 4)) >> 22 & 3) switch
        {
            0 => OpCode.ShiftLeft,
            1 or 2 => OpCode.ShiftRight,
            _ => null,
        };
    }

    /// <summary>
    /// The shift an instruction folds into its last operand, done at the width the instruction works in and
    /// bringing in what it really brings in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="LogicalShift"/> settled that arm64's two right shifts are different operations, and marked
    /// the ones it could see - but only the shifts that are instructions of their own. A shift <b>folded into
    /// an operand</b> went through a different path and was never marked, so <c>add w8, w9, w8, lsr #31</c>
    /// came out arithmetic. That is clang's sign fixup for a magic division, and getting it wrong makes the
    /// division wrong for every negative dividend - silently, with no marker and no statement lost.
    /// </para>
    /// <para>
    /// The width matters for the same reason and at the same sites. A <c>w</c>-form shift works on the low
    /// thirty-two bits; done at sixty-four the sign extension above them shifts down into the answer, so
    /// <c>lsr #31</c> yields a whole word rather than the one bit the fixup wanted. Both have to be right
    /// together: measured on <c>Corpus::Bits</c>, either one alone is wrong in essentially every case
    /// (3995 and 3999 of 4000 random inputs) and the two together are wrong in none.
    /// </para>
    /// <para>
    /// Only a right shift is narrowed. A folded left shift is how an array element is addressed, its result
    /// is used as a full-width address, and the <c>w</c>-form's truncation is somebody else's question.
    /// </para>
    /// </remarks>
    private static object FoldedShift(MethodAnalysisContext context, Arm64Instruction instruction, object value,
        OpCode direction, Func<OpCode, object[], Instruction> emit)
    {
        var logical = direction == OpCode.ShiftRight && BringsInZeroes(context, instruction);

        if (direction == OpCode.ShiftRight && WorksInThirtyTwoBits(context, instruction))
        {
            var types = context.AppContext.SystemTypes;

            var narrowed = new Register(null, "SHIFTED");
            emit(OpCode.Move, [narrowed, value, new ConversionTarget(logical ? types.SystemUInt32Type : types.SystemInt32Type)]);
            value = narrowed;
        }

        var shifted = new Register(null, "SHIFTED");
        var emitted = emit(direction, [shifted, value, instruction.Op3Imm]);

        if (logical)
            LogicalShift.Mark(emitted);

        return shifted;
    }

    /// <summary>Whether the folded shift brings in zeroes rather than the sign bit.</summary>
    private static bool BringsInZeroes(MethodAnalysisContext context, Arm64Instruction instruction)
    {
        if (instruction.FinalOpShiftType != Arm64ShiftType.NONE)
            return instruction.FinalOpShiftType == Arm64ShiftType.LSR;

        //The same two bits ShiftDirection reads: 1 is LSR, 2 is ASR.
        return RawWord(context, instruction) is { } word && (word >> 22 & 3) == 1;
    }

    /// <summary>Whether the instruction is the thirty-two bit form, which is bit 31 of its encoding.</summary>
    private static bool WorksInThirtyTwoBits(MethodAnalysisContext context, Arm64Instruction instruction)
        => RawWord(context, instruction) is { } word && (word >> 31 & 1) == 0;

    private static uint? RawWord(MethodAnalysisContext context, Arm64Instruction instruction)
    {
        var binary = context.AppContext.Binary;

        if (!binary.TryMapVirtualAddressToRaw(instruction.Address, out var raw) || raw <= 0)
            return null;

        var content = binary.GetRawBinaryContent();

        return raw + 4 > content.Length ? null : BitConverter.ToUInt32(content.Slice((int)raw, 4));
    }

    // add Xd, sp, #imm - the address of a slot in the frame, not an arithmetic result.
    private static bool IsStackSlotAddress(Arm64Instruction instruction) =>
        instruction.Mnemonic == Arm64Mnemonic.ADD
        && instruction.Op0Kind == Arm64OperandKind.Register && instruction.Op0Reg != Arm64Register.X31
        && instruction.Op1Kind == Arm64OperandKind.Register && instruction.Op1Reg == Arm64Register.X31
        && instruction.Op2Kind == Arm64OperandKind.Immediate;

    /// <summary>
    /// A memory operand, keeping the register the architecture lets an address be indexed by.
    /// </summary>
    /// <remarks>
    /// `ldr w11, [x10, x9, lsl #2]` is how every loop over an array reads its element: the base is the
    /// elements of the array, and x9 is the subscript, scaled by the width of one. Only the base was kept, so
    /// the operand read element zero - and the recovery said so, in code that compiled, ran, and answered on
    /// the first element every time round the loop. `CountOf` returned nought for every input; `AllNone`
    /// answered about `cells[0]`. Six of the fifteen corpus methods that behaved differently were this.
    ///
    /// The amount is a shift for `lsl` and an extend amount for `uxtw`/`sxtw`, which is the same number: how
    /// far the subscript moves to become a byte offset. A sign-extending index is still the same index, since
    /// a subscript is never negative by the time it reaches here.
    /// </remarks>
    private static MemoryOperand MemoryOperandFor(Arm64Instruction instruction, Arm64Register baseRegister, long offset)
    {
        offset = Scaled(instruction, offset);

        if (instruction.MemAddendReg == Arm64Register.INVALID)
            return new MemoryOperand(RegisterFor(baseRegister), addend: offset);

        var amount = IndexShift(instruction) ?? instruction.MemExtendOrShiftAmount;

        return new MemoryOperand(
            RegisterFor(baseRegister),
            RegisterFor(instruction.MemAddendReg),
            offset,
            amount is >= 0 and <= 4 ? 1 << amount : 1);
    }

    // add sp, sp, #imm and sub sp, sp, #imm, which move the frame rather than computing anything.
    private static bool IsStackPointerAdjustment(Arm64Instruction instruction) =>
        instruction.Op0Kind == Arm64OperandKind.Register && instruction.Op0Reg == Arm64Register.X31
        && instruction.Op1Kind == Arm64OperandKind.Register && instruction.Op1Reg == Arm64Register.X31
        && instruction.Op2Kind == Arm64OperandKind.Immediate;

    /// <summary>
    /// The value a store writes. It sits in operand 0, where <see cref="ConvertOperand"/> assumes a
    /// destination, so the zero register has to be recognised here instead.
    /// </summary>
    private object ConvertStoredValue(Arm64Instruction instruction, int operand)
    {
        var converted = ConvertOperand(instruction, operand);
        return converted is Register { Name: "X31" } ? 0L : converted;
    }
}
