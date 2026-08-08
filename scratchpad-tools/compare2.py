#!/usr/bin/env python3
"""Classify every recovered body in the game, not only the 96 that have an original beside them.

    python3 compare2.py <export>/ExportedProject

**Rebuilt after the original was lost; its numbers are a new baseline.** See the note in `cfscore.py`.

`cfscore` can only speak for the 96 files whose source this project has. Most of what a change touches is
outside them, and a change that helps there and hurts everywhere else looks like a win to `cfscore` alone.
This asks the same question of every method body the export contains.
"""
import collections
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from csharp import members
from markers import classify, has_body

EXPORT = sys.argv[1] if len(sys.argv) > 1 else None
ASSEMBLY = os.path.join('Assets', 'Scripts', 'Assembly-CSharp')


def main():
    if not EXPORT:
        raise SystemExit('usage: compare2.py <export>/ExportedProject')

    root = os.path.join(EXPORT, ASSEMBLY)
    totals = collections.Counter()
    markers_total = collections.Counter()

    for directory, _subdirectories, files in os.walk(root):
        for name in files:
            if not name.endswith('.cs'):
                continue
            for texts in members(os.path.join(directory, name)).values():
                for text in texts:
                    if not has_body(text):
                        continue
                    kind, found = classify(text)
                    totals[kind] += 1
                    markers_total.update(found)

    counted = sum(totals.values())
    print('classified method bodies                     : %d' % counted)
    for kind in ('full', 'partial', 'dead'):
        print('  %-10s : %6d  (%5.1f%%)' % (kind, totals[kind], 100 * totals[kind] / max(counted, 1)))
    print('markers: ' + ', '.join(f'{k}={v}' for k, v in markers_total.most_common() if v))


main()
