#!/usr/bin/env python3
"""Every exported .cs file, what it still owes, and whether the original says anything at all.

The point is a finite denominator. A body is only a defect if the original has statements for it, so the
walk is ordered by *recoverable* gap and not by marker count - `#if UNITY_EDITOR`, a `{ }` hook and a
third-party plugin with no source are all zero work and must not be in the queue.

Matching is by namespace + type + member + arity, never by short name: `Common.HomeMenu` and `CF.HomeMenu`
are different types, and matching loosely invented five defects that do not exist.

    inventory.py <export>/ExportedProject [out.md]
"""
import collections, os, pickle, re, sys

SP = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, SP)
from csharp import members, without_attributes
from markers import classify, has_body, statements

GAMEHUB = '/Users/playviet/Documents/_BZ/game-hub/Assets'
CACHE = os.path.join(SP, 'origin-index.pickle')
NS = re.compile(r'^\s*namespace\s+([A-Za-z_][\w.]*)', re.M)
TYPE = re.compile(r'\b(?:class|struct|interface|enum|record)\s+([A-Za-z_][A-Za-z0-9_]*)')
EMPTY_IF = re.compile(r'^if\s*\(.*\)$')


def arity(text):
    head = without_attributes(text).split('{')[0]
    arrow = head.find('=>')
    if arrow >= 0:
        head = head[:arrow]
    at = head.find('(')
    if at < 0:
        return None                      # a property
    depth, out = 0, []
    for ch in head[at:]:
        if ch in '([<':
            depth += 1
        elif ch in ')]>':
            depth -= 1
            if depth == 0:
                break
        out.append(ch)
    inner = ''.join(out[1:]).strip()
    if not inner:
        return 0
    depth, n = 0, 1
    for ch in inner:
        if ch in '([<':
            depth += 1
        elif ch in ')]>':
            depth -= 1
        elif ch == ',' and depth == 0:
            n += 1
    return n


def origin_index():
    if os.path.exists(CACHE):
        return pickle.load(open(CACHE, 'rb'))
    index = collections.defaultdict(list)
    for directory, _sub, files in os.walk(GAMEHUB):
        for name in files:
            if not name.endswith('.cs'):
                continue
            path = os.path.join(directory, name)
            src = open(path, encoding='utf-8', errors='replace').read()
            for s in (set(NS.findall(src)) or {''}):
                for t in set(TYPE.findall(src)) | {name[:-3]}:
                    index[(s, t)].append(path)
    pickle.dump(dict(index), open(CACHE, 'wb'))
    return dict(index)


def namespace_of(text, path):
    found = NS.findall(text)
    return found[0] if found else ''


def main():
    export = sys.argv[1]
    out = sys.argv[2] if len(sys.argv) > 2 else None
    root = os.path.join(export, 'Assets', 'Scripts', 'Assembly-CSharp')
    index = origin_index()

    rows = []
    for directory, _sub, files in os.walk(root):
        for name in sorted(files):
            if not name.endswith('.cs'):
                continue
            path = os.path.join(directory, name)
            text = open(path, encoding='utf-8', errors='replace').read()
            ns = namespace_of(text, path)
            types = set(TYPE.findall(text)) | {name[:-3]}
            candidates = []
            for t in types:
                candidates += index.get((ns, t), [])
            candidates = list(dict.fromkeys(candidates))

            originals = {}
            for p in candidates:
                try:
                    for member, texts in members(p).items():
                        originals.setdefault(member, []).extend(texts)
                except Exception:
                    pass

            whole = gap = notmine = empty = 0
            owed = []
            for member, texts in members(path).items():
                for t in texts:
                    if not has_body(t):
                        continue
                    kind, _found = classify(t)
                    if kind == 'full':
                        whole += 1
                        continue
                    want = arity(t)
                    best = None
                    for o in originals.get(member, []):
                        if not has_body(o):
                            continue
                        if arity(o) == want or best is None:
                            if arity(o) == want:
                                best = o
                                break
                            best = o
                    if best is None:
                        notmine += 1
                        continue
                    st = statements(best)
                    if not st or all(EMPTY_IF.match(s) for s in st):
                        empty += 1
                        continue
                    gap += 1
                    owed.append((member, kind, len(st)))

            rows.append(dict(path=os.path.relpath(path, root), whole=whole, gap=gap,
                             notmine=notmine, empty=empty, owed=owed,
                             origin=os.path.relpath(candidates[0], GAMEHUB) if candidates else None))

    rows.sort(key=lambda r: (-r['gap'], r['path']))
    total_gap = sum(r['gap'] for r in rows)
    total_whole = sum(r['whole'] for r in rows)
    with_gap = [r for r in rows if r['gap']]

    lines = []
    lines.append('# Every exported file, and what it still owes')
    lines.append('')
    lines.append('Generated by `scratchpad/inventory.py` from `%s`.' % os.path.basename(export.rstrip('/').rstrip('ExportedProject').rstrip('/')))
    lines.append('')
    lines.append('A body counts as **owed** only where the original has statements for it. A method that is '
                 '`{ }` in the original, one behind a `#if` this build does not set, and anything with no '
                 'original at all are all zero work - they are counted separately so the denominator is '
                 'finite and the walk terminates.')
    lines.append('')
    lines.append('| | |')
    lines.append('|---|---|')
    lines.append('| files | %d |' % len(rows))
    lines.append('| **files that owe something** | **%d** |' % len(with_gap))
    lines.append('| bodies whole | %d |' % total_whole)
    lines.append('| **bodies owed** | **%d** |' % total_gap)
    lines.append('| not whole, no original | %d |' % sum(r['notmine'] for r in rows))
    lines.append('| not whole, empty in original | %d |' % sum(r['empty'] for r in rows))
    lines.append('')
    lines.append('## The queue, worst first')
    lines.append('')
    lines.append('| # | file | owed | whole | members owed (kind, statements in the original) |')
    lines.append('|---|---|---|---|---|')
    for i, r in enumerate(with_gap, 1):
        owed = ', '.join('`%s` (%s, %d)' % (m, k, n) for m, k, n in sorted(r['owed'], key=lambda o: -o[2])[:6])
        if len(r['owed']) > 6:
            owed += ', +%d more' % (len(r['owed']) - 6)
        lines.append('| %d | `%s` | **%d** | %d | %s |' % (i, r['path'], r['gap'], r['whole'], owed))
    lines.append('')
    lines.append('## Files with nothing owed')
    lines.append('')
    lines.append('%d files. Either every body is whole, or what is not whole has no original with statements '
                 'behind it.' % (len(rows) - len(with_gap)))

    body = '\n'.join(lines) + '\n'
    if out:
        open(out, 'w').write(body)
        print('wrote %s: %d files, %d owe something, %d bodies owed' % (out, len(rows), len(with_gap), total_gap))
    else:
        print(body)


main()
