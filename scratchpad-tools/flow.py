#!/usr/bin/env python3
"""Follow a `= default(T)` local through copy chains to whatever really consumes it.

`object obj = default(object); List<X> value = (List<X>)obj; ... TryGetValue(.., out value)` is a dead
initialisation - the value never reaches a consumer. `int num = default(int); text = num.ToString()` is a
wrong answer. Only the second is a defect, and telling them apart needs the chain followed.
"""
import collections
import json
import os
import re
import sys

sys.path.insert(0, '/Users/playviet/Documents/_BZ/AssetRipper/scratchpad-tools')
from csharp import members            # noqa: E402
from markers import body_of, has_body  # noqa: E402

DECL = re.compile(r'^([\w<>,.\[\]?@ ]+?)\s+([\w@]+)\s*=\s*default\(([^;]*)\);\s*$')
# `T y = x;`  or  `T y = (T)x;`  or  `y = x;`  - a pure copy of one local into another
COPY = re.compile(r'^(?:[\w<>,.\[\]?@ ]+\s+)?([\w@]+)\s*=\s*(?:\(\s*[\w<>,.\[\]?@ ]+\s*\)\s*)?([\w@]+)\s*;$')


def analyse(body_lines):
    """yield (decl_line, var, type, verdict, consumer_line)"""
    lines = body_lines
    out = []
    for i, ln in enumerate(lines):
        m = DECL.match(ln)
        if not m:
            continue
        var, ty = m.group(2), m.group(3)
        seen = set()
        cur, at = var, i
        verdict, consumer = 'dead-init', ''
        while True:
            if (cur, at) in seen:
                break
            seen.add((cur, at))
            w = re.compile(r'(?<![\w.])' + re.escape(cur) + r'(?![\w])')
            asg = re.compile(r'(?<![\w.])' + re.escape(cur) + r'\s*(?:=(?!=)|\+\+|--|\+=|-=|\*=|/=)')
            oa = re.compile(r'\b(?:out|ref)\s+' + re.escape(cur) + r'\b')
            nxt = None
            for j in range(at + 1, len(lines)):
                s = lines[j]
                if s.startswith('//') or not w.search(s):
                    continue
                a = asg.match(s) or oa.search(s)
                if a:
                    rhs = s.split('=', 1)[1] if '=' in s and not oa.search(s) else ''
                    if not w.search(rhs):
                        break            # overwritten without being read - dead
                c = COPY.match(s)
                if c and c.group(2) == cur and c.group(1) != cur:
                    nxt = (c.group(1), j)   # a pure copy: follow it
                    break
                verdict, consumer = 'consumed', s
                break
            if nxt is None:
                break
            cur, at = nxt
        out.append((ln, var, ty, verdict, consumer))
    return out


def classify_use(var, u):
    v = re.escape(var)
    if re.search(r'(?<![\w.])' + v + r'\.\w+\s*\(', u):
        return 'receiver-of-instance-call'
    if re.search(r'\b(out|ref)\s+' + v + r'\b', u):
        return 'out/ref-arg'
    if re.match(r'^return\s', u):
        return 'returned'
    if re.search(r'\b(?:if|while|for)\s*\(', u):
        return 'in-a-condition'
    if re.search(r'(?<![\w.])' + v + r'\s*\.', u):
        return 'field-of'
    if re.search(r'\w\s*\(.*(?<![\w.])' + v + r'(?![\w])', u):
        return 'argument-to-call'
    if re.match(r'^[\w@][\w@.\[\]]*\s*=', u):
        return 'stored-into-field-or-element'
    return 'in-an-expression'


def run(root, label):
    v = collections.Counter()
    sh = collections.Counter()
    ty = collections.Counter()
    hit = set()
    ex = collections.defaultdict(list)
    for directory, _s, names in os.walk(root):
        for nm in names:
            if not nm.endswith('.cs'):
                continue
            path = os.path.join(directory, nm)
            for mname, texts in members(path).items():
                for text in texts:
                    if not has_body(text):
                        continue
                    lines = [l.strip() for l in body_of(text).split('\n')]
                    for decl, var, t, verdict, cons in analyse(lines):
                        v[verdict] += 1
                        if verdict == 'consumed':
                            s = classify_use(var, cons)
                            sh[s] += 1
                            ty[t] += 1
                            hit.add((path, mname))
                            if len(ex[s]) < 500:
                                ex[s].append({'path': path, 'member': mname, 'decl': decl,
                                              'use': cons, 'type': t})
    print('== %s' % label)
    print('   verdicts:', dict(v))
    print('   methods with a consumed default:', len(hit))
    for k, n in sh.most_common():
        print('   %5d  %s' % (n, k))
    print('   types:', ty.most_common(20))
    return {'shapes': dict(sh), 'examples': ex, 'types': ty.most_common(), 'methods': sorted(
        {(os.path.basename(p), m) for p, m in hit})}


if __name__ == '__main__':
    res = run(sys.argv[1], sys.argv[2] if len(sys.argv) > 2 else 'root')
    json.dump(res, open(sys.argv[3] if len(sys.argv) > 3 else 'flow.json', 'w'), indent=0)
