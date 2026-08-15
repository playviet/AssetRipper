#!/usr/bin/env python3
"""whyshapes.py <why.tsv> <exportRoot> <code> [--exclude=file:member,...]

The families inside one diagnostic code, counting only the sites that actually COST a statement.

`whycost.py` says which codes lose a statement; this says which SHAPES inside a code do. The message
is generalised - concrete type names kept, because the pair of types IS the family - and the file is
carried so a family can be told from one method's cascade.

Sites are deduplicated exactly as whycost does (the dump appends once per repair attempt), and a site
counts as lost only if the statement it points at is commented out in the finished export.
"""
import os, re, sys
from collections import Counter, defaultdict

why, root, code = sys.argv[1], sys.argv[2], sys.argv[3]
exclude = set()
for arg in sys.argv[4:]:
    if arg.startswith('--exclude='):
        exclude = {x.strip() for x in arg[len('--exclude='):].split(',') if x.strip()}

rows = set()
for line in open(why, encoding='utf-8', errors='replace'):
    parts = line.rstrip('\n').split('\t')
    if len(parts) >= 4 and parts[0] == code:
        rows.add((parts[1], parts[2], parts[3]))

# Every commented statement in the export, by file, verbatim behind the //.
commented = defaultdict(set)
for directory, _sub, files in os.walk(root):
    for name in files:
        if not name.endswith('.cs'):
            continue
        for line in open(os.path.join(directory, name), encoding='utf-8', errors='replace'):
            stripped = line.strip()
            if stripped.startswith('//'):
                commented[name].add(stripped.lstrip('/').strip())

shapes = Counter()
files = defaultdict(Counter)

for where, message, text in rows:
    name = where.split(':')[0]
    if name in exclude:
        continue
    if text.strip() not in commented.get(name, ()):
        continue
    shapes[message] += 1
    files[message][name] += 1

print('%s: %d sites that cost a statement, in %d shapes\n' % (code, sum(shapes.values()), len(shapes)))
for message, n in shapes.most_common(25):
    where = ', '.join('%s x%d' % (f, c) for f, c in files[message].most_common(4))
    print('%4d  %s\n      %s' % (n, message, where))
