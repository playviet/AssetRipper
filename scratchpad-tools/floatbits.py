#!/usr/bin/env python3
"""floatbits.py <export>/Assets/Scripts/Assembly-CSharp - a float constant left typed as an integer.

    python3 floatbits.py <export>/Assets/Scripts/Assembly-CSharp

A body that is **whole and wrong** is the class no scorer here can see: it compiles, it carries no marker,
`cfscore` and `compare2` and `roundtrip` all call it recovered, and it computes the wrong number. The
execution oracle existed to catch those and it is gone (`il2cpp-the-execution-oracle-is-gone`). This catches
one shape of it, in a second, and belongs in every round until the oracle is back.

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


if __name__ == '__main__':
    main()
