#!/usr/bin/env python3
"""What a recovered method body says the recovery could not do.

Every string here is one the generator itself writes - they were taken from the `Ldstr` operands in
`IlGenerator.cs` and `IlGenerator.Fork.cs` rather than guessed at - so the vocabulary is exactly the set of
things it gives up on, and a new one cannot appear without this file being out of date.
"""
import collections
import re

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
    # ...and the same thing one layer earlier: the *analysis* threw, and AssetRipper wrote the message out as
    # the whole method with no stack trace behind it - `throw new Exception("Decompilation failed: ...")`.
    # One statement, no marker of its own, and it scored as a whole method until this line was added.
    ('failed', ['at Cpp2IL.Core.IlGenerator.GenerateIl', 'Decompilation failed']),
    # The rest of the generator's own giving-up strings.
    ('unknown', ['Unknown operand: ', 'Unknown instruction:', 'Invalid instruction:',
                 'Store into unknown operand:', 'Stack shift:', 'Phi opcodes ']),
]


# The exceptions il2cpp's own inlined checks raise. A hand-written body throws `ArgumentException` or
# `InvalidOperationException`; it does not throw these, because the runtime raises them for it. So a body
# that throws one of these has a check in it, and a body that throws one *unconditionally* has a check whose
# subject the recovery decided was always bad - which is a recovery failure wearing a language construct.
RUNTIME_CHECKS = ('NullReferenceException', 'IndexOutOfRangeException', 'InvalidCastException',
                  'OutOfMemoryException', 'ArrayTypeMismatchException', 'ArgumentNullException',
                  'DivideByZeroException', 'OverflowException')

THROWS = re.compile(r'^throw new (%s)\s*\(' % '|'.join(RUNTIME_CHECKS))
DECLARATION = re.compile(r'^[A-Za-z_@][\w@<>,.\[\]? ]* [A-Za-z_@][\w@]* = ')
DIVERTS = ('if ', 'if(', 'while ', 'for ', 'foreach ', 'switch ', 'do', 'try', 'return', 'goto ',
           'break', 'continue', 'yield ', 'lock ', 'using ')

EXPRESSION_BODY = re.compile(r'=>\s*(?!\s*\{)', re.S)


def _arrow(text):
    """Where an expression body begins, for a member written `... => expression;`.

    A member whose whole body is one expression is written that way by the decompiler, and a member that
    recovers *completely* is the most likely to be. Reading only braces therefore stopped counting a body at
    the moment it became perfect: `public EAdsNetworkType NetworkType => EAdsNetworkType.AdMob;` dropped out
    of the score entirely, and ten of them read as ten lost methods.

    A lambda inside a braced body is also an arrow, so this only applies when there is no brace before it.
    """
    found = EXPRESSION_BODY.search(text)
    if not found:
        return -1
    opened = text.find('{')
    return found.start() if opened < 0 or found.start() < opened else -1


def body_of(text):
    """What is inside the outermost braces, or the expression of an expression-bodied member."""
    arrow = _arrow(text)
    if arrow >= 0:
        return text[arrow + 2:].rstrip().rstrip(';')
    opened = text.find('{')
    return text[opened + 1:].rstrip().rstrip('}') if opened >= 0 else ''


def has_body(text):
    return '{' in text or _arrow(text) >= 0


def statements(text):
    """The lines of the body that are neither blank, a brace, nor a comment."""
    return [line.strip() for line in body_of(text).split('\n')
            if line.strip() and not line.strip().startswith('//') and line.strip() not in ('{', '}')]


def top_level(text):
    """The body's statements that sit inside no nested block."""
    depth = 0
    for raw in body_of(text).split('\n'):
        line = raw.strip()
        if not line or line.startswith('//'):
            continue
        if line == '{':
            depth += 1
            continue
        if line == '}':
            depth -= 1
            continue
        if depth == 0:
            yield line
        depth += line.count('{') - line.count('}')


def always_throws(text):
    """Whether the body raises one of the runtime's own checks before it can do anything else.

    The check il2cpp inlines is `if (bad) throw`, and where the recovery could not say what was being
    checked it kept the throw and lost the condition - so the method's whole contribution became the
    exception. `GameStateLoading.InitRemoteConfig` is `this.Log(...); RemoteConfigManager.I.Init();` in the
    original and `throw new NullReferenceException();` here.

    This is the one failure with **no marker of its own**: the generator did not give up on anything, it
    wrote a statement the language is perfectly happy with. Without this every scorer counts such a body as
    a whole method, which is how two of them sat inside `full=601` unnoticed until they were read by hand.
    """
    for line in top_level(text):
        if THROWS.match(line):
            return True
        # A declaration cannot divert control, so a throw below one still throws first. Anything that can -
        # a branch, a loop, a return - means the throw beyond it is reachable rather than certain.
        if not DECLARATION.match(line) and line.startswith(DIVERTS):
            return False
    return False


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

    # And neither is one that throws a runtime check before it can do anything. It carries no marker, so it
    # has to be looked for rather than counted: `dead` where the throw is the whole body, `partial` where
    # real statements ran first - `InitConsent` gets three of its four lines right and then throws.
    if always_throws(text):
        found['thrown'] += 1
        return ('dead' if len(statements(text)) == 1 else 'partial'), found

    return ('partial' if sum(found.values()) else 'full'), found
