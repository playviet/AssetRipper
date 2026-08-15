#!/usr/bin/env python3
"""Census the generic seam: for every member in the export, classify it and cut by
whether the member is a generic *method*, its owner is a generic type, or neither.

usage: seam.py <Assembly-CSharp dir> [--files] [--markers]
"""
import collections
import os
import re
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import csharp
import markers

TYPE_DECL = re.compile(r'^\s*(?:\[[^\]]*\]\s*)*(?:public|internal|private|protected|\s)*'
                       r'(?:static\s+|sealed\s+|abstract\s+|partial\s+|unsafe\s+)*'
                       r'(?:class|struct|interface)\s+([A-Za-z_@][\w@]*)(<[^>]*>)?', re.M)

# a generic method declares its own type parameters between the name and the parameter list
GEN_METHOD = re.compile(r'\b[A-Za-z_@][\w@]*<[A-Za-z_@][\w@]*(?:\s*,\s*[A-Za-z_@][\w@]*)*>\s*\(')


def owner_is_generic(text):
    return bool(re.search(r'(?:class|struct|interface)\s+[A-Za-z_@][\w@]*<', text))


def main():
    root = sys.argv[1]
    want_files = '--files' in sys.argv
    want_markers = '--markers' in sys.argv
    wanted = set()
    for arg in sys.argv[2:]:
        if arg.startswith('--list='):
            wanted = set(arg.split('=', 1)[1].split(','))
    listing = []
    pops = collections.defaultdict(collections.Counter)
    per_file = collections.defaultdict(collections.Counter)
    marker_by_pop = collections.defaultdict(collections.Counter)
    for dirpath, _, names in os.walk(root):
        for name in names:
            if not name.endswith('.cs'):
                continue
            path = os.path.join(dirpath, name)
            try:
                text = open(path, encoding='utf-8', errors='replace').read()
            except OSError:
                continue
            gentype = owner_is_generic(text)
            for _name, sources in csharp.members(path).items():
              for body in sources:
                if not markers.has_body(body):
                    continue
                head = body[:body.find('{')] if '{' in body else body[:120]
                genmethod = bool(GEN_METHOD.search(head))
                pop = 'genMethod' if genmethod else ('genType' if gentype else 'plain')
                verdict, found = markers.classify(body)
                if verdict != 'full' and pop in wanted:
                    rel = os.path.relpath(path, root)
                    listing.append((pop, verdict, rel, _name,
                                    {k: v for k, v in found.items() if v}))
                pops[pop][verdict] += 1
                for k, v in found.items():
                    if v:
                        marker_by_pop[pop][k] += v
                if verdict != 'full':
                    rel = os.path.relpath(path, root)
                    per_file[rel][verdict] += 1
                    per_file[rel][pop] += 1
    total = collections.Counter()
    print(f'{"population":12} {"n":>6} {"full":>6} {"partial":>8} {"dead":>6}  partial%')
    for pop in ('genMethod', 'genType', 'plain'):
        c = pops[pop]
        n = sum(c.values())
        total.update(c)
        if n:
            print(f'{pop:12} {n:6} {c["full"]:6} {c["partial"]:8} {c["dead"]:6}  {100.0*c["partial"]/n:5.1f}%')
    n = sum(total.values())
    print(f'{"ALL":12} {n:6} {total["full"]:6} {total["partial"]:8} {total["dead"]:6}')
    if listing:
        print()
        for pop, verdict, rel, name, found in sorted(listing, key=lambda r: r[:4]):
            print(f'{pop:9} {verdict:7} {rel:44} {name:36} {found}')
    if want_markers:
        print()
        for pop in ('genMethod', 'genType', 'plain'):
            print(pop, dict(marker_by_pop[pop]))
    if want_files:
        print()
        rows = sorted(per_file.items(), key=lambda kv: -(kv[1]['partial'] + kv[1]['dead']))
        for rel, c in rows[:40]:
            print(f'{c["partial"]:4}p {c["dead"]:3}d  gm={c["genMethod"]:3} gt={c["genType"]:3}  {rel}')


main()
