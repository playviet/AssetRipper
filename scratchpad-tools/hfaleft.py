#!/usr/bin/env python3
"""hfaleft.py <export/ExportedProject> - HFA-returning methods that do NOT assemble their answer, and how
each of them ends. What is left of the callee-side family after VectorReturnAssembly."""
import os, re, sys, collections

HFA = r'(?:Vector2|Vector3|Vector4|Color|Quaternion|Rect)'
DECL = re.compile(r'^\s*(?:public|private|protected|internal|static|virtual|override|sealed|extern|unsafe|new|\s)*\b('
                  + HFA + r')\s+([A-Za-z_][\w<>]*)\s*\(')
FIELD = re.compile(r'^\s*\w+\.\w+\s*=\s')
RET = re.compile(r'^\s*return\b(.*);\s*$')

root = sys.argv[1] + '/Assets/Scripts/Assembly-CSharp'
kinds = collections.Counter()
rows = []
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
            assembled = any(FIELD.match(b) and b.strip().startswith(('result.', 'vector.', 'val.')) for b in body)
            rets = [RET.match(b).group(1).strip() for b in body if RET.match(b)]
            if assembled:
                kinds['assembled'] += 1
                continue
            end = rets[-1] if rets else '(no return)'
            if end.startswith('default('):
                kinds['LOST return default'] += 1
            elif end.startswith('new '):
                kinds['new T(...)'] += 1
            else:
                kinds['hands a value back'] += 1
            rows.append((rel, m.group(2), end[:70]))

for k, v in kinds.most_common():
    print(f'{v:5}  {k}')
print()
for r in rows:
    print(f'   {r[0]}::{r[1]}  ->  return {r[2]}')
