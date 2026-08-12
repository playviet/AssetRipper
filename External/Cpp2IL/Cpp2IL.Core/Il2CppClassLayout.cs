namespace Cpp2IL.Core;

/// <summary>
/// Where each field of <c>Il2CppClass</c> sits, for the 64-bit layout Unity 2022.3 builds.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Il2CppClassUsefulOffsets"/> describes an older metadata, and only the handful of fields the
/// analysis happened to need. Every offset here is computed from the struct Unity itself ships -
/// <c>Unity.app/Contents/il2cpp/libil2cpp/il2cpp-class-internals.h</c> - rather than guessed from how a value
/// is used, which is what every earlier attempt at these had to do.
/// </para>
/// <para>
/// Five of them were found empirically first, by chasing what the recovered code was reading, and all five
/// agree with the header exactly: <c>element_class</c> 0x40, <c>interfaceOffsets</c> 0xB0,
/// <c>static_fields</c> 0xB8, <c>rgctx_data</c> 0xC0 and <c>interface_offsets_count</c> 0x12E - the last of
/// which the old table puts at 0x12A. That agreement is what makes the rest of this table trustworthy.
/// </para>
/// <para>
/// The point of naming them is to stop guessing at what a read means. A read of <c>0xFC</c> is
/// <c>stack_slot_size</c> - a size the inlined allocation genuinely uses, so answering it with a constant
/// would compile and be wrong. A read of <c>0x135</c> is the byte holding <c>initialized_and_no_error</c>,
/// which is bookkeeping the managed program never sees, so answering it is right. Nothing but the layout
/// tells those two apart.
/// </para>
/// </remarks>
public static class Il2CppClassLayout
{
    /// <summary>The type this class stands for, whose second word carries the flags and the type code.</summary>
    public const long ByValArg = 0x20;

    public const long ElementClass = 0x40;
    public const long CastClass = 0x48;
    public const long DeclaringType = 0x50;
    public const long Parent = 0x58;
    public const long GenericClass = 0x60;
    public const long Fields = 0x80;
    public const long Events = 0x88;
    public const long Properties = 0x90;
    public const long Methods = 0x98;
    public const long NestedTypes = 0xA0;
    public const long ImplementedInterfaces = 0xA8;
    public const long InterfaceOffsets = 0xB0;
    public const long StaticFields = 0xB8;
    public const long RgctxData = 0xC0;

    /// <summary>The chain of base classes, indexed by <see cref="TypeHierarchyDepth"/> - an inlined cast.</summary>
    public const long TypeHierarchy = 0xC8;

    public const long CctorStarted = 0xDC;
    public const long CctorFinishedOrNoCctor = 0xE0;

    public const long InstanceSize = 0xF8;
    public const long StackSlotSize = 0xFC;
    public const long ActualSize = 0x100;
    public const long ElementSize = 0x104;
    public const long NativeSize = 0x108;
    public const long StaticFieldsSize = 0x10C;
    public const long Flags = 0x118;
    public const long Token = 0x11C;

    public const long MethodCount = 0x120;
    public const long FieldCount = 0x124;
    public const long VtableCount = 0x12A;
    public const long InterfacesCount = 0x12C;
    public const long InterfaceOffsetsCount = 0x12E;

    /// <summary>How many classes deep <see cref="TypeHierarchy"/> goes.</summary>
    public const long TypeHierarchyDepth = 0x130;

    public const long Rank = 0x132;
    public const long MinimumAlignment = 0x133;
    public const long PackingSize = 0x134;

    /// <summary>
    /// The first byte of the bit flags, whose lowest bit is <c>initialized_and_no_error</c>. A body reads it
    /// to decide whether the class still has to be prepared, which is the runtime's business and not the
    /// program's - so a read of it can be answered rather than kept.
    /// </summary>
    public const long Bitflags = 0x135;

    public const long BitflagsSecondByte = 0x136;

    public const long Vtable = 0x138;

    /// <summary>Whether a read at this offset is bookkeeping the managed program never sees.</summary>
    /// <remarks>
    /// Only the flags: everything else in the second half of the structure is a size, a count or a pointer
    /// the compiled code genuinely computes with.
    /// </remarks>
    public static bool IsInitialisationFlag(long offset)
        => offset is Bitflags or BitflagsSecondByte or CctorFinishedOrNoCctor or CctorStarted;

}

/// <summary>
/// Where each field of <c>MethodInfo</c> sits, for the same 64-bit layout, from the same header.
/// </summary>
/// <remarks>
/// Two of these were already in use and both agree: the runtime generic context at 0x38 and the class at
/// 0x20 - where <see cref="Il2CppMethodInfoUsefulOffsets"/> puts the class at 0x18, which is the name.
/// </remarks>
public static class Il2CppMethodInfoLayout
{
    public const long MethodPointer = 0x00;
    public const long VirtualMethodPointer = 0x08;
    public const long InvokerMethod = 0x10;
    public const long Name = 0x18;
    public const long Klass = 0x20;
    public const long ReturnType = 0x28;
    public const long Parameters = 0x30;
    public const long RgctxData = 0x38;
    public const long GenericMethod = 0x40;
    public const long Token = 0x48;
    public const long Slot = 0x50;
    public const long ParametersCount = 0x52;

    /// <summary>
    /// The three pointers a body can reach a method's own code through. Which one the compiler picks depends
    /// on how the method is shared - a fully shared generic is entered through the invoker - and all three
    /// arrive at the same managed method, which is what a recovered call has to name.
    /// </summary>
    public static bool IsEntryPoint(long offset)
        => offset is MethodPointer or VirtualMethodPointer or InvokerMethod;
}
