#!/usr/bin/env python3
"""hfacensus.py <exportA/ExportedProject> [exportB/ExportedProject] - the callee-side HFA return family.

Counts, over Assembly-CSharp:

  decl        methods declaring a homogeneous-float-aggregate return type
  lost        of those, whose body's only return is `return default(<that type>)`
  assembled   of those, that assemble the answer field by field and return it

`return default(T)` is the whole defect and no compilability scorer sees any of it: all 64 sites in this
game score `full`. Given two exports it prints the per-file difference as well.
"""
import os, re, sys

HFA = r'(?:Vector2|Vector3|Vector4|Color|Quaternion|Rect)'
DECL = re.compile(r'^\s*(?:public|private|protected|internal|static|virtual|override|sealed|extern|unsafe|new|\s)*\b('
                  + HFA + r')\s+([A-Za-z_][\w<>]*)\s*\(')
#Both spellings of the same refusal: ILSpy writes the one-liner where nothing else names the local, and
#`T result = default(T); return result;` where it does. Counting only the first understates the family.
LOST = re.compile(r'^\s*(?:return default\((' + HFA + r')\);|(?:' + HFA + r') \w+ = default\((' + HFA + r')\);)\s*$')
FIELD = re.compile(r'^\s*\w+\.\w+\s*=\s')


def census(root):
    decl = {}
    seen = {}
    for dirpath, _, names in os.walk(root):
        for n in names:
            if not n.endswith('.cs'):
                continue
            path = os.path.join(dirpath, n)
            rel = os.path.relpath(path, root)
            lines = open(path, encoding='utf-8', errors='replace').read().splitlines()
            for i, line in enumerate(lines):
                m = DECL.match(line)
                if not m:
                    continue
                # the body: from here to the next line at the same indentation closing it
                indent = len(line) - len(line.lstrip())
                body = []
                for j in range(i + 1, min(i + 400, len(lines))):
                    if lines[j].strip() == '}' and (len(lines[j]) - len(lines[j].lstrip())) == indent:
                        break
                    body.append(lines[j])
                #Never the line number: it shifts between exports and every method then reads as both
                #"no longer lost" and "newly lost". The nth overload of a name in a file is stable.
                seen[(rel, m.group(2))] = seen.get((rel, m.group(2)), -1) + 1
                key = (rel, m.group(2), seen[(rel, m.group(2))])
                assembled = any(FIELD.match(b) for b in body)
                #The declaration of the buffer is the first line of an assembled body too, so a body that
                #fills the buffer in is not lost however it declares it.
                lost = not assembled and any(LOST.match(b) for b in body)
                decl[key] = (m.group(1), lost, assembled)
    return decl


def report(name, d):
    lost = sum(1 for v in d.values() if v[1])
    asm = sum(1 for v in d.values() if v[2])
    print(f'{name}:  decl {len(d)}   lost(return default) {lost}   assembled {asm}')
    return lost


a = census(sys.argv[1] + '/Assets/Scripts/Assembly-CSharp')
report(sys.argv[1].split("/")[-2], a)

if len(sys.argv) > 2:
    b = census(sys.argv[2] + '/Assets/Scripts/Assembly-CSharp')
    report(sys.argv[2].split("/")[-2], b)
    fixed = [k for k in a if a[k][1] and (k not in b or not b[k][1])]
    broke = [k for k in b if b[k][1] and (k not in a or not a[k][1])]
    print(f'\nno longer lost: {len(fixed)}')
    for k in sorted(fixed)[:80]:
        print(f'   {k[0]}::{k[1]}')
    print(f'newly lost: {len(broke)}')
    for k in sorted(broke)[:40]:
        print(f'   {k[0]}::{k[1]}')
