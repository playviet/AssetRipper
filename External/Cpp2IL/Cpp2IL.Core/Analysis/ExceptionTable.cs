using System;
using System.Collections.Generic;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// The C++ exception tables the compiler left in the binary: which range of instructions a <c>try</c>
/// covered, and where the landing pad for it is.
/// </summary>
/// <remarks>
/// <para>
/// Everything the recovery had been inferring about a <c>try</c> is <b>written down</b>. <c>.eh_frame_hdr</c>
/// holds a sorted table of (function, FDE); the FDE names an LSDA in <c>.gcc_except_table</c>; and the LSDA's
/// call-site table maps a range of addresses to the landing pad that covers it. <b>That range is the
/// <c>try</c></b>, exactly, including every call inside it that can throw.
/// </para>
/// <para>
/// Why this is worth reading rather than guessing at: a landing pad is not laid after the throw it belongs
/// to. In <c>CFramework.SaveIO::Load</c> the pads are twenty four-byte stubs collected <i>after</i> the whole
/// body, and the graph has no edge to any of them - which is why 2390 throwing blocks in this game had no
/// successor for the structural recognition to follow. The table names them all.
/// </para>
/// <para>
/// What the table does <b>not</b> say is which managed type is caught: il2cpp raises one C++ type,
/// <c>Il2CppExceptionWrapper</c>, and does the managed dispatch itself inside the pad. So the type still
/// comes from the <c>Il2CppClass&lt;T&gt;</c> operand <see cref="CatchClauses"/> already reads. The two
/// halves are complementary and neither replaces the other.
/// </para>
/// <para>
/// Read directly out of the file rather than through LibCpp2IL's ELF model, because none of this is
/// il2cpp metadata - it is ordinary C++ unwind information, and the only thing needed from the loader is the
/// virtual-to-raw mapping. Absent tables are not an error: a build stripped of them simply answers nothing
/// and the structural recognition carries on alone.
/// </para>
/// </remarks>
public static class ExceptionTable
{
    /// <summary>One row of an LSDA call-site table.</summary>
    /// <param name="Start">First address the clause protects.</param>
    /// <param name="End">One past the last address it protects.</param>
    /// <param name="Pad">Where the unwinder resumes, or zero where the range has no handler.</param>
    /// <param name="Action">Zero for a cleanup, non-zero for a clause that tests the exception's type.</param>
    public readonly record struct CallSite(ulong Start, ulong End, ulong Pad, int Action);

    private const byte Omit = 0xFF;

    private sealed class Tables
    {
        public ulong[] FunctionStarts = [];
        public ulong[] FrameDescriptors = [];
        public ulong HeaderAddress;
        public ulong ExceptTableAddress;
        public bool Usable;
    }

    private static readonly Dictionary<ApplicationAnalysisContext, Tables> Cache = new();

    /// <summary>Every call-site row for the function starting at this address. Empty where there is none.</summary>
    public static List<CallSite> For(ApplicationAnalysisContext app, ulong function)
    {
        var tables = TablesFor(app);

        if (!tables.Usable || function == 0)
            return [];

        try
        {
            return Read(app, tables, function);
        }
        catch
        {
            //A malformed or unexpected encoding answers nothing rather than taking the method down with it.
            return [];
        }
    }

    private static Tables TablesFor(ApplicationAnalysisContext app)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(app, out var found))
                return found;

            var tables = new Tables();
            Cache[app] = tables;

            try
            {
                Build(app, tables);
            }
            catch
            {
                tables.Usable = false;
            }

            return tables;
        }
    }

    private static void Build(ApplicationAnalysisContext app, Tables tables)
    {
        var file = app.Binary.GetRawBinaryContent();

        if (file.Length < 64 || file[0] != 0x7F || file[1] != 'E' || file[2] != 'L' || file[3] != 'F' || file[4] != 2)
            return;

        if (!Sections(file, out var header, out var exceptTable))
            return;

        tables.HeaderAddress = header;
        tables.ExceptTableAddress = exceptTable;

        var reader = new Reader(app, file, header, header);
        if (reader.U8() != 1)
            return;

        var framePointerEncoding = reader.U8();
        var countEncoding = reader.U8();
        var tableEncoding = reader.U8();

        reader.Encoded(framePointerEncoding, 0);
        var count = reader.Encoded(countEncoding, 0);

        if (count is null or 0 or > 4_000_000)
            return;

        var starts = new ulong[count.Value];
        var descriptors = new ulong[count.Value];

        for (ulong i = 0; i < count.Value; i++)
        {
            starts[i] = reader.Encoded(tableEncoding, 0) ?? 0;
            descriptors[i] = reader.Encoded(tableEncoding, 0) ?? 0;
        }

        tables.FunctionStarts = starts;
        tables.FrameDescriptors = descriptors;
        tables.Usable = true;
    }

    /// <summary>The virtual addresses of the two sections this needs, straight out of the section headers.</summary>
    private static bool Sections(ReadOnlySpan<byte> file, out ulong header, out ulong exceptTable)
    {
        header = exceptTable = 0;

        var sectionHeaderOffset = (long)BitConverter.ToUInt64(file.Slice(0x28, 8));
        var entrySize = BitConverter.ToUInt16(file.Slice(0x3A, 2));
        var entryCount = BitConverter.ToUInt16(file.Slice(0x3C, 2));
        var nameTableIndex = BitConverter.ToUInt16(file.Slice(0x3E, 2));

        if (sectionHeaderOffset <= 0 || entryCount == 0 || nameTableIndex >= entryCount)
            return false;

        if (sectionHeaderOffset + (long)entrySize * entryCount > file.Length)
            return false;

        var names = (long)BitConverter.ToUInt64(file.Slice((int)(sectionHeaderOffset + (long)entrySize * nameTableIndex) + 0x18, 8));

        for (var i = 0; i < entryCount; i++)
        {
            var at = (int)(sectionHeaderOffset + (long)entrySize * i);
            var name = BitConverter.ToUInt32(file.Slice(at, 4));
            var address = BitConverter.ToUInt64(file.Slice(at + 0x10, 8));

            var start = (int)(names + name);
            var end = start;
            while (end < file.Length && file[end] != 0)
                end++;

            var text = System.Text.Encoding.ASCII.GetString(file.Slice(start, end - start));

            if (text == ".eh_frame_hdr")
                header = address;
            else if (text == ".gcc_except_table")
                exceptTable = address;
        }

        return header != 0 && exceptTable != 0;
    }

    private static List<CallSite> Read(ApplicationAnalysisContext app, Tables tables, ulong function)
    {
        var index = UpperBound(tables.FunctionStarts, function) - 1;

        if (index < 0)
            return [];

        var file = app.Binary.GetRawBinaryContent();
        var reader = new Reader(app, file, tables.FrameDescriptors[index], tables.HeaderAddress);

        var length = reader.U32();
        if (length == 0xFFFFFFFF)
            reader.U64();

        var atCieField = reader.Address;
        var cieOffset = reader.U32();

        //A zero here is a CIE, not a frame descriptor.
        if (cieOffset == 0)
            return [];

        var (lsdaEncoding, pcEncoding) = CommonInformation(app, file, tables, atCieField - cieOffset);

        var start = reader.Encoded(pcEncoding, 0) ?? 0;
        var range = reader.Encoded((byte)(pcEncoding & 0x0F), 0) ?? 0;

        //The table is sorted by function, so the row before the query is the only candidate - but it only
        //answers for the query if the query is actually inside it.
        if (start == 0 || function < start || function >= start + range)
            return [];

        reader.Uleb();                                  //augmentation data length

        if (lsdaEncoding == Omit)
            return [];

        var lsda = reader.Encoded(lsdaEncoding, 0);

        if (lsda is null or 0)
            return [];

        return CallSites(app, file, tables, lsda.Value, start);
    }

    private static (byte Lsda, byte Pc) CommonInformation(ApplicationAnalysisContext app, ReadOnlySpan<byte> file,
        Tables tables, ulong cie)
    {
        var reader = new Reader(app, file, cie, tables.HeaderAddress);

        var length = reader.U32();
        if (length == 0xFFFFFFFF)
            reader.U64();

        reader.U32();                                   //CIE id, zero
        reader.U8();                                    //version
        var augmentation = reader.String();
        reader.Uleb();                                  //code alignment
        reader.Sleb();                                  //data alignment
        reader.Uleb();                                  //return address register

        byte lsda = Omit, pc = 0;

        if (!augmentation.StartsWith('z'))
            return (lsda, pc);

        reader.Uleb();                                  //augmentation data length

        foreach (var letter in augmentation.AsSpan(1))
        {
            switch (letter)
            {
                case 'L':
                    lsda = reader.U8();
                    break;
                case 'R':
                    pc = reader.U8();
                    break;
                case 'P':
                    reader.Encoded(reader.U8(), 0);
                    break;
            }
        }

        return (lsda, pc);
    }

    private static List<CallSite> CallSites(ApplicationAnalysisContext app, ReadOnlySpan<byte> file, Tables tables,
        ulong lsda, ulong function)
    {
        var reader = new Reader(app, file, lsda, tables.ExceptTableAddress);

        var landingPadStartEncoding = reader.U8();
        var landingPadStart = landingPadStartEncoding == Omit ? function : reader.Encoded(landingPadStartEncoding, function) ?? function;

        var typeTableEncoding = reader.U8();
        if (typeTableEncoding != Omit)
            reader.Uleb();

        var callSiteEncoding = reader.U8();
        var tableLength = reader.Uleb();
        var end = reader.Address + tableLength;

        var found = new List<CallSite>();

        while (reader.Address < end && found.Count < 4096)
        {
            var start = reader.Encoded(callSiteEncoding, 0) ?? 0;
            var range = reader.Encoded(callSiteEncoding, 0) ?? 0;
            var pad = reader.Encoded(callSiteEncoding, 0) ?? 0;
            var action = reader.Uleb();

            found.Add(new CallSite(function + start, function + start + range,
                pad == 0 ? 0 : landingPadStart + pad, (int)action));
        }

        return found;
    }

    private static int UpperBound(ulong[] sorted, ulong value)
    {
        int low = 0, high = sorted.Length;

        while (low < high)
        {
            var middle = (low + high) / 2;

            if (sorted[middle] <= value)
                low = middle + 1;
            else
                high = middle;
        }

        return low;
    }

    /// <summary>A cursor over the file, addressed the way the tables are: by virtual address.</summary>
    private ref struct Reader(ApplicationAnalysisContext app, ReadOnlySpan<byte> file, ulong address, ulong sectionBase)
    {
        private readonly ApplicationAnalysisContext _app = app;
        private readonly ReadOnlySpan<byte> _file = file;
        private readonly ulong _sectionBase = sectionBase;

        public ulong Address = address;

        private ReadOnlySpan<byte> Take(int count)
        {
            if (!_app.Binary.TryMapVirtualAddressToRaw(Address, out var raw) || raw < 0 || raw + count > _file.Length)
                throw new InvalidOperationException($"no bytes at {Address:X}");

            Address += (ulong)count;
            return _file.Slice((int)raw, count);
        }

        public byte U8() => Take(1)[0];

        public uint U32() => BitConverter.ToUInt32(Take(4));

        public ulong U64() => BitConverter.ToUInt64(Take(8));

        public ulong Uleb()
        {
            ulong result = 0;
            var shift = 0;

            while (shift < 64)
            {
                var b = U8();
                result |= (ulong)(b & 0x7F) << shift;
                shift += 7;

                if ((b & 0x80) == 0)
                    break;
            }

            return result;
        }

        public long Sleb()
        {
            long result = 0;
            var shift = 0;
            byte b;

            do
            {
                b = U8();
                result |= (long)(b & 0x7F) << shift;
                shift += 7;
            } while ((b & 0x80) != 0 && shift < 64);

            if (shift < 64 && (b & 0x40) != 0)
                result -= 1L << shift;

            return result;
        }

        public string String()
        {
            var text = new System.Text.StringBuilder();

            while (true)
            {
                var c = U8();

                if (c == 0)
                    return text.ToString();

                text.Append((char)c);
            }
        }

        /// <summary>A pointer in one of DWARF's several spellings.</summary>
        public ulong? Encoded(byte encoding, ulong function)
        {
            if (encoding == Omit)
                return null;

            var here = Address;

            long value = (encoding & 0x0F) switch
            {
                0x00 => (long)U64(),
                0x01 => (long)Uleb(),
                0x02 => BitConverter.ToUInt16(Take(2)),
                0x03 => U32(),
                0x04 => (long)U64(),
                0x09 => Sleb(),
                0x0A => BitConverter.ToInt16(Take(2)),
                0x0B => BitConverter.ToInt32(Take(4)),
                0x0C => (long)U64(),
                _ => throw new InvalidOperationException($"encoding {encoding:X}"),
            };

            //A relative zero means "nothing here", not "the base" - most often a call site with no pad.
            if (value == 0 && (encoding & 0x70) is 0x10 or 0x30)
                return 0;

            return (encoding & 0x70) switch
            {
                0x10 => (ulong)(value + (long)here),
                0x30 => (ulong)(value + (long)_sectionBase),
                0x40 => (ulong)(value + (long)function),
                _ => (ulong)value,
            };
        }
    }
}
