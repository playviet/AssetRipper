#!/usr/bin/env python3
"""What the 243 owed bodies are actually waiting on.

The inventory says WHICH bodies are owed. This says WHY, by taking the FIRST thing that goes wrong in each
one - the first marker in source order, or where there is none the fact that statements are simply absent -
because a body's later complaints are nearly always collateral from its first: a local that would not
declare is then unusable by every statement after it.

    owedcensus.py <export>/ExportedProject [substring-of-shape]
"""
import collections, os, re, sys

SP = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, SP)
from csharp import members
from markers import MARKERS, classify, has_body, statements, body_of
from inventory import origin_index, arity, namespace_of, NS, TYPE, EMPTY_IF, GAMEHUB


def first_reason(text):
    body = body_of(text)
    best, at = None, len(body) + 1
    for name, strings in MARKERS:
        for s in strings:
            found = body.find(s)
            if 0 <= found < at:
                best, at = name, found
    if best is None:
        return None, ''
    end = body.find('\n', at)
    return best, body[at:end if end > 0 else len(body)].strip()


def shape_of(name, line):
    """A coarse family, so the census ranks roots rather than texts."""
    if name in (None, 'commented'):
        return name or 'no marker at all'
    line = re.sub(r'0x[0-9A-Fa-f]{4,}', '<addr>', line)
    line = re.sub(r'\b\d+\b', 'N', line)
    line = re.sub(r'"\)\).*$', '"))', line)
    return '%s: %s' % (name, line[:120])


def main():
    export = sys.argv[1]
    want = sys.argv[2] if len(sys.argv) > 2 else None
    root = os.path.join(export, 'Assets', 'Scripts', 'Assembly-CSharp')
    index = origin_index()

    tally = collections.Counter()
    where = collections.defaultdict(list)
    owed_total = 0

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

            for member, texts in members(path).items():
                for t in texts:
                    if not has_body(t):
                        continue
                    kind, _found = classify(t)
                    if kind == 'full':
                        continue
                    want_arity = arity(t)
                    best = None
                    for o in originals.get(member, []):
                        if not has_body(o):
                            continue
                        if arity(o) == want_arity:
                            best = o
                            break
                        if best is None:
                            best = o
                    if best is None:
                        continue
                    st = statements(best)
                    if not st or all(EMPTY_IF.match(s) for s in st):
                        continue
                    owed_total += 1
                    reason, line = first_reason(t)
                    shape = shape_of(reason, line)
                    tally[shape] += 1
                    where[shape].append('%s::%s' % (os.path.relpath(path, root)[:-3], member))

    print('%d owed bodies\n' % owed_total)
    for shape, n in tally.most_common(30):
        print('%4d  %s' % (n, shape))
        if want and want in shape:
            for site in where[shape]:
                print('        %s' % site)
        else:
            print('        %s' % ', '.join(where[shape][:3]))


if __name__ == '__main__':
    main()
