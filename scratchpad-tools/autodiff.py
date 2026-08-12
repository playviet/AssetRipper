#!/usr/bin/env python3
"""Run every method it can against the original, without being told which ones.

`difftest.py` needs a table: which file each side lives in, how to build an argument. That is fine for ten
methods picked by hand and useless for a corpus. This reads the original source, takes every static method
whose parameters it knows how to make a value of, and generates the comparison itself - so adding a shape to
the corpus is adding a method, and nothing else.

  autodiff.py <originalSource.cs> <recoveredSource.cs> [iterations]

Prints one line per method, and for each disagreement the first input it disagrees on.
"""
import os
import re
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import difftest  # noqa: E402

PROJECT = os.path.join(HERE, "autodiff")

# How to make a random value of each type the corpus uses. A parameter whose type is not here is a method
# this cannot judge, and it says so rather than guessing.
GENERATORS = {
    'int': 'rng.Next(-40, 140)',
    'long': '(long)rng.Next(-999, 999)',
    'float': '(float)(rng.NextDouble() * 8 - 4)',
    'double': 'rng.NextDouble() * 8 - 4',
    'bool': 'rng.Next(2) == 0',
    'string': 'Text(rng)',
    'string[]': 'rng.Next(5) == 0 ? null : new[] { Text(rng), Text(rng) }',
    'Colour': '(Colour)rng.Next(0, 8)',
    'Colour[]': 'Cells(rng)',
    'int[]': 'rng.Next(5) == 0 ? null : new[] { rng.Next(9), rng.Next(9), rng.Next(9) }',
    'int[][]': 'new[] { new[] { rng.Next(9), rng.Next(9) }, rng.Next(4) == 0 ? null : new[] { rng.Next(9) } }',
    'int[,]': 'new int[2, 2] { { rng.Next(9), rng.Next(9) }, { rng.Next(9), rng.Next(9) } }',
    'Pair': 'new Pair((float)rng.NextDouble(), (float)rng.NextDouble())',
    'Nested': 'new Nested { A = new Pair(1f, 2f), Count = rng.Next(5), B = new Pair(3f, 4f) }',
    'List<int>': 'new List<int> { rng.Next(9), rng.Next(9), rng.Next(9) }',
    'IList<int>': 'new List<int> { rng.Next(9), rng.Next(9), rng.Next(9) }',
    'IList<IShape>': 'new List<IShape> { new Square(), new Triangle() }',
    'IEnumerable<int>': 'new List<int> { rng.Next(9), rng.Next(9) }',
    'Predicate<int>': '(Predicate<int>)(n => n > 3)',
    'Dictionary<string, int>': 'rng.Next(5) == 0 ? null : new Dictionary<string, int> { { "a", rng.Next(4) } }',
}

HELPERS = """
    static string Text(Random rng)
    {
        switch (rng.Next(7))
        {
            case 0: return null;
            case 1: return "";
            case 2: return rng.Next(-99, 199).ToString();
            case 3: return (rng.NextDouble() * 20 - 10).ToString("0.##");
            case 4: return rng.Next(2) == 0 ? "true" : "false";
            case 5: return "  ";
            default: return "s" + rng.Next(30);
        }
    }
    static Colour[] Cells(Random rng)
    {
        if (rng.Next(6) == 0) return null;
        var c = new Colour[rng.Next(2) == 0 ? 4 : 6];
        for (int i = 0; i < c.Length; i++) c[i] = rng.Next(3) == 0 ? Colour.None : (Colour)rng.Next(1, 8);
        return c;
    }
"""

DECLARATION = re.compile(
    r'^\s*public\s+static\s+(?!class\b)([\w<>,\[\]\. ]+?)\s+(\w+)\s*(?:<[^(>]*>)?\s*\(([^)]*)\)', re.M)


def parameters(text):
    """The type of each parameter, or None if any of them cannot be made up."""
    text = text.strip()
    if not text:
        return []

    types = []
    depth, current = 0, ''
    for char in text + ',':
        if char == ',' and depth == 0:
            words = current.strip().rsplit(' ', 1)
            if len(words) != 2:
                return None
            kind = re.sub(r'\s+', ' ', words[0].strip())
            if kind.startswith(('out ', 'ref ', 'params ')):
                return None
            types.append(kind)
            current = ''
            continue
        depth += char in '<['
        depth -= char in '>]'
        current += char

    return types if all(t in GENERATORS for t in types) else None


def types(text):
    """Every type the corpus declares that is not the class holding the methods, however it is written."""
    found = []
    # Modifiers count: `public abstract class Shape` is as much a type the corpus declares as
    # `public class Square`, and dropping it left the harness referring to a type it never copied.
    for match in re.finditer(r'^public (?:abstract |sealed |partial |static |readonly |unsafe )*(?:enum|struct|interface|class)\b[^\n{]*', text, re.M):
        if 'static class' in match.group(0):
            continue
        opened = text.find('{', match.start())
        depth, i = 0, opened
        while i < len(text):
            depth += text[i] == '{'
            depth -= text[i] == '}'
            if depth == 0:
                break
            i += 1
        found.append(text[match.start():i + 1])
    return found


def whole_class(text, name):
    """The class as it stands, nested types and all.

    Taking one method at a time meant chasing what it calls, what it reads and - once recovered - the class
    its iterator was compiled into. The class is self-contained by construction, so there is nothing to chase.
    """
    match = re.search(rf'^[ \t]*(?:public|internal|\s)*static\s+class\s+{re.escape(name)}\b', text, re.M)
    if not match:
        return None

    depth, i = 0, text.index('{', match.end())
    while i < len(text):
        depth += text[i] == '{'
        depth -= text[i] == '}'
        if depth == 0:
            break
        i += 1
    return 'public static class ' + name + text[text.index('{', match.end()):i + 1]


def main():
    original_path, recovered_path = sys.argv[1], sys.argv[2]
    iterations = int(sys.argv[3]) if len(sys.argv) > 3 else 5000
    original_text = open(original_path, errors='ignore').read()

    recovered_text = open(recovered_path, errors='ignore').read()
    subject = re.search(r'static\s+class\s+(\w+)', original_text).group(1)

    ours = whole_class(original_text, subject)
    theirs = whole_class(recovered_text, subject)
    if ours is None or theirs is None:
        print(f"{subject} is not on the {'original' if ours is None else 'recovered'} side")
        return

    cases, skipped = [], []
    for match in DECLARATION.finditer(ours):
        name, arguments = match.group(2), parameters(match.group(3))
        if arguments is None:
            skipped.append(name)
            continue
        if not re.search(rf'\b{re.escape(name)}\s*\(', theirs):
            skipped.append(name + " (not exported)")
            continue
        cases.append((name, arguments))

    if not cases:
        print("nothing to compare - are both files there?")
        return

    checks = '\n'.join(f"""
        Check("{name}", rng => {{
            {' '.join(f'var a{i} = {GENERATORS[t]};' for i, t in enumerate(arguments))}
            object want, got;
            try {{ want = Orig.{subject}.{name}({', '.join(f'a{i}' for i in range(len(arguments)))}); }}
            catch (Exception e) {{ want = "!" + e.GetType().Name; }}
            try {{ got  = Rec .{subject}.{name}({', '.join(f'a{i}' for i in range(len(arguments)))}); }}
            catch (Exception e) {{ got  = "!" + e.GetType().Name; }}
            return (want, got, new object[] {{ {', '.join(f'a{i}' for i in range(len(arguments)))} }});
        }});""" for name, arguments in cases)

    # The corpus's own types come from the original file, so both sides agree on what a `Pair` is. `Shared`
    # is what `difftest.wrap` opens with; there is nothing to share here, but it has to exist.
    # The supporting types are built from the original, where a member il2cpp inlined is still private -
    # `Box._side` is read straight out of `Areas` because `Area()` was inlined into it, and the export widens
    # the declaration in `Box.cs`, a file this script never opens. Accessibility changes no answer, so widen
    # it here rather than teach the harness to read a second file.
    support = "public static class Shared { }\n" + '\n'.join(types(original_text))
    support = re.sub(r'\b(private|protected)\b(?!\s+(?:class|struct|interface|enum)\b)', 'internal', support)

    program = f"""
using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;

{support}

namespace Orig {{ using System; using System.Collections; using System.Collections.Generic;
using System.Diagnostics; using System.Runtime.CompilerServices; using System.Globalization;
using System.Text;
{ours}
}}

namespace Rec {{ using System; using System.Collections; using System.Collections.Generic;
using System.Diagnostics; using System.Runtime.CompilerServices; using System.Globalization;
using System.Text;
{theirs}
}}

public static class Program
{{
    static int agreed, differed;
{HELPERS}

    static string Describe(object v)
    {{
        if (v is null) return "null";
        if (v is Array a)
        {{
            var parts = new List<string>();
            foreach (var item in a) parts.Add(Describe(item));
            return "[" + string.Join(",", parts) + "]";
        }}
        if (v is IEnumerable e && !(v is string))
        {{
            // A recovered iterator can throw on the way round, which is an answer too.
            var parts = new List<string>();
            try {{ foreach (var item in e) parts.Add(Describe(item)); }}
            catch (Exception ex) {{ parts.Add("!" + ex.GetType().Name); }}
            return "{{" + string.Join(",", parts) + "}}";
        }}
        return Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture);
    }}

    static void Check(string name, Func<Random, (object, object, object[])> once)
    {{
        var rng = new Random(name.GetHashCode());

        for (int i = 0; i < {iterations}; i++)
        {{
            var (want, got, args) = once(rng);
            if (Describe(want) == Describe(got)) continue;

            differed++;
            var shown = new List<string>();
            foreach (var arg in args) shown.Add(Describe(arg));
            Console.WriteLine($"  DIFFERS  {{name,-22}}  ({{string.Join(", ", shown)}})  original {{Describe(want)}}, recovered {{Describe(got)}}");
            return;
        }}

        agreed++;
        Console.WriteLine($"  agrees   {{name}}");
    }}

    public static void Main()
    {{
        {checks}
        Console.WriteLine($"\\n{{agreed + differed}} methods run, {{agreed}} behave the same, {{differed}} do not");
    }}
}}
"""

    os.makedirs(PROJECT, exist_ok=True)
    with open(os.path.join(PROJECT, "Generated.cs"), "w") as handle:
        handle.write(program)
    with open(os.path.join(PROJECT, "autodiff.csproj"), "w") as handle:
        handle.write("""<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>disable</Nullable>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <AssemblyName>autodiff</AssemblyName>
    <NoWarn>CS0164;CS0168;CS0219;CS0162;CS8321;CS0429;CS0184</NoWarn>
  </PropertyGroup>
</Project>
""")

    environment = dict(os.environ, DOTNET_ROOT=os.path.expanduser("~/.dotnet"),
                       PATH=os.path.expanduser("~/.dotnet") + ":" + os.environ["PATH"])
    run = subprocess.run(["dotnet", "run", "-c", "Release", "--project", PROJECT],
                         capture_output=True, text=True, env=environment)
    print(run.stdout)
    if run.returncode != 0:
        print(run.stderr[-2500:] or run.stdout[-2500:])
    if skipped:
        print(f"\nnot judged ({len(skipped)}): {', '.join(skipped[:14])}")


if __name__ == '__main__':
    main()
