#!/usr/bin/env python3
"""floatbits.py <export>/Assets/Scripts/Assembly-CSharp - two checks for a body that is whole and WRONG.

    python3 floatbits.py <export>/Assets/Scripts/Assembly-CSharp

A body that is **whole and wrong** is the class no scorer here can see: it compiles, it carries no marker,
`cfscore` and `compare2` and `roundtrip` all call it recovered, and it computes the wrong number. The
execution oracle catches them, at the cost of a Unity build and an export of the corpus
(`il2cpp-what-full-is-actually-worth`). These two checks catch two shapes of it in a second, directly in the
game's own export, where the oracle can only establish the RATE on 79 stand-in shapes.

The shape, from `ProceduralImage::EncodeFloats_0_1_16_16`:

    long num = 1199570688L;                     // 0x477FFF00 - the BITS of 65535.0f
    long num4 = (long)(num2 / num);             // a float divided by it, and the answer typed integer
    return num4 + num5;                         // from a method whose return type is `float`

A float constant is materialised into a general register (`MOV W8, #0x477FFF00`) and moved across with
`FMOV S1, W8`. That FMOV is lifted as a plain move, copy propagation folds it away - the same failure
`il2cpp-a-float-in-an-integer-register-is-its-bits` records for the OTHER direction - and the constant keeps
its integer type into the divide, poisoning every type downstream.

The test: an integer literal whose bit pattern decodes to a **clean** float. `1199570688` is not a number
anyone writes; `65535.0f` is. Ordinary integers do not decode to round floats, which is what makes this
cheap and specific.

**The second check: a body scored `full` whose whole content is a default and a return.**

    Pair result = default(Pair);
    return result;

No marker, no compile error, rated `full`, and the method does nothing. The corpus names three of these
among its nineteen whole-and-wrong shapes (`Scale`, `Cross`, `Blend`); in the game it is 24 bodies of 3252.

The two checks are **independent, and measured to be so**: of the empty bodies, **none** carries an ILSpy
`Unknown` note, and the 34 methods that do carry one contain none of them. Neither signal is a pre-filter
for the other, so run both - and neither is complete, because the only complete answer is the oracle.
"""
import collections
import os
import re
import struct
import sys

#Integer literals in the export, as C# writes them.
LITERAL = re.compile(r'(?<![\w.])(\d{6,})[Ll]?\b')


def as_float(n):
    """The float those 32 bits denote, or None where they are not a plausible float constant."""
    if not 0 <= n < 2 ** 32:
        return None
    value = struct.unpack('<f', struct.pack('<I', n))[0]
    if value != value or value in (float('inf'), float('-inf')):
        return None
    #A float somebody wrote. The bits of a real constant decode to something with few significant digits;
    #an ordinary integer decodes to noise like 3.7911267e-38. Both bounds matter: the magnitude rules out
    #denormals and absurdities, the short repr rules out noise that happens to be in range.
    if not (1e-6 < abs(value) < 1e9):
        return None
    return value if len(repr(round(value, 4)).rstrip('0').rstrip('.')) <= 8 else None


def main():
    if len(sys.argv) < 2:
        raise SystemExit(__doc__)
    root = sys.argv[1].rstrip('/')

    found = collections.Counter()
    where = collections.defaultdict(list)

    for directory, _sub, files in os.walk(root):
        for name in files:
            if not name.endswith('.cs'):
                continue
            path = os.path.join(directory, name)
            for number, line in enumerate(open(path, encoding='utf-8', errors='replace'), 1):
                stripped = line.strip()
                if stripped.startswith('//'):
                    continue
                for text in LITERAL.findall(line):
                    value = as_float(int(text))
                    if value is None:
                        continue
                    found[text] += 1
                    if len(where[text]) < 4:
                        where[text].append((name, number, stripped[:88]))

    print('integer literals whose bits decode to a clean float: %d distinct, %d sites'
          % (len(found), sum(found.values())))
    print()
    print('Each is a float constant the recovery left typed as an integer, and every type downstream of it')
    print('is wrong with it. No other scorer here can see this: the body compiles and carries no marker.')
    print()
    for text, n in found.most_common():
        print(f'  {text:>12}  =  {as_float(int(text))!r:<14} {n} site(s)')
        for name, number, line in where[text]:
            print(f'                 {name}:{number}  {line}')


def empty_full_bodies(root):
    """Bodies scored `full` whose whole content is a default and a return - they do nothing."""
    import importlib
    members = importlib.import_module('csharp').members
    markers = importlib.import_module('markers')

    declares = re.compile(r'^[\w<>,.\[\]? ]+\s+(\w+)\s*=\s*default\(')
    found, whole = [], 0
    for directory, _sub, files in os.walk(root):
        for name in files:
            if not name.endswith('.cs'):
                continue
            try:
                declared = members(os.path.join(directory, name))
            except Exception:
                continue
            for member, texts in declared.items():
                for text in texts:
                    if not markers.has_body(text) or markers.classify(text)[0] != 'full':
                        continue
                    whole += 1
                    lines = [s.strip() for s in markers.statements(text)]
                    if not lines or len(lines) > 3:
                        continue
                    local, saw = None, False
                    for line in lines:
                        match = declares.match(line)
                        if match:
                            local, saw = match.group(1), True
                        elif line.startswith('return default('):
                            saw = True
                        elif not (line == 'return;' or (local and line == f'return {local};')):
                            saw = False
                            break
                    if saw:
                        found.append((name, member))
    return whole, found


if __name__ == '__main__':
    main()
    print()
    ROOT = sys.argv[1].rstrip('/')
    total, empties = empty_full_bodies(ROOT)
    print('bodies scored `full` whose whole body is a default and a return: %d of %d' % (len(empties), total))
    print('They carry no marker and no compile error, and they do nothing.')
    seen = set()
    for name, member in sorted(empties):
        if (name, member) in seen:
            continue
        seen.add((name, member))
        print(f'  {name:34s} {member}')
