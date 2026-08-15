#!/usr/bin/env python3
"""whycost.py <why.tsv> <exportRoot> [code ...]

The REPAIR_WHY dump says what the FIRST compilations objected to. It does not say whether the
statement survived: most of what the compiler rejects is repaired, and the line numbers are
pre-repair so they cannot be read off the exported file.

This answers the only question that matters per error: does the statement the compiler pointed at
end up **commented out** in the finished export? The 4th field of the dump carries the source line
as the compiler saw it, and a commented statement keeps its text verbatim behind a `//`, so the
test is textual and exact.

Rows are deduplicated first: DumpDiagnostics runs once per repair attempt and appends, so the raw
file counts every site two or three times.
"""
import os, re, sys
from collections import defaultdict

why, root = sys.argv[1], sys.argv[2]
codes = set(sys.argv[3:])

rows = set()
for line in open(why, encoding="utf-8", errors="replace"):
    parts = line.rstrip("\n").split("\t")
    if len(parts) >= 3:
        rows.add(tuple(parts[:4]) if len(parts) >= 4 else tuple(parts[:3] + [""]))

# every .cs of the export, by file name
byname = defaultdict(list)
for dirpath, _, names in os.walk(root):
    for n in names:
        if n.endswith(".cs"):
            byname[n].append(os.path.join(dirpath, n))

text = {}
def load(path):
    if path not in text:
        text[path] = open(path, encoding="utf-8", errors="replace").read()
    return text[path]

#Six examples is enough to recognise a shape and never enough to census one. Every family
#census so far has had to re-run this by hand; `WHYCOST_EXAMPLES=999` prints them all.
LIMIT = int(os.environ.get("WHYCOST_EXAMPLES", 6))

stat = defaultdict(lambda: [0, 0, 0])   # code -> [total, commented, missing-file]
examples = defaultdict(list)

for code, where, msg, said in sorted(rows):
    if codes and code not in codes:
        continue
    name = where.split(":")[0]
    said = said.strip()
    paths = byname.get(name, [])
    s = stat[code]
    s[0] += 1
    if not paths or not said:
        s[2] += 1
        continue
    # the statement is commented where its text appears behind a `//` in the finished file
    hit = False
    for p in paths:
        body = load(p)
        for line in body.splitlines():
            t = line.strip()
            if t.startswith("//") and t.lstrip("/").strip() == said:
                hit = True
                break
        if hit:
            break
    if hit:
        s[1] += 1
        if len(examples[code]) < LIMIT:
            examples[code].append((where, msg[:110], said[:110]))
    else:
        if len(examples[code + " KEPT"]) < LIMIT:
            examples[code + " KEPT"].append((where, msg[:110], said[:110]))

print(f"{'code':8} {'sites':>6} {'lost':>6} {'kept':>6} {'nofile':>6}")
for code, (total, lost, miss) in sorted(stat.items(), key=lambda kv: -kv[1][1]):
    print(f"{code:8} {total:6} {lost:6} {total-lost-miss:6} {miss:6}")

if codes:
    for key, items in sorted(examples.items()):
        print(f"\n--- {key}")
        for where, msg, said in items:
            print(f"  {where}\n    {msg}\n    {said}")
