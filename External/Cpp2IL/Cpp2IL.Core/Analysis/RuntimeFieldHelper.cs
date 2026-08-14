using System.Collections.Generic;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using Disarm;
using Disarm.InternalDisassembly;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// What one of the runtime's field accessors does, read off its own code.
/// </summary>
/// <remarks>
/// <para>
/// A shared generic body cannot inline a field's offset, so it hands the object and a <c>FieldInfo</c> to a
/// helper. There are two of them and they are not interchangeable: one hands the field's <b>address</b> back,
/// the other <b>stores</b> a value into it. They take the same two arguments in the same registers, so
/// nothing at the call site tells them apart - and reading a store as an address loses the write outright,
/// which no scorer can see.
/// </para>
/// <code>
/// ldr   x9,  [x1, #0x10]    // FieldInfo->parent
/// ldrsw x10, [x1, #0x18]    // FieldInfo->offset
/// ldr   w9,  [x9, #0x28]    // the parent's byval_arg bits, whose sign bit is `valuetype`
/// add   x9,  x0,  x10       // object + offset
/// csel  x8,  #-16, xzr, lt  // less the object header, where the parent is a value type
/// add   x0,  x9,  x8
/// ret
/// </code>
/// <para>
/// That is the address helper, and the store helper is that call followed by a store of <c>x2</c> through
/// what it answered. So each is recognised by <b>what it does</b> rather than by its address: the address
/// helper is a body that reads <c>FieldInfo->offset</c>, stores nothing and returns; the store helper is one
/// that calls an address helper and writes its third argument through the answer. Nothing here is pinned to
/// one binary, and a build that inlines or renumbers them is read the same way.
/// </para>
/// </remarks>
public static class RuntimeFieldHelper
{
    /// <summary>What a helper at an address does with the <c>FieldInfo</c> it is handed.</summary>
    public enum Kind
    {
        /// <summary>Not one of these at all.</summary>
        None,

        /// <summary><c>&amp;object.field</c>, answered in x0.</summary>
        Address,

        /// <summary><c>object.field = x2</c>.</summary>
        Store,
    }

    /// <summary>Where <c>FieldInfo</c> records the distance into the object - the read that gives it away.</summary>
    private const long OffsetMember = 0x18;

    /// <summary>Enough instructions for either helper; both are under fifteen.</summary>
    private const int Window = 32;

    /// <summary>The highest register a call is allowed to clobber, so a value held across one is believed.</summary>
    private const int CallerSaved = 18;

    private static readonly Dictionary<(ApplicationAnalysisContext App, ulong Address), Kind> Known = [];

    public static Kind Of(ApplicationAnalysisContext app, ulong address)
    {
        lock (Known)
        {
            if (Known.TryGetValue((app, address), out var known))
                return known;

            //Recorded before it is worked out, so a helper that somehow reaches itself answers None rather
            //than running for ever.
            Known[(app, address)] = Kind.None;

            return Known[(app, address)] = Decide(app, address, 0);
        }
    }

    private static Kind Decide(ApplicationAnalysisContext app, ulong address, int depth)
    {
        //One instruction is enough to be a veneer, and the body reader stops at the branch - so asking for
        //more than one is what kept the thunk table's entries from being followed at all.
        if (depth > 2 || Body(app, address) is not { Count: > 0 } body)
        {
            Trace($"{address:X} depth={depth} no body");

            return Kind.None;
        }

        //A veneer - one unconditional branch and nothing else - is the helper it branches to. Every one of
        //these is reached through the thunk table as often as directly.
        if (body[0].Mnemonic == Arm64Mnemonic.B && Unconditional(body[0]))
            return Decide(app, body[0].BranchTarget, depth + 1);

        var readsTheOffset = false;
        var stored = false;
        var value = new HashSet<int> { 2 };
        var answered = new HashSet<int>();
        var wroteTheField = false;

        foreach (var instruction in body)
        {
            switch (instruction.Mnemonic)
            {
                case Arm64Mnemonic.RET:
                    //Nothing after the return belongs to this function.
                    Trace($"{address:X} ret offset={readsTheOffset} stored={stored} wrote={wroteTheField}");

                    return Decided(readsTheOffset, stored, wroteTheField);

                case Arm64Mnemonic.LDR or Arm64Mnemonic.LDRSW or Arm64Mnemonic.LDRSH or Arm64Mnemonic.LDRSB
                    or Arm64Mnemonic.LDRH or Arm64Mnemonic.LDRB or Arm64Mnemonic.LDUR:

                    if (Register(instruction.MemBase) == 1 && instruction.MemOffset == OffsetMember)
                        readsTheOffset = true;

                    Clobber(value, answered, instruction.Op0Reg);
                    break;

                case Arm64Mnemonic.STR or Arm64Mnemonic.STRH or Arm64Mnemonic.STRB or Arm64Mnemonic.STUR
                    or Arm64Mnemonic.STURH or Arm64Mnemonic.STURB or Arm64Mnemonic.STP:

                    stored = true;

                    //The whole point: the value the caller passed third, written through what the address
                    //helper answered, at no further distance.
                    if (instruction.MemOffset == 0 && answered.Contains(Register(instruction.MemBase))
                        && value.Contains(Register(instruction.Op0Reg)))
                    {
                        wroteTheField = true;
                    }

                    break;

                case Arm64Mnemonic.MOV or Arm64Mnemonic.MOVZ or Arm64Mnemonic.MOVN or Arm64Mnemonic.MOVK:
                    var from = instruction.Op1Kind == Arm64OperandKind.Register ? Register(instruction.Op1Reg) : -1;
                    var to = Register(instruction.Op0Reg);

                    //Register 31 read as a source is the zero register, and a copy of nought is not a copy
                    //of the value.
                    Follow(value, from, to);
                    Follow(answered, from, to);
                    break;

                case Arm64Mnemonic.BL:
                    //A call keeps only the callee-saved registers, and answers in x0 where what it called
                    //was an address helper.
                    Forget(value);
                    Forget(answered);

                    //Through the cache rather than straight down, because both store helpers reach the same
                    //address helper and every call site asks about them again.
                    if (Of(app, instruction.BranchTarget) == Kind.Address)
                        answered.Add(0);

                    break;

                case Arm64Mnemonic.B when Unconditional(instruction):
                    //A tail call ends the function as surely as a return does.
                    Trace($"{address:X} tail offset={readsTheOffset} stored={stored} wrote={wroteTheField}");

                    return Decided(readsTheOffset, stored, wroteTheField);

                default:
                    if (instruction.Op0Kind == Arm64OperandKind.Register)
                        Clobber(value, answered, instruction.Op0Reg);

                    break;
            }
        }

        return Decided(readsTheOffset, stored, wroteTheField);
    }

    private static Kind Decided(bool readsTheOffset, bool stored, bool wroteTheField)
        => wroteTheField ? Kind.Store
            : readsTheOffset && !stored ? Kind.Address
            : Kind.None;

    private static void Trace(string what)
    {
        if (System.Environment.GetEnvironmentVariable("RUNTIMEFIELD_TRACE") == "1")
            System.Console.Error.WriteLine("RUNTIMEFIELD " + what);
    }

    private static void Follow(HashSet<int> carried, int from, int to)
    {
        if (to < 0)
            return;

        if (from >= 0 && from != 31 && carried.Contains(from))
            carried.Add(to);
        else
            carried.Remove(to);
    }

    private static void Clobber(HashSet<int> value, HashSet<int> answered, Arm64Register register)
    {
        var written = Register(register);

        if (written < 0)
            return;

        value.Remove(written);
        answered.Remove(written);
    }

    private static void Forget(HashSet<int> carried) => carried.RemoveWhere(register => register <= CallerSaved);

    private static bool Unconditional(Arm64Instruction instruction)
        => instruction.MnemonicConditionCode is Arm64ConditionCode.NONE or Arm64ConditionCode.AL;

    private static List<Arm64Instruction>? Body(ApplicationAnalysisContext app, ulong address)
    {
        try
        {
            return NewArm64Utils.GetArm64MethodBodyAtVirtualAddress(app.Binary, address, false, Window);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The register's number, whichever width it was named at.</summary>
    private static int Register(Arm64Register register) => register switch
    {
        >= Arm64Register.X0 and <= Arm64Register.X31 => register - Arm64Register.X0,
        >= Arm64Register.W0 and <= Arm64Register.W31 => register - Arm64Register.W0,
        _ => -1,
    };
}
