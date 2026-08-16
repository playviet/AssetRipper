#!/usr/bin/env python3
"""Read the C++ exception tables out of an arm64 libil2cpp.so, and say what the `try` ranges are.

    lsda.py <libil2cpp.so> <hex function address> [...]
    lsda.py <libil2cpp.so> --census            how many functions have a table at all

The recovery has been guessing the `try` range from the shape of the graph, which can only see a landing pad
clang happened to lay directly after the throw. The range is *written down*: `.eh_frame_hdr` has a sorted
(function, FDE) table, the FDE names an LSDA in `.gcc_except_table`, and the LSDA's call-site table maps a
PC range to a landing pad. That range IS the try.

Python first, on purpose: this answers "does the data say what I think" for the price of a script, before a
line of it is written in C# where every iteration costs a build.
"""
import bisect
import struct
import sys

DW_EH_PE_omit = 0xFF


class Elf:
    def __init__(self, path):
        self.data = open(path, 'rb').read()
        assert self.data[:4] == b'\x7fELF' and self.data[4] == 2, "64-bit ELF only"
        e_shoff, = struct.unpack_from('<Q', self.data, 0x28)
        e_shentsize, e_shnum, e_shstrndx = struct.unpack_from('<HHH', self.data, 0x3A)

        raw = []
        for i in range(e_shnum):
            off = e_shoff + i * e_shentsize
            name, _, _, addr, offset, size = struct.unpack_from('<IIQQQQ', self.data, off)
            raw.append((name, addr, offset, size))

        strtab = raw[e_shstrndx][2]
        self.sections = {}
        for name, addr, offset, size in raw:
            end = self.data.index(b'\0', strtab + name)
            self.sections[self.data[strtab + name:end].decode()] = (addr, offset, size)

        self._map = sorted((addr, offset, size) for addr, offset, size in self.sections.values() if addr)
        self._keys = [a for a, _, _ in self._map]

    def at(self, vaddr):
        """The file offset holding a virtual address."""
        i = bisect.bisect_right(self._keys, vaddr) - 1
        if i >= 0:
            addr, offset, size = self._map[i]
            if vaddr < addr + size:
                return offset + (vaddr - addr)
        raise KeyError(hex(vaddr))


class Cursor:
    def __init__(self, elf, vaddr):
        self.elf, self.vaddr = elf, vaddr

    def bytes(self, n):
        off = self.elf.at(self.vaddr)
        self.vaddr += n
        return self.elf.data[off:off + n]

    def u8(self):
        return self.bytes(1)[0]

    def uleb(self):
        result, shift = 0, 0
        while True:
            b = self.u8()
            result |= (b & 0x7F) << shift
            shift += 7
            if not b & 0x80:
                return result

    def sleb(self):
        result, shift = 0, 0
        while True:
            b = self.u8()
            result |= (b & 0x7F) << shift
            shift += 7
            if not b & 0x80:
                if b & 0x40:
                    result -= 1 << shift
                return result

    def cstr(self):
        out = b''
        while True:
            c = self.bytes(1)
            if c == b'\0':
                return out.decode()
            out += c

    def encoded(self, enc, func=0):
        """A pointer in one of DWARF's dozen spellings. Answers None where the encoding says `omit`."""
        if enc == DW_EH_PE_omit:
            return None

        here = self.vaddr
        fmt = enc & 0x0F

        if fmt == 0x00:
            value = struct.unpack('<Q', self.bytes(8))[0]
        elif fmt == 0x01:
            value = self.uleb()
        elif fmt == 0x02:
            value = struct.unpack('<H', self.bytes(2))[0]
        elif fmt == 0x03:
            value = struct.unpack('<I', self.bytes(4))[0]
        elif fmt == 0x04:
            value = struct.unpack('<Q', self.bytes(8))[0]
        elif fmt == 0x09:
            value = self.sleb()
        elif fmt == 0x0A:
            value = struct.unpack('<h', self.bytes(2))[0]
        elif fmt == 0x0B:
            value = struct.unpack('<i', self.bytes(4))[0]
        elif fmt == 0x0C:
            value = struct.unpack('<q', self.bytes(8))[0]
        else:
            raise ValueError(f"encoding {enc:#x}")

        if value == 0 and (enc & 0x70) in (0x10, 0x30):
            return 0

        rel = enc & 0x70
        if rel == 0x10:      # pcrel
            value += here
        elif rel == 0x30:    # datarel - relative to the section the pointer lives in
            value += self.base
        elif rel == 0x40:    # funcrel
            value += func

        return value & 0xFFFFFFFFFFFFFFFF


def cie_of(elf, cie_vaddr):
    """The encodings an FDE that points here uses: (lsda encoding, pc encoding)."""
    c = Cursor(elf, cie_vaddr)
    length = struct.unpack('<I', c.bytes(4))[0]
    if length == 0xFFFFFFFF:
        length = struct.unpack('<Q', c.bytes(8))[0]
    c.bytes(4)                          # CIE id, zero
    version = c.u8()
    augmentation = c.cstr()
    c.uleb()                            # code alignment
    c.sleb()                            # data alignment
    if version >= 3:
        c.uleb()                        # return address register
    else:
        c.uleb()

    lsda_enc, pc_enc = DW_EH_PE_omit, 0x00

    if augmentation.startswith('z'):
        c.uleb()                        # augmentation data length
        for ch in augmentation[1:]:
            if ch == 'L':
                lsda_enc = c.u8()
            elif ch == 'R':
                pc_enc = c.u8()
            elif ch == 'P':
                enc = c.u8()
                c.base = 0
                c.encoded(enc)
            elif ch == 'S':
                pass

    return lsda_enc, pc_enc


class Frames:
    """`.eh_frame_hdr`'s sorted table: which FDE covers which function."""

    def __init__(self, elf):
        self.elf = elf
        hdr_addr, hdr_off, _ = elf.sections['.eh_frame_hdr']
        c = Cursor(elf, hdr_addr)
        c.base = hdr_addr
        version = c.u8()
        assert version == 1, version
        eh_frame_enc, count_enc, table_enc = c.u8(), c.u8(), c.u8()
        c.encoded(eh_frame_enc)
        count = c.encoded(count_enc)

        self.starts, self.fdes = [], []
        for _ in range(count):
            self.starts.append(c.encoded(table_enc))
            self.fdes.append(c.encoded(table_enc))

    def lsda_for(self, func):
        """The LSDA address of the function containing `func`, and the function's own start."""
        i = bisect.bisect_right(self.starts, func) - 1
        if i < 0:
            return None, None

        c = Cursor(self.elf, self.fdes[i])
        c.base = self.elf.sections['.eh_frame_hdr'][0]
        length = struct.unpack('<I', c.bytes(4))[0]
        if length == 0xFFFFFFFF:
            length = struct.unpack('<Q', c.bytes(8))[0]
        at_cie_field = c.vaddr
        cie_offset = struct.unpack('<I', c.bytes(4))[0]
        if cie_offset == 0:
            return None, None                      # this is a CIE, not an FDE

        lsda_enc, pc_enc = cie_of(self.elf, at_cie_field - cie_offset)

        start = c.encoded(pc_enc)
        c.encoded(pc_enc & 0x0F)                   # range, never relative
        aug_len = c.uleb()
        after = c.vaddr + aug_len

        if lsda_enc == DW_EH_PE_omit:
            return None, start

        lsda = c.encoded(lsda_enc)
        c.vaddr = after
        return (lsda or None), start


def call_sites(elf, lsda, func):
    """Every `(try start, try end, landing pad, action)` the table records, as addresses."""
    c = Cursor(elf, lsda)
    c.base = elf.sections['.gcc_except_table'][0]

    lp_enc = c.u8()
    lpstart = func if lp_enc == DW_EH_PE_omit else c.encoded(lp_enc, func)

    ttype_enc = c.u8()
    if ttype_enc != DW_EH_PE_omit:
        c.uleb()                                   # distance to the type table

    cs_enc = c.u8()
    cs_len = c.uleb()
    end = c.vaddr + cs_len

    out = []
    while c.vaddr < end:
        cs_start = c.encoded(cs_enc)
        cs_range = c.encoded(cs_enc)
        cs_lp = c.encoded(cs_enc)
        action = c.uleb()
        out.append((func + cs_start, func + cs_start + cs_range,
                    (lpstart + cs_lp) if cs_lp else 0, action))
    return out


def main():
    elf = Elf(sys.argv[1])
    frames = Frames(elf)

    if sys.argv[2] == '--census':
        with_lsda = catching = cleanup_only = 0
        catch_sites = 0
        for f in frames.starts:
            lsda, start = frames.lsda_for(f)
            if not lsda or not start:
                continue
            with_lsda += 1
            try:
                sites = call_sites(elf, lsda, start)
            except Exception:
                continue
            catches = [s for s in sites if s[2] and s[3]]
            if catches:
                catching += 1
                catch_sites += len(catches)
            elif any(s[2] for s in sites):
                cleanup_only += 1
        print(f"functions with an FDE                     {len(frames.starts)}")
        print(f"  of those, with an LSDA                  {with_lsda}")
        print(f"    with at least one CATCH call site     {catching}")
        print(f"    catch call sites in total             {catch_sites}")
        print(f"    cleanup/finally pads only             {cleanup_only}")
        return

    for arg in sys.argv[2:]:
        func = int(arg, 16)
        lsda, start = frames.lsda_for(func)
        print(f"=== {func:X}  (function starts {start:X})" if start else f"=== {func:X}  no FDE")
        if not lsda:
            print("    no LSDA - this function has no exception table")
            continue
        print(f"    LSDA @ {lsda:X}")
        for cs_start, cs_end, cs_lp, action in call_sites(elf, lsda, start):
            kind = "cleanup" if action == 0 and cs_lp else ("catch" if cs_lp else "no handler")
            print(f"    try [{cs_start:X}, {cs_end:X})  ->  pad {cs_lp:X}  action {action}   {kind}")


main()
