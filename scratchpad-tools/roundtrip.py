#!/usr/bin/env python3
"""Score a recovered export against the binary it came from, with no original source.

The ISIL Cpp2IL builds is read straight out of the native code, so what it says a method does is ground
truth that every game carries with it. `probe ... roundtrip` writes that out - the calls a method makes,
the fields it touches, the string literals it holds - and this reads the exported C# back and asks how
much of it survived.

Only live source counts. A statement AssetRipper commented out is still in the file, so every commented
line is dropped before matching: a call that ended up behind `//` was lost, which is exactly what this
is meant to notice.

  roundtrip.py <exportRoot> [dump.jsonl]

Prints recall per assembly and the worst files, and writes roundtrip.json.
"""
import collections
import json
import os
import re
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import csharp  # noqa: E402

EXPORT = (sys.argv[1] if len(sys.argv) > 1 else
          "/Users/playviet/Documents/_APK/FluffyField/Fluffy Field_1.0.2_607311538A_level3/ExportedProject")
DUMP = sys.argv[2] if len(sys.argv) > 2 else "/tmp/rt.jsonl"
SCRIPTS = os.path.join(EXPORT, "Assets", "Scripts")

# Members the language spells as something other than a call, or does not spell at all.
INVISIBLE = re.compile(r'^(\.ctor|\.cctor|op_[A-Za-z]+|Invoke|BeginInvoke|EndInvoke)$')


def outer(type_name):
    """The type a file is named after: the outermost one, without its arity."""
    return type_name.split('/')[0].split('+')[0].split('`')[0].split('.')[-1]


def index_files(live=True):
    """(assembly, type) -> {member: source}, with commented lines dropped where live."""
    found = {}

    for directory, _subdirectories, files in os.walk(SCRIPTS):
        for name in files:
            if not name.endswith('.cs'):
                continue

            path = os.path.join(directory, name)
            relative = os.path.relpath(path, SCRIPTS).split(os.sep)
            key = (relative[0], name[:-3])
            members = {}

            for member, texts in csharp.members(path).items():
                text = '\n'.join(texts)

                if live:
                    text = '\n'.join(line for line in text.splitlines() if not line.strip().startswith('//'))

                members[member] = text

            found.setdefault(key, {}).update(members)

    return found


def wanted(record):
    """Everything the binary says the method does, that C# has a way of saying."""
    names = {name for name in record['calls'] if not INVISIBLE.match(name)}
    names |= set(record['fields'])
    return sorted(names), sorted(set(record['literals']))


def kept(text, names, literals):
    """How many of them are still there."""
    words = set(re.findall(r'[A-Za-z_][A-Za-z0-9_]*', text))
    held = sum(1 for name in names if name in words)
    held += sum(1 for literal in literals if literal in text)
    return held


def main():
    files = index_files()
    raw = index_files(live=False)
    per_assembly = collections.defaultdict(lambda: collections.Counter())
    per_file = collections.defaultdict(lambda: collections.Counter())
    grades = collections.Counter()
    detail = {}
    located = 0

    for line in open(DUMP, errors='ignore'):
        record = json.loads(line)
        key = (record['assembly'], outer(record['type']))

        if key not in files or record['method'] not in files[key]:
            continue

        names, literals = wanted(record)
        expected = len(names) + len(literals)

        if expected == 0:
            continue

        located += 1
        held = kept(files[key][record['method']], names, literals)
        whole = kept(raw[key][record['method']], names, literals)

        per_assembly[key[0]]['expected'] += expected
        per_assembly[key[0]]['kept'] += held
        per_file[key]['expected'] += expected
        per_file[key]['kept'] += held

        if held >= 0.9 * expected:
            grades['whole'] += 1
        elif held == 0:
            grades['dead'] += 1
        else:
            grades['partial'] += 1

        detail[f"{key[0]}::{record['type']}::{record['method']}"] = [held, expected, whole]

    print('methods located in the export: %d' % located)
    for assembly, counts in sorted(per_assembly.items(), key=lambda pair: -pair[1]['expected']):
        print('  %-34s %6d of %6d  %5.1f%%' % (assembly, counts['kept'], counts['expected'],
                                               100 * counts['kept'] / max(counts['expected'], 1)))

    print()
    for grade in ('whole', 'partial', 'dead'):
        label = {'whole': 'whole (>=90%)', 'partial': 'partial', 'dead': 'dead (kept nothing)'}[grade]
        print('    %-22s %6d %6.1f%%' % (label, grades[grade], 100 * grades[grade] / max(located, 1)))

    json.dump(detail, open('roundtrip.json', 'w'), indent=0)


main()
