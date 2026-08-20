#!/usr/bin/env python3
"""How big is the native-int typing defect, really?

    python3 nintcensus.py <export>                 # the census, by shape and by gamefilter tier
    python3 nintcensus.py <export> --shape compare # every line of one shape, with its file
    python3 nintcensus.py <export> --files         # the files carrying the most live occurrences

Three CS0019 compile errors in the round-5 Snacky Dash export are `int` or an enum compared against
`IntPtr`/`UIntPtr`. Three errors is what *survived* to the compiler; the same wrong type sits silently in
bodies where it happens to compile, and a wrong type that compiles is worse than one that does not
(`il2cpp-what-full-is-actually-worth`). This counts the whole family.

A pointer-sized native int is almost never something this game's source wrote: `Assembly-CSharp` gameplay
code has no `IntPtr` in it. So every occurrence in a recovered body is either a marker's collateral or a
width that got taken for a type - and the shape says which.

SHAPES, in the order they are tested (first match wins, so the specific ones come first):

    guard-zero    `if ((nint)0 == 0)`     the placeholder guard an unmanaged load/store leaves behind.
                                          Collateral of a marker, NOT a typing defect - chase the marker.
    addr-arith    `x + ((nint)8 << 3)`    address arithmetic written out; usually already commented.
    intptr-zero   `IntPtr.Zero`           a constant nought whose type came out pointer-sized. This is the
                                          shape that produced two of the three compile errors.
    unsafe-as     `Unsafe.As<T, UIntPtr>` ILSpy's rendering of a comparison whose IL stack type is native
                                          int and whose operands are not. The third compile error.
    compare       any relational/equality with a native-int operand
    decl-default  `IntPtr x = default(IntPtr);`
    decl-zero     `nint x = 0;`
    decl-copy     `nint x = y;`
    decl-bare     `nint x;`
    param/field/return  a native int in a declaration the type itself carries
    cast          any other `(nint)` / `(IntPtr)`
    other

Each is split `live` / `commented` (a line AssetRipper commented out, or one inside a `//` comment) and by
`gamefilter.tier` - gameplay, meta, and the 93% of the binary that is neither.
"""
import os
import re
import sys
from collections import Counter

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import gamefilter

TOKEN = re.compile(r'\b(?:System\.)?(?:U?IntPtr|nint|nuint)\b')

SHAPES = (
    ('guard-zero',   re.compile(r'\((?:nint|nuint|IntPtr|UIntPtr)\)\s*\d+\s*[!=]=')),
    ('addr-arith',   re.compile(r'[+\-]\s*\(*\((?:nint|nuint)\)')),
    ('intptr-zero',  re.compile(r'\b(?:U?IntPtr)\.Zero\b')),
    ('unsafe-as',    re.compile(r'Unsafe\.As<[^>]*U?IntPtr>')),
    ('compare',      re.compile(r'(?:[<>]=?|[!=]=)')),          # only reached with a token on the line
    ('decl-default', re.compile(r'\b(?:nint|nuint|U?IntPtr)\s+\w+\s*=\s*default\(')),
    ('decl-zero',    re.compile(r'\b(?:nint|nuint|U?IntPtr)\s+\w+\s*=\s*[-\d]')),
    ('decl-copy',    re.compile(r'\b(?:nint|nuint|U?IntPtr)\s+\w+\s*=\s*\w')),
    ('decl-bare',    re.compile(r'\b(?:nint|nuint|U?IntPtr)\s+\w+\s*;')),
    ('param',        re.compile(r'\b(?:nint|nuint|U?IntPtr)\s+\w+\s*[,)]')),
    ('cast',         re.compile(r'\(\s*(?:nint|nuint|U?IntPtr)\s*\)')),
)

# A declaration the *file* carries rather than a body: a field, a property, a method's return type. These
# are what the metadata says, not what analysis guessed, so they are not defects at all.
MODIFIER = r'(?:public|private|protected|internal|static|readonly|const|extern|unsafe|override|virtual|sealed|abstract)'
DECLARED = re.compile(r'^\s*(?:\[[^\]]*\]\s*)?' + MODIFIER + r'(?:\s+' + MODIFIER + r')*\s+'
                      r'[\w<>\[\], .]*\b(?:nint|nuint|U?IntPtr)\b')


def shape_of(line):
    stripped = line.strip()
    if DECLARED.match(line) and '(' not in stripped.split('=')[0]:
        return 'declared-member'
    for name, pattern in SHAPES:
        if pattern.search(stripped):
            return name
    return 'other'


def commented(line):
    return line.lstrip().startswith('//')


def walk(root):
    for base, _, names in os.walk(root):
        for name in names:
            if name.endswith('.cs'):
                yield os.path.join(base, name)


def census(export, want_shape=None, want_files=False):
    root = gamefilter.scripts_root(export)
    if not root:
        sys.exit(f'no Assets/Scripts under {export}')
    counts = Counter()          # (tier, shape, live|commented) -> n
    bodies = Counter()          # (tier, shape) -> files touched
    per_file = Counter()
    examples = {}
    seen = set()
    for path in walk(root):
        assembly, namespace, type_name = gamefilter.split_path(root, path)
        bucket = gamefilter.bucket(assembly, namespace, type_name)
        t = gamefilter.tier(assembly, namespace, type_name) or bucket
        try:
            lines = open(path, encoding='utf-8', errors='replace').read().splitlines()
        except OSError:
            continue
        for n, line in enumerate(lines, 1):
            hits = len(TOKEN.findall(line))
            if not hits:
                continue
            shape = shape_of(line)
            state = 'commented' if commented(line) else 'live'
            counts[(t, shape, state)] += hits
            if (t, shape) not in seen or path not in examples.get((t, shape), ()):
                pass
            key = (t, shape)
            if key not in seen:
                seen.add(key)
            bodies[(path, shape)] += 1
            if t in ('gameplay', 'meta') and state == 'live':
                examples.setdefault(shape, []).append((path, n, line.strip(), t))
            if state == 'live':
                per_file[path] += hits
            if want_shape and shape == want_shape and state == 'live':
                print(f'{os.path.relpath(path, root)}:{n}: {line.strip()[:160]}')
    if want_shape:
        return
    if want_files:
        for path, n in per_file.most_common(40):
            a, ns, tn = gamefilter.split_path(root, path)
            print(f'{n:6}  {gamefilter.tier(a, ns, tn) or gamefilter.bucket(a, ns, tn):9} '
                  f'{os.path.relpath(path, root)}')
        return
    report(counts, examples, root)


ORDER = ['gameplay', 'meta', 'sdk', 'unity', 'bcl']


def report(counts, examples, root):
    shapes = sorted({s for _, s, _ in counts},
                    key=lambda s: -sum(v for (t, sh, st), v in counts.items() if sh == s))
    tiers = [t for t in ORDER if any(t == tt for tt, _, _ in counts)]
    tiers += sorted({t for t, _, _ in counts} - set(tiers))
    width = max(len(s) for s in shapes) + 2
    head = 'shape'.ljust(width) + ''.join(t.rjust(11) for t in tiers) + 'total'.rjust(11)
    print(head)
    print('-' * len(head))
    for state in ('live', 'commented'):
        print(f'[{state}]')
        for s in shapes:
            row = [sum(v for (t, sh, st), v in counts.items() if sh == s and t == tt and st == state)
                   for tt in tiers]
            if not sum(row):
                continue
            print(s.ljust(width) + ''.join(str(v).rjust(11) for v in row) + str(sum(row)).rjust(11))
        row = [sum(v for (t, _, st), v in counts.items() if t == tt and st == state) for tt in tiers]
        print('TOTAL'.ljust(width) + ''.join(str(v).rjust(11) for v in row) + str(sum(row)).rjust(11))
        print()
    print('game (gameplay+meta) live occurrences by shape, with one example each:')
    for s in shapes:
        n = sum(v for (t, sh, st), v in counts.items()
                if sh == s and st == 'live' and t in ('gameplay', 'meta'))
        if not n:
            continue
        ex = next(iter(examples.get(s, ())), None)
        where = f'{ex[3]} {os.path.relpath(ex[0], root)}:{ex[1]}' if ex else ''
        print(f'  {s:14} {n:6}  {where}')
        if ex:
            print(f'                 {ex[2][:150]}')


def main():
    args = [a for a in sys.argv[1:] if not a.startswith('--')]
    flags = [a for a in sys.argv[1:] if a.startswith('--')]
    if not args:
        sys.exit(__doc__)
    shape = None
    for f in flags:
        if f.startswith('--shape='):
            shape = f.split('=', 1)[1]
    if '--shape' in flags:
        shape = sys.argv[sys.argv.index('--shape') + 1]
        args = [a for a in args if a != shape]
    census(args[0], shape, '--files' in flags)


if __name__ == '__main__':
    main()
