#!/usr/bin/env python3
"""callcensus.py <export>/ExportedProject [--json out.json] [--top N] [--assembly NAME] [--so PATH]

Census of the two call-resolution markers, BY ADDRESS:

    "Method not found @<hex>"      -> notfound
    "Indirect call: <expr>"        -> indirect
    "Indirect jump: <expr>"        -> indirect

`notfound` is keyed on the address literal the marker carries.  `--so=<libil2cpp.so>` names every address from the ELF: the imported symbol behind a `.plt`
stub, an exported symbol, or where a one-instruction `b` thunk goes.  `indirect` carries no
address, so it is keyed on the *shape* of the expression it could not resolve, which is
what separates a virtual dispatch (a load out of a vtable off a receiver) from a register
whose value analysis simply failed to propagate.

Prints a histogram, most frequent first, plus the number of distinct bodies each key
touches.  A handful of addresses covers most of the sites; that handful is the answer.
"""
import sys, os, re, json, struct, collections

NOTFOUND = re.compile(r'Method not found @([0-9A-Fa-f]+)')
INDIRECT = re.compile(r'Indirect (call|jump): *(.*)')

# The marker text is the ISIL statement, e.g.
#   Indirect call: 185 IndirectCall v215 @ X9_v5, v217 @ X0_v52 (System.String), ...
# The FIRST operand is the branch target; everything after it is the argument run.
# So the bucket that matters is where the target lives: il2cpp emits a virtual call as
# `ldr x9,[recv,#vtable]; ... ; blr x9`, so a target in X9 is a dispatch, while a target
# that is a literal or a `methodof(...)` is a constant the analysis failed to propagate.
TARGET = re.compile(r'IndirectCall\s+([^,]+)')

def shape(expr):
    m = TARGET.search(expr)
    if not m:
        return 'unparsed'
    t = m.group(1).strip()
    reg = re.search(r'@\s*([A-Za-z_][A-Za-z_0-9]*?)(?:_v\d+)?\s*$', t.split('(')[0])
    if t.startswith('methodof(') or re.match(r'^-?\d+$', t) or t.startswith('0x'):
        return 'constant'
    if reg:
        r = reg.group(1)
        if re.match(r'^[XW]\d+$', r):
            return 'reg ' + r
        return 'slot ' + r
    return 'other'


# ---------------------------------------------------------------- naming from the ELF
class Elf:
    """Just enough of an arm64 ELF to say what an address is: which section, which
    imported symbol a .plt stub resolves to, and where a one-instruction `b` thunk goes."""

    def __init__(self, path):
        self.d = open(path, 'rb').read()
        d = self.d
        e_shoff, = struct.unpack_from('<Q', d, 0x28)
        e_shentsize, e_shnum, e_shstrndx = struct.unpack_from('<HHH', d, 0x3a)
        self.secs = []
        for i in range(e_shnum):
            o = e_shoff + i * e_shentsize
            nm, typ, flags, addr, off, size, link, info, align, entsize = struct.unpack_from('<IIQQQQIIQQ', d, o)
            self.secs.append([nm, typ, addr, off, size])
        sh = self.secs[e_shstrndx]
        def nameat(base):
            return d[base:d.index(b'\0', base)].decode()
        for s in self.secs:
            s[0] = nameat(sh[3] + s[0])
        self.by = {s[0]: s for s in self.secs}
        # dynsym / dynstr / rela.plt -> jump slot names
        self.imports = {}
        self.exports = {}
        try:
            ds, st = self.by['.dynsym'], self.by['.dynstr']
            syms = []
            for i in range(ds[4] // 24):
                o = ds[3] + i * 24
                nm, info, other, shndx, value, size = struct.unpack_from('<IBBHQQ', d, o)
                n = nameat(st[3] + nm)
                syms.append(n)
                if value:
                    self.exports.setdefault(value, n)
            slot = {}
            rp = self.by.get('.rela.plt')
            if rp:
                for i in range(rp[4] // 24):
                    o = rp[3] + i * 24
                    off, info, addend = struct.unpack_from('<QQq', d, o)
                    slot[off] = syms[info >> 32]
            plt = self.by.get('.plt')
            if plt:
                a = plt[2]
                while a < plt[2] + plt[4]:
                    n = self._stub(a, slot)
                    if n:
                        self.imports[a] = n
                    a += 16
        except (KeyError, IndexError, struct.error):
            pass

    def v2o(self, a):
        for nm, typ, addr, off, size in self.secs:
            if typ != 8 and addr and addr <= a < addr + size:
                return off + a - addr
        return None

    def word(self, a):
        o = self.v2o(a)
        return struct.unpack_from('<I', self.d, o)[0] if o is not None else None

    def section(self, a):
        for nm, typ, addr, off, size in self.secs:
            if addr and addr <= a < addr + size:
                return nm
        return '?'

    def _stub(self, a, slot):
        page = pagereg = None
        for i in range(4):
            w = self.word(a + 4 * i)
            if w is None:
                return None
            if (w & 0x9f000000) == 0x90000000:
                immlo = (w >> 29) & 3
                immhi = (w >> 5) & 0x7ffff
                imm = (immhi << 2) | immlo
                if imm & (1 << 20):
                    imm -= 1 << 21
                page = ((a + 4 * i) & ~0xfff) + (imm << 12)
                pagereg = w & 31
            elif page is not None and (w & 0xffc00000) == 0xf9400000 and ((w >> 5) & 31) == pagereg:
                return slot.get(page + ((w >> 10) & 0xfff) * 8)
        return None

    def describe(self, a):
        if a in self.imports:
            return f'.plt  {self.imports[a]}'
        parts = [self.section(a)]
        if a in self.exports:
            parts.append(self.exports[a])
        w = self.word(a)
        if w is not None and (w & 0xFC000000) == 0x14000000:
            imm = w & 0x03FFFFFF
            if imm & 0x02000000:
                imm -= 0x04000000
            t = a + imm * 4
            parts.append(f'thunk -> {t:X}')
            if t in self.exports:
                parts.append(self.exports[t])
        return '  '.join(parts)


def main():
    args = [a for a in sys.argv[1:] if not a.startswith('--')]
    opts = [a for a in sys.argv[1:] if a.startswith('--')]
    if not args:
        print(__doc__); return 2
    root = args[0]
    top = 40
    jsonout = None
    for i, o in enumerate(opts):
        if o.startswith('--top'):
            top = int(o.split('=')[1]) if '=' in o else 40
        if o.startswith('--json'):
            jsonout = o.split('=', 1)[1]
    so = None
    for o in opts:
        if o.startswith('--so='):
            so = Elf(o.split('=', 1)[1])
    only = None
    for o in opts:
        if o.startswith('--assembly='):
            only = o.split('=', 1)[1]

    scripts = os.path.join(root, 'Assets', 'Scripts')
    if not os.path.isdir(scripts):
        scripts = root

    nf = collections.Counter()
    nf_bodies = collections.defaultdict(set)
    ind = collections.Counter()
    ind_bodies = collections.defaultdict(set)
    ind_exprs = collections.defaultdict(collections.Counter)
    files_nf, files_ind = set(), set()
    nf_sites, ind_sites = 0, 0

    for dirpath, _dirs, names in os.walk(scripts):
        for name in names:
            if not name.endswith('.cs'):
                continue
            path = os.path.join(dirpath, name)
            rel = os.path.relpath(path, scripts)
            if only and not rel.startswith(only + os.sep):
                continue
            try:
                text = open(path, encoding='utf-8', errors='replace').read()
            except OSError:
                continue
            if 'Method not found @' in text:
                for m in NOTFOUND.finditer(text):
                    a = m.group(1).upper().lstrip('0') or '0'
                    nf[a] += 1
                    nf_bodies[a].add(rel)
                    nf_sites += 1
                files_nf.add(rel)
            if 'Indirect call:' in text or 'Indirect jump:' in text:
                for m in INDIRECT.finditer(text):
                    s = shape(m.group(2))
                    ind[s] += 1
                    ind_bodies[s].add(rel)
                    ind_exprs[s][m.group(2).strip()[:90]] += 1
                    ind_sites += 1
                files_ind.add(rel)

    print(f'root {scripts}')
    print(f'notfound  {nf_sites:6d} sites  {len(nf):5d} distinct addresses  {len(files_nf):5d} files')
    print(f'indirect  {ind_sites:6d} sites  {len(ind):5d} distinct shapes     {len(files_ind):5d} files')
    print()
    print(f'{"address":>10} {"sites":>7} {"files":>6}   cum%   what it is')
    run = 0
    for a, c in nf.most_common(top):
        run += c
        what = so.describe(int(a, 16)) if so else ''
        print(f'{a:>10} {c:7d} {len(nf_bodies[a]):6d}  {100.0*run/max(nf_sites,1):5.1f}%   {what}')
    print()
    print(f'{"shape":>10} {"sites":>7} {"files":>6}   example')
    for s, c in ind.most_common():
        ex = ind_exprs[s].most_common(1)[0][0] if ind_exprs[s] else ''
        print(f'{s:>10} {c:7d} {len(ind_bodies[s]):6d}   {ex}')

    if jsonout:
        json.dump({
            'root': scripts,
            'notfound': {'sites': nf_sites, 'addresses': nf.most_common(),
                         'files': {a: sorted(v) for a, v in nf_bodies.items()}},
            'indirect': {'sites': ind_sites, 'shapes': ind.most_common(),
                         'examples': {s: c.most_common(8) for s, c in ind_exprs.items()}},
        }, open(jsonout, 'w'), indent=1)
        print(f'\nwrote {jsonout}')
    return 0

if __name__ == '__main__':
    sys.exit(main())
