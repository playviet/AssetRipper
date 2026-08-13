using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using Disarm;
using Disarm.InternalDisassembly;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Preparing a class hands the class straight back, so the call is a copy.
/// </summary>
/// <remarks>
/// <para>
/// Before touching a type's statics il2cpp makes sure it is ready, through a thunk of five instructions that
/// tests one bit and tail-calls the real initialiser if it is clear:
/// </para>
/// <code>
/// add   x8, x0, #309      // 0x135, the flags byte
/// ldrh  w8, [x8]
/// tbnz  w8, #0, +8        // ready already
/// b     Class::Init       // which also answers with the class
/// ret
/// </code>
/// <para>
/// <b>x0 is never written</b>, so the answer is the argument. A second thunk sits directly in front of it and
/// picks the class out of a table first - <c>ldr x0, [x0, w1, sxtw #3]</c> - and answers with that.
/// </para>
/// <para>
/// Left as a call to an address, the class the whole chain hangs off has no type: everything read through it
/// afterwards - the runtime generic context at <c>0xC0</c>, the static storage at <c>0xB8</c> - is a read of
/// unmanaged memory, and the statement that wanted a static field goes. Saying the call is a copy types the
/// class from its argument and lets the passes that already understand a class pointer take the rest.
/// </para>
/// <para>
/// Recognised by disassembling the callee, not by its address, so nothing here is pinned to this binary.
/// Preparing a class is the runtime's business and not the program's - which is why
/// <see cref="ClassInitCallRemover"/> already takes the calls it can see; this one only differs in answering.
/// </para>
/// </remarks>
public static class ClassInitAnswersWithTheClass
{
    /// <summary>Where a class keeps the flags whose first bit says it is ready.</summary>
    private const long ReadyFlags = 0x135;

    /// <summary>Long enough for the thunk and its table read, short enough to reject anything else.</summary>
    private const int Window = 6;

    private static readonly ConcurrentDictionary<ulong, int> Answers = [];

    public static bool Run(MethodAnalysisContext method)
    {
        if (method.ControlFlowGraph is not { } graph)
            return false;

        var changed = false;

        foreach (var call in graph.Instructions)
        {
            if (call.OpCode != OpCode.Call || call.Operands.Count < 4 || call.Operands[0] is not ulong address
                || call.Operands[1] is not LocalVariable answer || call.Operands[2] is not LocalVariable klass)
            {
                continue;
            }

            var arguments = Answers.GetOrAdd(address, at => Shape(method, at));

            if (arguments == 1)
            {
                call.OpCode = OpCode.Move;
                call.Operands = [answer, klass];
                changed = true;
            }
            else if (arguments == 2 && Constant(call.Operands[3]) is { } index && index >= 0 && index < 0x1000)
            {
                //The table read the second thunk does first, written as what it is: one entry of the runtime
                //generic context, which is the shape everything downstream already resolves. Written as a
                //distance rather than as an index times a width because that is the only shape the resolver
                //takes, and the index is a constant every time - the slot is chosen when the body is
                //compiled, not when it runs.
                call.OpCode = OpCode.Move;
                call.Operands = [answer, new MemoryOperand(klass, null, index * 8, 0)];
                changed = true;
            }
        }

        return changed;
    }

    private static long? Constant(object operand)
        => operand switch
        {
            int i => i,
            uint u => u,
            long l => l,
            ulong ul => (long)ul,
            _ => null,
        };

    /// <summary>
    /// How many arguments the initialisation thunk at an address takes, or nought where it is not one.
    /// </summary>
    private static int Shape(MethodAnalysisContext method, ulong address)
    {
        List<Arm64Instruction> body;

        try
        {
            body = NewArm64Utils.GetArm64MethodBodyAtVirtualAddress(method.AppContext.Binary, address, false, Window);
        }
        catch
        {
            return 0;
        }

        if (System.Environment.GetEnvironmentVariable("CLASSINIT_TRACE") is not null)
            System.Console.Error.WriteLine($"CLASSINIT {address:X}: "
                + string.Join(" | ", body.Select(i => $"{i.Mnemonic} op0={i.Op0Reg} op1kind={i.Op1Kind} membase={i.MemBase} memadd={i.MemAddendReg} op2kind={i.Op2Kind} imm={i.Op2Imm}")));

        var from = 0;
        var arguments = 1;

        //The table read, where there is one: the class comes out of a list before it is prepared.
        if (body.Count > 0 && body[0] is { Mnemonic: Arm64Mnemonic.LDR, Op0Reg: Arm64Register.X0, Op1Kind: Arm64OperandKind.Memory }
            && body[0].MemBase == Arm64Register.X0 && body[0].MemAddendReg != Arm64Register.INVALID)
        {
            from = 1;
            arguments = 2;
        }

        //And then the four that test the flag and either return or hand over.
        if (body.Count < from + 4
            || body[from] is not { Mnemonic: Arm64Mnemonic.ADD, Op1Reg: Arm64Register.X0, Op2Kind: Arm64OperandKind.Immediate }
            || body[from].Op2Imm != ReadyFlags
            || body[from + 1].Mnemonic is not (Arm64Mnemonic.LDRH or Arm64Mnemonic.LDRB or Arm64Mnemonic.LDR)
            || body[from + 2].Mnemonic is not (Arm64Mnemonic.TBNZ or Arm64Mnemonic.TBZ)
            || body[from + 3].Mnemonic != Arm64Mnemonic.B)
        {
            return 0;
        }

        //Nothing after the table read may touch the answer, or it is not the argument that comes back.
        for (var i = from; i < body.Count; i++)
            if (body[i].Op0Reg is Arm64Register.X0 or Arm64Register.W0)
                return 0;

        return arguments;
    }
}
