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
ACCESSOR = re.compile(r'^(get|set|add|remove)_(.+)$')
BACKING = re.compile(r'^<(.+)>k__BackingField$')

PLACEHOLDER = re.compile(r'\{\d+(?::[^}]*)?\}')

# A declaration in decompiled output ends its signature on one line and opens the body on the next.
DECLARATION = re.compile(r'([A-Za-z_]\w*)\s*(?:<[^()]*>)?\s*\([^;]*\)\s*$')
# A property has no parentheses at all; its accessors live in the block that follows.
PROPERTY = re.compile(r'([A-Za-z_]\w*)\s*$')
NOT_A_MEMBER = re.compile(r'\b(class|struct|interface|enum|namespace|else|try|finally|do|unsafe|fixed|lock|switch)\b')
LAMBDA = re.compile(r'^<(.+)>[bg]__')
# An iterator or async method compiles to a class holding its body. Recovered well enough, the decompiler
# folds the class back into the method it came from - so its members are read there, under that name.
STATE_MACHINE = re.compile(r'^<(.+)>d__\d+$')


def wanted(name):
    """The identifier a member appears under in C#, or None where it appears as no identifier at all."""
    if INVISIBLE.match(name):
        return None
    accessor = ACCESSOR.match(name)
    if accessor:
        return accessor.group(2)
    backing = BACKING.match(name)
    if backing:
        return backing.group(1)
    if name.startswith('<') or '$' in name:
        return None
    return name


def live_source(text):
    """The file with every commented-out line and every recovery marker taken out."""
    kept = []
    for line in text.split('\n'):
        stripped = line.lstrip()
        if stripped.startswith('//') or stripped.startswith('*') or stripped.startswith('/*'):
            continue
        # The markers are string literals in real statements, so they have to go by content.
        if 'Unmanaged memory load' in line or 'Method not found @' in line \
                or 'Not implemented instruction' in line or 'Indirect call:' in line \
                or 'Invalid instruction:' in line or 'Unknown call target' in line:
            continue
        kept.append(line)
    return '\n'.join(kept)


def bodies(text):
    """method name -> the live source of every body declared under that name in this file.

    Matching against a whole file counts a call as kept when any other method in the type happens to make
    it, which is generous enough to hide exactly what this is looking for. The decompiler writes a
    signature on one line and opens the body on the next, so the bodies can be cut apart by that.
    """
    lines = text.split('\n')
    found = collections.defaultdict(list)

    for i, line in enumerate(lines):
        bare = line.rstrip()

        if i + 1 >= len(lines) or lines[i + 1].strip() != '{':
            continue

        match = DECLARATION.search(bare)

        # No parentheses means a property, whose accessors are the block that follows - but so does a
        # type, and a block opened by a keyword, and neither of those is a member of anything.
        if not match and not NOT_A_MEMBER.search(bare):
            match = PROPERTY.search(bare)

        if not match:
            continue

        depth, j, body = 0, i + 1, []
        while j < len(lines):
            depth += lines[j].count('{') - lines[j].count('}')
            body.append(lines[j])
            if depth <= 0:
                break
            j += 1

        found[match.group(1)].append('\n'.join(body))

    return {name: '\n'.join(parts) for name, parts in found.items()}


def index_files(live=True):
    """(assembly, simple type name) -> the file's path and its bodies, by method name."""
    files = {}
    for root, _, names in os.walk(SCRIPTS):
        for name in names:
            if not name.endswith('.cs'):
                continue
            relative = os.path.relpath(root, SCRIPTS).split(os.sep)
            assembly = relative[0] if relative and relative[0] != '.' else ''
            path = os.path.join(root, name)

            # Parsing beats splitting by hand: an accessor, a constructor and a lifted lambda are all
            # declarations the text splitter had to be taught one at a time, and it still missed some.
            if csharp.available():
                found = {n: '\n'.join(live_source(b) if live else b for b in parts)
                         for n, parts in csharp.members(path).items()}
            else:
                with open(path, errors='ignore') as handle:
                    text = handle.read()
                found = bodies(live_source(text) if live else text)

            files.setdefault((assembly, name[:-3]), (path, found))
    return files


def mangled(name):
    """The name a compiler-generated identifier is written under in exported source."""
    return name.replace('<', '_003C').replace('>', '_003E')


def candidates(method, type_name):
    """Every name the exported source might have written this method's body under."""
    names = [method, mangled(method)]

    # An explicit interface implementation keeps the interface in its name; the source does not.
    if '.' in method and not method.startswith('.'):
        names.append(method.rsplit('.', 1)[-1])

    # A constructor is declared under the name of the type it builds.
    if method in ('.ctor', '.cctor'):
        names.append(mangled(type_name.replace('/', '+').split('+')[-1].rsplit('.', 1)[-1]))

    accessor = ACCESSOR.match(method.rsplit('.', 1)[-1])
    if accessor:
        names.append(accessor.group(2))

    # A lambda is written where it was written: inside the method that declares it.
    lambda_of = LAMBDA.match(method)
    if lambda_of:
        names.append(lambda_of.group(1))
        names.append(mangled(lambda_of.group(1)))

    # A state machine's body belongs to the method that was compiled into it. Where the decompiler managed to
    # fold it back, that is where its work is written - and looking only under `MoveNext` scores a method that
    # came back *exactly right* as having kept nothing.
    machine = STATE_MACHINE.match(type_name.replace('/', '+').split('+')[-1])
    if machine:
        names.append(machine.group(1))
        names.append(mangled(machine.group(1)))

    return names


def outer(type_name):
    """A nested type is written into the file of the type that declares it."""
    simple = type_name.replace('/', '+').split('+')[0]
    return simple.rsplit('.', 1)[-1]


def grade(kept, total):
    """What became of a method, judged only by how much of what it does survived."""
    if kept == 0:
        return 'dead (kept nothing)'
    ratio = kept / total
    return ('whole (>=90%)' if ratio >= 0.9
            else 'partial (>=50%)' if ratio >= 0.5
            else 'poor (<50%)')


def main():
    files = index_files()
    raw = index_files(live=False)
    per_assembly = collections.defaultdict(lambda: collections.Counter())
    per_file = collections.defaultdict(lambda: collections.Counter())
    missing_types = collections.Counter()
    unlocated = collections.Counter()
    per_method = {}
    grades = collections.Counter()
    detail = {}

    for line in open(DUMP, errors='ignore'):
        record = json.loads(line)
        assembly, type_name = record['assembly'], record['type']
        key = (assembly, outer(type_name))
        found = files.get(key)

        if found is None:
            missing_types[assembly] += 1
            continue

        path, by_name = found
        text = ''
        declared = raw.get((assembly, outer(type_name)), (None, {}))[1]
        has_declaration = any(c in declared for c in candidates(record['method'], type_name))

        for candidate in candidates(record['method'], type_name):
            if candidate in by_name:
                text = by_name[candidate]
                break
        else:
            unlocated[assembly] += 1

        counts = per_assembly[assembly]
        file_counts = per_file[os.path.relpath(path, SCRIPTS)]

        before = (counts['call_have'] + counts['field_have'] + counts['literal_have'],
                  counts['call_want'] + counts['field_want'] + counts['literal_want'])

        for kind, names in (('call', record['calls']), ('field', record['fields'])):
            for name in names:
                target = wanted(name)
                if target is None:
                    continue
                counts[kind + '_want'] += 1
                file_counts[kind + '_want'] += 1
                if present(target, text, called=kind == 'call' and target == name):
                    counts[kind + '_have'] += 1
                    file_counts[kind + '_have'] += 1

        for literal in record['literals']:
            pieces = fragments(literal)
            if not pieces:
                continue
            counts['literal_want'] += 1
            file_counts['literal_want'] += 1
            if all(piece in text for piece in pieces):
                counts['literal_have'] += 1
                file_counts['literal_have'] += 1

        after = (counts['call_have'] + counts['field_have'] + counts['literal_have'],
                 counts['call_want'] + counts['field_want'] + counts['literal_want'])
        kept, asked = after[0] - before[0], after[1] - before[1]
        per_method[f"{os.path.basename(path)}::{record['method']}"] = [kept, asked]

        # A method too small to say anything about, or one this cannot find a body for, is not judged:
        # a verdict of "kept nothing" has to mean the export lost it, not that the reader missed it.
        if asked >= 3 and has_declaration:
            grades[grade(kept, asked)] += 1

    total = collections.Counter()
    for counts in per_assembly.values():
        total.update(counts)

    def rate(counts, kind):
        want = counts[kind + '_want']
        return (100.0 * counts[kind + '_have'] / want) if want else 0.0

    print(f"{'assembly':32} {'calls':>16} {'fields':>16} {'literals':>16}")
    for assembly, counts in sorted(per_assembly.items(), key=lambda kv: -kv[1]['call_want'])[:14]:
        print(f"{assembly:32} "
              f"{rate(counts,'call'):5.1f}% {counts['call_want']:9} "
              f"{rate(counts,'field'):5.1f}% {counts['field_want']:9} "
              f"{rate(counts,'literal'):5.1f}% {counts['literal_want']:9}")

    print(f"\n{'TOTAL':32} "
          f"{rate(total,'call'):5.1f}% {total['call_want']:9} "
          f"{rate(total,'field'):5.1f}% {total['field_want']:9} "
          f"{rate(total,'literal'):5.1f}% {total['literal_want']:9}")
    judged = sum(grades.values())
    if judged:
        print(f"\n{judged} methods the binary says do three or more things, judged by how much survived:")
        for verdict in ('whole (>=90%)', 'partial (>=50%)', 'poor (<50%)', 'dead (kept nothing)'):
            print(f"    {verdict:22} {grades[verdict]:6}  {grades[verdict] / judged * 100:5.1f}%")

    print(f"\nmethods with no body found in the export: {sum(unlocated.values())}"
          f" (counted as having kept nothing)")
    print(f"methods whose assembly was not exported as source: {sum(missing_types.values())}")
    for assembly, count in missing_types.most_common(4):
        print(f"    {assembly:32} {count}")

    for name, counts in per_file.items():
        detail[name] = {
            'call': [counts['call_have'], counts['call_want']],
            'field': [counts['field_have'], counts['field_want']],
            'literal': [counts['literal_have'], counts['literal_want']],
        }

    out = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'roundtrip.json')
    with open(out, 'w') as handle:
        json.dump({'files': detail, 'methods': per_method}, handle)
    print(f"per-file detail -> {out}")


def present(name, text, called):
    """Whether the body still names this member - and, for a real call, still calls it."""
    pattern = rf'\b{re.escape(name)}\s*[(<]' if called else rf'\b{re.escape(name)}\b'
    return re.search(pattern, text) is not None


def fragments(literal):
    """The parts of a literal that still appear verbatim once the decompiler has rewritten it.

    A format string is written back as interpolation, so `"got {0} ms"` becomes `$"got {x} ms"` and the
    literal as a whole is nowhere in the file - but every piece between the placeholders still is. Pieces
    too short to mean anything are dropped, and a literal left with none is not counted either way.
    """
    pieces = []
    for piece in PLACEHOLDER.split(literal):
        for line in piece.split('\n'):
            line = line.strip()
            if len(line) >= 3:
                pieces.append(line)
    return pieces


if __name__ == '__main__':
    main()
