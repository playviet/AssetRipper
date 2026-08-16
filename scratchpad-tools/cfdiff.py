#!/usr/bin/env python3
"""Which METHODS changed verdict between two exports, by cfscore's own reckoning.

    python3 cfdiff.py <exportA>/ExportedProject <exportB>/ExportedProject

cfscore prints per-file counts, so a file that loses one body and gains another reads as unchanged and a
file that loses one reads as "-1" with no name on it. This names them, which is what makes it possible to
read the body against its original.
"""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import cfscore
from markers import classify


def verdicts(export):
    originals = cfscore.index(cfscore.ORIGIN)
    recovered = cfscore.index(export)
    out = {}

    for name in sorted(originals):
        wanted = cfscore.scorable(originals[name], cfscore.not_in_the_build(originals[name]))
        got = cfscore.recovered_members(name, originals[name], recovered)

        for method in wanted:
            if method not in got:
                out[(name, method)] = 'missing'
                continue
            kind, found = classify(got[method])
            if kind == 'dead' and not found['failed'] and cfscore.compiles_to_nothing(wanted[method]):
                kind = 'full'
            out[(name, method)] = kind

    return out


a, b = verdicts(sys.argv[1]), verdicts(sys.argv[2])

for key in sorted(set(a) | set(b)):
    if a.get(key) != b.get(key):
        print(f"{key[0]:34} {key[1]:44} {a.get(key)} -> {b.get(key)}")
