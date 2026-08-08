#!/usr/bin/env python3
"""What a recovered method body says the recovery could not do.

Every string here is one the generator itself writes - they were taken from the `Ldstr` operands in
`IlGenerator.cs` and `IlGenerator.Fork.cs` rather than guessed at - so the vocabulary is exactly the set of
things it gives up on, and a new one cannot appear without this file being out of date.
"""
import collections

MARKERS = [
    # AssetRipper could not write the statement down at all.
    ('commented', ['//AssetRipper: commented out']),
    # The generator could not say what a load or store reached.
    ('unmanaged', ['Unmanaged memory ']),
    # A call to an address no method table names.
    ('notfound', ['Method not found @']),
    # An arm64 instruction the lifter does not translate.
    ('notimpl', ['Not implemented instruction:']),
    # A call or jump through a register that nothing resolved.
    ('indirect', ['Indirect call:', 'Indirect jump:']),
    # A key function the generator has no instruction for.
    ('unknowncall', ['Unknown call target operand:']),
    # A struct handed to something that wanted one of its members.
    ('structvalue', ['Part of a struct used as a value:']),
    # The generator threw while building the body, and AssetRipper wrote the exception out as the method.
    # It has no marker of its own and exactly one statement, so without this it scores as a whole method -
    # which is how a change that broke 689 methods once read as an improvement on every other measure.
    ('failed', ['at Cpp2IL.Core.IlGenerator.GenerateIl']),
    # The rest of the generator's own giving-up strings.
    ('unknown', ['Unknown operand: ', 'Unknown instruction:', 'Invalid instruction:',
                 'Store into unknown operand:', 'Stack shift:', 'Phi opcodes ']),
]


def body_of(text):
    """What is inside the outermost braces, or '' for a declaration without a body."""
    opened = text.find('{')
    return text[opened + 1:].rstrip().rstrip('}') if opened >= 0 else ''


def has_body(text):
    return '{' in text


def statements(text):
    """The lines of the body that are neither blank, a brace, nor a comment."""
    return [line.strip() for line in body_of(text).split('\n')
            if line.strip() and not line.strip().startswith('//') and line.strip() not in ('{', '}')]


def count(text):
    found = collections.Counter()
    for name, needles in MARKERS:
        for needle in needles:
            found[name] += text.count(needle)
    return found


def classify(text):
    """dead where nothing survived, partial where the generator left a marker, full otherwise."""
    found = count(text)

    # A body the generator threw on is not a body at all, whatever it looks like.
    if found['failed'] or not statements(text):
        return 'dead', found

    return ('partial' if sum(found.values()) else 'full'), found
