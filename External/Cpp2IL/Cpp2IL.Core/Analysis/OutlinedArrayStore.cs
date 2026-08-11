using System.Collections.Concurrent;
using System.Collections.Generic;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using Disarm;
using Disarm.InternalDisassembly;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Puts back the statement behind a call to the out-of-line copy of an array store.
/// </summary>
/// <remarks>
/// <para>
/// <c>a[i] = x</c> on an array of references is four things - a bounds check, the address, the store and
/// the write barrier - and where the compiler decided it had written them out often enough it made them a
/// function. Nothing names that function, so all <b>1077</b> of its call sites came out as
/// <c>_ = "Method not found @1E7CB84";</c> and took the assignment with them. The check that goes in front
/// of it, which asks whether the value fits the array's element type, is another <b>591</b>.
/// </para>
/// <code>
/// 1E7CB84   ldr w8, [x0, #0x18]      ; the length
///           cmp w1, w8               ; against the index
///           b.cs throw
///           add x0, x0, x1, lsl #3   ; and the element's address
///           str x2, [x0, #0x20]!
///           b   il2cpp_codegen_write_barrier
///
/// 1E7CB50   cbz x1, ok               ; null fits anything
///           ldr x8, [x0]             ; the array's class
///           ldr x8, [x8, #0x40]      ; its element class
///           bl  il2cpp_vm_object_is_inst
///           cbz x0, throw
///     ok:   ret
/// </code>
/// <para>
/// So the store becomes the store it is - <c>Move [array + 0x20 + index * 8], value</c>, which is exactly
/// what the inlined form produces, so everything downstream reads it without knowing the difference - and
/// the check becomes nothing, for the same reason <see cref="ArrayStoreCheckRemover"/> deletes the inlined
/// one: the language makes that promise itself and says nothing about it.
/// </para>
/// <para>
/// Recognised by what the body does, not by its address, which differs in every build. Both shapes are
/// checked against fixed registers because both are reached through the calling convention and have no
/// choice about which registers they use.
/// </para>
/// </remarks>
public static class OutlinedArrayStore
{
    public enum Kind
    {
        Neither,
        /// <summary>The store itself: array in x0, index in x1, value in x2.</summary>
        Store,
        /// <summary>The check in front of it, which answers nothing the language can see.</summary>
        Check,
    }

    /// <summary>Where an array's length is, and where its elements begin.</summary>
    private const long Length = 0x18;
    private const long Elements = 0x20;

    /// <summary>Where a class records the class of the elements it holds.</summary>
    private const long ElementClass = 0x40;

    /// <summary>As much of a body as either shape needs, plus room for the frame either may set up.</summary>
    private const int Window = 12;

    private static readonly ConcurrentDictionary<ApplicationAnalysisContext, ConcurrentDictionary<ulong, Kind>>
        answers = new();

    private static readonly bool Trace = System.Environment.GetEnvironmentVariable("ARRAYSTORE_TRACE") is not null;

    /// <summary>The ISIL a call to one of these is really made of, where it is a call to one of these.</summary>
    public static List<(OpCode OpCode, object[] Operands)>? Rewrite(MethodAnalysisContext context, ulong target,
        System.Func<Arm64Register, Register> registerFor)
    {
        return Of(context.AppContext, target) switch
        {
            //`a[i] = x`, with the index scaled by the width of a reference and the header stepped over.
            Kind.Store =>
            [
                (OpCode.Move, [
                    new MemoryOperand(registerFor(Arm64Register.X0), registerFor(Arm64Register.X1), Elements,
                        context.AppContext.Binary.is32Bit ? 4 : 8),
                    registerFor(Arm64Register.X2),
                ]),
            ],

            Kind.Check => [(OpCode.Nop, [])],

            _ => null,
        };
    }

    private static Kind Of(ApplicationAnalysisContext app, ulong target)
        => answers.GetOrAdd(app, _ => new()).GetOrAdd(target, address => Recognise(app, address));

    private static Kind Recognise(ApplicationAnalysisContext app, ulong address)
    {
        //Read as a fixed window rather than through `GetArm64MethodBodyAtVirtualAddress`, whose unmanaged
        //path stops at the first `b` - and `b.cs` is a `b`. The store's bounds check is the third
        //instruction, so that reader returns three instructions and never reaches the store itself.
        List<Arm64Instruction> body;

        try
        {
            if (!app.Binary.TryMapVirtualAddressToRaw(address, out var raw) || raw <= 0)
                return Kind.Neither;

            var content = app.Binary.GetRawBinaryContent();

            if (raw + Window * 4 > content.Length)
                return Kind.Neither;

            body = Disassembler.Disassemble(content.Slice((int)raw, Window * 4), address,
                new Disassembler.Options(true, true, false)).ToList();
        }
        catch
        {
            return Kind.Neither;
        }

        var kind = IsStore(body) ? Kind.Store : IsCheck(body) ? Kind.Check : Kind.Neither;

        if (Trace && kind != Kind.Neither)
            System.Console.Error.WriteLine($"ARRAYSTORE {address:X} = {kind}");

        return kind;
    }

    /// <summary>
    /// The store: the length read, the index compared against it, the element address worked out at the
    /// width of a reference, and the value written there.
    /// </summary>
    private static bool IsStore(List<Arm64Instruction> body)
    {
        var length = false;

        foreach (var instruction in body)
        {
            switch (instruction.Mnemonic)
            {
                //The length, which is the only thing at 0x18 and the only reason to read it.
                case Arm64Mnemonic.LDR when Reads(instruction, Arm64Register.X0, Length):
                    length = true;
                    break;

                //and then `str x2, [x0, #0x20]!`, the value into the element address the compare guarded.
                //The `add x0, x0, x1, lsl #3` between them is not matched: what it shifts by is spelled
                //several ways in the encoding, and a body that reads a length and stores its third argument
                //at the start of an array's elements is not something else.
                case Arm64Mnemonic.STR when length && Register(instruction.Op0Reg) == 2
                    && Reads(instruction, Arm64Register.X0, Elements):
                    return true;

                case Arm64Mnemonic.RET:
                    return false;
            }
        }

        return false;
    }

    /// <summary>
    /// The check: the array's class, the class of its elements out of that, and a question asked about the
    /// value - which is a shape nothing else has, as <see cref="ArrayStoreCheckRemover"/> also relies on.
    /// </summary>
    private static bool IsCheck(List<Arm64Instruction> body)
    {
        var klass = false;

        foreach (var instruction in body)
        {
            switch (instruction.Mnemonic)
            {
                //`ldr x8, [x0]` - the object header, which is the class.
                case Arm64Mnemonic.LDR when Reads(instruction, Arm64Register.X0, 0):
                    klass = true;
                    break;

                //and the element class out of it.
                case Arm64Mnemonic.LDR when klass && Reads(instruction, Arm64Register.X8, ElementClass):
                    return true;

                case Arm64Mnemonic.RET:
                    return false;
            }
        }

        return false;
    }

    /// <summary>Whether an instruction reads through a register at a fixed distance from it.</summary>
    private static bool Reads(Arm64Instruction instruction, Arm64Register through, long addend)
        => instruction.MemBase == through && instruction.MemOffset == addend
            && instruction.MemAddendReg == Arm64Register.INVALID;

    private static int Register(Arm64Register register) => register switch
    {
        >= Arm64Register.X0 and <= Arm64Register.X31 => register - Arm64Register.X0,
        >= Arm64Register.W0 and <= Arm64Register.W31 => register - Arm64Register.W0,
        _ => -1,
    };
}
