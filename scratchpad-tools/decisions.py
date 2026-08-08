#!/usr/bin/env python3
"""How much of the branching survived, method by method, over the 96 reference files.

    python3 decisions.py <export>/ExportedProject

**Rebuilt after the original was lost, and it measures a different thing from the original.** That one
counted decisions in the binary; its data source is gone with it. This counts them in the *source* - every
place the original C# can go two ways - and asks how many of those are still there in the recovered method.
The numbers are a new baseline and are not comparable to the ones in the notes before 1.0.453.

Why it earns its place beside the others: a method can compile whole and always take one path, and nothing
else here would notice. See `il2cpp-decisions-are-the-unmeasured-half`.
"""
import collections
import os
import re
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from csharp import members
from markers import body_of, has_body

ORIGIN = '/Users/playviet/Documents/_BZ/game-hub/Assets/AAA/CF'
EXPORT = sys.argv[1] if len(sys.argv) > 1 else None

# Every place control can go two ways. The short-circuit operators are decisions in their own right - the
# right side runs or it does not - and a `case` is one arm of a jump table.
BRANCHES = [
    re.compile(r'\b(?:if|while|for|foreach|switch)\s*\('),
    re.compile(r'\bcase\s'),
    re.compile(r'\?[^;:]*:'),
    re.compile(r'&&'),
    re.compile(r'\|\|'),
]


def decisions(text):
    live = '\n'.join(line for line in body_of(text).split('\n')
                     if not line.strip().startswith('//'))
    return sum(len(pattern.findall(live)) for pattern in BRANCHES)


def index(root):
    found = {}
    for directory, _subdirectories, files in os.walk(root):
        for name in files:
            if name.endswith('.cs'):
                found.setdefault(name, os.path.join(directory, name))
    return found


def scorable(path):
    return {name: texts[0] for name, texts in members(path).items() if any(has_body(t) for t in texts)}


def main():
    if not EXPORT:
        raise SystemExit('usage: decisions.py <export>/ExportedProject')

    originals = index(ORIGIN)
    recovered = index(EXPORT)

    asked = survived = 0
    whole = branching = 0
    lost = []

    for name in sorted(originals):
        if name not in recovered:
            continue
        wanted = scorable(originals[name])
        got = scorable(recovered[name])

        for method, text in wanted.items():
            expected = decisions(text)
            if expected == 0:
                continue

            branching += 1
            asked += expected
            kept = min(expected, decisions(got[method])) if method in got else 0
            survived += kept

            if kept >= expected:
                whole += 1
            else:
                lost.append((expected - kept, f'{name}::{method}', kept, expected))

    lost.sort(reverse=True)
    for missing, where, kept, expected in lost[:15]:
        print('  %-58s %3d of %3d' % (where[:58], kept, expected))

    print()
    print('  keeping all of them : %5d  (%.1f%%)' % (whole, 100 * whole / max(branching, 1)))
    print('  decisions           : %d asked, %d survived  (%.1f%%)'
          % (asked, survived, 100 * survived / max(asked, 1)))


main()
