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
import re
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from csharp import members, name_of
from markers import classify, has_body, statements

ORIGIN = '/Users/playviet/Documents/_BZ/game-hub/Assets/AAA/CF'
EXPORT = sys.argv[1] if len(sys.argv) > 1 else None


EDITOR_ONLY = re.compile(r'^\s*#(if|elif|else|endif)\b(.*)$')


def not_in_the_build(path):
    """The members this file declares inside `#if UNITY_EDITOR`, which the player build does not contain.

    Asking the recovery for them is asking for code that is not in the binary: five of the nine members
    counted missing were editor-only, and no amount of work on the recovery can produce one.
    """
    names = set()
    depth = []

    for line in open(path, encoding='utf-8', errors='replace'):
        directive = EDITOR_ONLY.match(line)

        if directive:
            kind, rest = directive.group(1), directive.group(2)
            if kind == 'if':
                depth.append('UNITY_EDITOR' in rest)
            elif kind in ('elif', 'else') and depth:
                depth[-1] = False
            elif kind == 'endif' and depth:
                depth.pop()
            continue

        if any(depth) and (found := name_of(line)):
            names.add(found)

    return names


EMPTY_IF = re.compile(r'^if\s*\(.*\)$')


def compiles_to_nothing(source):
    """Whether the original body has nothing in it for the compiler to emit.

    Either it is empty outright, or everything in it is an `if` with no body - `RepairMigrate` is
    `if (commonSaveData.saveVersion > 1) { }`, and a condition with nothing to do is removed along with the
    test, since reading a field has no effect the language has to keep. Asking recovery for either is asking
    for code the binary does not contain.
    """
    lines = statements(source)
    return not lines or all(EMPTY_IF.match(line) for line in lines)


def scorable(path, skip=()):
    """name -> source, for every member of this file that has a body."""
    return {name: texts[0] for name, texts in members(path).items()
            if name not in skip and any(has_body(t) for t in texts)}


DECLARED = re.compile(r'^\s*(?:\[[^\]]*\]\s*)*(?:public|internal|private|protected|abstract|sealed|static|partial|\s)*'
                      r'(?:class|struct|interface|enum|record)\s+(\w+)')


def types_declared(path):
    """Every type this source file declares, in order.

    AssetRipper writes **one file per type**, so a source file declaring two of them is recovered as two
    files - and looking only in the same-named one calls the second type's members missing. `CatData` sits in
    `CellData.cs` and comes back, correctly and completely, in `CatData.cs`.
    """
    found = []
    for line in open(path, encoding='utf-8', errors='replace'):
        match = DECLARED.match(line)
        if match:
            found.append(match.group(1))
    return found


def recovered_members(name, path, recovered):
    """What the export has for an original file, gathering the types it was split into."""
    got = scorable(recovered[name]) if name in recovered else {}

    for declared in types_declared(path):
        sibling = declared + '.cs'
        if sibling != name and sibling in recovered:
            for member, text in scorable(recovered[sibling]).items():
                got.setdefault(member, text)

    return got


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
        wanted = scorable(originals[name], not_in_the_build(originals[name]))

        got = recovered_members(name, originals[name], recovered)

        if not got:
            rows.append((len(wanted), 0, 0, len(wanted), name))
            totals['missing'] += len(wanted)
            continue
        counts = collections.Counter()

        for method in wanted:
            if method not in got:
                counts['missing'] += 1
                continue
            kind, found = classify(got[method])

            # A method the source declares empty - `private void HandleCoinClick() { }`, of which this game
            # has several as button handlers - is recovered perfectly by coming back empty. Scoring that
            # `dead` demands a body the binary does not contain, which is the same mistake as asking for an
            # editor-only member.
            if kind == 'dead' and not found['failed'] and compiles_to_nothing(wanted[method]):
                kind = 'full'

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
