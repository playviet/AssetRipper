#!/usr/bin/env python3
"""Cross the corpus's execution oracle against the project's own compile-whole score.

Every scorer in the loop - cfscore, compare2, roundtrip, decisions, allscore - asks whether a recovered body
**compiles whole**. autodiff asks whether it **computes the right answer**. This puts the two answers in one
table, per method, so the question the project has not been able to state can be stated:

    of the shapes recovery rates `full`, how many actually work?

  corpusscore.py <recoveredCorpus.cs> <autodiffOutput.txt>

The `full`/`partial`/`dead` verdict is `markers.classify` - the same function cfscore.py and allscore.py use -
so this is not a second opinion about compilability, it is theirs.
"""
import collections
import os
import re
import sys

# markers.py lives beside this file in scratchpad-tools and in memory/tools; the third path is for a copy
# kept anywhere else.
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
sys.path.append("/Users/playviet/Documents/_BZ/AssetRipper/scratchpad-tools")
from markers import classify  # noqa: E402


def members(text):
    """Every top-level member of the recovered class, as (name, text)."""
    start = text.index('{', text.index('class '))
    depth, at, i = 0, start + 1, start
    found = []
    while i < len(text):
        if text[i] == '{':
            depth += 1
        elif text[i] == '}':
            depth -= 1
            if depth == 0:
                break
            if depth == 1:
                found.append(text[at:i + 1])
                at = i + 1
        elif text[i] == ';' and depth == 1:
            found.append(text[at:i + 1])
            at = i + 1
        i += 1

    named = []
    for member in found:
        # Attribute lines first: `[IteratorStateMachine(typeof(...))]` above an iterator otherwise names the
        # member `IteratorStateMachine`, and every iterator then reads as one the export does not contain.
        signature = '\n'.join(line for line in (member.split('{')[0] or member).split('\n')
                              if not line.strip().startswith('['))
        call = re.search(r'\b(\w+)\s*\(', signature)
        if call:
            named.append((call.group(1), member))
    return named


def main():
    recovered = open(sys.argv[1], errors='ignore').read()
    report = open(sys.argv[2], errors='ignore').read()

    verdict = {}
    for line in report.split('\n'):
        agrees = re.match(r'\s*agrees\s+(\w+)', line)
        differs = re.match(r'\s*DIFFERS\s+(\w+)', line)
        if agrees:
            verdict[agrees.group(1)] = 'right'
        elif differs:
            verdict[differs.group(1)] = 'WRONG'
    for line in report.split('\n'):
        broken = re.match(r'DOES NOT COMPILE \(\d+\): (.*)', line)
        if broken:
            for name in broken.group(1).split(', '):
                verdict[name.strip()] = 'nocompile'

    scored = {}
    for name, text in members(recovered):
        if name in verdict:
            scored[name] = classify(text)[0]

    table = collections.Counter()
    rows = []
    for name, answer in verdict.items():
        rated = scored.get(name, '(not found)')
        table[(rated, answer)] += 1
        rows.append((rated, answer, name))

    print(f"{'method':24} {'compiles':10} {'runs'}")
    for rated, answer, name in sorted(rows):
        print(f"  {name:24} {rated:10} {answer}")

    print()
    whole = sum(n for (rated, _), n in table.items() if rated == 'full')
    right = table[('full', 'right')]
    print(f"rated `full` by the project's own scorer: {whole}")
    print(f"  of those, actually compute the right answer: {right}")
    print(f"  of those, compile whole and are WRONG:      {whole - right}")
    print()
    for key in sorted(table):
        print(f"  {key[0]:10} + {key[1]:10} {table[key]}")


if __name__ == '__main__':
    main()
