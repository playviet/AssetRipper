using System.Collections.Generic;

namespace LibCpp2IL.Elf;

public sealed partial class ElfFile
{
    /// <summary>
    /// Whether an address is in a section the process cannot write to, so that whatever the file holds there
    /// is what the program will read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is what makes it safe to answer a load out of the binary rather than leaving it unresolved. A
    /// constant pool - the sixteen bytes of field initialisers a constructor copies in one instruction - is in
    /// <c>.rodata</c>, and reading it gives the number the source wrote. The same read against <c>.data</c> or
    /// <c>.bss</c> would give whatever the value happened to be before anything ran, which is not a constant
    /// but an initial value, and stating it as a constant would be stating it wrongly.
    /// </para>
    /// <para>
    /// <c>.data.rel.ro</c> is deliberately not accepted even though it is read-only by the time the program
    /// runs: the dynamic linker writes it at load, so the bytes in the file are relocations rather than values.
    /// Requiring <see cref="ElfSectionHeaderFlags.SHF_WRITE"/> to be clear excludes it, since it carries the
    /// flag until the linker is done with it.
    /// </para>
    /// </remarks>
    public bool IsReadOnlyAddress(ulong address, int length)
    {
        foreach (var section in _elfSectionHeaderEntries)
        {
            if ((section.Flags & ElfSectionHeaderFlags.SHF_WRITE) != 0
                || (section.Flags & ElfSectionHeaderFlags.SHF_ALLOC) == 0
                || section.Type == ElfSectionEntryType.SHT_NOBITS)
                continue;

            if (address >= section.VirtualAddress && address + (ulong)length <= section.VirtualAddress + section.Size)
                return true;
        }

        return false;
    }

    private Dictionary<ulong, string>? _importsByJumpSlot;

    /// <summary>
    /// The imported function a call at this address ends up in, where the address is a stub that jumps to one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A call to <c>memcpy</c> or <c>sinf</c> does not go to the function: it goes to a four-instruction stub
    /// in <c>.plt</c> that loads the real address out of a slot the dynamic linker fills in, and jumps there.
    /// Nothing in the method tables names the stub, so the call resolves to nothing and the whole statement is
    /// written out as <c>Method not found @4AFA510</c> - <b>523 times</b> in this game, over 51 distinct
    /// addresses.
    /// </para>
    /// <para>
    /// The name is in the file, twice removed: the stub says which slot it reads, and the slot has a
    /// <c>JUMP_SLOT</c> relocation naming the symbol. Following both is exact - it is how the loader itself
    /// decides what the call reaches - so nothing here is a guess.
    /// </para>
    /// </remarks>
    public string? ImportedFunctionAt(ulong address)
    {
        _importsByJumpSlot ??= ReadJumpSlots();

        return _importsByJumpSlot.Count > 0 && JumpSlotOfStub(address) is { } slot
            && _importsByJumpSlot.TryGetValue(slot, out var name)
            ? name
            : null;
    }

    /// <summary>Every jump slot the dynamic linker fills, by the name it fills it with.</summary>
    private Dictionary<ulong, string> ReadJumpSlots()
    {
        var slots = new Dictionary<ulong, string>();

        //Only the 64-bit `RELA` form, which is what aarch64 uses. A 32-bit build would need the `REL` form
        //and a different entry size, and this fork has no such binary to check it against.
        if (is32Bit
            || GetDynamicEntryOfType(ElfDynamicType.DT_JMPREL) is not { } relocations
            || GetDynamicEntryOfType(ElfDynamicType.DT_PLTRELSZ) is not { } size
            || GetDynamicEntryOfType(ElfDynamicType.DT_SYMTAB) is not { } symbols
            || GetDynamicEntryOfType(ElfDynamicType.DT_STRTAB) is not { } strings)
            return slots;

        const int RelaSize = 24, SymbolSize = 24;

        var symbolTable = MapVirtualAddressToRaw(symbols.Value);

        foreach (var relocation in ReadReadableArrayAtRawAddr<ElfRelaEntry>(
            MapVirtualAddressToRaw(relocations.Value), (long)(size.Value / RelaSize)))
        {
            if (relocation.Type != ElfRelocationType.R_AARCH64_JUMP_SLOT)
                continue;

            var symbol = ReadReadable<ElfDynamicSymbol64>(symbolTable + (long)relocation.Symbol * SymbolSize);
            var name = Reader.ReadStringToNull(strings.Value + symbol.NameOffset);

            if (!string.IsNullOrEmpty(name))
                slots[relocation.Offset] = name;
        }

        return slots;
    }

    /// <summary>
    /// The jump slot a procedure-linkage stub reads, if the four instructions at this address are one.
    /// </summary>
    /// <remarks>
    /// The stub is always the same shape - <c>adrp</c> a page, <c>ldr</c> the slot out of it, <c>add</c> the
    /// same offset back for the resolver, <c>br</c> - and only the first two are needed. Anything that does
    /// not begin exactly that way is not a stub and is left alone, which is what keeps this from naming an
    /// ordinary method after whatever happened to sit at the same offset.
    /// </remarks>
    private ulong? JumpSlotOfStub(ulong address)
    {
        if (!TryMapVirtualAddressToRaw(address, out var raw))
            return null;

        uint[] code;

        try
        {
            code = ReadClassArrayAtRawAddr<uint>(raw, 4);
        }
        catch
        {
            return null;
        }

        //ADRP Xd, page - the immediate is split, low two bits high in the word, and counts pages.
        if ((code[0] & 0x9F00_0000) != 0x9000_0000)
            return null;

        var immediate = (long)(((code[0] >> 5) & 0x7_FFFF) << 2 | ((code[0] >> 29) & 3));

        //Sign-extended over 21 bits before it is scaled to a page.
        if ((immediate & 0x10_0000) != 0)
            immediate -= 0x20_0000;

        //LDR Xt, [Xn, #imm12] at sixty-four bits, which is what makes the immediate count eight bytes.
        if ((code[1] & 0xFFC0_0000) != 0xF940_0000)
            return null;

        return (ulong)((long)(address & ~0xFFFul) + (immediate << 12)) + (((code[1] >> 10) & 0xFFF) * 8ul);
    }
}
