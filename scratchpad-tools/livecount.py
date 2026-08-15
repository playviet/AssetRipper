#!/usr/bin/env python3
"""How much live code an export has, and what moved between two of them.

`commented` can FALL while live statements are deleted - a block made unreachable leaves the file rather
than being commented out - so a round that improves every marker count can still have thrown code away.
This counts what is actually there: statements that are not comments, and the branching among them.

usage: livecount.py <Assembly-CSharp dir> [<other Assembly-CSharp dir>]

With one directory it prints the totals; with two it prints both and the per-file differences, worst first.
"""
import collections
import os
import re
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import csharp
import markers

# What makes a statement a decision. `case` counts because a switch with no cases kept nothing, and `&&`/`||`
# because a condition that lost half of itself is a branch lost even though the `if` survived.
BRANCHES = re.compile(r'(?<![\w.])(if|while|for|foreach|switch|case|goto|catch)(?![\w])|&&|\|\||(?<![?<>=!])\?(?!\?)')


def measure(root):
    files = {}
    for dirpath, _, names in os.walk(root):
        for name in names:
            if not name.endswith('.cs'):
                continue
            path = os.path.join(dirpath, name)
            statements = branches = 0
            for _member, sources in csharp.members(path).items():
                for body in sources:
                    if not markers.has_body(body):
                        continue
                    for line in markers.statements(body):
                        # a marker the generator wrote is not the program's own statement
                        if line.startswith('_ = "') or line.startswith('//'):
                            continue
                        statements += 1
                        branches += len(BRANCHES.findall(line))
            files[os.path.relpath(path, root)] = (statements, branches)
    return files


def total(files):
    return sum(s for s, _ in files.values()), sum(b for _, b in files.values())


def main():
    left = measure(sys.argv[1])
    ls, lb = total(left)
    print(f'{sys.argv[1].split("/")[-4] if "/" in sys.argv[1] else sys.argv[1]}: live {ls}  branches {lb}  files {len(left)}')

    if len(sys.argv) < 3:
        return

    right = measure(sys.argv[2])
    rs, rb = total(right)
    print(f'{sys.argv[2].split("/")[-4] if "/" in sys.argv[2] else sys.argv[2]}: live {rs}  branches {rb}  files {len(right)}')
    print(f'delta: live {rs - ls:+}  branches {rb - lb:+}')

    moved = collections.Counter()
    for rel in set(left) | set(right):
        a = left.get(rel, (0, 0))
        b = right.get(rel, (0, 0))
        if a != b:
            moved[rel] = b[0] - a[0]

    print('\nfiles that moved, worst first:')
    for rel, delta in sorted(moved.items(), key=lambda kv: kv[1]):
        a = left.get(rel, (0, 0))
        b = right.get(rel, (0, 0))
        print(f'  {delta:+5} live  {b[1] - a[1]:+4} branches   {rel}')


main()
