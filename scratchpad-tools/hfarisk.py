#!/usr/bin/env python3
"""hfarisk.py <export/ExportedProject> - assembled HFA returns whose every lane is a member of one and the
same parameter, i.e. `return <thatParameter>` written the long way.

That is the exact signature of the one shape the relaxed lane gate can get wrong: a body that wrote no lane
at all, whose answer really came from a call the lifter could not resolve, and whose parameters were taken
for the answer because they occupy those registers. A body that is genuinely the identity looks the same and
is harmless. Nothing else can look like this.
"""
import os, re, sys, collections

HFA = r'(?:Vector2|Vector3|Vector4|Color|Quaternion|Rect)'
DECL = re.compile(r'^\s*(?:public|private|protected|internal|static|virtual|override|sealed|extern|unsafe|new|\s)*\b('
                  + HFA + r')\s+([A-Za-z_][\w<>]*)\s*\(([^)]*)\)')
LANE = re.compile(r'^\s*(\w+)\.(\w+) = ([A-Za-z_]\w*)\.(\w+);\s*$')
ANY = re.compile(r'^\s*(\w+)\.(\w+) = ')

root = sys.argv[1] + '/Assets/Scripts/Assembly-CSharp'
risky = []
for dirpath, _, names in os.walk(root):
    for n in sorted(names):
        if not n.endswith('.cs'):
            continue
        path = os.path.join(dirpath, n)
        rel = os.path.relpath(path, root)
        lines = open(path, encoding='utf-8', errors='replace').read().splitlines()
        for i, line in enumerate(lines):
            m = DECL.match(line)
            if not m:
                continue
            indent = len(line) - len(line.lstrip())
            body = []
            for j in range(i + 1, min(i + 400, len(lines))):
                if lines[j].strip() == '}' and (len(lines[j]) - len(lines[j].lstrip())) == indent:
                    break
                body.append(lines[j])
            lanes = [b for b in body if ANY.match(b)]
            if not lanes:
                continue
            sources = set()
            for b in lanes:
                mm = LANE.match(b)
                if not mm:
                    sources.add(None)
                else:
                    sources.add(mm.group(3))
            if len(sources) == 1 and None not in sources:
                src = sources.pop()
                params = [p.strip().split()[-1] for p in m.group(3).split(',') if p.strip()]
                if src in params:
                    risky.append((rel, m.group(2), src, len(lanes)))

print(f'assembled returns that are a copy of one parameter and nothing else: {len(risky)}')
for r in risky:
    print(f'   {r[0]}::{r[1]}   every one of {r[3]} lanes from `{r[2]}`')
