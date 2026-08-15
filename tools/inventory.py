#!/usr/bin/env python3
"""Every exported .cs file, what it still owes, and whether the original says anything at all.

The point is a finite denominator. A body is only a defect if the original has statements for it, so the
walk is ordered by *recoverable* gap and not by marker count - `#if UNITY_EDITOR`, a `{ }` hook and a
third-party plugin with no source are all zero work and must not be in the queue.

Matching is by namespace + type + member + arity, never by short name: `Common.HomeMenu` and `CF.HomeMenu`
are different types, and matching loosely invented five defects that do not exist.

    inventory.py <export>/ExportedProject [out.md]      # writes RECOVERY-FILES.md unless told otherwise
"""
import collections, os, pickle, re, sys

SP = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, SP)
from csharp import members, without_attributes
from markers import classify, has_body, statements

GAMEHUB = '/Users/playviet/Documents/_BZ/game-hub/Assets'
CACHE = os.path.join(SP, 'origin-index.pickle')
BARE = os.path.join(SP, 'bare-returns.tsv')
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


PARAMETER_KEYWORD = re.compile(r'\b(?:this|ref|out|in|params|readonly|scoped)\b\s+')
SAME_TYPE = {'int32': 'int', 'single': 'float', 'boolean': 'bool', 'int64': 'long', 'uint32': 'uint',
             'uint64': 'ulong', 'int16': 'short', 'uint16': 'ushort', 'sbyte': 'sbyte',
             'double': 'double', 'char': 'char', 'string': 'string', 'object': 'object'}


def parameter_types(text):
    """The parameter types of a declaration, normalised until the two sides agree on spelling.

    Inlined rather than imported from `allscore.py`, deliberately: everything this script needs sits beside
    it, and the one time that was not true - `bare-returns.tsv` read from a directory a copy had been run
    from - the queue was silently 17 too high and the difference was misdiagnosed as a lost tool. `allscore`
    carries the reference copy of this function; it is small and it does not change.

    Used ONLY to tell two overloads of one arity apart, never to decide whether a body is owed.
    """
    head = without_attributes(text).split('{')[0]
    arrow = head.find('=>')
    if arrow >= 0:
        head = head[:arrow]
    start = head.find('(')
    if start < 0:
        return ()
    depth, end = 0, None
    for i in range(start, len(head)):
        if head[i] == '(':
            depth += 1
        elif head[i] == ')':
            depth -= 1
            if depth == 0:
                end = i
                break
    if end is None:
        return ()
    inner = head[start + 1:end].strip()
    if not inner:
        return ()
    parts, angle, square, current = [], 0, 0, ''
    for ch in inner:
        if ch == '<':
            angle += 1
        elif ch == '>':
            angle -= 1
        elif ch == '[':
            square += 1
        elif ch == ']':
            square -= 1
        if ch == ',' and angle == 0 and square == 0:
            parts.append(current)
            current = ''
        else:
            current += ch
    parts.append(current)
    found = []
    for part in parts:
        part = PARAMETER_KEYWORD.sub('', part.split('=')[0].strip()).strip()
        split = part.rsplit(None, 1)
        name = (split[0] if len(split) == 2 else part).split('.')[-1]
        found.append(SAME_TYPE.get(re.sub(r'\s+', '', name).lower(), re.sub(r'\s+', '', name).lower()))
    return tuple(found)


def bare_returns():
    """Every method the binary compiles to a single RET, by type, member and parameter count.

    `void SetX(this Vector2 v, float x) => v.x = x;` writes to a copy of a by-value struct and the C#
    compiler discards it too, so the binary is one `RET` and an empty export is the *right* answer. Counting
    those as owed counts a defect that is not there - five of them in `VectorExtensions` alone. Regenerate
    the file with probe2's `bare` mode; the header of it says how.
    """
    #NOT a silent empty set. Returning one folds all 17 one-RET bodies back into `owed` and the queue reads
    #17 too high with nothing to say it is wrong - which is exactly what happened to a copy of this script
    #run from a directory the .tsv was not in, and the difference was then misdiagnosed as a lost better
    #version of the tool. A missing input is a broken run, so say so.
    found = set()
    if not os.path.exists(BARE):
        raise SystemExit(
            f'inventory.py: {BARE} is missing.\n'
            '  It is read from beside this script, so run tools/inventory.py in place rather than a copy.\n'
            '  Without it the 17 bodies the binary compiles to a single RET count as owed and the queue is\n'
            '  17 too high. Regenerate the file with probe2\'s `bare` mode; its header says how.')
    for line in open(BARE, encoding='utf8'):
        if line.startswith('#'):
            continue
        parts = line.rstrip('\n').split('\t')
        if len(parts) == 4 and parts[3].isdigit():
            found.add((parts[1], parts[2], int(parts[3])))
    return found


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
    #The queue is a file in the repository, not something to read off a terminal. Left optional it was
    #silently not written for six rounds while its numbers were being quoted from the stale copy on disk.
    out = sys.argv[2] if len(sys.argv) > 2 else os.path.join(os.path.dirname(SP), 'RECOVERY-FILES.md')
    root = os.path.join(export, 'Assets', 'Scripts', 'Assembly-CSharp')
    index = origin_index()
    bare = bare_returns()

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

            whole = gap = notmine = empty = nothing = 0
            fullnames = [(ns + '.' + t) if ns else t for t in types]
            owed = []
            for member, texts in members(path).items():
                for t in texts:
                    if not has_body(t):
                        continue
                    kind, _found = classify(t)
                    if kind == 'full':
                        whole += 1
                        continue
                    want, wanttypes = arity(t), parameter_types(t)
                    best = None
                    for o in originals.get(member, []):
                        if not has_body(o):
                            continue
                        if arity(o) == want:
                            #Two overloads of one arity both matched the FIRST original, so a body could be
                            #judged against the wrong one and owe the wrong number of statements - which is
                            #what orders this queue. The types separate them exactly; arity still decides,
                            #and the types only choose among what arity already accepted, so a spelling this
                            #misses falls back to exactly the old behaviour. AESUtils::EncryptAES owed 8 and
                            #owes 9, GoogleDesignConfigSo::GetDataById owed 1 and owes 4,
                            #JsonExtension::ToNewlineDelimitedJson owed 2 and owes 5.
                            if best is None or parameter_types(o) == wanttypes:
                                best = o
                            if parameter_types(o) == wanttypes:
                                break
                        elif best is None:
                            best = o
                    if best is None:
                        notmine += 1
                        continue
                    st = statements(best)
                    if not st or all(EMPTY_IF.match(s) for s in st):
                        empty += 1
                        continue
                    #The original says something; the binary does not. There is nothing here to recover.
                    if any((f, member, want) in bare for f in fullnames):
                        nothing += 1
                        continue
                    gap += 1
                    owed.append((member, kind, len(st)))

            rows.append(dict(path=os.path.relpath(path, root), whole=whole, gap=gap,
                             notmine=notmine, empty=empty, nothing=nothing, owed=owed,
                             origin=os.path.relpath(candidates[0], GAMEHUB) if candidates else None))

    rows.sort(key=lambda r: (-r['gap'], r['path']))
    total_gap = sum(r['gap'] for r in rows)
    total_whole = sum(r['whole'] for r in rows)
    with_gap = [r for r in rows if r['gap']]

    lines = []
    lines.append('# Every exported file, and what it still owes')
    lines.append('')
    lines.append('Generated by `tools/inventory.py` from `%s`. It reads `bare-returns.tsv` from beside itself;'
                 ' run it from `tools/`, or the 17 one-RET bodies count as owed.'
                 % os.path.basename(export.rstrip('/').rstrip('ExportedProject').rstrip('/')))
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
    lines.append('| not whole, one RET in the binary | %d |' % sum(r['nothing'] for r in rows))
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


if __name__ == '__main__':
    main()
