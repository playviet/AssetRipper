#!/usr/bin/env python3
"""The whole picture of an export whose game has no original source beside it.

    python3 gamescore.py <export>/ExportedProject [--top N] [--json out.json]

Every scorer here that compares against originals - `cfscore.py`, `allscore.py`, `autodiff.py`,
`corpusscore.py` - is silent on a game this project has no source for. What is left is what the export can
be asked about itself, and until now that was `compare2.py` alone, which prints six numbers. This asks the
same question `compare2` does and then the five that follow it: what did the recovery give up on, where, and
which file should be opened first.

Nothing here re-implements the vocabulary. `markers.classify` decides what a body is and `csharp.members`
decides what a member is, exactly as `compare2.py` uses them, and `compare2.substituted` decides whether a
file was decompiled at all - so a number that moves here moves for the same reason it would move there.

What it adds over `compare2.py`:

  * every assembly in the export, not `Assembly-CSharp` alone;
  * the marker census by *body* as well as by occurrence - 400 `commented` lines in one method and one line
    in 400 methods are the same number to `compare2` and nothing like the same problem;
  * **rethrown generator crashes counted in their own right.** `AsmResolverDllOutputFormatIlRecovery`
    catches anything the analysis or the generator throws and writes the exception out as the method body,
    so the method comes back as one statement the language is perfectly happy with. `markers.MARKERS` has a
    `failed` entry for two shapes of this and misses a third - see `crash_of` below - so this counts them
    itself and says which of them the marker vocabulary agrees with;
  * the fix queue: which files and which methods carry the damage.
"""
import argparse
import collections
import concurrent.futures
import json
import os
import re
import sys
import time
import types

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

from csharp import members
from markers import MARKERS, classify, has_body, statements

MARKER_NAMES = [name for name, _needles in MARKERS]
# `classify` adds this one itself where a body throws one of the runtime's own checks before it can do
# anything else. It is in no MARKERS row because it is not a string the generator writes - it is a shape -
# but it is the same kind of evidence and belongs in the same census.
CENSUS_NAMES = MARKER_NAMES + ['thrown']


def _compare2():
    """`compare2.py` with its own `main()` removed, so importing it does not run it.

    `substituted` is compare2's, and duplicating three lines of it here is how the two copies of a scorer
    start disagreeing. The module calls `main()` at the bottom rather than guarding it, so it is loaded from
    source with that one line dropped; if it ever grows an `if __name__` guard this imports it normally.
    """
    path = os.path.join(HERE, 'compare2.py')
    source = open(path, encoding='utf-8').read()

    if '__name__' in source:
        import compare2
        return compare2

    stripped = re.sub(r'(?m)^main\(\)[ \t]*\r?$', '', source)
    if stripped == source:
        raise SystemExit('gamescore: compare2.py no longer ends in a bare main(); update _compare2()')

    module = types.ModuleType('compare2')
    module.__file__ = path
    exec(compile(stripped, path, 'exec'), module.__dict__)
    return module


substituted = _compare2().substituted


# ---------------------------------------------------------------------------------------------------
# The crashed body
#
# `AsmResolverDllOutputFormatIlRecovery.FillMethodBody` wraps the whole of `methodContext.Analyze()` and
# `IlGenerator.GenerateIl` in one `try`, and on any exception emits three IL instructions in place of the
# method: `ldstr <detail>`, `newobj Exception`, `throw`. So the body comes back as exactly one statement
# with no marker in it and nothing a compiler objects to, which is how 109 of them once sat inside `full`
# (`il2cpp-source-free-oracle`).
#
# `detail` is `e.Message` for a `DecompilerException` - always "Decompilation failed: ..." - and
# `e.ToCollapsedString()`, which is `e.ToString()` with runs of identical lines folded, for anything else.
# `ToString()` opens `<full type name>: <message>` and then a run of `   at <frame>` lines, and the string
# reaches C# escaped, so the whole trace arrives on one line with `\n` in it.
#
# `markers.MARKERS` has `('failed', ['at Cpp2IL.Core.IlGenerator.GenerateIl', 'Decompilation failed'])`,
# which catches the DecompilerException shape and a crash inside the generator - and misses a crash inside
# `methodContext.Analyze()`, whose trace has no `GenerateIl` frame at all because the generator was never
# reached. Verified against export _50, where `Joystick.cs` carries
# `throw new Exception("System.NullReferenceException: ...\n   at Cpp2IL.Core.Analysis.StructInArithmetic
# .IsNumber ...\n   at ...MetadataResolver.ResolveFieldOffsets ...")` - no `GenerateIl` frame, no marker,
# rated `full`.
# ---------------------------------------------------------------------------------------------------
CRASH_BODY = re.compile(r'^throw new (?:System\.)?Exception\("(.*)"\)\s*;?$', re.S)
CRASH_FRAME = re.compile(r'\\n\s*at (?:Cpp2IL|AssetRipper|LibCpp2IL|AsmResolver|System|Microsoft)[.\s]')
CRASH_TYPE = re.compile(r'^([A-Za-z_][\w.`+]*(?:Exception|Error))\s*:')
DECOMPILER_EXCEPTION = 'Cpp2IL.Core.DecompilerException'


# The same statement found in the raw file text rather than in a member `csharp.members` returned. It is
# there because `csharp.KINDS` has no `event_declaration`, so an `add`/`remove` accessor is in no member
# this - or `compare2`, or `cfscore`, or `allscore` - can see, and on export _50 twelve of the fifty-two
# crashed bodies are event accessors and are counted by none of them. Extending `csharp.KINDS` would move
# the scale every number in this project is recorded on, so the gap is measured here instead of closed.
# The lead is `[^"\n]*?` rather than whitespace so that an expression-bodied member -
# `public int X() => throw new Exception("...");` - is found too, and cannot begin inside another
# string literal on the same line.
RAW_CRASH = re.compile(r'(?m)^[^"\n]*?\bthrow new (?:System\.)?Exception\("(.*)"\)\s*;[ \t]*$')


def exception_named(detail):
    """The exception a rethrown `detail` string names, or None if the string is not one of ours.

    A hand-written `throw new Exception("Not supported")` is a method too, so the string has to name a frame
    of the recovery's own before the body is called a crash rather than a program.
    """
    if detail.startswith('Decompilation failed'):
        return DECOMPILER_EXCEPTION

    if not CRASH_FRAME.search(detail):
        return None

    named = CRASH_TYPE.match(detail)
    return named.group(1) if named else '<exception type not named>'


def crash_of(text):
    """The exception a body is the rethrow of, or None if the body is a real one."""
    lines = statements(text)
    if len(lines) != 1:
        return None

    found = CRASH_BODY.match(lines[0])
    return exception_named(found.group(1)) if found else None


def raw_crashes(text):
    """Every rethrown crash in a file, whether or not it sits in a member anything here can name."""
    found = collections.Counter()
    for detail in RAW_CRASH.findall(text):
        named = exception_named(detail)
        if named:
            found[named] += 1
    return found


# ---------------------------------------------------------------------------------------------------
# Counting the types
#
# One `ast-grep` over the whole tree rather than one per file: the per-file scan `csharp.members` does is
# already the bulk of the run, and this answer is a headline number that does not need to be attributed to
# anything. `--json=stream` is one object per line, so a tree of thousands of files never lands in memory
# whole - the match text of a type declaration is the entire type.
# ---------------------------------------------------------------------------------------------------
TYPE_KINDS = ('class_declaration', 'struct_declaration', 'interface_declaration',
              'enum_declaration', 'record_declaration', 'delegate_declaration')

TYPE_RULES = ('id: type\nlanguage: csharp\nrule:\n  kind: identifier\n  inside:\n    field: name\n    any:\n'
              + ''.join(f'      - kind: {kind}\n' for kind in TYPE_KINDS))


def count_types(root):
    import subprocess
    try:
        process = subprocess.Popen(['ast-grep', 'scan', '--inline-rules', TYPE_RULES, '--json=stream', root],
                                   stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, text=True)
    except FileNotFoundError:
        return None

    total = 0
    for line in process.stdout:
        if line.strip():
            total += 1
    process.stdout.close()
    process.wait()
    return total


# ---------------------------------------------------------------------------------------------------
# Walking the export
# ---------------------------------------------------------------------------------------------------
def find_root(given):
    """`Assets/Scripts`, from whichever level of the export the caller happened to name."""
    for candidate in (os.path.join(given, 'Assets', 'Scripts'),
                      os.path.join(given, 'ExportedProject', 'Assets', 'Scripts'),
                      given):
        if os.path.isdir(candidate) and os.path.basename(candidate) == 'Scripts':
            return candidate
    raise SystemExit('gamescore: no Assets/Scripts under %s' % given)


def source_files(root):
    """Every `.cs` file under the tree, with the assembly it belongs to.

    An assembly is a directory immediately under `Assets/Scripts`; AssetRipper puts each exported one there
    and a game with more than `Assembly-CSharp` in source form is the normal case outside this project's
    own target.
    """
    for entry in sorted(os.listdir(root)):
        path = os.path.join(root, entry)
        assembly = entry if os.path.isdir(path) else '<loose>'
        walk = os.walk(path) if os.path.isdir(path) else [(root, [], [entry])]
        for directory, _subdirectories, files in walk:
            for name in sorted(files):
                if name.endswith('.cs'):
                    yield assembly, os.path.join(directory, name)


def read_file(job):
    """One file's members, whether it was decompiled at all, and its crashes. Runs on a worker thread."""
    assembly, path = job
    text = open(path, encoding='utf-8', errors='replace').read()
    return (assembly, path, substituted(path), members(path), raw_crashes(text),
            os.path.getmtime(path))


def scan(root, jobs):
    """Every file in the tree, in order, with at most `2 * jobs` of them parsed ahead of the consumer."""
    files = source_files(root)
    with concurrent.futures.ThreadPoolExecutor(max_workers=jobs) as pool:
        pending = collections.deque()
        for job in files:
            pending.append(pool.submit(read_file, job))
            if len(pending) >= 2 * jobs:
                yield pending.popleft().result()
        while pending:
            yield pending.popleft().result()


class Report:
    def __init__(self):
        self.assemblies = set()
        self.files = self.substituted_files = 0
        self.with_body = self.without_body = 0
        self.newest = 0.0

        self.by_class = collections.Counter()          # every body
        self.decompiled = collections.Counter()        # the bodies that were actually recovered
        self.substituted_bodies = 0

        self.marker_bodies = collections.Counter()     # bodies carrying the marker at least once
        self.marker_hits = collections.Counter()       # occurrences, the scale compare2 reports

        self.crashes = collections.Counter()           # by exception type, attributed to a member
        self.crash_class = collections.Counter()       # what classify() makes of them
        self.crashes_missed = 0                        # ...and how many it calls whole
        self.raw = collections.Counter()               # by exception type, over the raw file text

        self.per_file = {}
        self.worst_methods = []

    def add_file(self, assembly, relative, copied, found, raw):
        self.assemblies.add(assembly)
        self.files += 1
        self.substituted_files += copied
        self.raw.update(raw)

        counts = collections.Counter()

        for name, texts in found.items():
            for text in texts:
                if not has_body(text):
                    self.without_body += 1
                    continue

                self.with_body += 1
                kind, markers = classify(text)
                self.by_class[kind] += 1
                counts[kind] += 1

                if copied:
                    self.substituted_bodies += 1
                else:
                    self.decompiled[kind] += 1

                carried = 0
                for marker, hits in markers.items():
                    if hits:
                        self.marker_bodies[marker] += 1
                        self.marker_hits[marker] += hits
                        carried += hits

                if carried:
                    counts['marked'] += 1
                    self.worst_methods.append((carried, relative, name,
                                               {k: v for k, v in markers.items() if v}))

                crashed = crash_of(text)
                if crashed:
                    self.crashes[crashed] += 1
                    self.crash_class[kind] += 1
                    counts['crash'] += 1
                    if kind != 'dead':
                        self.crashes_missed += 1

        if counts['dead'] or counts['marked']:
            self.per_file[relative] = {
                'assembly': assembly,
                'full': counts['full'], 'partial': counts['partial'], 'dead': counts['dead'],
                'marked': counts['marked'], 'crash': counts['crash'],
                'bodies': counts['full'] + counts['partial'] + counts['dead'],
                'score': counts['dead'] + counts['marked'],
            }


def percent(part, whole):
    return 100.0 * part / max(whole, 1)


def render(report, types, top, root, stream=sys.stdout):
    def say(line=''):
        print(line, file=stream)

    bodies = sum(report.by_class.values())
    honest = sum(report.decompiled.values())

    say('gamescore  %s' % root)
    say()
    say('TOTALS')
    say('  assemblies                   : %8d   (%s)' % (len(report.assemblies),
                                                         ', '.join(sorted(report.assemblies)) or 'none'))
    say('  files                        : %8d   (%d substituted original source, never decompiled)'
        % (report.files, report.substituted_files))
    say('  types                        : %8s' % ('?' if types is None else types))
    say('  members with a body          : %8d' % report.with_body)
    say('  members without a body       : %8d' % report.without_body)

    say()
    say('BODY CLASSIFICATION                 all bodies            decompiled only')
    for kind in ('full', 'partial', 'dead'):
        say('  %-28s : %6d (%5.1f%%)   %6d (%5.1f%%)'
            % (kind, report.by_class[kind], percent(report.by_class[kind], bodies),
               report.decompiled[kind], percent(report.decompiled[kind], honest)))
    say('  %-28s : %6d              %6d' % ('total', bodies, honest))
    say('  substituted (copied in, not recovered): %d bodies in %d files'
        % (report.substituted_bodies, report.substituted_files))

    total_crashes = sum(report.crashes.values())
    total_raw = sum(report.raw.values())
    say()
    say('RETHROWN GENERATOR CRASHES     : %8d   (%.2f%% of bodies)'
        % (total_crashes, percent(total_crashes, bodies)))
    if total_raw:
        for name, hits in report.raw.most_common():
            say('    %-52s %6d   (%d in a member)' % (name, hits, report.crashes[name]))
        say('  classify() rates them        : %s'
            % (', '.join('%s=%d' % (k, v) for k, v in report.crash_class.most_common()) or 'none'))
        if report.crashes_missed:
            say('  !! %d of them are NOT rated dead. markers.MARKERS["failed"] looks for'
                % report.crashes_missed)
            say('  !! "at Cpp2IL.Core.IlGenerator.GenerateIl" or "Decompilation failed"; a crash inside')
            say('  !! methodContext.Analyze() has neither, and scores as a whole method everywhere.')
        elif total_crashes:
            say('  every one of them is rated dead - the marker vocabulary is keeping up.')
        if total_raw != total_crashes:
            say('  !! %d crashed bodies are in the file text but in no member csharp.members returns:'
                % (total_raw - total_crashes))
            say('  !! csharp.KINDS has no event_declaration, so an add/remove accessor is invisible to')
            say('  !! every scorer here. Counted above, and in nothing else.')
    else:
        say('  none. The `failed` marker and this check both find nothing.')

    say()
    say('MARKER CENSUS                        bodies   %bodies   occurrences')
    for name in CENSUS_NAMES:
        carrying = report.marker_bodies[name]
        say('  %-28s : %6d   %6.2f%%   %8d'
            % (name, carrying, percent(carrying, bodies), report.marker_hits[name]))

    queue = sorted(report.per_file.items(), key=lambda item: (-item[1]['score'], item[0]))
    say()
    say('WORST FILES  (dead + marker-carrying bodies; %d files carry any)' % len(report.per_file))
    say('    %-58s %5s %5s %5s %6s' % ('file', 'cost', 'dead', 'mark', 'bodies'))
    for relative, entry in queue[:top]:
        say('    %-58s %5d %5d %5d %6d'
            % (relative[-58:], entry['score'], entry['dead'], entry['marked'], entry['bodies']))

    report.worst_methods.sort(key=lambda row: (-row[0], row[1], row[2]))
    say()
    say('WORST METHODS  (by marker count; %d methods carry any)' % len(report.worst_methods))
    for hits, relative, name, detail in report.worst_methods[:top]:
        where = '%s::%s' % (os.path.basename(relative), name)
        say('    %-52s %5d  %s' % (where[:52], hits,
                                   ', '.join('%s=%d' % kv for kv in sorted(detail.items()))))


def to_json(report, types, top, root):
    bodies = sum(report.by_class.values())
    honest = sum(report.decompiled.values())
    queue = sorted(report.per_file.items(), key=lambda item: (-item[1]['score'], item[0]))

    return {
        'tool': 'gamescore.py',
        'schema': 1,
        'root': root,
        'when': time.strftime('%Y-%m-%dT%H:%M:%S'),
        'totals': {
            'assemblies': len(report.assemblies),
            'assembly_names': sorted(report.assemblies),
            'files': report.files,
            'substituted_files': report.substituted_files,
            'types': types,
            'with_body': report.with_body,
            'without_body': report.without_body,
        },
        'classification': {
            'all': {kind: report.by_class[kind] for kind in ('full', 'partial', 'dead')} | {'total': bodies},
            'decompiled': ({kind: report.decompiled[kind] for kind in ('full', 'partial', 'dead')}
                           | {'total': honest}),
            'substituted_bodies': report.substituted_bodies,
        },
        'crashes': {
            'total': sum(report.crashes.values()),
            'by_exception': dict(report.crashes),
            'by_classification': dict(report.crash_class),
            'missed_by_markers': report.crashes_missed,
            # Over the raw file text, which sees the event accessors no member here covers.
            'raw_total': sum(report.raw.values()),
            'raw_by_exception': dict(report.raw),
        },
        'markers': {name: {'bodies': report.marker_bodies[name],
                           'occurrences': report.marker_hits[name],
                           'percent_of_bodies': round(percent(report.marker_bodies[name], bodies), 4)}
                    for name in CENSUS_NAMES},
        # Only the files that carry damage: the ones a round is trying to move. A file that becomes clean
        # drops out, and `gamescorediff.py` reads its absence as the improvement it is.
        'files': {relative: entry for relative, entry in queue},
        'worst_methods': [{'file': relative, 'member': name, 'markers': hits, 'detail': detail}
                          for hits, relative, name, detail in report.worst_methods[:max(top, 50)]],
    }


def main(argv=None):
    parser = argparse.ArgumentParser(description='Score an export with no original source beside it.')
    parser.add_argument('export', help='<export>/ExportedProject (or the export root, or Assets/Scripts)')
    parser.add_argument('--top', type=int, default=20, help='how many worst files and methods to list')
    parser.add_argument('--json', dest='json_path', help='write the same numbers as JSON')
    parser.add_argument('--jobs', type=int, default=min(8, (os.cpu_count() or 4)),
                        help='parser threads')
    parser.add_argument('--quiet', action='store_true', help='write only the JSON')
    arguments = parser.parse_args(argv)

    root = find_root(os.path.abspath(arguments.export))
    report = Report()

    seen = 0
    for assembly, path, copied, found, raw, modified in scan(root, arguments.jobs):
        report.newest = max(report.newest, modified)
        report.add_file(assembly, os.path.relpath(path, root), copied, found, raw)
        seen += 1
        # `csharp.members` caches 4096 files' worth of member text, which on a big export is most of the
        # tree held at once for no gain - every path is asked for exactly once.
        if seen % 512 == 0:
            members.cache_clear()
    members.cache_clear()

    types = count_types(root)

    if not arguments.quiet:
        render(report, types, arguments.top, root)

    # `il2cpp-score-only-a-finished-export`: a half-written body is shorter and cleaner, so an export still
    # being written scores *better*. The only cheap tell from outside the runner is that the tree is still
    # moving.
    age = time.time() - report.newest
    if age < 90:
        print('\n!! a file in this tree was written %d seconds ago - the export may still be running,'
              ' and a partly-written body scores better than a whole one. Wait for DONE.' % age,
              file=sys.stderr)

    if arguments.json_path:
        with open(arguments.json_path, 'w', encoding='utf-8') as handle:
            json.dump(to_json(report, types, arguments.top, root), handle, indent=1, sort_keys=True)
        if not arguments.quiet:
            print('\nwrote %s' % arguments.json_path)


if __name__ == '__main__':
    main()
