#!/usr/bin/env python3
"""Split a C# file into its members, by parsing it rather than by matching text.

**Rebuilt after the original was lost a second time.** The numbers it produces are checked against the
export the loss was noticed on, so a rebuild that splits differently is caught rather than believed - see
`il2cpp-the-scorers-were-lost-and-rebuilt`.

`members(path)` gives `{name: [source, ...]}` for every member a C# file declares. A name can appear more
than once: overloads, and a partial class split across nested types. `markers.has_body` is what decides
whether a member had anything to recover, so members with no body are returned too.

The splitting is `ast-grep`, which parses the language. A regular expression cannot: a method containing a
nested type, a lambda with braces in a string, or an attribute with a parenthesis all break it, and
`il2cpp-parse-csharp-do-not-match-it` records that replacing the regex splitter found nineteen whole
methods it had been hiding.
"""
import functools
import json
import re
import subprocess

# The declarations that are members with something to recover. A property is taken whole rather than by its
# accessors, so that `int X { get; set; }` is one member and not two.
KINDS = (
    'method_declaration',
    'constructor_declaration',
    'destructor_declaration',
    'operator_declaration',
    'conversion_operator_declaration',
    'property_declaration',
    'indexer_declaration',
    'local_function_statement',
)

RULES = 'id: member\nlanguage: csharp\nrule:\n  any:\n' + ''.join(f'    - kind: {kind}\n' for kind in KINDS)

# The name is the identifier the declaration is known by: the one before the parameter list for anything
# callable, and before the body for a property.
CALLABLE = re.compile(r'([A-Za-z_][A-Za-z0-9_]*)\s*(?:<[^<>()]*>)?\s*\(')
PROPERTY = re.compile(r'([A-Za-z_][A-Za-z0-9_]*)\s*(?:\{|=>)')
OPERATOR = re.compile(r'\boperator\s+(\S+)')

# `int X { get; private set; }` declares no code, so there is nothing in it to recover and it is not a
# member this counts - the same reason an interface method or an `abstract` declaration is not. Its
# accessors are generated, and the recovered file writes them out with real bodies, so counting it would
# ask for something the original never wrote.
AUTO_PROPERTY = re.compile(r'\{(?:\s*(?:private|protected|internal|public|init)?\s*(?:get|set|init)\s*;)+\s*\}\s*$')


def is_auto_property(text):
    return '=>' not in text.split('{')[0] and AUTO_PROPERTY.search(text.strip()) is not None


def without_attributes(text):
    """The declaration with its attribute lists removed.

    An attribute is a call too - `[IteratorStateMachine(typeof(...))]` - and the name is looked for by the
    parenthesis, so a member behind one was being named after its attribute. Every coroutine in the export
    carries that attribute and so every coroutine came out named `IteratorStateMachine`, which is why five
    methods that are plainly there were counted as missing. Brackets are balanced rather than matched to the
    first `]`, because `typeof(X[])` contains one.
    """
    text = text.lstrip()

    while text.startswith('['):
        depth = 0
        for at, character in enumerate(text):
            if character in '[(':
                depth += 1
            elif character in '])':
                depth -= 1
                if depth == 0:
                    text = text[at + 1:].lstrip()
                    break
        else:
            return text

    return text


def name_of(text):
    text = without_attributes(text)
    head = text.split('=>')[0] if '=>' in text.split('{')[0] else text.split('{')[0]

    if (found := OPERATOR.search(head)) is not None:
        return 'operator' + found.group(1)

    if (found := CALLABLE.search(head)) is not None:
        return found.group(1)

    #A property or an indexer, which has no parameter list to be found by.
    matches = PROPERTY.findall(text)
    return matches[0] if matches else None


@functools.lru_cache(maxsize=4096)
def members(path):
    try:
        output = subprocess.run(['ast-grep', 'scan', '--inline-rules', RULES, '--json=compact', path],
                                capture_output=True, text=True).stdout
        found = json.loads(output) if output.strip() else []
    except Exception:
        return {}

    #A member declared inside another - a local function, or a method of a nested type - is returned by
    #ast-grep alongside the one containing it. Keeping both would count its body twice, so anything whose
    #range lies inside another match is dropped.
    found.sort(key=lambda match: (match['range']['byteOffset']['start'],
                                  -match['range']['byteOffset']['end']))

    result = {}
    end = -1

    for match in found:
        span = match['range']['byteOffset']

        if span['start'] < end:
            continue

        end = span['end']

        if is_auto_property(match['text']):
            continue

        name = name_of(match['text'])

        if name:
            result.setdefault(name, []).append(match['text'])

    return result
