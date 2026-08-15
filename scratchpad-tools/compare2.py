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
import re
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from csharp import members
from markers import classify, has_body

#Whether an exported file is original source AssetRipper copied in rather than decompiled output. An XML doc
#comment cannot survive IL and neither can a `#region`, so a file carrying either was never decompiled - the
#same test `allscore.py` uses, and the same one it validated: a default parameter value is NOT evidence,
#because it is a `Constant` in the metadata and comes back intact.
XMLDOC = re.compile(r'^\s*///')
REGION = re.compile(r'^\s*#(region|endregion)\b')


def substituted(path):
    lines = open(path, encoding='utf-8', errors='replace').read().split('\n')
    return any(XMLDOC.match(line) for line in lines) or any(REGION.match(line) for line in lines)

EXPORT = sys.argv[1] if len(sys.argv) > 1 else None
ASSEMBLY = os.path.join('Assets', 'Scripts', 'Assembly-CSharp')


def main():
    if not EXPORT:
        raise SystemExit('usage: compare2.py <export>/ExportedProject')

    root = os.path.join(EXPORT, ASSEMBLY)
    totals = collections.Counter()
    markers_total = collections.Counter()
    #The same counts over the decompiled files only. Kept beside the totals rather than replacing them:
    #every number this project has recorded since 1.0.607 is on the all-files scale, and a scorer that
    #silently changes scale makes a recovery change indistinguishable from a scorer change.
    recovered = collections.Counter()
    copied_files = copied_bodies = 0

    for directory, _subdirectories, files in os.walk(root):
        for name in files:
            if not name.endswith('.cs'):
                continue
            path = os.path.join(directory, name)
            copied = substituted(path)
            copied_files += copied
            for texts in members(path).values():
                for text in texts:
                    if not has_body(text):
                        continue
                    kind, found = classify(text)
                    totals[kind] += 1
                    markers_total.update(found)
                    if copied:
                        copied_bodies += 1
                    else:
                        recovered[kind] += 1

    counted = sum(totals.values())
    print('classified method bodies                     : %d' % counted)
    for kind in ('full', 'partial', 'dead'):
        print('  %-10s : %6d  (%5.1f%%)' % (kind, totals[kind], 100 * totals[kind] / max(counted, 1)))
    print('markers: ' + ', '.join(f'{k}={v}' for k, v in markers_total.most_common() if v))

    #And the honest half. `full` above counts bodies AssetRipper copied in from original source and never
    #decompiled at all, which score whole for free - about a fifth of the total.
    honest = sum(recovered.values())
    print()
    print('substituted original source (never decompiled): %d files, %d bodies' % (copied_files, copied_bodies))
    print('DECOMPILED ONLY, which is the honest number   : %d bodies' % honest)
    for kind in ('full', 'partial', 'dead'):
        print('  %-10s : %6d  (%5.1f%%)' % (kind, recovered[kind], 100 * recovered[kind] / max(honest, 1)))


main()
