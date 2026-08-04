namespace Cpp2IL.Core.ISIL;

/// <summary>
/// The address of a stack slot, as opposed to what is in it.
/// </summary>
/// <remarks>
/// `add x1, sp, #0xc` and `ldrb w8, [sp, #0xc]` were both lifted to a <see cref="StackOffset"/>, so taking a
/// slot's address and reading its contents became the same instruction. That is what an out parameter is made
/// of: the caller hands over the address, the callee writes the slot, the caller reads it back. Modelled as a
/// read, the write in the middle is invisible, and the zero the compiler put there beforehand flows straight
/// past the call - `bool.TryParse(raw, out var b)` came back as `result = 0L != 0L`.
/// </remarks>
public struct StackSlotAddress(int offset)
{
    public int Offset = offset;

    public override string ToString() => $"&stack[{Format(Offset)}]";

    internal static string Format(int offset) =>
        offset < 0 ? "-" + (-offset).ToString("X") : offset.ToString("X");
}

/// <summary>
/// The two ways a stack slot reaches the rest of the pipeline, handled together so that the one place each is
/// recognised does not have to know about both.
/// </summary>
public static class StackSlots
{
    public const string ValuePrefix = "stack_";
    public const string AddressPrefix = "stackaddr_";

    public static int? OffsetOf(object operand) => operand switch
    {
        StackOffset slot => slot.Offset,
        StackSlotAddress address => address.Offset,
        _ => null,
    };

    /// <summary>The same kind of operand, moved to where the frame actually put it.</summary>
    public static object WithOffset(object operand, int offset) => operand switch
    {
        StackSlotAddress => new StackSlotAddress(offset),
        _ => new StackOffset(offset),
    };

    /// <summary>
    /// The register a slot becomes. An address gets a name of its own, so that single assignment form treats
    /// it as a variable in its own right rather than as another read of the slot it points at.
    /// </summary>
    public static string? NameOf(object operand) => operand switch
    {
        StackOffset slot => ValuePrefix + StackSlotAddress.Format(slot.Offset),
        StackSlotAddress address => AddressPrefix + StackSlotAddress.Format(address.Offset),
        _ => null,
    };
}
