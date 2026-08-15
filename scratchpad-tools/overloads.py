#!/usr/bin/env python3
"""overloads.py <export>/Assets/Scripts/Assembly-CSharp - is a same-arity overload in the binary at all?

`allscore.py` keys members by `name#arity`, which separates 226 of the 495 overload bodies in the
originals. The other 401 are same-name AND same-arity - `Log(string, object)` beside
`Log(string, Exception)` - and no amount of text matching separates them, because the scorer does not
read parameter types.

Pairing them by POSITION was tried and reverted: `missing` went 62 -> 149, a 55% miss rate. The
suspicion recorded then was that il2cpp's managed linker had STRIPPED the unused overloads, which
would put them out of scope like editor code rather than missing. This tool decides that, and it does
not need `probe`: **AssetRipper writes the export from the binary's own metadata**, so the export
declares exactly the methods the binary declares. If the source has 4 `Log` of arity 2 and the export
has 2, the other two are not in the build.

Reports, over the same in-scope population `allscore.py` uses:

    same-arity groups     how many (name, arity) groups have >1 body in the source
    source bodies         how many bodies those groups hold
    export declarations   how many the binary declares for the same keys
    stripped              source - export, where export is FEWER  <- out of scope
    present               groups where the export has >= the source  <- genuinely unscored
"""
import collections
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import allscore as A
from csharp import members
from markers import has_body


CONDITIONAL = __import__('re').compile(r'^\s*#\s*(if|ifdef|ifndef|elif|else|endif)\b(.*)$')


def branches(path):
    """text-of-declaration -> the conditional-compilation arm it sits in.

    A file that says `#if ABYSSCORE_TRACKING ... #else ... #endif` declares
    `TrackAdInterClose(string, string)` TWICE, and they are the same member: only one arm is ever
    compiled. Counting them as two overloads is what made this census report 44 stripped bodies in
    `TrackingManager.Abysscore.cs` on its first run. The arm is `(conditional id, which arm)` per
    enclosing directive, so two declarations are the same member when their arms diverge anywhere.
    """
    arms, stack, nth = collections.defaultdict(list), [], 0
    for line in open(path, encoding='utf-8', errors='replace'):
        directive = CONDITIONAL.match(line)
        if directive:
            kind = directive.group(1)
            if kind.startswith('if'):
                nth += 1
                stack.append([nth, 0])
            elif kind in ('elif', 'else') and stack:
                stack[-1][1] += 1
            elif kind == 'endif' and stack:
                stack.pop()
            continue
        # A list, not a value: the two arms of an `#if` usually declare byte-identical signature
        # lines, and keeping only the last would make them look like one arm - compatible with
        # itself, and counted twice again.
        arms[line.strip()].append(tuple(tuple(frame) for frame in stack))
    return arms


def compatible(one, other):
    """Can these two arms both be compiled into one build?"""
    for a, b in zip(one, other):
        if a[0] == b[0] and a[1] != b[1]:
            return False
    return True


unresolved = collections.Counter()


def arm_of(text, arms, used):
    """Which conditional arm this declaration sits in, by finding its own text in the file."""
    for line in text.strip().splitlines():
        line = line.strip()
        if not line or line.startswith(('[', '//', '/*', '*')):
            continue
        found = arms.get(line)
        if not found:
            continue
        for candidate in found:
            if (line, candidate) not in used:
                used.add((line, candidate))
                return candidate
        return found[-1]
    unresolved['declaration not located in file'] += 1
    return None


def keyed(path, skip=(), bodies_only=True):
    """(name, arity) -> how many declarations ONE build of this file contains."""
    arms, used = branches(path), set()
    counts = collections.Counter()
    for name, texts in members(path).items():
        if name in skip:
            continue
        kept = collections.defaultdict(list)
        for text in texts:
            if bodies_only and not has_body(text):
                continue
            key = A.key_of(name, text)
            where = arm_of(text, arms, used)
            if where is None:
                where = ()
            if all(compatible(where, seen) for seen in kept[key]):
                kept[key].append(where)
        for key, seen in kept.items():
            if seen:
                counts[key] = len(seen)
    return counts


def main():
    if len(sys.argv) < 2:
        raise SystemExit(__doc__)
    export_root = sys.argv[1].rstrip('/')
    roots = sys.argv[2:] or [os.path.join(A.AAA, name) for name in ('CF', 'CFramework', 'Common')]

    recovered = A.index_multi(export_root)
    asmdef_dirs = [d for root in roots for d in A.asmdefs(root)]

    originals = []
    for root in roots:
        for directory, _sub, files in os.walk(root):
            originals += [os.path.join(directory, n) for n in sorted(files) if n.endswith('.cs')]
    originals.sort()

    groups = source_bodies = export_decls = 0
    stripped = present = properties = 0
    per_file = collections.Counter()
    unpaired = 0
    examples = []

    for path in originals:
        basename = os.path.basename(path)
        buildable, _why = A.in_the_player_assembly(path, asmdef_dirs)
        if not buildable:
            continue

        want = keyed(path, A.not_in_the_build(path))
        # Only keys that carry an arity. A key without one is a property or a field, and a property
        # cannot be overloaded - two of them under one name is the parser seeing an accessor twice.
        overloaded = {key: n for key, n in want.items() if n > 1 and '#' in key}
        properties += sum(1 for key, n in want.items() if n > 1 and '#' not in key)
        if not overloaded:
            continue

        # Every export file this original's types could have landed in: the same-named one, plus one
        # per type the original declares. Sum them - the original counts nested types together too.
        namespace = A.namespace_of(path)
        files = []
        chosen, _ = A.pick(recovered.get(basename, []), namespace, export_root)
        if chosen:
            files.append(chosen)
        for declared in A.types_declared(path):
            sibling = declared + '.cs'
            if sibling == basename:
                continue
            other, _ = A.pick(recovered.get(sibling, []), namespace, export_root)
            if other and other not in files:
                files.append(other)
        if not files:
            unpaired += len(overloaded)
            continue

        got = collections.Counter()
        for where in files:
            got.update(keyed(where, bodies_only=False))

        for key, n in sorted(overloaded.items()):
            groups += 1
            source_bodies += n
            have = got.get(key, 0)
            export_decls += have
            if have < n:
                stripped += n - have
                per_file[basename] += n - have
                if len(examples) < 12:
                    examples.append((basename, key, n, have))
            else:
                present += 1

    print(f'same-arity groups   : {groups}')
    print(f'properties skipped  : {properties} (a property is never an overload)')
    print(f'source bodies       : {source_bodies}')
    print(f'export declarations : {export_decls}')
    print(f'stripped (not built): {stripped}')
    print(f'groups fully present: {present} of {groups}')
    if unpaired:
        print(f'unpaired originals  : {unpaired} groups in files with no export at all')
    print()
    print('worst files by stripped bodies:')
    for name, n in per_file.most_common(12):
        print(f'  {n:4d}  {name}')
    print()
    if unresolved:
        print('instrument misses:', dict(unresolved))
    print()
    print('examples (file, name#arity, source, export):')
    for row in examples:
        print(f'  {row[0]:34s} {row[1]:28s} {row[2]} -> {row[3]}')


if __name__ == '__main__':
    main()
