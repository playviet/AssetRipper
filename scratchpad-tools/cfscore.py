#!/usr/bin/env python3
"""Score every recovered file against its original, method by method - the 96 reference files.

    python3 cfscore.py <export>/ExportedProject

**Rebuilt after the original was lost, and its numbers are a new baseline rather than a continuation.**
The original scored 423 members where this scores 443; the filter it used to drop the other twenty could
not be recovered from what survived, and inventing one to make a total match would be fitting the tool to
a remembered number. Everything from 1.0.453 onwards is measured with this; anything earlier in the notes
is on the old scale and the two are not comparable. See `il2cpp-the-scorers-were-lost-and-rebuilt`.

What it counts is a member of an original file that has a body at all - a bodiless declaration, an
interface method or an auto-property accessor has nothing to recover - and how the recovered member of the
same name came out. Splitting is `csharp.members`, which asks ast-grep rather than matching text.
"""
import collections
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from csharp import members
from markers import classify, has_body

ORIGIN = '/Users/playviet/Documents/_BZ/game-hub/Assets/AAA/CF'
EXPORT = sys.argv[1] if len(sys.argv) > 1 else None


def scorable(path):
    """name -> source, for every member of this file that has a body."""
    return {name: texts[0] for name, texts in members(path).items() if any(has_body(t) for t in texts)}


def index(root):
    found = {}
    for directory, _subdirectories, files in os.walk(root):
        for name in files:
            if name.endswith('.cs'):
                found.setdefault(name, os.path.join(directory, name))
    return found


def main():
    if not EXPORT:
        raise SystemExit('usage: cfscore.py <export>/ExportedProject')

    originals = index(ORIGIN)
    recovered = index(EXPORT)

    rows = []
    totals = collections.Counter()
    markers_total = collections.Counter()

    for name in sorted(originals):
        wanted = scorable(originals[name])

        if name not in recovered:
            rows.append((len(wanted), 0, 0, len(wanted), name))
            totals['missing'] += len(wanted)
            continue

        got = scorable(recovered[name])
        counts = collections.Counter()

        for method in wanted:
            if method not in got:
                counts['missing'] += 1
                continue
            kind, found = classify(got[method])
            counts[kind] += 1
            markers_total.update(found)

        totals.update(counts)
        left = counts['partial'] + counts['dead'] + counts['missing']
        rows.append((left, counts['full'], counts['partial'] + counts['dead'], counts['missing'], name))

    rows.sort(key=lambda row: (-row[0], row[4]))

    print('%-42s %5s %5s %5s %5s' % ('file', 'left', 'full', 'part', 'miss'))
    for left, full, part, miss, name in rows:
        if left == 0 and full == 0:
            continue
        print('%-42s %5d %5d %5d %5d' % (name[:42], left, full, part, miss))

    print()
    print('totals: ' + ', '.join(f'{k}={v}' for k, v in sorted(totals.items())))
    print('markers: ' + ', '.join(f'{k}={v}' for k, v in markers_total.most_common() if v))
    print('files with nothing left: %d of %d' % (sum(1 for row in rows if row[0] == 0), len(rows)))


main()
