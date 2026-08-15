#!/usr/bin/env python3
"""Score every recovered file against its original across ALL the ground truth, not just the 96.

    python3 allscore.py <export>/Assets/Scripts/Assembly-CSharp [root ...]

Default roots are CF, CFramework and Common under /Users/playviet/Documents/_BZ/game-hub/Assets/AAA.

Everything that decides a number - `scorable`, `not_in_the_build`, `compiles_to_nothing`, `types_declared`,
`classify` - is copied verbatim from `cfscore.py` so the totals stay on the same scale. Two things are new,
and both exist because they are silent drops in `cfscore` that only show up once more than one tree is in
play:

* `index()` in cfscore keys the export by **basename** with `setdefault`, so when two exported files share a
  name - `CF/GameManager.cs` and `Common/GameManager.cs`, twelve such pairs - whichever os.walk reached
  first won. Here the candidates are kept and chosen between by the original's **namespace**, which is what
  AssetRipper names the containing folder after.
* cfscore iterates `index(ORIGIN)`, i.e. unique basenames, so an original whose basename is already taken is
  never scored at all. Here every original path is scored and the collisions are reported.
"""
import collections
import json
import os
import re
import sys

sys.path.insert(0, '/Users/playviet/Documents/_BZ/AssetRipper/scratchpad-tools')
from csharp import members, name_of, without_attributes  # noqa: E402
from markers import classify, has_body, statements      # noqa: E402

AAA = '/Users/playviet/Documents/_BZ/game-hub/Assets/AAA'

# ---------------------------------------------------------------- verbatim from cfscore.py

EDITOR_ONLY = re.compile(r'^\s*#(if|elif|else|endif)\b(.*)$')


def not_in_the_build(path):
    names = set()
    depth = []
    for line in open(path, encoding='utf-8', errors='replace'):
        directive = EDITOR_ONLY.match(line)
        if directive:
            kind, rest = directive.group(1), directive.group(2)
            if kind == 'if':
                depth.append('UNITY_EDITOR' in rest)
            elif kind in ('elif', 'else') and depth:
                depth[-1] = False
            elif kind == 'endif' and depth:
                depth.pop()
            continue
        if any(depth) and (found := name_of(line)):
            names.add(found)
    return names


EMPTY_IF = re.compile(r'^if\s*\(.*\)$')


def compiles_to_nothing(source):
    lines = statements(source)
    return not lines or all(EMPTY_IF.match(line) for line in lines)


#How many member names carried more than one body, and how many bodies were dropped by keeping only the
#first. This is the third silent drop in cfscore and the largest of them: `scorable` keys by NAME, so a
#set of overloads collapses to one entry and the rest are never scored at all. Pairing them by order is a
#guess and would report a false `partial` wherever the order differs between original and export, so this
#counts them and leaves them out rather than pretending - the number is the size of the unknown, and it
#belongs in the output of every run instead of in a memory nobody reads.
overloads_seen = collections.Counter()
#How many members the type spelling failed to pair and `reconcile` had to fall back to arity for. A
#number that grows means the two sides have started spelling a type differently and `SAME_TYPE` owes
#it a row; it is not an error, only a mispairing risk inside one overload group.
reconciled = collections.Counter()


def arity_of(text):
    """How many parameters the declaration takes, or None where it has no parameter list at all.

    A property and an indexer have none, so they keep their bare name as a key. Everything else is keyed by
    name **and** parameter count, which is what lets an overload group be scored member by member instead of
    collapsing to its first body. Arity rather than the parameter types: the export writes a type name
    differently from the original often enough (a shared generic's `T` arrives as `object`, a type is
    qualified on one side and not the other) that matching on types would report a false `missing` where the
    body is plainly there. Two overloads of the same arity and different types are the residue, and they are
    counted rather than guessed at.
    """
    head = without_attributes(text)
    head = head.split('=>')[0] if '=>' in head.split('{')[0] else head.split('{')[0]

    start = head.find('(')
    if start < 0:
        return None

    depth, end = 0, None

    for i in range(start, len(head)):
        if head[i] == '(':
            depth += 1
        elif head[i] == ')':
            depth -= 1
            if depth == 0:
                end = i
                break

    if end is None:
        return None

    inner = head[start + 1:end].strip()

    if not inner:
        return 0

    #A comma inside `Dictionary<K, V>` or `int[,]` separates nothing.
    count, angle, square = 1, 0, 0

    for ch in inner:
        if ch == '<':
            angle += 1
        elif ch == '>':
            angle -= 1
        elif ch == '[':
            square += 1
        elif ch == ']':
            square -= 1
        elif ch == ',' and angle == 0 and square == 0:
            count += 1

    return count


PARAMETER_KEYWORD = re.compile(r'\b(?:this|ref|out|in|params|readonly|scoped)\b\s+')

#What the export calls a type where the original calls it something else. Only the spellings that
#actually differ - the export writes the CLR name for a primitive and qualifies a namespace.
SAME_TYPE = {'int32': 'int', 'single': 'float', 'boolean': 'bool', 'int64': 'long', 'uint32': 'uint',
             'uint64': 'ulong', 'int16': 'short', 'uint16': 'ushort', 'sbyte': 'sbyte',
             'double': 'double', 'char': 'char', 'string': 'string', 'object': 'object'}


def parameter_types(text):
    """The parameter types of a declaration, normalised until the two sides agree on spelling.

    Used ONLY to tell two overloads of one arity apart - never to decide whether a member is present.
    See `reconcile`: a key that fails to match falls back to its arity, so a spelling this misses
    costs a mispairing inside a group and can never report a body that is there as `missing`.
    """
    head = without_attributes(text)
    head = head.split('=>')[0] if '=>' in head.split('{')[0] else head.split('{')[0]

    start = head.find('(')
    if start < 0:
        return ()

    depth, end = 0, None
    for i in range(start, len(head)):
        if head[i] == '(':
            depth += 1
        elif head[i] == ')':
            depth -= 1
            if depth == 0:
                end = i
                break
    if end is None:
        return ()

    inner = head[start + 1:end].strip()
    if not inner:
        return ()

    parts, angle, square, current = [], 0, 0, ''
    for ch in inner:
        if ch == '<':
            angle += 1
        elif ch == '>':
            angle -= 1
        elif ch == '[':
            square += 1
        elif ch == ']':
            square -= 1
        if ch == ',' and angle == 0 and square == 0:
            parts.append(current)
            current = ''
        else:
            current += ch
    parts.append(current)

    found = []
    for part in parts:
        part = PARAMETER_KEYWORD.sub('', part.split('=')[0].strip()).strip()
        #`string message` -> `string`; a parameter with no name is already just its type.
        split = part.rsplit(None, 1)
        name = (split[0] if len(split) == 2 else part).split('.')[-1]
        name = re.sub(r'\s+', '', name).lower()
        found.append(SAME_TYPE.get(name, name))
    return tuple(found)


def key_of(name, text):
    """The key a member is scored under - its name, its arity, and the types that fill it.

    Arity alone collapsed every same-arity overload group onto one body. The types separate them,
    and they do it exactly: measured over the 52 in-scope groups in this game, all 52 paired
    body-for-body with no partial and no miss. The safety is not in the matching though - it is in
    `reconcile`, which drops back to the arity key when a spelling disagrees.
    """
    arity = arity_of(text)
    if arity is None:
        return name
    return f'{name}#{arity}#{",".join(parameter_types(text))}'


def arity_key(key):
    """The `name#arity` prefix of a full key - what two overloads of one arity share."""
    parts = key.split('#')
    return '#'.join(parts[:2]) if len(parts) > 2 else key


def reconcile(wanted, got, source_of):
    """Pair by arity whatever the type spelling failed to pair.

    The group is already known to match: `name#arity` is the same on both sides. All that is in
    question is which body goes with which, so an unmatched pair is assigned positionally within its
    own group. This is why keying on types cannot cost a false `missing` - the worst it can do is
    pair two members of one group the wrong way round, which is what the arity key did to ALL of them.
    """
    spare = collections.defaultdict(list)
    for key in got:
        if key not in wanted:
            spare[arity_key(key)].append(key)

    for key in wanted:
        if key in got:
            continue
        candidates = spare.get(arity_key(key))
        if candidates:
            stand_in = candidates.pop(0)
            got[key] = got[stand_in]
            if stand_in in source_of:
                source_of[key] = source_of[stand_in]
            reconciled['by arity'] += 1

    return got, source_of


def scorable(path, skip=()):
    """key -> source, for every member of this file that has a body.

    Keyed by signature rather than by name. `cfscore` keys by name and keeps `texts[0]`, which silently drops
    every overload after the first - **653 bodies, 328 groups, about 29% of the denominator**. They were not
    scored badly; they were not scored at all.
    """
    found = {}

    for name, texts in members(path).items():
        if name in skip:
            continue

        with_bodies = [t for t in texts if has_body(t)]

        if not with_bodies:
            continue

        #Two overloads of one name AND one arity - `Log(string, object)` beside `Log(string, Exception)`.
        #Pairing those by POSITION was built and measured: scorable 2267 -> 2425, but `missing` 62 -> 149,
        #so 87 of the 158 it added came back unmatched - a 55% miss rate against 6.6% overall. The export does
        #not rename overloads (no `_1` suffixes anywhere), so they are not mis-paired names; the likeliest
        #reading is that il2cpp's managed linker **stripped the unused ones**, which makes them out of scope
        #like editor code rather than missing. Telling "stripped" from "not recovered" is a question for the
        #metadata, not for matching text, so the residue is counted here and left out until it can be asked
        #properly. Inflating `missing` with code that is not in the binary is the same mistake the `readonly`
        #keyword fix made.
        for text in with_bodies:
            key = key_of(name, text)

            if key in found:
                overloads_seen['ambiguous'] += 1
                continue

            found[key] = text

    return found


#`readonly`, `ref` and `unsafe` are missing from this list, so `public readonly struct GameHubDecision`
#declares nothing as far as it is concerned. Adding them WAS TRIED and reverted: scorable went 2190 -> 2251
#and `missing` 4 -> 58, and the 61 members it exposed are almost all editor and third-party code
#(`Assets/Scripts/Editor/Runtime/BuildPreprocessor`, `SerializableDictionaryLite/Editor/`, `Rotary Heart/
#PhysicsDebugExtension`, `NiceVibrations`) that is not in the player build at all. The keyword list is
#genuinely wrong, but fixing it without also fixing the scope filter makes the headline number *worse*
#rather than more honest - `not_in_the_build` only understands `#if UNITY_EDITOR`, not an `Editor/`
#directory nested under `Assets/Scripts/`. Fix the filter first, then this, and diff per file both times.
DECLARED = re.compile(r'^\s*(?:\[[^\]]*\]\s*)*'
                      r'(?:public|internal|private|protected|abstract|sealed|static|partial|readonly|ref|unsafe|\s)*'
                      r'(?:class|struct|interface|enum|record)\s+(\w+)')


def types_declared(path):
    found = []
    for line in open(path, encoding='utf-8', errors='replace'):
        match = DECLARED.match(line)
        if match:
            found.append(match.group(1))
    return found


# ---------------------------------------------------------------- new: namespace-aware pairing

NAMESPACE = re.compile(r'^\s*namespace\s+([A-Za-z_][\w.]*)')


def namespace_of(path):
    for line in open(path, encoding='utf-8', errors='replace'):
        found = NAMESPACE.match(line)
        if found:
            return found.group(1)
    return ''


def index_multi(root):
    """basename -> [path, ...] rather than cfscore's basename -> first path."""
    found = collections.defaultdict(list)
    for directory, _sub, files in os.walk(root):
        for name in files:
            if name.endswith('.cs'):
                found[name].append(os.path.join(directory, name))
    return found


def pick(candidates, namespace, export_root):
    """Which of several same-named exported files is the one this original's namespace names.

    AssetRipper writes a type into a folder named after its namespace, so `Common.GameManager` is
    `Common/GameManager.cs` and `CF.GameManager` is `CF/GameManager.cs`. Returns (path, ambiguous).
    """
    if not candidates:
        return None, False
    if len(candidates) == 1:
        return candidates[0], False

    wanted = namespace.replace('.', os.sep)
    for path in candidates:
        relative = os.path.dirname(os.path.relpath(path, export_root))
        if relative == wanted:
            return path, False
    if not namespace:
        for path in candidates:
            if os.path.dirname(os.path.relpath(path, export_root)) == '':
                return path, False
    # Last resort: the deepest folder matching the namespace's last segment.
    tail = namespace.split('.')[-1] if namespace else ''
    for path in candidates:
        if tail and os.path.basename(os.path.dirname(path)) == tail:
            return path, False
    return candidates[0], True


def recovered_members(basename, path, recovered, export_root):
    """cfscore.recovered_members, with the candidate chosen by namespace instead of by walk order."""
    namespace = namespace_of(path)
    got, source_of, ambiguous = {}, {}, False

    chosen, unsure = pick(recovered.get(basename, []), namespace, export_root)
    ambiguous |= unsure
    if chosen:
        for member, text in scorable(chosen).items():
            got[member] = text
            source_of[member] = chosen

    for declared in types_declared(path):
        sibling = declared + '.cs'
        if sibling == basename:
            continue
        other, unsure = pick(recovered.get(sibling, []), namespace, export_root)
        ambiguous |= unsure
        if other:
            for member, text in scorable(other).items():
                got.setdefault(member, text)
                source_of.setdefault(member, other)

    return got, source_of, chosen, ambiguous


# ---------------------------------------------------------------- new: is this file even in the build

def asmdefs(root):
    found = []
    for directory, _sub, files in os.walk(root):
        for name in files:
            if name.endswith('.asmdef'):
                found.append(directory)
    return found


def in_the_player_assembly(path, asmdef_dirs):
    """Whether this original compiles into Assembly-CSharp for the player.

    Three ways it does not, and none of them is a recovery failure:
      * a path segment named `Editor` - Assembly-CSharp-Editor, absent from a player build;
      * a file under an `.asmdef`, which is its own DLL and this export has only Assembly-CSharp;
      * a `.Tests` folder, likewise.
    """
    parts = path.split(os.sep)
    if 'Editor' in parts:
        return False, 'editor'
    for directory in asmdef_dirs:
        if path.startswith(directory + os.sep):
            return False, 'asmdef'
    return True, ''


# ---------------------------------------------------------------- new: substituted original source

XMLDOC = re.compile(r'^\s*///')
REGION = re.compile(r'^\s*#region\b')


def substituted(path):
    """Whether an exported file is original source AssetRipper copied in rather than decompiled output.

    XML doc comments cannot survive IL, so a file carrying one was never decompiled. 110 such files are
    known: EnhancedScrollerDemos/, EnhancedUI/, DG/, SoftMasking/, Dreamteck/.

    **A default parameter value is not evidence** - it is a `Constant` on the parameter in the metadata and
    comes back through decompilation intact. Testing for one flagged twelve plainly-recovered CF files
    (`SubCellVisual.PlaySpawn(float delay = 0f)`) and cost 148 members out of the CF total.
    """
    text = open(path, encoding='utf-8', errors='replace').read()
    lines = text.split('\n')
    return any(XMLDOC.match(line) for line in lines) or any(REGION.match(line) for line in lines)


# ----------------------------------------------------------------

def group_of(path):
    relative = os.path.relpath(path, AAA)
    return relative.split(os.sep)[0]


def main():
    if len(sys.argv) < 2:
        raise SystemExit('usage: allscore.py <export>/Assets/Scripts/Assembly-CSharp [root ...]')

    export_root = sys.argv[1].rstrip('/')
    roots = sys.argv[2:] or [os.path.join(AAA, name) for name in ('CF', 'CFramework', 'Common')]

    recovered = index_multi(export_root)
    asmdef_dirs = [directory for root in roots for directory in asmdefs(root)]

    originals = []
    for root in roots:
        for directory, _sub, files in os.walk(root):
            for name in sorted(files):
                if name.endswith('.cs'):
                    originals.append(os.path.join(directory, name))
    originals.sort()

    seen_basename = {}
    shadowed = []
    rows = []
    pairs = []
    totals = collections.Counter()
    per_group = collections.defaultdict(collections.Counter)
    per_group_markers = collections.defaultdict(collections.Counter)
    markers_total = collections.Counter()

    for path in originals:
        basename = os.path.basename(path)
        group = group_of(path)
        buildable, why = in_the_player_assembly(path, asmdef_dirs)

        if basename in seen_basename:
            shadowed.append((path, seen_basename[basename]))
        else:
            seen_basename[basename] = path

        wanted = scorable(path, not_in_the_build(path))
        got, source_of, chosen, ambiguous = recovered_members(basename, path, recovered, export_root)
        got, source_of = reconcile(wanted, got, source_of)

        is_substituted = any(substituted(where) for where in set(source_of.values()))

        counts = collections.Counter()
        for method in sorted(wanted):
            record = {'file': basename, 'group': group, 'origpath': path, 'member': method,
                      'buildable': buildable, 'why': why, 'substituted': is_substituted,
                      'recpath': source_of.get(method)}
            if method not in got:
                counts['missing'] += 1
                record['kind'] = 'missing'
                pairs.append(record)
                continue
            kind, found = classify(got[method])
            if kind == 'dead' and not found['failed'] and compiles_to_nothing(wanted[method]):
                kind = 'full'
            counts[kind] += 1
            record['kind'] = kind
            record['markers'] = {k: v for k, v in found.items() if v}
            pairs.append(record)
            if buildable and not is_substituted:
                markers_total.update(found)
                per_group_markers[group].update(found)

        rows.append({'file': basename, 'path': path, 'group': group, 'buildable': buildable, 'why': why,
                     'substituted': is_substituted, 'paired': bool(chosen) or bool(got),
                     'samename': bool(chosen), 'viasibling': not chosen and bool(got),
                     'recfiles': sorted(set(source_of.values())), 'ambiguous': ambiguous,
                     'wanted': len(wanted),
                     'full': counts['full'], 'partial': counts['partial'], 'dead': counts['dead'],
                     'missing': counts['missing']})

        if buildable and not is_substituted:
            totals.update(counts)
            per_group[group].update(counts)

    out = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'allscore.json')
    json.dump({'rows': rows, 'pairs': pairs}, open(out, 'w'))

    def show(label, counter):
        n = sum(counter[k] for k in ('full', 'partial', 'dead', 'missing'))
        if not n:
            print('%-14s  (nothing scorable)' % label)
            return
        print('%-14s scorable=%-6d full=%-6d partial=%-5d dead=%-5d missing=%-5d  full%%=%.1f'
              % (label, n, counter['full'], counter['partial'], counter['dead'], counter['missing'],
                 100.0 * counter['full'] / n))

    print('=== in the player Assembly-CSharp, decompiled (not substituted source) ===')
    for group in ('CF', 'CFramework', 'Common'):
        show(group, per_group[group])
    show('ALL', totals)
    print()
    print('markers: ' + ', '.join(f'{k}={v}' for k, v in markers_total.most_common() if v))
    for group in ('CF', 'CFramework', 'Common'):
        line = ', '.join(f'{k}={v}' for k, v in per_group_markers[group].most_common() if v)
        print('  %-11s %s' % (group, line))
    print()
    print('files: %d originals, %d paired to an exported file' % (len(rows), sum(1 for r in rows if r['paired'])))
    print('  not in Assembly-CSharp player: %d (editor %d, asmdef %d)'
          % (sum(1 for r in rows if not r['buildable']),
             sum(1 for r in rows if r['why'] == 'editor'), sum(1 for r in rows if r['why'] == 'asmdef')))
    print('  substituted original source:   %d' % sum(1 for r in rows if r['substituted']))
    print('  unpaired but buildable:        %d' % sum(1 for r in rows if r['buildable'] and not r['paired']))
    print('  ambiguous basename choice:     %d' % sum(1 for r in rows if r['ambiguous']))
    print('  overloads scored by full signature; still ambiguous: %d (reconciled by arity: %d)'
          % (overloads_seen['ambiguous'], reconciled['by arity']))
    #`missing` splits into two facts that read very differently. A member of a type the export never
    #wrote at all is a type the managed linker removed - DOTweenTk2d and DOTweenModuleEPOOutline are
    #35 of them, third-party shortcuts for packages this game does not use. A member absent from a
    #type that IS there is the only half that could be a recovery failure.
    orphaned = sum(1 for p in pairs if p['kind'] == 'missing' and p['buildable']
                   and not p['substituted'] and not p['recpath']
                   and not any(r['file'] == p['file'] and r['recfiles'] for r in rows))
    print('  missing: %d, of which %d are in a type the export never wrote (linker-removed)'
          % (totals['missing'], orphaned))
    print('  shadowed basenames (cfscore would have dropped these): %d' % len(shadowed))
    for path, first in shadowed:
        print('     %s  <- shadowed by %s' % (os.path.relpath(path, AAA), os.path.relpath(first, AAA)))
    print()
    print('wrote', out)


if __name__ == '__main__':
    main()
