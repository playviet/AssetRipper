#!/usr/bin/env python3
"""whyshape.py <why.tsv> <exportRoot> <code> - lost/kept split by the member the message names."""
import os, re, sys
from collections import defaultdict

why, root, code = sys.argv[1], sys.argv[2], sys.argv[3]

rows = set()
for line in open(why, encoding="utf-8", errors="replace"):
    p = line.rstrip("\n").split("\t")
    if len(p) >= 3:
        rows.add(tuple(p[:4]) if len(p) >= 4 else tuple(p[:3] + [""]))

byname = defaultdict(list)
for dirpath, _, names in os.walk(root):
    for n in names:
        if n.endswith(".cs"):
            byname[n].append(os.path.join(dirpath, n))

cache = {}
def commented(name, said):
    for p in byname.get(name, []):
        if p not in cache:
            cache[p] = [l.strip() for l in open(p, encoding="utf-8", errors="replace").read().splitlines()]
        for t in cache[p]:
            if t.startswith("//") and t.lstrip("/").strip() == said:
                return True
    return False

stat = defaultdict(lambda: [0, 0])
where = defaultdict(list)
for c, w, msg, said in sorted(rows):
    if c != code:
        continue
    said = said.strip()
    key = re.findall(r"'([^']*)'", msg)
    key = key[1] if len(key) > 1 else (key[0] if key else msg[:40])
    lost = commented(w.split(":")[0], said)
    s = stat[key]
    s[0] += 1
    s[1] += 1 if lost else 0
    if lost and len(where[key]) < 4:
        where[key].append((w, said[:100]))

print(f"{code}: {'member':45} {'sites':>6} {'lost':>6}")
for k, (n, lost) in sorted(stat.items(), key=lambda kv: -kv[1][1]):
    print(f"  {k:45} {n:6} {lost:6}")
print()
for k, items in sorted(where.items()):
    print(f"--- {k}")
    for w, s in items:
        print(f"  {w:34} {s}")
