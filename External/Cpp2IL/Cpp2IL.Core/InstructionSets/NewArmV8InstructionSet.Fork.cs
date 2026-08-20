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

    /// <summary>The method <see cref="FrameIsStatic"/> last answered about, and what it answered.</summary>
    [ThreadStatic]
    private static ulong frameAnsweredFor;

    [ThreadStatic]
    private static bool frameAnswer;


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
    /// <c>ldexp</c> is left too, its second argument being an integer in <c>w0</c> rather than a float in
    /// <c>v1</c>; <c>Analysis.UnresolvedCallMarker</c> at least names it where it stays a marker.
    /// </para>
    /// <para>
    /// <b>And one of them is not a method at all.</b> <c>fmod</c> is the <c>%</c> operator, so it is emitted
    /// as an <c>OpCode.Modulus</c> rather than as a call - the largest unmapped import in either game,
    /// 642 sites here and 40 on the other binary.
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

        //`fmod` is not a call at all - it is the `%` operator. C# defines the remainder of two floating
        //point values as exactly what `fmod` computes, the truncated remainder `x - trunc(x / y) * y`, and
        //that is also what the CLR's `rem` does, so this is an identity rather than an approximation.
        //`Math.IEEERemainder` is emphatically *not* it: it rounds the quotient to nearest rather than
        //truncating, and gives a different answer for half of all inputs - which is why the table below has
        //no entry for `fmod` and why one could not be added.
        //
        //**The largest unmapped import in either game**: 642 sites on Snacky Dash (`fmod` 292, `fmodf` 350)
        //and 40 on Fluffy Field, counted from the two ELFs' `.plt` stubs.
        //
        //Operand order is the call's own - `fmod(x, y)` is `x % y`, and aapcs64 puts `x` in `v0` and `y` in
        //`v1`, the same run of registers `pow` and `atan2` two lines below already use. Width is not spelled
        //here for the same reason it is not spelled there: `RegisterFor` gives `s0`, `d0` and `v0` one name,
        //and what the value *is* comes from the type that reaches the register, not from this instruction.
        if (bare == "fmod")
        {
            return [(OpCode.Modulus, [RegisterFor(Arm64Register.V0), RegisterFor(Arm64Register.V0),
                RegisterFor(Arm64Register.V0 + 1)])];
        }

        string[]? names = bare switch
        {
            "sin" => ["Sin"], "cos" => ["Cos"], "tan" => ["Tan"],
            "asin" => ["Asin"], "acos" => ["Acos"], "atan" => ["Atan"],
            "exp" => ["Exp"], "log" => ["Log"], "log10" => ["Log10"],
            "sqrt" => ["Sqrt"], "fabs" => ["Abs"],
            "ceil" => ["Ceil", "Ceiling"], "floor" => ["Floor"], "round" => ["Round"],
            "pow" => ["Pow"], "atan2" => ["Atan2"], "fmin" => ["Min"], "fmax" => ["Max"],
            //The hyperbolic three are an ordinary managed method of the same shape - `Math.Sinh(double)` and
            //its two siblings - so they need nothing but the word. 63 sites on Snacky Dash, 21 each, all of
            //them the double form; Fluffy Field imports none of the three. Should a binary ever import
            //`sinhf`, the suffix rule takes it to the same entry and `MathIntrinsics.Resolve` reaches
            //`System.MathF`, which has all three - and where a runtime has neither, it resolves to nothing
            //and the marker stays, which is the right answer.
            "sinh" => ["Sinh"], "cosh" => ["Cosh"], "tanh" => ["Tanh"],
            //**Not `ldexp`/`ldexpf`, and not `scalbn`.** `ldexp(x, n)` is `x * 2^n`, and its second argument
            //is an *integer* - `w0`, not `v1` - so it is neither a method of this shape nor a call whose
            //operands this hook could name from the float run. `Math.ScaleB` is the managed equivalent and
            //takes `(double, int)`, which `MathIntrinsics.Resolve` cannot match: it requires every parameter
            //to be of the instruction's own float width. 3 sites on Snacky Dash and 5 on Fluffy Field, so
            //there is nothing here worth a second convention. A marker is better than a wrong answer.
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
    //Internal rather than private so that Analysis.GenericVirtualCallRecovery can present the same address
    //the lifter presents: a call the lifter followed a thunk to is recorded at the followed address, and a
    //pass matching on the raw branch target would never see it. Fork file, fork caller - no upstream
    //declaration changes.
    internal static ulong FollowThunks(LibCpp2IL.Il2CppBinary binary, ulong target)
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
        //register is not scaled at all, and a pre- or post-indexed one is a byte count too - the remark said
        //so all along but the test named only the pre-indexed half, so `ldr q0, [x8], #0x10` walked sixteen
        //times as far as it should.
        if (instruction.Mnemonic is not (Arm64Mnemonic.LDR or Arm64Mnemonic.STR)
            || instruction.MemAddendReg != Arm64Register.INVALID
            || instruction.MemIndexMode is Arm64MemoryIndexMode.PreIndex or Arm64MemoryIndexMode.PostIndex
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

        //The hidden pointer a fully shared generic answers through. A shared body cannot know how big a `T`
        //is, so it neither returns one in `x0` nor uses the architecture's own `x8`: the caller passes
        //somewhere to put it as an ordinary pointer argument after the declared ones, and the MethodInfo moves
        //along by one. `IListExtension::RandomItem<T>` reads its generic context from `x2` and writes its
        //answer through `x1`, so naming `x1` the MethodInfo both lost the context and stamped
        //`Il2CppMethodInfo` on the destination of the memcpy that is the method's `return`. 252 shared bodies
        //in this game return a type parameter and every one was off by a register.
        //
        //Stepped over rather than added as an operand: every pass that reads the parameter list indexes it
        //positionally, and what is wrong here is which register the MethodInfo is in, not how many parameters
        //there are. The test is the *raw* return type, because it has to be exactly a type parameter -
        //`ArrayExtension::ResizeArray<T>` returns `T[]`, which is a pointer like any other and takes no
        //buffer, and its MethodInfo is in `x3` today, correctly.
        //And not where the registered body is a value-type specialisation's rather than the shared one -
        //there is no buffer in that convention, so the MethodInfo is where it would be without one. See
        //Analysis.SharedBody; 85 definitions in this game return a type parameter and have no shared body.
        var stepped = context.Definition?.RawReturnType?.Type is LibCpp2IL.BinaryStructures.Il2CppTypeEnum.IL2CPP_TYPE_VAR
            or LibCpp2IL.BinaryStructures.Il2CppTypeEnum.IL2CPP_TYPE_MVAR
            && !Cpp2IL.Core.Analysis.SharedBody.IsASpecialisation(context);

        if (stepped)
            used++;

        //And the parameters that take more than the one integer register the walk above counts: a composite
        //of nine to sixteen bytes takes two, so everything after it - the runtime method included - moves
        //along. Stated as the difference so that every method without such a parameter counts exactly as it
        //did. See ParametersOnTheStack.Widen, which moves the parameters themselves.
        if (System.Environment.GetEnvironmentVariable("PARAMWIDEN_OFF") != "1")
            used += Cpp2IL.Core.Analysis.ParametersOnTheStack.ExtraIntegerRegisters(context);

        if (used >= 8)
            return;

        //And the body has the last word. Whether a hidden buffer exists is a property of the code il2cpp
        //generated, not of the signature: it emits reference-shared bodies that answer in `x0` and fully
        //shared ones that answer through a pointer for declarations that look identical.
        //`IListExtension::RandomItem` and `::RemoveRandom` are both `static T (IList<T>)` and only the first
        //has the buffer. So where the register the rule above chose is never opened as a `MethodInfo*` and
        //the one beside it is, the one beside it is the MethodInfo - which is not a guess, since nothing
        //else in the world is read at `klass` and `rgctx_data`.
        var chosen = Arm64Register.X0 + used;
        var beside = Arm64Register.X0 + (stepped ? used - 1 : used + 1);

        if (beside is >= Arm64Register.X0 and <= Arm64Register.X7
            && !OpensAsAMethodInfo(context, chosen) && OpensAsAMethodInfo(context, beside))
        {
            chosen = beside;
        }

        operands.Add(RegisterFor(chosen));
    }

    /// <summary>
    /// Whether the body reads this register at one of the two places only a <c>MethodInfo*</c> is read at.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>MethodInfo::klass</c> is 0x20 and <c>::rgctx_data</c> 0x38, and a shared body opens by reading one
    /// of them - that is how it finds out what it was called as. Only the top of the method, and only while
    /// something still holds the value, so a later reuse of the register says nothing.
    /// </para>
    /// <para>
    /// <b>Following the copy is the whole of it.</b> A body that has a metadata-init preamble saves the
    /// pointer first and reads it afterwards: <c>SingletonMonoBehaviour&lt;T&gt;::get_I</c> does
    /// <c>MOV X19, X0</c>, then clobbers <c>X0</c> with an <c>ADRP</c> four instructions later, and only then
    /// reads <c>LDR X0, [X19 + 0x20]</c>. Asking about <c>X0</c> alone answers no, the register the signature
    /// chose is left in place, and the whole
    /// <c>methodInfo -&gt; klass -&gt; rgctx_data -&gt; entry -&gt; static_fields</c> chain is unmanaged memory -
    /// eight bodies whose <i>only</i> defect that is.
    /// </para>
    /// </remarks>
    private static bool OpensAsAMethodInfo(MethodAnalysisContext context, Arm64Register register)
    {
        if (context.UnderlyingPointer == 0 || context.AppContext?.Binary is not { } binary)
            return false;

        try
        {
            var read = 0;
            var carried = false;
            var holding = new HashSet<Arm64Register> { register };

            foreach (var instruction in Utils.NewArm64Utils.GetArm64MethodBodyAtVirtualAddress(binary, context.UnderlyingPointer))
            {
                //Forty instructions is enough where the value is read out of the register it arrived in. Once a
                //copy has carried it into a callee-saved one the body is deliberately keeping it across
                //something long: `ScriptableObjectConfig<T>::get_E` saves it at instruction 4, runs eleven
                //`il2cpp_codegen_runtime_class_init` pairs, and reads it at **42** - two past this bound, which
                //cost the accessor its entire body and 25 unmanaged markers, `GoogleDesignConfigSo<S,T>::get_E`
                //another 25. The copy is what tells the two apart, so it is what widens the window; `holding`
                //emptying still ends the scan wherever the value is genuinely gone.
                if (++read > (carried ? 128 : 40))
                    return false;

                if (instruction.Mnemonic == Arm64Mnemonic.LDR && holding.Contains(instruction.MemBase)
                    && instruction.MemAddendReg == Arm64Register.INVALID
                    && instruction.MemOffset is 0x20 or 0x38)
                {
                    return true;
                }

                //A copy carries it, so the copy holds it too.
                if (instruction.Mnemonic == Arm64Mnemonic.MOV
                    && instruction.Op1Reg != Arm64Register.INVALID && holding.Contains(instruction.Op1Reg)
                    && instruction.Op0Reg != Arm64Register.INVALID)
                {
                    holding.Add(instruction.Op0Reg);
                    carried = true;
                    continue;
                }

                //A write to one of them, and whatever that one held is gone. Only when nothing holds it any
                //more is the answer no.
                if (instruction.Op0Reg != Arm64Register.INVALID && holding.Remove(instruction.Op0Reg)
                    && holding.Count == 0)
                {
                    return false;
                }
            }
        }
        catch
        {
            //A body this cannot read back says nothing either way.
        }

        return false;
    }

    /// <summary>
    /// Which of the methods at one address says how its arguments were handed over.
    /// </summary>
    /// <remarks>
    /// <para>
    /// il2cpp gives a generic method's own definition the pointer of one of the instantiations it generated,
    /// so <c>MethodsByAddress</c> holds both - and <c>First()</c> is the definition, because
    /// <c>PopulateMethodsByAddressTable</c> adds every definition before it adds a concrete generic. The
    /// definition's parameters are generic parameters, which name no size and no fields, so a <c>Vector3</c>
    /// argument is not seen to be a struct of floats, is given an integer register, and every argument after
    /// it shifts by one.
    /// </para>
    /// <code>
    /// From&lt;T1,T2,TPlugOptions&gt;(TweenerCore&lt;...&gt; t, T2 fromValue, bool setImmediately, bool isRelative)
    ///   definition @284CDD4  regs=[X0,X1,X2,X3]   aapcs64: x0=t, s0..s2=fromValue, w1, w2, x3=MethodInfo*
    /// </code>
    /// <para>
    /// <c>FeedbackPopup::BuildAnimation</c> is the case: <c>.From(new Vector3(0f, 0f, -wobbleAngle))</c> came
    /// out as <c>From((Vector3)1L, setImmediately: false, isRelative: false)</c> - the <c>1</c> is
    /// <c>setImmediately</c> arriving in <c>w1</c>, and <c>isRelative</c> is the trailing <c>MethodInfo*</c>.
    /// </para>
    /// <para>
    /// Chosen by what the parameters are rather than by the context's class, because identical bodies are
    /// folded together and one address may hold unrelated methods: only a candidate with no generic parameter
    /// left in its signature can answer, and where the first already has none nothing changes at all.
    /// </para>
    /// </remarks>
    private static MethodAnalysisContext InstantiationAmong(List<MethodAnalysisContext> candidates)
    {
        if (candidates.Count < 2 || !LeavesAParameterOpen(candidates[0]))
            return candidates[0];

        foreach (var candidate in candidates)
            if (!LeavesAParameterOpen(candidate))
                return candidate;

        return candidates[0];
    }

    /// <summary>Whether any parameter is still a generic parameter, which names no size and no fields.</summary>
    private static bool LeavesAParameterOpen(MethodAnalysisContext method)
    {
        foreach (var parameter in method.Parameters)
            if (parameter.ParameterType is GenericParameterTypeAnalysisContext)
                return true;

        return false;
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

    /// <summary>
    /// Marks a comparison the unsigned one where the condition it came from reads the carry.
    /// </summary>
    /// <remarks>
    /// The whole of the reasoning is in <see cref="Analysis.UnsignedComparison"/>. This is only the place
    /// that knows the condition: <see cref="ReadsCarry"/> already separates the four unsigned conditions
    /// out to give them their own pair of pseudo-registers, and the same answer says which comparison to
    /// emit. Set <c>UNSIGNEDCMP_OFF=1</c> to measure the same build without it - see ROUND-LOG.md.
    /// </remarks>
    private static void MarkUnsigned(Arm64ConditionCode condition, Instruction? comparison)
    {
        if (!UnsignedComparisonOff)
            Analysis.UnsignedComparison.Mark(ReadsCarry(condition), comparison);
    }

    private static readonly bool UnsignedComparisonOff = Environment.GetEnvironmentVariable("UNSIGNEDCMP_OFF") == "1";

    /// <summary>
    /// The registers holding more of a value than a <c>w</c>-form instruction can read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// On arm64 the <c>w</c> form of a data-processing instruction takes the low word of each source, works
    /// in thirty-two bits, and zero-extends its result over the whole register. ISIL carries no width, so all
    /// of that happens in <see cref="long"/> and the answer is silently wrong. It is only wrong when a source
    /// really does hold more than thirty-two bits, and counted over the binary that has exactly one cause:
    /// <b>370 sites in 191 methods, every one of them descended from a widening multiply</b>.
    /// </para>
    /// <para>
    /// Which is to say: this is magic division. Clang does not divide by a constant, it multiplies by a magic
    /// number and shifts - <c>smaddl x8, w0, w8, xzr</c> / <c>lsr x8, x8, #32</c> / <c>add w8, w8, w0</c> -
    /// and that last <c>add</c> is the point where the high word becomes a signed thirty-two bit number
    /// again. Without the truncation <c>Corpus::DivMagic</c> returned 1073741846 where the source says 22,
    /// rated <c>full</c> by every scorer this project has.
    /// </para>
    /// <para>
    /// Narrowing the first <c>w</c>-form read is enough for the whole chain: the result of that instruction
    /// is a thirty-two bit value and everything below it inherits the type. Straight-line only, and every
    /// branch clears the map - a linear scan is not a def-use analysis, and the earlier attempt at this
    /// family ([[il2cpp-a-w-register-write-is-a-truncation]]) was inflated fifteenfold by ignoring exactly
    /// that. Being conservative costs a site; being wrong costs a method.
    /// </para>
    /// </remarks>
    internal static class WordWidth
    {
        [ThreadStatic] private static int[]? held;

        /// <summary>Set <c>WORDWIDTH_OFF=1</c> to measure the same build without this - see ROUND-LOG.md.</summary>
        private static readonly bool Off = Environment.GetEnvironmentVariable("WORDWIDTH_OFF") == "1";

        internal static void Reset()
        {
            if (held is null)
                held = new int[32];
            else
                Array.Clear(held);
        }

        /// <summary>What the instruction leaves behind, recorded after it has been lifted.</summary>
        internal static void Note(Arm64Instruction instruction)
        {
            if (held is null)
                return;

            //A branch either way, and the map is worthless: the register could have been written on a path
            //this walk did not take.
            if (instruction.Mnemonic is Arm64Mnemonic.B or Arm64Mnemonic.BL or Arm64Mnemonic.BR
                or Arm64Mnemonic.BLR or Arm64Mnemonic.CBZ or Arm64Mnemonic.CBNZ
                or Arm64Mnemonic.TBZ or Arm64Mnemonic.TBNZ or Arm64Mnemonic.RET)
            {
                Array.Clear(held);
                return;
            }

            var destination = Index(instruction.Op0Reg);

            if (destination < 0 || destination == 31)
                return;

            if (IsWidening(instruction.Mnemonic))
            {
                held[destination] = 1;
                return;
            }

            //A w write zeroes bits 32..63, so whatever was up there is gone.
            if (instruction.Op0Reg is >= Arm64Register.W0 and <= Arm64Register.W31)
            {
                held[destination] = 0;
                return;
            }

            if (instruction.Op0Reg is not (>= Arm64Register.X0 and <= Arm64Register.X31))
                return;

            //An x-form shift or arithmetic carries the width of what it read. Anything else - a load, a
            //move, an address - assigns a whole value of its own, and ISIL holds values rather than
            //registers with a stale half.
            held[destination] = Truncates(instruction.Mnemonic) && Widest(instruction) > 0 ? 1 : 0;
        }

        /// <summary>The source of a <c>w</c>-form instruction, truncated where it holds more than a word.</summary>
        internal static object Narrowed(MethodAnalysisContext context, Arm64Instruction instruction, object value,
            int operand, Action<OpCode, object[]> emit)
        {
            if (Off || held is null || operand == 0 || value is not Register
                || instruction.Op0Reg is not (>= Arm64Register.W0 and <= Arm64Register.W31)
                || !Truncates(instruction.Mnemonic))
            {
                return value;
            }

            var index = Index(RegisterOf(instruction, operand));

            if (index < 0 || index == 31 || held[index] == 0)
                return value;

            //A name per operand. `add w9, w9, w11` narrows two sources, and one shared name makes them two
            //definitions in a row - single assignment form then resolves both reads to the second, so the
            //addition came out as `x + x`. The same reason WidenedSource numbers its NARROW registers.
            var narrowed = new Register(null, "TRUNC" + operand);
            emit(OpCode.Move, [narrowed, value, new ConversionTarget(context.AppContext.SystemTypes.SystemInt32Type)]);
            return narrowed;
        }

        private static int Widest(Arm64Instruction instruction)
        {
            var worst = 0;

            for (var operand = 1; operand <= 3; operand++)
            {
                var index = Index(RegisterOf(instruction, operand));

                if (index >= 0 && index != 31 && held![index] > worst)
                    worst = held[index];
            }

            return worst;
        }

        private static Arm64Register RegisterOf(Arm64Instruction instruction, int operand) => operand switch
        {
            1 => instruction.Op1Kind == Arm64OperandKind.Register ? instruction.Op1Reg : Arm64Register.INVALID,
            2 => instruction.Op2Kind == Arm64OperandKind.Register ? instruction.Op2Reg : Arm64Register.INVALID,
            3 => instruction.Op3Kind == Arm64OperandKind.Register ? instruction.Op3Reg : Arm64Register.INVALID,
            _ => Arm64Register.INVALID,
        };

        private static int Index(Arm64Register register) =>
            register is >= Arm64Register.W0 and <= Arm64Register.W31 ? register - Arm64Register.W0
            : register is >= Arm64Register.X0 and <= Arm64Register.X31 ? register - Arm64Register.X0
            : -1;

        private static bool IsWidening(Arm64Mnemonic mnemonic) => mnemonic is Arm64Mnemonic.SMADDL
            or Arm64Mnemonic.UMADDL or Arm64Mnemonic.SMULL or Arm64Mnemonic.UMULL
            or Arm64Mnemonic.SMSUBL or Arm64Mnemonic.UMSUBL
            or Arm64Mnemonic.SMULH or Arm64Mnemonic.UMULH;

        /// <summary>The instructions whose <c>w</c> form really does work in thirty-two bits.</summary>
        private static bool Truncates(Arm64Mnemonic mnemonic) => mnemonic is Arm64Mnemonic.ADD
            or Arm64Mnemonic.SUB or Arm64Mnemonic.ADDS or Arm64Mnemonic.SUBS or Arm64Mnemonic.MUL
            or Arm64Mnemonic.MADD or Arm64Mnemonic.MSUB or Arm64Mnemonic.NEG
            or Arm64Mnemonic.AND or Arm64Mnemonic.ORR or Arm64Mnemonic.EOR or Arm64Mnemonic.ANDS
            or Arm64Mnemonic.UBFM or Arm64Mnemonic.SBFM or Arm64Mnemonic.BFM
            or Arm64Mnemonic.LSLV or Arm64Mnemonic.LSRV or Arm64Mnemonic.ASRV or Arm64Mnemonic.RORV;
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
    /// <summary>
    /// The constant a <c>mov</c> of a bitmask immediate moves, where the disassembler could not render it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>mov x9, #-4294967296</c> is <c>orr x9, xzr, #0xffffffff00000000</c> - the same bitmask encoding
    /// <see cref="LogicalImmediate"/> already decodes for <c>and</c>, <c>orr</c> and <c>eor</c>. Disarm
    /// reports the mnemonic and then hands over an operand it cannot represent, so the instruction arrives as
    /// <c>MOV X9, INVALID</c>: the lifter moves a register **nothing ever assigns**, and everything computed
    /// from it is arithmetic on a local with no definition.
    /// </para>
    /// <para>
    /// <c>Corpus::Reversed</c> is the shape. Counting an index down is done in the <b>high half</b> of a
    /// register - <c>x10 = len &lt;&lt; 32</c>, then <c>x10 += x9</c> each turn with <c>x9</c> being
    /// <c>-1 &lt;&lt; 32</c>, and <c>asr #30</c> reads the index back out already scaled by four. With
    /// <c>x9</c> undefined the whole chain is, and <c>copy[i] = values[len - 1 - i]</c> came out as an
    /// index the analysis could say nothing about - <c>IndexOutOfRangeException</c> on every input.
    /// </para>
    /// <para>
    /// Taken only where the raw word really is <c>orr Xd, XZR, #imm</c>, and only where the disassembler did
    /// not give an immediate of its own - so an instruction it read correctly is left exactly as it was.
    /// </para>
    /// </remarks>
    internal static long? MovedBitmask(MethodAnalysisContext context, Arm64Instruction instruction)
    {
        //Disarm reports the operand as a register and names it INVALID - it is not a register at all, and
        //`RegisterFor` then makes a local called INVALID that nothing ever assigns.
        var unrenderable = instruction.Op1Kind == Arm64OperandKind.Register
            && instruction.Op1Reg == Arm64Register.INVALID;

        if (!unrenderable)
            return null;

        if (RawWord(context, instruction) is not { } word)
            return null;

        //`orr` of a bitmask immediate against the zero register, which is what `mov` of one assembles as.
        //opc is bits 30..29 and 01 is ORR; Rn is bits 9..5 and 31 is the zero register.
        if ((word >> 23 & 0x3F) != 0b100100 || (word >> 29 & 3) != 1 || (word >> 5 & 0x1F) != 31)
            return null;

        return LogicalImmediate(context, instruction);
    }

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

    /// <summary>The page a register was given by an <c>adrp</c>, where it still holds one.</summary>
    /// <remarks>
    /// <see cref="VectorLanes"/> needs this at the moment of a load rather than at the moment something reads
    /// it. It defers a whole-register load and materialises the lanes where the first operation on them is,
    /// and the base register can be written again in between - which is exactly what
    /// <c>WinMenu::ComputeBeatPercent</c> does: two <c>adrp</c> into <c>x8</c> four instructions apart, and
    /// the lanes of the first load came out reading the second one's page.
    /// </remarks>
    internal static bool TryPageOf(Arm64Register register, out ulong page)
    {
        if (adrpOffsets is null)
        {
            page = 0;
            return false;
        }

        return adrpOffsets.TryGetValue(register, out page);
    }

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
    /// The frame opening or closing: a pre- or post-indexed access through the stack pointer, whose writeback
    /// moves the frame and so has to become a <see cref="OpCode.ShiftStack"/> like any other move of SP.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>stp x29, x30, [sp, #-0x30]!</c> is how most prologues open and <c>ldp x29, x30, [sp], #0x30</c> is
    /// how they close, and neither emitted a shift. Upstream's writeback branches are all guarded on
    /// <c>ConvertOperand(...) is MemoryOperand</c>, and an SP base answers a <see cref="StackOffset"/>
    /// instead - so the branch fell through and the move of the frame was dropped. Every slot named before
    /// the first real shift is then normalised against a depth of nought, i.e. named as though the frame were
    /// not yet open.
    /// </para>
    /// <para>
    /// What that costs is not a marker. A method's <b>stacked incoming parameters</b> are named from the true
    /// entry-relative offset by <see cref="Analysis.ParametersOnTheStack"/>, while the body reads them
    /// through the frame - so with the frame short by the push amount the two names never meet, and
    /// <c>HomogeneousFloatParameters.LocalForSlot</c>, which looks the parameter up by that exact name,
    /// returns nothing and the parameter is dropped. `VectorExtensions::RotatePointAroundPivot(Vector3,
    /// Vector3, Quaternion q)` reads `q` at <c>[sp+0x20]</c> behind a <c>str d10, [sp, #-0x20]!</c>, so it was
    /// named <c>stack_20</c> against a parameter called <c>stack_0</c>, and the body recovered as
    /// <c>(Quaternion)default(object) * ...</c>. Three parameters in Assembly-CSharp are this shape, and every
    /// method that keeps its stacked parameter opens with <c>sub sp, sp, #N</c> instead.
    /// </para>
    /// <para>
    /// <b>Both directions have to land together.</b> The pop is not merely unmodelled, it is wrong: the
    /// disassembler reports a post-index writeback in <c>MemOffset</c> and the lifter read it as the access
    /// offset, so the reload named a slot nothing wrote. Fixing only the push leaves that read at a depth of
    /// <c>-N</c>, where its raw <c>N</c> normalises to <c>stack_0</c> - which is exactly the first stacked
    /// parameter, turning inert garbage into a read of a real value.
    /// </para>
    /// <para>
    /// The access itself is at offset nought either way: pre-indexed moves the pointer and then reads at it,
    /// post-indexed reads at the pointer and then moves it. Only the order of the shift differs, and
    /// <see cref="Analysis.StackAnalyzer"/> records an instruction's state <i>before</i> applying that
    /// instruction's own shift, so emitting the shift first is what puts the moves that follow at the new
    /// depth.
    /// </para>
    /// </remarks>
    /// <summary>
    /// Whether the method's frame moves only by amounts written into the instructions that move it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>mov sp, x29</c> and <c>mov sp, x23</c> - the frame pointer put back, and the stack pointer taken
    /// from wherever an <c>alloca</c> left it - move SP by an amount no additive analysis can name, and
    /// neither is modelled. In a method containing one, the depth is already wrong from that instruction on,
    /// and the slot names either side of it line up only by luck.
    /// </para>
    /// <para>
    /// <b>Measured.</b> Shifting the frame in those methods changes <i>which</i> pairs line up by luck, and at
    /// 1.1.46 that was a net loss: the four generic extension files gained twenty-two <c>default(...)</c>
    /// between them, <c>ArrayExtension::ResizeArray</c> turned <c>num4 = length</c> into <c>num4 = 0L</c> and
    /// three frame-pointer stores became unmanaged-memory markers, against six recovered in
    /// <c>VectorExtensions</c>, <c>ColorExtension</c> and <c>AssetLoader</c>. So the shift is applied only
    /// where the frame can actually be followed; the rest waits on SP-from-a-register being modelled, which
    /// is a dataflow change in <see cref="Analysis.StackAnalyzer"/> rather than a lifting one.
    /// </para>
    /// </remarks>
    private static bool FrameIsStatic(MethodAnalysisContext method)
    {
        if (frameAnsweredFor == method.UnderlyingPointer && frameAnsweredFor != 0)
            return frameAnswer;

        frameAnsweredFor = method.UnderlyingPointer;
        frameAnswer = true;

        foreach (var instruction in NewArm64Utils.GetArm64MethodBodyAtVirtualAddress(method.AppContext.Binary, method.UnderlyingPointer))
        {
            //The stack pointer written from a register that is not itself the stack pointer.
            if (instruction.Op0Kind == Arm64OperandKind.Register && instruction.Op0Reg == Arm64Register.X31
                && instruction.Op1Kind == Arm64OperandKind.Register && instruction.Op1Reg != Arm64Register.X31
                && instruction.Mnemonic is Arm64Mnemonic.ADD or Arm64Mnemonic.SUB or Arm64Mnemonic.MOV or Arm64Mnemonic.ORR)
            {
                frameAnswer = false;
                break;
            }
        }

        return frameAnswer;
    }

    private bool StackFrameWriteback(Arm64Instruction instruction, Action<OpCode, object[]> add)
    {
        if (currentMethod is not { } method || !FrameIsStatic(method))
            return false;

        var pair = instruction.Mnemonic is Arm64Mnemonic.STP or Arm64Mnemonic.LDP;

        if ((pair ? instruction.Op2Kind : instruction.Op1Kind) != Arm64OperandKind.Memory
            || instruction.MemBase != Arm64Register.X31
            || instruction.MemAddendReg != Arm64Register.INVALID
            || instruction.MemIndexMode is not (Arm64MemoryIndexMode.PreIndex or Arm64MemoryIndexMode.PostIndex))
            return false;

        var isLoad = instruction.Mnemonic is Arm64Mnemonic.LDP or Arm64Mnemonic.LDR or Arm64Mnemonic.LDRB
            or Arm64Mnemonic.LDRH or Arm64Mnemonic.LDRSB or Arm64Mnemonic.LDRSH or Arm64Mnemonic.LDRSW
            or Arm64Mnemonic.LDUR or Arm64Mnemonic.LDURB or Arm64Mnemonic.LDURH or Arm64Mnemonic.LDURSW;

        var isStore = instruction.Mnemonic is Arm64Mnemonic.STP or Arm64Mnemonic.STR or Arm64Mnemonic.STRB
            or Arm64Mnemonic.STRH or Arm64Mnemonic.STUR or Arm64Mnemonic.STURB or Arm64Mnemonic.STURH;

        if (!isLoad && !isStore)
            return false;

        //Taken raw rather than through `Scaled`, which multiplies a 128-bit access's immediate by sixteen -
        //`ldr v10, [sp], #0x20` closes RotatePointAroundPivot and was being read as a slot at 0x200.
        var shift = (int)instruction.MemOffset;
        var movesFirst = instruction.MemIndexMode == Arm64MemoryIndexMode.PreIndex;

        if (movesFirst)
            add(OpCode.ShiftStack, [shift]);

        var second = pair ? PairElementSize(instruction.Op0Reg) : 0;

        if (isStore)
        {
            add(OpCode.Move, [new StackOffset(0), ConvertStoredValue(instruction, 0)]);

            if (pair)
                add(OpCode.Move, [new StackOffset(second), ConvertStoredValue(instruction, 1)]);
        }
        else
        {
            //A register an ADRP put a page address in no longer holds one once it is loaded over.
            adrpOffsets.Remove(instruction.Op0Reg);

            add(OpCode.Move, [ConvertOperand(instruction, 0), new StackOffset(0)]);

            if (pair)
            {
                adrpOffsets.Remove(instruction.Op1Reg);
                add(OpCode.Move, [ConvertOperand(instruction, 1), new StackOffset(second)]);
            }
        }

        if (!movesFirst)
            add(OpCode.ShiftStack, [shift]);

        return true;
    }

    /// <summary>
    /// The value a store writes. It sits in operand 0, where <see cref="ConvertOperand"/> assumes a
    /// destination, so the zero register has to be recognised here instead.
    /// </summary>
    private object ConvertStoredValue(Arm64Instruction instruction, int operand)
    {
        var converted = ConvertOperand(instruction, operand);
        return converted is Register { Name: "X31" } ? 0L : converted;
    }

    /// <summary>
    /// The name a decoded vector element shares with the lane <see cref="VectorLanes"/> writes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two spellings of the same four bytes never met. The operand decoder wrote <c>V2.S1</c> - register,
    /// element width, index - while <c>VectorLanes.Lane</c> writes <c>V2#4</c>, register and the byte the lane
    /// starts at. A lane written by one and read by the other was a local nothing defined: <b>361 occurrences
    /// across 141 methods, and a destination in none of them</b>, every one becoming <c>default(float)</c>
    /// with no marker.
    /// </para>
    /// <code>
    /// fmul v1.2s, v9.2s, v9.2s   // VectorLanes writes V1 and V1#4
    /// fadd s0, s0, s1            // reads V1        - matches
    /// mov  s1, v1.s[1]           // named V1.S1     - matched nothing
    /// </code>
    /// <para>
    /// so the lane-one multiply was collected as dead and <c>Vector3.magnitude</c>, which almost everything
    /// calls, recovered as <c>sqrt(x*x + y*y + default(float))</c>.
    /// </para>
    /// <para>
    /// Naming by where the lane starts is the scheme that composes, being the one fact both readings agree on.
    /// <c>Lane</c>'s own remark says the cost: the same eight bytes are lane 1 of a pair of doubles and lanes
    /// 2 and 3 of four floats, so a <c>.D[1]</c> read names only the low half of what it wants - three of
    /// those in this assembly against seventy-one <c>.S[1]</c>, and the previous spelling matched none of
    /// either.
    /// </para>
    /// <para>
    /// This could not land on its own. Connecting the lane makes a constant beside it live, and a floating
    /// point constant reaches recovery as its own bit pattern: <c>Vector3.one * 0.85f</c> came out as
    /// <c>one.y * 1062836634L</c>, a wrong value where a stand-in had been. The second run of
    /// <c>FloatLiteralRecovery</c> at the end of <c>ForkPipeline</c> is what answers that, and the two belong
    /// in one change.
    /// </para>
    /// </remarks>
    private static Register LaneOperand(Arm64Register reg, Arm64VectorElement element)
    {
        var bytes = element.Width switch
        {
            Arm64VectorElementWidth.B => 1,
            Arm64VectorElementWidth.H => 2,
            Arm64VectorElementWidth.S => 4,
            Arm64VectorElementWidth.D => 8,
            _ => throw new System.ArgumentOutOfRangeException(nameof(element), $"Unknown vector element width {element.Width}"),
        };

        var number = reg.ToString().ToUpperInvariant();
        var offset = element.Index * bytes;

        return new Register(null, offset == 0 ? number : $"{number}#{offset}");
    }


    /// <summary>
    /// The library call an <c>FMOV</c> into a general register is, or nothing where it is an ordinary move.
    /// </summary>
    /// <remarks>
    /// See <see cref="Analysis.FloatBitsInAnInteger"/> for what it is and why it is a call rather than a
    /// conversion. The test is the two register banks: a general destination and a vector source is a
    /// reinterpretation, and the width of the pair says which of the two methods it is.
    /// </remarks>
    private static MethodAnalysisContext? Reinterpretation(Arm64Instruction instruction, MethodAnalysisContext context)
    {
        if (instruction.Op0Kind != Arm64OperandKind.Register || instruction.Op1Kind != Arm64OperandKind.Register)
            return null;

        var into = instruction.Op0Reg;
        var from = instruction.Op1Reg;

        //Thirty-two bits of float into a word, or sixty-four of double into an extended register. Anything
        //else - a vector destination, or a pair of different widths - is not this.
        if (into is >= Arm64Register.W0 and <= Arm64Register.W31 && from is >= Arm64Register.S0 and <= Arm64Register.S31)
            return Analysis.FloatBitsInAnInteger.Naming(context.AppContext, false);

        if (into is >= Arm64Register.X0 and <= Arm64Register.X31 && from is >= Arm64Register.D0 and <= Arm64Register.D31)
            return Analysis.FloatBitsInAnInteger.Naming(context.AppContext, true);

        return null;
    }

    /// <summary>
    /// Whether a load pair writes its own base register with its <em>first</em> destination.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A pair is one instruction and both halves are addressed off the base as it was on entry, but ISIL has
    /// no pairs - upstream writes two moves, the second addressed off the same register the first has just
    /// overwritten. Where the two are the same register that second move reads a value the machine never
    /// addressed with, silently and with no marker:
    /// </para>
    /// <code>
    /// LDP X8, X9, [X8]                                 // X8 = table[0], X9 = table[1]
    /// Move v33 (Il2CppClass&lt;Sequence`1&lt;T&gt;&gt;), [v31 (Il2CppRgctx)]
    /// Move v34,                                        [v33 + 8]   // should be [v31 + 8]
    /// </code>
    /// <para>
    /// That is how a shared body opens: entry nought of the runtime generic context is the containing class
    /// and entry one is the class of <c>T</c>, and reading the second through the first leaves the size the
    /// body then allocas by - <c>[Il2CppClass&lt;T&gt; + 0xFC]</c> - based on something nothing can name. Every
    /// pass that asks whether a length is a class's own size then declines, so the alloca, the copies sized by
    /// it and the field addresses handed to them all stay as unmanaged memory.
    /// </para>
    /// <para>
    /// The two loads are independent of one another, so emitting the second first says exactly what the
    /// machine does. Only this order is wrong: where the <b>second</b> destination is the base the first move
    /// has not touched it yet, which is why the answer is not simply "always swap".
    /// </para>
    /// </remarks>
    private static bool PairClobbersItsOwnBase(Arm64Instruction instruction)
    {
        if (instruction.MemBase == Arm64Register.INVALID || instruction.Op0Kind != Arm64OperandKind.Register)
            return false;

        var written = RegisterNumber(instruction.Op0Reg);

        //A `w` destination is the low half of the same register, so it clobbers the base just as surely.
        var clobbers = written >= 0 && written != 31 && written == RegisterNumber(instruction.MemBase);

        //Says which analysed bodies this actually reaches. A binary-wide count of the encoding says nothing
        //about how many of them are in methods anything looks at.
        if (clobbers && System.Environment.GetEnvironmentVariable("LDP_TRACE") == "1")
            System.Console.WriteLine($"LDPBASE {currentMethod?.DeclaringType?.Name}::{currentMethod?.Name} @ {instruction.Address:X}");

        return clobbers;
    }

    /// <summary>A general purpose register's number, whichever width it was named at.</summary>
    private static int RegisterNumber(Arm64Register register) => register switch
    {
        >= Arm64Register.X0 and <= Arm64Register.X31 => register - Arm64Register.X0,
        >= Arm64Register.W0 and <= Arm64Register.W31 => register - Arm64Register.W0,
        _ => -1,
    };
}
