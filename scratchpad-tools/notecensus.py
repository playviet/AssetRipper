#!/usr/bin/env python3
"""Does an ILSpy `Expected X, but got Y` note still predict a lost statement?

    notecensus.py <export>/ExportedProject

Re-derives the table in `il2cpp-ilspy-notes-are-a-scorer`, which was last taken at 1.0.386.
"""
import collections, os, re, sys

sys.path.insert(0, '/Users/playviet/Documents/_BZ/AssetRipper/tools')
from csharp import members
from markers import classify, has_body

NOTE = re.compile(r'// *IL_[0-9a-f]+: Expected ([A-Za-z0-9]+), but got ([A-Za-z0-9]+)')

export = sys.argv[1]
root = os.path.join(export, 'Assets', 'Scripts', 'Assembly-CSharp')

with_note = without = 0
with_note_commented = without_commented = 0
shape_methods = collections.Counter()      # note shape -> methods carrying it
shape_lost = collections.Counter()         # note shape -> those with a commented statement
shape_notes = collections.Counter()

for directory, _sub, files in os.walk(root):
    for name in sorted(files):
        if not name.endswith('.cs'):
            continue
        path = os.path.join(directory, name)
        # members() keys a name to every overload of it, so the value is a list.
        for member, texts in members(path).items():
          for text in (texts if isinstance(texts, list) else [texts]):
            if not has_body(text):
                continue
            kind, found = classify(text)
            lost = bool(found['commented'])
            shapes = set()
            for a, b in NOTE.findall(text):
                shapes.add('%s<-%s' % (a, b))
                shape_notes['%s<-%s' % (a, b)] += 1
            if shapes:
                with_note += 1
                with_note_commented += lost
                for s in shapes:
                    shape_methods[s] += 1
                    shape_lost[s] += lost
            else:
                without += 1
                without_commented += lost

def rate(n, d):
    return '%.1f%%' % (100.0 * n / d) if d else '-'

print('methods with a note      %5d   with a commented statement %5d  (%s)'
      % (with_note, with_note_commented, rate(with_note_commented, with_note)))
print('methods with none        %5d   with a commented statement %5d  (%s)'
      % (without, without_commented, rate(without_commented, without)))
if without and with_note:
    a = with_note_commented / with_note
    b = without_commented / without
    print('ratio                    %.1fx' % (a / b) if b else 'ratio                    inf')

print('\n notes  methods  lost  rate   shape (Expected <- got)')
for shape, n in shape_notes.most_common(20):
    print('%6d %8d %5d %6s   %s'
          % (n, shape_methods[shape], shape_lost[shape],
             rate(shape_lost[shape], shape_methods[shape]), shape))
