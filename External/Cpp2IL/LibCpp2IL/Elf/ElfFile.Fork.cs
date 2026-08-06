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
}
