#!/usr/bin/env python3
"""Cut a C# file into its members, by parsing it rather than by matching text.

Every scorer here started out splitting C# with regular expressions, and every one of them has been wrong in
a way that cost real time: a call to a method taken for the method itself, accessors and constructors and
lambdas not found at all (body coverage sat at 78% until they were special-cased one by one), a type
declaration written on one line not matched, marker strings counted as discarded comparisons.

`ast-grep` has a C# grammar, so the question "is this a method declaration" has an exact answer. This asks it
once per file and hands back what everything else needs.

    from csharp import members
    members(path)   # -> {name: [source of each member declared under that name]}

Falls back to returning nothing if ast-grep is missing, so a caller can keep its old path.
"""
import json
import os
import subprocess
import functools

HERE = os.path.dirname(os.path.abspath(__file__))
AST_GREP = os.path.join(HERE, "tools-venv", "bin", "ast-grep")

# What counts as "a member with a body worth scoring". Accessors are included because a property's getter is
# where its work is, and local functions because a lambda the compiler lifted is written back as one.
KINDS = [
    "method_declaration",
    "constructor_declaration",
    "destructor_declaration",
    "operator_declaration",
    "conversion_operator_declaration",
    "accessor_declaration",
    "local_function_statement",
]

RULE = """
id: members
language: csharp
rule:
  any:
%s
""" % '\n'.join(f"    - kind: {kind}" for kind in KINDS)


def available():
    return os.path.exists(AST_GREP)


@functools.lru_cache(maxsize=None)
def _rule_file():
    path = os.path.join(HERE, ".members.yml")
    with open(path, "w") as handle:
        handle.write(RULE)
    return path


def nodes(path):
    """Every member declaration in the file, as ast-grep reports it."""
    if not available():
        return []

    run = subprocess.run([AST_GREP, "scan", "--rule", _rule_file(), "--json", path],
                         capture_output=True, text=True)
    if run.returncode not in (0, 1) or not run.stdout.strip():
        return []

    try:
        return json.loads(run.stdout)
    except json.JSONDecodeError:
        return []


def name_of(text):
    """The identifier a member is declared under, read off the front of its own source.

    The parse says where the member is; the name is the last identifier before the parameter list, or - for an
    accessor, which has neither - the accessor keyword, which the caller pairs with its property.
    """
    head = text.split('{', 1)[0].split('=>', 1)[0]
    opened = head.find('(')
    if opened >= 0:
        head = head[:opened]

    words = [w for w in head.replace('\t', ' ').split() if w]
    if not words:
        return None

    last = words[-1]
    # A generic method carries its parameters in the name; the declaration does not.
    return last.split('<', 1)[0].strip()


def members(path):
    """name -> the source of every member declared under that name in this file."""
    found = {}
    for node in nodes(path):
        text = node.get("text", "")
        name = name_of(text)
        if not name:
            continue
        found.setdefault(name, []).append(text)
    return found


if __name__ == '__main__':
    import sys
    for name, bodies in sorted(members(sys.argv[1]).items()):
        print(f"{len(bodies):3}  {name}")
