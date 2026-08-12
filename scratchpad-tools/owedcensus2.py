#!/usr/bin/env python3
"""The first commented statement of each owed body, generalised into a shape.

`commented` is the first thing to go wrong in 124 of the 243 owed bodies, and on its own that is not a
diagnosis - it is the decompiler saying it could not write a statement down, and the reason is in the
statement. So take the FIRST commented statement of each body (the later ones are collateral) and
generalise it: literals to N, locals to v, types kept, because the type and the shape are the family.

    owedcensus2.py <export>/ExportedProject [substring]
"""
import collections, os, re, sys

SP = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, SP)
from csharp import members
from markers import classify, has_body, statements, body_of
from inventory import origin_index, arity, namespace_of, TYPE, EMPTY_IF

MARK = '//AssetRipper: commented out'
IDENT = re.compile(r'\b(?:v|val_|num|obj|ptr|local|item|result|text|flag|array|list)\d+\b')
NUM = re.compile(r'\b(?<![\w.])\d+(?:[uUlLfFdDmM]|\.\d+f?)?\b')


def first_commented(text):
    """The statement itself, which is the line AFTER the marker - the marker line is boilerplate.

    The decompiler writes

        //AssetRipper: commented out, this could not be kept as code.
        //_ = null;

    and only the second line says anything. Reading the first was the same mistake as reading `commented`
    as a diagnosis in the first place.
    """
    body = body_of(text)
    at = body.find(MARK)
    if at < 0:
        return None
    end = body.find('\n', at)
    if end < 0:
        return ''
    nxt = body.find('\n', end + 1)
    line = body[end + 1:nxt if nxt > 0 else len(body)].strip()
    return line.lstrip('/').strip()


def shape(line):
    line = IDENT.sub('v', line)
    line = NUM.sub('N', line)
    line = re.sub(r'\s+', ' ', line)
    return line[:150]


def main():
    export = sys.argv[1]
    want = sys.argv[2] if len(sys.argv) > 2 else None
    root = os.path.join(export, 'Assets', 'Scripts', 'Assembly-CSharp')
    index = origin_index()
    tally, where = collections.Counter(), collections.defaultdict(list)

    for directory, _sub, files in os.walk(root):
        for name in sorted(files):
            if not name.endswith('.cs'):
                continue
            path = os.path.join(directory, name)
            text = open(path, encoding='utf-8', errors='replace').read()
            ns = namespace_of(text, path)
            candidates = []
            for t in set(TYPE.findall(text)) | {name[:-3]}:
                candidates += index.get((ns, t), [])
            originals = {}
            for p in dict.fromkeys(candidates):
                try:
                    for member, texts in members(p).items():
                        originals.setdefault(member, []).extend(texts)
                except Exception:
                    pass
            for member, texts in members(path).items():
                for t in texts:
                    if not has_body(t) or classify(t)[0] == 'full':
                        continue
                    best = None
                    for o in originals.get(member, []):
                        if not has_body(o):
                            continue
                        if arity(o) == arity(t):
                            best = o
                            break
                        best = best or o
                    if best is None:
                        continue
                    st = statements(best)
                    if not st or all(EMPTY_IF.match(s) for s in st):
                        continue
                    line = first_commented(t)
                    if line is None:
                        continue
                    s = shape(line)
                    tally[s] += 1
                    where[s].append('%s::%s' % (os.path.relpath(path, root)[:-3], member))

    print('%d owed bodies whose first commented statement was read\n' % sum(tally.values()))
    for s, n in tally.most_common(45):
        print('%4d  %s' % (n, s))
        if want and want in s:
            for site in where[s]:
                print('        %s' % site)


if __name__ == '__main__':
    main()
