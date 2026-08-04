using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Cpp2IL.Core.Api;
using LibCpp2IL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using Disarm;
using Disarm.InternalDisassembly;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Finds the property that a static field the code cannot name is reached through.
///
/// <c>Vector2.zero</c> is a property over a private static field, and il2cpp reads the field directly - so the
/// recovered code names the field, which the project cannot see and which its own build of that assembly may not
/// even have. The property is the accessible way to the same value, and which property it is can be read straight
/// out of the getter: it is a handful of instructions that load the type's static storage and take one offset out
/// of it. Matching on the offset the getter reads is exact, unlike matching on the field's name, which would only
/// be a convention and would put a wrong value in the output when it did not hold.
/// </summary>
public static class StaticPropertyRecovery
{
    //A getter of this shape is a few instructions. Reading further into something that turned out not to be one
    //costs time on every candidate that misses.
    private const int MaxInstructions = 32;

    //Keyed by the type and the offset within its static storage, which is what identifies the value being read.
    private static readonly ConcurrentDictionary<(TypeAnalysisContext Type, long Offset), MethodAnalysisContext?> Cache = new();

    /// <summary>
    /// The accessible static property getter that returns the value at <paramref name="fieldOffset"/> in
    /// <paramref name="type"/>'s static storage, if exactly one getter reads it.
    /// </summary>
    public static MethodAnalysisContext? FindGetter(TypeAnalysisContext type, TypeAnalysisContext fieldType, long fieldOffset, long staticFieldsOffset)
    {
        if (type.AppContext.Binary.InstructionSetId != DefaultInstructionSets.ARM_V8)
            return null;

        return Cache.GetOrAdd((type, fieldOffset), key => Search(key.Type, fieldType, key.Offset, staticFieldsOffset));
    }

    private static MethodAnalysisContext? Search(TypeAnalysisContext type, TypeAnalysisContext fieldType, long fieldOffset, long staticFieldsOffset)
    {
        MethodAnalysisContext? found = null;

        foreach (var property in type.Properties)
        {
            if (property.Getter is not { IsStatic: true } getter || getter.UnderlyingPointer == 0)
                continue;

            if ((getter.Attributes & MethodAttributes.MemberAccessMask) != MethodAttributes.Public)
                continue;

            //A getter over a different field can still read the right offset if its value happens to be the same
            //size, so the type it returns has to agree as well.
            if (property.PropertyType.FullName != fieldType.FullName)
                continue;

            if (!ReadsStaticStorageAt(getter, fieldOffset, staticFieldsOffset))
                continue;

            //Two getters over one value is an alias, and there is no way to tell which one the code meant.
            if (found != null)
                return null;

            found = getter;
        }

        return found;
    }

    /// <summary>
    /// Whether the method loads the declaring type's static storage and then reads <paramref name="fieldOffset"/>
    /// out of it. Read from the instruction stream rather than from ISIL, so that it costs almost nothing, cannot
    /// recurse back into the analysis that asked, and cannot race another thread doing the same.
    /// </summary>
    private static bool ReadsStaticStorageAt(MethodAnalysisContext getter, long fieldOffset, long staticFieldsOffset)
    {
        List<Arm64Instruction> body;

        try
        {
            body = NewArm64Utils.GetArm64MethodBodyAtVirtualAddress(getter.AppContext.Binary, getter.UnderlyingPointer, false, MaxInstructions);
        }
        catch
        {
            return false;
        }

        //The registers currently holding the static storage pointer.
        var storage = new HashSet<Arm64Register>();

        foreach (var instruction in body)
        {
            if (instruction.Mnemonic == Arm64Mnemonic.RET)
                return false;

            //Which operand carries the address differs between forms - a pair load puts it third - so the
            //memory is taken from the instruction rather than from a fixed operand position. A store has an
            //address too, and is not a read of it.
            var mnemonic = instruction.Mnemonic.ToString();
            var isLoad = mnemonic.StartsWith("LD", StringComparison.Ordinal);
            var addressable = instruction.MemBase != Arm64Register.INVALID && instruction.MemAddendReg == Arm64Register.INVALID;

            //The value has to go where the method returns from, or the getter is doing something to it rather
            //than handing it back, and this is not the property that names the field.
            if (isLoad && addressable && storage.Contains(Canonical(instruction.MemBase)) && instruction.MemOffset == fieldOffset
                && instruction.Op0Kind == Arm64OperandKind.Register && IsReturnRegister(instruction.Op0Reg))
                return true;

            //Whatever the instruction writes stops holding what it held, unless what it just loaded is the
            //static storage pointer itself.
            if (instruction.Op0Kind == Arm64OperandKind.Register)
            {
                var destination = Canonical(instruction.Op0Reg);

                if (isLoad && addressable && instruction.MemOffset == staticFieldsOffset)
                    storage.Add(destination);
                else
                    storage.Remove(destination);
            }

            //A pair form writes a second register.
            if (instruction.Op1Kind == Arm64OperandKind.Register && mnemonic.StartsWith("LDP", StringComparison.Ordinal))
                storage.Remove(Canonical(instruction.Op1Reg));
        }

        return false;
    }

    //Where a value is handed back from, whichever width it is being returned at.
    private static bool IsReturnRegister(Arm64Register register) => register is Arm64Register.X0 or Arm64Register.W0
        or Arm64Register.V0 or Arm64Register.D0 or Arm64Register.S0 or Arm64Register.H0 or Arm64Register.B0;

    //w1 is the low half of x1, so both have to name the same register or a write through one name would not be
    //seen as clobbering what the other holds.
    private static Arm64Register Canonical(Arm64Register register) => register switch
    {
        >= Arm64Register.W0 and <= Arm64Register.W31 => Arm64Register.X0 + (register - Arm64Register.W0),
        _ => register,
    };
}
