#!/usr/bin/env python3
"""Which files' marker counts moved between two exports, and by how much.

`compare2` says four bodies went from full to partial; this says WHERE, so the claim that the cost landed
inside newly visible code can be checked rather than asserted.
"""
import os
import re
import sys

MARKERS = ("Unmanaged memory load", "Unmanaged memory store", "Indirect call",
           "Unknown call target", "Not implemented instruction", "Invalid instruction")


def scan(root):
    found = {}
    for base, _, names in os.walk(root):
        for name in names:
            if not name.endswith(".cs"):
                continue
            path = os.path.join(base, name)
            text = open(path, errors="ignore").read()
            n = sum(text.count(m) for m in MARKERS)
            if n:
                found[os.path.relpath(path, root)] = n
    return found


def main():
    a, b = scan(sys.argv[1]), scan(sys.argv[2])
    moved = sorted(set(a) | set(b), key=lambda k: b.get(k, 0) - a.get(k, 0))
    total = 0
    for name in moved:
        delta = b.get(name, 0) - a.get(name, 0)
        if delta:
            total += delta
            print(f"  {delta:+4d}   {a.get(name, 0):4d} -> {b.get(name, 0):4d}   {name}")
    print(f"  net {total:+d} markers over {sum(1 for n in moved if b.get(n, 0) != a.get(n, 0))} files")


main()
