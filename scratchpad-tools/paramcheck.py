#!/usr/bin/env python3
"""paramcheck.py <off.txt> <on.txt> [assemblySubstring] - what a change to the argument allocator moved.

Positional, not `diff`: the two runs walk the methods in the same order, so line N is the same method in
both. `diff`'s hunk alignment reports neighbouring identical lines as changed and produced eight false
positives on exactly the invariant this is meant to check.

Prints, and this is the whole point:
  * how many methods' operand assignment moved,
  * how many of those the rule says should NOT have moved (extra=0) - which must be ZERO,
  * how many methods the rule applies to but which did not move (the wide parameter is last).
"""
import sys

off = open(sys.argv[1], encoding='utf-8', errors='replace').read().splitlines()
on = open(sys.argv[2], encoding='utf-8', errors='replace').read().splitlines()
where = sys.argv[3] if len(sys.argv) > 3 else None

if len(off) != len(on):
    print('the two runs listed different numbers of methods: %d and %d' % (len(off), len(on)))
    sys.exit(1)

moved = wrongly = wide = widestill = 0
examples = []

for a, b in zip(off, on):
    if where and where not in a:
        continue
    fa, fb = a.split('\t'), b.split('\t')
    extra = fb[-1] if len(fb) > 3 else 'extra=0'
    isWide = extra != 'extra=0'
    wide += isWide
    if a == b:
        widestill += isWide
        continue
    moved += 1
    if not isWide:
        wrongly += 1
        if len(examples) < 8:
            examples.append((fa[0], fa[2] if len(fa) > 2 else '', fb[2] if len(fb) > 2 else ''))

print('methods listed            %6d%s' % (len(off), '' if not where else ' (filtered on %r)' % where))
print('assignment moved          %6d' % moved)
print('MOVED WITHOUT A WIDE PARAM%6d   <- must be zero' % wrongly)
print('has a wide param          %6d' % wide)
print('  of those, did not move  %6d   (its wide parameter is the last one)' % widestill)

for name, before, after in examples:
    print('  %s\n    %s\n    %s' % (name, before, after))
