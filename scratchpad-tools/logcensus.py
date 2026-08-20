#!/usr/bin/env python3
"""Census of per-method decompile failures in a riprun log.

    python3 logcensus.py <log> [top]

Cpp2IL catches a generator crash per method and logs
    Cpp2IL [Error] : Decompiling <Type>::<Method> failed: <ExceptionType>: <message>
then gives the method a `throw new ...` body. Those bodies score as `dead`, so every line here is a
recovered method that was lost outright. Grouping them by (exception, first fork frame) turns thousands of
lines into the handful of root causes actually worth fixing.
"""
import collections, re, sys

log = sys.argv[1]
top = int(sys.argv[2]) if len(sys.argv) > 2 else 25

HEAD = re.compile(r'Decompiling (.+?) failed: ([\w.`+]+(?:Exception|Error))\s*:?\s*(.*)')
# a stack frame that is ours, not the framework's
FRAME = re.compile(r'at ((?:Cpp2IL|LibCpp2IL|AssetRipper)[\w.`<>+]*\.[\w`<>_]+)')

by_exc = collections.Counter()
by_site = collections.Counter()
by_msg = collections.Counter()
examples = {}
methods = []

cur = None
with open(log, errors='replace') as f:
    for line in f:
        m = HEAD.search(line)
        if m:
            method, exc, msg = m.group(1), m.group(2), m.group(3).strip()
            cur = (method, exc, msg)
            by_exc[exc] += 1
            by_msg[(exc, msg[:90])] += 1
            methods.append(method)
            continue
        if cur is not None:
            fm = FRAME.search(line)
            if fm:
                site = fm.group(1)
                by_site[(cur[1], site)] += 1
                examples.setdefault((cur[1], site), cur[0])
                cur = None

print(f"decompile failures : {sum(by_exc.values())}")
print(f"distinct methods   : {len(set(methods))}")
print()
print("== by exception ==")
for exc, n in by_exc.most_common(top):
    print(f"{n:7d}  {exc}")
print()
print("== by (exception, first fork frame) ==")
for (exc, site), n in by_site.most_common(top):
    print(f"{n:7d}  {exc}\n         at {site}\n         e.g. {examples[(exc,site)]}")
print()
print("== by message ==")
for (exc, msg), n in by_msg.most_common(top):
    print(f"{n:7d}  {exc}: {msg}")
