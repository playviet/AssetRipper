#!/usr/bin/env python3
"""What moved between two `gamescore.py --json` runs, so keep-or-revert is one command.

    python3 gamescorediff.py <a.json> <b.json> [--top N]

`a` is the baseline and `b` is the round being judged. Everything is printed as `a -> b (delta)`, and the
delta is signed the way the measure runs, not the way "better" runs - `full` going up is a win and
`commented` going up is not, so the verdict at the bottom says which way each one counts rather than
leaving it to be read off the sign.

A file that becomes clean drops out of the `files` map entirely, so an absence on the `b` side is an
improvement and is reported as one. Read the verdict as `compare2` should be read: `full` counts bodies that
compile whole, and a body can compile whole and be wrong (`il2cpp-what-full-is-actually-worth`), so a round
that moves `full` up while moving `dead` and the crash count up with it has not necessarily won.
"""
import argparse
import json
import sys

# name -> whether a rise is an improvement. Nothing here is scored automatically; it is what the verdict
# line uses to say which direction each number moved in.
BETTER_UP = {'full': True, 'partial': False, 'dead': False, 'crashes': False, 'markers': False}


def load(path):
    with open(path, encoding='utf-8') as handle:
        return json.load(handle)


def delta(before, after):
    return '%+d' % (after - before) if after != before else '.'


def row(label, before, after, width=30):
    return '  %-*s %8s %8s %9s' % (width, label[:width], before, after, delta(before, after))


def section(title):
    print()
    print(title)
    print('  %-30s %8s %8s %9s' % ('', 'a', 'b', 'delta'))


def main(argv=None):
    parser = argparse.ArgumentParser(description='Diff two gamescore.py JSON runs.')
    parser.add_argument('a', help='the baseline JSON')
    parser.add_argument('b', help='the round being judged')
    parser.add_argument('--top', type=int, default=15, help='how many moved files to list each way')
    arguments = parser.parse_args(argv)

    a, b = load(arguments.a), load(arguments.b)

    print('gamescorediff  %s -> %s' % (arguments.a, arguments.b))
    print('  a: %s   %s' % (a.get('when', '?'), a.get('root', '?')))
    print('  b: %s   %s' % (b.get('when', '?'), b.get('root', '?')))
    if a.get('root') != b.get('root'):
        print('  (different trees - make sure these are two rounds of the same game)')

    section('TOTALS')
    for key in ('assemblies', 'files', 'substituted_files', 'types', 'with_body', 'without_body'):
        print(row(key, a['totals'].get(key), b['totals'].get(key)))

    for scale in ('all', 'decompiled'):
        section('BODY CLASSIFICATION (%s)' % scale)
        before, after = a['classification'][scale], b['classification'][scale]
        for kind in ('full', 'partial', 'dead'):
            # The share as well as the count: an export whose body total moved - a pass that stops a whole
            # type being emitted will do that - moves every count with it for no reason of its own.
            was = 100.0 * before.get(kind, 0) / max(before.get('total', 0), 1)
            now = 100.0 * after.get(kind, 0) / max(after.get('total', 0), 1)
            print('%s   (%5.1f%% -> %5.1f%%, %+.1fpt)'
                  % (row(kind, before.get(kind, 0), after.get(kind, 0)), was, now, now - was))
        print(row('total', before.get('total', 0), after.get('total', 0)))

    section('RETHROWN GENERATOR CRASHES')
    print(row('bodies (attributed)', a['crashes']['total'], b['crashes']['total']))
    print(row('bodies (raw file text)', a['crashes'].get('raw_total', 0), b['crashes'].get('raw_total', 0)))
    print(row('rated whole, not dead', a['crashes']['missed_by_markers'], b['crashes']['missed_by_markers']))
    for name in sorted(set(a['crashes'].get('raw_by_exception', {})) | set(b['crashes'].get('raw_by_exception', {}))):
        before = a['crashes'].get('raw_by_exception', {}).get(name, 0)
        after = b['crashes'].get('raw_by_exception', {}).get(name, 0)
        print('    %-44s %8s %8s %9s' % (name[:44], before, after, delta(before, after)))

    section('MARKER CENSUS (bodies carrying)')
    marker_delta = 0
    for name in sorted(set(a['markers']) | set(b['markers'])):
        before = a['markers'].get(name, {})
        after = b['markers'].get(name, {})
        if not before.get('occurrences') and not after.get('occurrences'):
            continue
        marker_delta += after.get('bodies', 0) - before.get('bodies', 0)
        print('  %-30s %8s %8s %9s      occurrences %s -> %s (%s)'
              % (name, before.get('bodies', 0), after.get('bodies', 0),
                 delta(before.get('bodies', 0), after.get('bodies', 0)),
                 before.get('occurrences', 0), after.get('occurrences', 0),
                 delta(before.get('occurrences', 0), after.get('occurrences', 0))))

    moved = []
    for name in set(a['files']) | set(b['files']):
        before = a['files'].get(name, {}).get('score', 0)
        after = b['files'].get(name, {}).get('score', 0)
        if before != after:
            moved.append((after - before, name, before, after))
    moved.sort(reverse=True)

    worse = [entry for entry in moved if entry[0] > 0]
    better = [entry for entry in moved if entry[0] < 0]

    print()
    print('FILES THAT GOT WORSE  (cost = dead + marker-carrying bodies; %d of them)' % len(worse))
    for change, name, before, after in worse[:arguments.top]:
        print('    %-58s %4d -> %-4d %+d' % (name[-58:], before, after, change))

    print()
    print('FILES THAT GOT BETTER  (%d of them)' % len(better))
    for change, name, before, after in reversed(better[-arguments.top:]):
        print('    %-58s %4d -> %-4d %+d' % (name[-58:], before, after, change))

    # The verdict. Five numbers, each with the direction that counts as an improvement written beside it -
    # a round that moves them all one way needs no argument, and one that splits them needs the argument
    # made in words rather than inferred from a sign.
    print()
    print('VERDICT')
    scores = [
        ('full', a['classification']['all']['full'], b['classification']['all']['full'], True),
        ('dead', a['classification']['all']['dead'], b['classification']['all']['dead'], False),
        ('partial', a['classification']['all']['partial'], b['classification']['all']['partial'], False),
        ('crashes (raw)', a['crashes'].get('raw_total', 0), b['crashes'].get('raw_total', 0), False),
        ('marked bodies', sum(v.get('bodies', 0) for v in a['markers'].values()),
         sum(v.get('bodies', 0) for v in b['markers'].values()), False),
    ]
    won = lost = 0
    for name, before, after, up_is_good in scores:
        if after == before:
            verdict = 'unchanged'
        elif (after > before) == up_is_good:
            verdict = 'better'
            won += 1
        else:
            verdict = 'WORSE'
            lost += 1
        print('  %-16s %8d -> %-8d %-6s   %s' % (name, before, after, delta(before, after), verdict))
    print('  %d better, %d worse, %d unchanged' % (won, lost, len(scores) - won - lost))
    print('  `full` alone does not settle it: a body can compile whole and compute the wrong answer.')
    return 0


if __name__ == '__main__':
    sys.exit(main())
