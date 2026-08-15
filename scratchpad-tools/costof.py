#!/usr/bin/env python3
"""costof.py <export>/…/Assembly-CSharp <names.txt> - what a named set of methods still costs.

A cause found in the binary names METHODS; every scorer here counts statements. This crosses the two, so
"27 methods take a composite the allocator mishandles" can be turned into "and they hold N commented
statements", which is the number that decides whether the cause is worth a round.

One method name per line in <names.txt>; overloads collapse, which is right here because the cause is per
signature and the count wanted is per body.
"""
import os, sys

sys.path.insert(0, os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), 'tools'))
from csharp import members
from markers import classify, has_body

root, names = sys.argv[1], sys.argv[2]
want = {line.strip() for line in open(names) if line.strip()}

total = 0
hits = []

for directory, _sub, files in os.walk(root):
    for name in sorted(files):
        if not name.endswith('.cs'):
            continue
        for member, texts in members(os.path.join(directory, name)).items():
            if member not in want:
                continue
            for text in (texts if isinstance(texts, list) else [texts]):
                if not has_body(text):
                    continue
                _kind, found = classify(text)
                if found['commented']:
                    hits.append((found['commented'], name, member))
                    total += found['commented']

for commented, name, member in sorted(hits, reverse=True):
    print('%4d  %-34s %s' % (commented, name, member))

print('\n%d commented statements across %d of the named methods' % (total, len(hits)))
