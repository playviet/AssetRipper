#!/usr/bin/env python3
"""What the `Unmanaged memory` markers are made of, by the SHAPE of the address, not its text.

    python3 unmanagedcensus.py <export>                 # ranked buckets, split by gamefilter tier
    python3 unmanagedcensus.py <export> --tier gameplay # only one tier, with example sites
    python3 unmanagedcensus.py <export> --bucket "load local untyped +small"   # every site in one bucket
    python3 unmanagedcensus.py <export> --methods       # rank the methods, not the buckets
    python3 unmanagedcensus.py <export> --json out.json
    python3 unmanagedcensus.py --selftest               # the parser, against no files

`<export>` is the export root or its `ExportedProject`; both are accepted (gamefilter.scripts_root).

WHAT THE MARKER IS. `IlGenerator.cs:855` (load) and `:1041` (store) write

    _ = "Unmanaged memory load: [v357 @ X0_v38+8]";

whenever an ISIL `MemoryOperand` reaches IL generation still a memory operand - i.e. no pass turned it
into a `FieldReference`, a `NestedFieldReference`, an array element, or a read through a by-ref parameter.
So the marker is never about the *instruction*; it is about the **address**, and the interesting question
is what the base of that address was when everything upstream had finished with it.

THE GRAMMAR, from `ISIL/MemoryOperand.ToString()`:

    [ base? (+|-)ADDEND_HEX? +index (*scale)? ]

with `base`/`index` printed by whatever object they are:

    LocalVariable   `name @ REG`  or  `name @ REG (Full.Type.Name)`      (ISIL/LocalVariable.cs:19)
    Register        `X8_v20`, `stackaddr_-D0`, `X8#pin0`                 (ISIL/Register.cs:46)
    a constant      bare hex, e.g. `[50D4000]`  - IsConstant, a static address

Note `stackaddr_0-8` is the register `stackaddr_0` with addend `-8`, and a nested type prints with a `+`
in its own name (`AssetLoader+<LoadFromAddressables>d__7`1`), so the operand CANNOT be split on `+`/`-`.
This parses left to right instead, longest-match on each part.

THE BUCKET is four words - `<load|store> <base kind> <type class> <offset class>` plus `[i]` when the
operand has an index register. Identifiers are normalised away, so

    [worldBounds @ X2 (UnityEngine.Bounds&)+8]   and   [bounds @ X3 (UnityEngine.Bounds&)+4]

fall in one bucket, `load byref-local byref +small`. The type is kept in the examples, never in the key.

  base kind   const     no base at all: a fixed address, static data or a hard-coded pointer
              this      the local is `this`
              param     a named local that is not `this` and not `vN` - a parameter or a named variable
              local     `vN`, a value analysis made up a name for
              reg       a bare register - no local was ever made for it
              stack     the base register is `stackaddr_N`: the frame, i.e. a stack slot's address

  type class  untyped   the base carries no type at all. This is the wall: nothing can name a field of a
                        value whose type is unknown, and the honest answer is a marker.
              byref     `Type&` - a struct reached through a reference
              runtime   `Il2CppClass<>`, `Il2CppStaticFields<>`, `Il2CppMethodRgctx<>`, `MethodInfo`, ...
                        a runtime structure, which has no managed field to name at all
              statemachine  a compiler-generated `<...>d__N` type
              array     `T[]`
              type      an ordinary managed type - a field SHOULD have been nameable here

  offset      +0, +small (<0x20), +mid (<0x100), +large, -neg

WHY THE TIER SPLIT. 93.1% of Snacky Dash's 167817 methods are Voodoo's SDK, Easy Save, Unity and the BCL
(`gamefilter.py`). A bucket that is large only in `sdk` is not the fix we want; the gameplay column is the
one that decides. `gamefilter` is imported, never re-implemented.
"""
import argparse
import collections
import json
import os
import re
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import gamefilter

MARKER = re.compile(r'Unmanaged memory (load|store): \[(.*?)\]"')

# A LocalVariable: `name @ REG` or `name @ REG (Type)`. The register may be `X8`, `X8_v20`, `X8#pin0`,
# `stackaddr_-D0`, `V0#4`. The type, when present, has no parentheses of its own.
REGNAME = r'(?:stack(?:addr)?_-?[0-9A-Fa-f]+|[A-Za-z][A-Za-z0-9]*(?:_v\d+)?(?:#\w+)?)'
LOCAL = re.compile(r'^(?P<name>[A-Za-z_@<][\w@<>.`]*) @ (?P<reg>' + REGNAME + r')'
                   r'(?: \((?P<type>[^()]*)\))?')
# A bare register, same spellings, no `name @` in front.
REG = re.compile(r'^(?P<reg>' + REGNAME + r')(?: \((?P<type>[^()]*)\))?')
ADDEND = re.compile(r'^(?P<sign>[+-])(?P<hex>[0-9A-F]+)')
CONST = re.compile(r'^(?P<hex>[0-9A-F]+)$')

RUNTIME_TYPES = ('Il2Cpp', 'MethodInfo', 'Il2CppClass', 'Il2CppStaticFields', 'Il2CppMethodRgctx',
                 'System.RuntimeTypeHandle', 'Token')


# ---------------------------------------------------------------------------------------------------------
# What an offset MAY name, when the base has no type.
#
# `il2cpp-an-offset-signature-names-nothing` settled that a MANAGED type cannot be recovered from the set of
# offsets read through a base: at N=2 the median is 711 candidate types, and even a base whose type is known
# matches its own layout only 42% of the time. That measurement also says what offsets CAN identify - the
# RUNTIME structures, by table lookup rather than by search, because their layout is fixed for the build.
# So this is a lookup, never a guess, and it labels only offsets no managed layout would put a field at.
#
# Offsets from `il2cpp-struct-layouts` (64-bit, metadata 31).
RUNTIME_FIELDS = {
    0xB0: 'Il2CppClass.interfaceOffsets',
    0xB8: 'Il2CppClass.static_fields',
    0xC0: 'Il2CppClass.rgctx_data',
    0xC8: 'Il2CppClass.typeHierarchy',
    0xFC: 'Il2CppClass.actualSize',
    0x12E: 'Il2CppClass.interface_offsets_count',
    0x130: 'Il2CppClass.typeHierarchyDepth',
    0x135: 'Il2CppClass.initialized_and_no_error',
}
VTABLE = 0x138


def runtime_shape(address):
    """The runtime structure an untyped base's offset names, or '' when nothing does.

    A vtable entry is the one that pays: `[klass + 0x188]` and `[klass + 0x190]` are the method pointer and
    the MethodInfo of vtable slot 5, and they always arrive as a PAIR - which is a virtual call nothing
    recovered, not a field read at all.
    """
    # A constant address is not a base at all, and an offset past any plausible vtable is an address that
    # happens to be divisible by 16 - both matched `Il2CppClass.vtable[9346540]` before this guard.
    if address.index or address.addend <= 0 or address.base_kind() == 'const' or address.addend > 0x8000:
        return ''
    if address.addend in RUNTIME_FIELDS:
        return RUNTIME_FIELDS[address.addend]
    if address.addend >= VTABLE and (address.addend - VTABLE) % 0x10 in (0, 8):
        which = 'method' if (address.addend - VTABLE) % 0x10 == 0 else 'MethodInfo'
        return 'Il2CppClass.vtable[%d].%s' % ((address.addend - VTABLE) // 0x10, which)
    return ''


class Address:
    """One parsed `[...]` operand."""

    def __init__(self, text):
        self.text = text
        self.base_name = self.base_reg = self.base_type = None
        self.addend = 0
        self.index = False
        self.scale = 0
        self.ok = self._parse(text)

    def _parse(self, text):
        rest = text
        if CONST.match(rest):
            self.base_reg = 'const'
            self.addend = int(rest, 16)
            return True

        found = LOCAL.match(rest)
        if found:
            self.base_name = found.group('name')
            self.base_reg = found.group('reg')
            self.base_type = found.group('type')
            rest = rest[found.end():]
        else:
            found = REG.match(rest)
            if not found:
                return False
            self.base_reg = found.group('reg')
            self.base_type = found.group('type')
            rest = rest[found.end():]

        found = ADDEND.match(rest)
        if found:
            self.addend = int(found.group('hex'), 16) * (-1 if found.group('sign') == '-' else 1)
            rest = rest[found.end():]

        if rest.startswith('+'):
            self.index = True
            rest = rest[1:]
            found = LOCAL.match(rest) or REG.match(rest)
            if found:
                rest = rest[found.end():]
            if rest.startswith('*'):
                self.scale = int(rest[1:])
                rest = ''
        return rest == ''

    # --- the four words -----------------------------------------------------------------------------

    def base_kind(self):
        if self.base_reg == 'const':
            return 'const'
        if self.base_reg and self.base_reg.startswith('stack'):
            return 'stack'
        if self.base_name == 'this':
            return 'this'
        if self.base_name is None:
            return 'reg'
        if re.fullmatch(r'v\d+', self.base_name):
            return 'local'
        return 'param'

    def type_class(self):
        kind = self.base_type
        if self.base_reg == 'const':
            return 'address'
        if not kind:
            return 'untyped'
        if kind.endswith('&'):
            return 'byref'
        if kind.endswith('[]'):
            return 'array'
        if any(kind.startswith(prefix) or ('.' + prefix) in kind for prefix in RUNTIME_TYPES):
            return 'runtime'
        if re.search(r'<[\w<>`]*>[cd]__|\+<', kind):
            return 'statemachine'
        return 'type'

    def offset_class(self):
        if self.base_reg == 'const':
            return ''
        if self.addend == 0:
            return '+0'
        if self.addend < 0:
            return '-neg'
        if self.addend < 0x20:
            return '+small'
        if self.addend < 0x100:
            return '+mid'
        return '+large'

    def bucket(self, operation):
        words = [operation, self.base_kind(), self.type_class(), self.offset_class()]
        key = ' '.join(word for word in words if word)
        return key + (' [i]' if self.index else '')


def selftest():
    cases = [
        ('v357 @ X0_v38+8', 'local', 'untyped', '+small', False),
        ('this @ X0 (CFramework.AssetLoader+<LoadFromAddressables>d__7`1)+28',
         'this', 'statemachine', '+mid', False),
        ('v36 @ stackaddr_-D0 (UnityEngine.UIVertex&)+50', 'stack', 'byref', '+mid', False),
        ('v23 @ stackaddr_0-8', 'stack', 'untyped', '-neg', False),
        ('50D4000', 'const', 'address', '', False),
        ('v857 @ stackaddr_-60+v854 @ X20_v22*8', 'stack', 'untyped', '+0', True),
        ('v327 @ X8#pin0+6C', 'local', 'untyped', '+mid', False),
        ('v78 @ X8_v6 (Il2CppStaticFields<Vector3>)+4', 'local', 'runtime', '+small', False),
        ('v52 (Il2CppClass<T>)+FC', 'reg', 'runtime', '+mid', False),
        ('worldBounds @ X2 (UnityEngine.Bounds&)+8', 'param', 'byref', '+small', False),
        ('stackaddr_-18+10', 'stack', 'untyped', '+small', False),
        ('v66 @ stack_-60 (UnityEngine.Component)+28', 'stack', 'type', '+mid', False),
        ('v397 @ X8_v20 (System.Int32[])+18', 'local', 'array', '+small', False),
    ]
    bad = 0
    for text, kind, klass, offset, index in cases:
        address = Address(text)
        got = (address.base_kind(), address.type_class(), address.offset_class(), address.index)
        if not address.ok or got != (kind, klass, offset, index):
            bad += 1
            print('FAIL %-60s parsed=%s got=%s want=%s' % (text, address.ok, got, (kind, klass, offset, index)))
    print('selftest: %d cases, %d failures' % (len(cases), bad))
    return 1 if bad else 0


# ---------------------------------------------------------------------------------------------------------


def method_index(path, text):
    """[(start_offset, member_name)] so a marker's offset can be attributed to the member holding it."""
    try:
        from csharp import members
    except ImportError:
        return []
    spans = []
    for name, texts in members(path).items():
        for body in texts:
            at = text.find(body)
            if at >= 0:
                spans.append((at, at + len(body), name))
    spans.sort()
    return spans


def member_at(spans, offset):
    holder = '?'
    for start, end, name in spans:
        if start <= offset < end:
            holder = name          # innermost wins: spans are sorted, a nested one comes later
    return holder


def scan(root, want_methods):
    """[(tier, bucket, file, member, text)] for every occurrence in the export."""
    sites = []
    for directory, _subdirectories, files in os.walk(root):
        for name in files:
            if not name.endswith('.cs'):
                continue
            path = os.path.join(directory, name)
            try:
                text = open(path, encoding='utf-8', errors='replace').read()
            except OSError:
                continue
            if 'Unmanaged memory' not in text:
                continue
            assembly, namespace, type_name = gamefilter.split_path(root, path)
            where = gamefilter.bucket(assembly, namespace, type_name)
            level = gamefilter.tier(assembly, namespace, type_name) or where
            spans = method_index(path, text) if want_methods else []
            for found in MARKER.finditer(text):
                operation, operand = found.group(1), found.group(2)
                address = Address(operand)
                key = address.bucket(operation) if address.ok else '%s UNPARSED' % operation
                member = member_at(spans, found.start()) if spans else '?'
                sites.append((level, key, os.path.relpath(path, root), member, operand))
    return sites


TIERS = ['gameplay', 'meta', 'sdk', 'unity', 'bcl']


def report(sites, only_tier=None, only_bucket=None, methods=False, limit=40):
    if only_tier:
        sites = [site for site in sites if site[0] == only_tier]
    if only_bucket:
        sites = [site for site in sites if site[1] == only_bucket]

    if only_bucket:
        print('SITES in bucket %r%s\n' % (only_bucket, ' (%s)' % only_tier if only_tier else ''))
        for level, _key, path, member, operand in sorted(sites)[:limit * 4]:
            print('  %-9s %-58s %-28s [%s]' % (level, path, member, operand))
        print('\n  %d occurrences' % len(sites))
        return

    if methods:
        counts = collections.Counter((level, path, member) for level, _k, path, member, _o in sites)
        print('METHODS ranked by occurrences%s\n' % (' (%s)' % only_tier if only_tier else ''))
        print('  %6s  %-9s %-58s %s' % ('occ', 'tier', 'file', 'member'))
        for (level, path, member), count in counts.most_common(limit):
            print('  %6d  %-9s %-58s %s' % (count, level, path, member))
        return

    totals = collections.Counter(level for level, _k, _p, _m, _o in sites)
    bodies = collections.defaultdict(set)
    per_bucket = collections.defaultdict(collections.Counter)
    for level, key, path, member, _operand in sites:
        per_bucket[key][level] += 1
        bodies[key].add((path, member))

    print('UNMANAGED CENSUS - %d occurrences' % len(sites))
    print('  by tier: ' + '  '.join('%s %d' % (level, totals.get(level, 0)) for level in TIERS
                                    if totals.get(level)))
    print()
    header = '  %8s %8s %8s %8s %8s %8s   %s' % ('gameplay', 'meta', 'sdk', 'unity', 'bcl', 'TOTAL', 'bucket')
    print(header)
    print('  ' + '-' * (len(header) - 2))
    ranked = sorted(per_bucket.items(),
                    key=lambda item: (-item[1].get('gameplay', 0), -sum(item[1].values())))
    for key, counts in ranked[:limit]:
        total = sum(counts.values())
        print('  %8d %8d %8d %8d %8d %8d   %s'
              % (counts.get('gameplay', 0), counts.get('meta', 0), counts.get('sdk', 0),
                 counts.get('unity', 0), counts.get('bcl', 0), total, key))
    if len(ranked) > limit:
        print('  ... %d more buckets' % (len(ranked) - limit))

    # The runtime lookup applies to a TYPED base too, and has to: the biggest bucket in Snacky Dash is
    # `[v565 @ X8_v18 (Cysharp.Threading.Tasks.UniTask)+12E]`, where 0x12E is `Il2CppClass`'s
    # interface_offsets_count and the type on the base is simply wrong. A managed struct has no field at
    # 0x12E, so the table still decides.
    untyped = list(sites)
    shapes = collections.Counter()
    for level, _key, _path, _member, operand in untyped:
        label = runtime_shape(Address(operand))
        shapes[(label or '(no runtime structure has a field there)', level)] += 1
    if shapes:
        print('\nWHAT THE OFFSET NAMES, by table lookup over every site (never by search:')
        print('see il2cpp-an-offset-signature-names-nothing)\n')
        by_label = collections.defaultdict(collections.Counter)
        for (label, level), count in shapes.items():
            by_label[label][level] += count
        for label, counts in sorted(by_label.items(), key=lambda item: -sum(item[1].values()))[:14]:
            print('  %6d  gameplay %-5d  %s' % (sum(counts.values()), counts.get('gameplay', 0), label))
        roll = collections.defaultdict(collections.Counter)
        for label, counts in by_label.items():
            if label.startswith('Il2CppClass.vtable'):
                roll['THE VIRTUAL CALL - a vtable entry, always a method/MethodInfo pair'] += counts
            elif label in ('Il2CppClass.interfaceOffsets', 'Il2CppClass.interface_offsets_count'):
                roll['THE INTERFACE WALK - 0xB0 and 0x12E (InterfaceCallRecovery)'] += counts
            elif label.startswith('Il2Cpp'):
                roll['another runtime structure of the class'] += counts
        print()
        for label, counts in sorted(roll.items(), key=lambda item: -item[1].get('gameplay', 0)):
            print('  %6d  gameplay %-5d  %s' % (sum(counts.values()), counts.get('gameplay', 0), label))

    print('\nTOP GAMEPLAY BUCKETS, with one example site each\n')
    gameplay = [item for item in ranked if item[1].get('gameplay')]
    for key, counts in gameplay[:10]:
        examples = [site for site in sites if site[1] == key and site[0] == 'gameplay']
        distinct = len({(path, member) for _l, _k, path, member, _o in examples})
        print('  %-46s %5d occ in %3d bodies' % (key, counts['gameplay'], distinct))
        for _level, _key, path, member, operand in examples[:3]:
            print('        %-52s %-26s [%s]' % (path, member, operand))


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('export', nargs='?')
    parser.add_argument('--tier', choices=TIERS)
    parser.add_argument('--bucket')
    parser.add_argument('--methods', action='store_true')
    parser.add_argument('--limit', type=int, default=40)
    parser.add_argument('--json')
    parser.add_argument('--no-members', action='store_true', help='skip ast-grep member attribution (fast)')
    parser.add_argument('--selftest', action='store_true')
    args = parser.parse_args()

    if args.selftest:
        return selftest()
    if not args.export:
        parser.error('an export is required')

    root = gamefilter.scripts_root(args.export)
    if not root:
        print('no Assets/Scripts under %s' % args.export, file=sys.stderr)
        return 2

    sites = scan(root, not args.no_members)
    if args.json:
        payload = {'root': root, 'occurrences': len(sites),
                   'sites': [{'tier': t, 'bucket': b, 'file': f, 'member': m, 'operand': o}
                             for t, b, f, m, o in sites]}
        json.dump(payload, open(args.json, 'w'), indent=1, sort_keys=True)
    report(sites, args.tier, args.bucket, args.methods, args.limit)
    return 0


if __name__ == '__main__':
    sys.exit(main())
