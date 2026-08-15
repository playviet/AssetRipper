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
    'Pair[]': 'Pairs(rng)',
    'Triple': 'new Triple(rng.Next(-9, 9), rng.Next(-9, 9), rng.Next(-9, 9))',
    'Quad': 'new Quad((float)rng.NextDouble(), (float)rng.NextDouble(), (float)rng.NextDouble(), (float)rng.NextDouble())',
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
"""

# A helper that names a type the corpus declares. Emitted unconditionally, one of these makes the harness
# fail to compile for any corpus that happens not to declare that type - which reads exactly like the
# recovered side not compiling, and is not. Each is emitted only when its type is actually there.
CORPUS_HELPERS = {
    'Colour': """
    static Colour[] Cells(Random rng)
    {
        if (rng.Next(6) == 0) return null;
        var c = new Colour[rng.Next(2) == 0 ? 4 : 6];
        for (int i = 0; i < c.Length; i++) c[i] = rng.Next(3) == 0 ? Colour.None : (Colour)rng.Next(1, 8);
        return c;
    }
""",
    'Pair': """
    static Pair[] Pairs(Random rng)
    {
        if (rng.Next(6) == 0) return null;
        var p = new Pair[rng.Next(2) == 0 ? 3 : 5];
        for (int i = 0; i < p.Length; i++) p[i] = new Pair(rng.Next(-9, 9), rng.Next(-9, 9));
        return p;
    }
""",
}

# Recovery names an intrinsic back to the engine method that spells it - `FSQRT` becomes `Mathf.Sqrt`, and
# that is the RIGHT answer for Unity code. The harness is a plain console app, so without this the correct
# recovery of every method using one fails to compile and is scored as broken. A shim, not a reference to the
# engine: it keeps the harness a two-file build and it cannot drift with the editor version.
UNITY_SHIM = """
public static class Mathf
{
    public const float PI = 3.14159274f;
    public const float Infinity = float.PositiveInfinity;
    public const float NegativeInfinity = float.NegativeInfinity;
    public const float Epsilon = 1.401298E-45f;
    public const float Deg2Rad = 0.0174532924f;
    public const float Rad2Deg = 57.29578f;
    public static float Sqrt(float f) => (float)System.Math.Sqrt(f);
    public static float Abs(float f) => System.Math.Abs(f);
    public static int Abs(int f) => System.Math.Abs(f);
    public static float Floor(float f) => (float)System.Math.Floor(f);
    public static float Ceil(float f) => (float)System.Math.Ceiling(f);
    public static float Round(float f) => (float)System.Math.Round(f);
    public static int FloorToInt(float f) => (int)System.Math.Floor(f);
    public static int CeilToInt(float f) => (int)System.Math.Ceiling(f);
    public static int RoundToInt(float f) => (int)System.Math.Round(f);
    public static float Sin(float f) => (float)System.Math.Sin(f);
    public static float Cos(float f) => (float)System.Math.Cos(f);
    public static float Tan(float f) => (float)System.Math.Tan(f);
    public static float Asin(float f) => (float)System.Math.Asin(f);
    public static float Acos(float f) => (float)System.Math.Acos(f);
    public static float Atan(float f) => (float)System.Math.Atan(f);
    public static float Atan2(float y, float x) => (float)System.Math.Atan2(y, x);
    public static float Pow(float f, float p) => (float)System.Math.Pow(f, p);
    public static float Exp(float f) => (float)System.Math.Exp(f);
    public static float Log(float f) => (float)System.Math.Log(f);
    public static float Log(float f, float b) => (float)System.Math.Log(f, b);
    public static float Log10(float f) => (float)System.Math.Log10(f);
    public static float Min(float a, float b) => a < b ? a : b;
    public static int Min(int a, int b) => a < b ? a : b;
    public static float Max(float a, float b) => a > b ? a : b;
    public static int Max(int a, int b) => a > b ? a : b;
    public static float Sign(float f) => f >= 0f ? 1f : -1f;
    public static float Clamp(float v, float lo, float hi) => v < lo ? lo : (v > hi ? hi : v);
    public static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);
    public static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);
    public static float Lerp(float a, float b, float t) => a + (b - a) * Clamp01(t);
    public static float LerpUnclamped(float a, float b, float t) => a + (b - a) * t;
    public static float Repeat(float t, float length) => Clamp(t - Floor(t / length) * length, 0f, length);
    public static float MoveTowards(float a, float b, float d) => Abs(b - a) <= d ? b : a + Sign(b - a) * d;
    public static bool Approximately(float a, float b) => Abs(b - a) < Max(1E-06f * Max(Abs(a), Abs(b)), Epsilon * 8f);
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


def skip_literal(text, i):
    """Past a comment or a string/char literal starting at i, or i itself if there is none.

    Naive brace matching is enough for a hand-written corpus and is not enough for recovered code, which is
    full of `"{"` and `'}'`. Getting this wrong splits the class in the wrong place and then blames the wrong
    method for a compile error.
    """
    two = text[i:i + 2]
    if two == '//':
        end = text.find('\n', i)
        return len(text) if end < 0 else end
    if two == '/*':
        end = text.find('*/', i + 2)
        return len(text) if end < 0 else end + 2
    if two in ('@"', '$@') or text[i:i + 3] == '$@"':
        quote = text.find('"', i)
        j = quote + 1
        while j < len(text):
            if text[j] == '"':
                if text[j + 1:j + 2] == '"':
                    j += 2
                    continue
                return j + 1
            j += 1
        return len(text)
    if text[i] in '"\'' or two in ('$"',):
        j = i + (2 if two == '$"' else 1)
        quote = '"' if text[i] != "'" else "'"
        while j < len(text):
            if text[j] == '\\':
                j += 2
                continue
            if text[j] == quote:
                return j + 1
            j += 1
        return len(text)
    return i


def members(body):
    """(start, end) of every top-level member of a class body, the body's own braces excluded."""
    open_brace = body.index('{')
    found, depth, start, i = [], 0, open_brace + 1, open_brace
    while i < len(body):
        j = skip_literal(body, i)
        if j != i:
            i = j
            continue
        if body[i] == '{':
            depth += 1
        elif body[i] == '}':
            depth -= 1
            if depth == 0:
                break
            if depth == 1:
                found.append((start, i + 1))
                start = i + 1
        elif body[i] == ';' and depth == 1:
            found.append((start, i + 1))
            start = i + 1
        i += 1
    return found


def member_name(text):
    """A name to report a removed member by."""
    kind = re.search(r'\b(?:class|struct|interface|enum)\s+([\w<>]+)', text)
    if kind:
        return kind.group(1)
    call = re.search(r'([\w<>]+)\s*\(', text)
    return call.group(1) if call else text.strip().split('\n')[0][:40]


def build_program(subject, support, ours, theirs, helpers, cases, iterations):
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

    return f"""
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

{support}

namespace Orig {{ using System; using System.Collections; using System.Collections.Generic;
using System.Diagnostics; using System.Runtime.CompilerServices; using System.Globalization;
using System.Text; using System.Threading;
{ours}
}}

namespace Rec {{ using System; using System.Collections; using System.Collections.Generic;
using System.Diagnostics; using System.Runtime.CompilerServices; using System.Globalization;
using System.Text; using System.Threading;
{theirs}
}}

public static class Program
{{
    static int agreed, differed;
{helpers}

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

    // string.GetHashCode() is randomised per process on .NET Core, so seeding with it gave every run a
    // DIFFERENT set of inputs - two runs of the same export disagreed about which input a method failed on,
    // and a method that fails on only some inputs could pass one run and fail the next. A measurement
    // instrument has to be reproducible, so the seed is computed here.
    static int Seed(string name)
    {{
        int h = 17;
        foreach (char c in name) h = h * 31 + c;
        return h;
    }}

    static void Check(string name, Func<Random, (object, object, object[])> once)
    {{
        var rng = new Random(Seed(name));

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


CSPROJ = """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>disable</Nullable>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <AssemblyName>autodiff</AssemblyName>
    <NoWarn>CS0164;CS0168;CS0219;CS0162;CS8321;CS0429;CS0184;CS0067;CS0414</NoWarn>
  </PropertyGroup>
</Project>
"""

ERROR = re.compile(r'Generated\.cs\((\d+),\d+\): error (CS\d+): ([^\[\n]*)')


def line_starts(text):
    starts, at = [0], text.find('\n')
    while at >= 0:
        starts.append(at + 1)
        at = text.find('\n', at + 1)
    return starts


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

    declared, skipped, missing = [], [], []
    for match in DECLARATION.finditer(ours):
        name, arguments = match.group(2), parameters(match.group(3))
        if arguments is None:
            skipped.append(name)
            continue
        if not re.search(rf'\b{re.escape(name)}\s*\(', theirs):
            missing.append(name)
            continue
        declared.append((name, arguments))

    if not declared:
        print("nothing to compare - are both files there?")
        return

    # The corpus's own types come from the original file, so both sides agree on what a `Pair` is. `Shared`
    # is what `difftest.wrap` opens with; there is nothing to share here, but it has to exist.
    # The supporting types are built from the original, where a member il2cpp inlined is still private -
    # `Box._side` is read straight out of `Areas` because `Area()` was inlined into it, and the export widens
    # the declaration in `Box.cs`, a file this script never opens. Accessibility changes no answer, so widen
    # it here rather than teach the harness to read a second file.
    support = "public static class Shared { }\n" + '\n'.join(types(original_text))
    support = re.sub(r'\b(private|protected)\b(?!\s+(?:class|struct|interface|enum)\b)', 'internal', support)
    if re.search(r'\bMathf\s*\.', recovered_text) and not re.search(r'\bclass\s+Mathf\b', support):
        support += UNITY_SHIM

    helpers = HELPERS + ''.join(
        body for name, body in CORPUS_HELPERS.items()
        if re.search(rf'\b(?:enum|struct|class|interface)\s+{re.escape(name)}\b', support))

    os.makedirs(PROJECT, exist_ok=True)
    with open(os.path.join(PROJECT, "autodiff.csproj"), "w") as handle:
        handle.write(CSPROJ)

    environment = dict(os.environ, DOTNET_ROOT=os.path.expanduser("~/.dotnet"),
                       PATH=os.path.expanduser("~/.dotnet") + ":" + os.environ["PATH"])

    # One member of the recovered class that does not compile takes the whole harness down with it, and the
    # run then reports nothing at all - which is the worst possible answer, because a corpus of fifty shapes
    # is judged by whichever one is worst. So: build, and if it fails, drop the members the compiler names
    # and build again. A method dropped this way is a failure of kind "does not compile", which is exactly
    # one of the three the baseline table has to distinguish.
    cases, dropped, uncallable = list(declared), [], []
    for attempt in range(40):
        alive = {name for name, _ in cases}
        program = build_program(subject, support, ours, theirs, helpers, cases, iterations)
        with open(os.path.join(PROJECT, "Generated.cs"), "w") as handle:
            handle.write(program)

        run = subprocess.run(["dotnet", "build", "-c", "Release", "--nologo", "-v", "q", PROJECT],
                             capture_output=True, text=True, env=environment)
        if run.returncode == 0:
            break

        output = run.stdout + run.stderr
        starts = line_starts(program)
        rec_at = program.index(theirs, program.index("namespace Rec {"))
        spans = members(theirs)
        check_at = program.index("    public static void Main()")

        cut, lost, unknown = {}, {}, []
        for match in ERROR.finditer(output):
            line, code, message = int(match.group(1)), match.group(2), match.group(3).strip()
            if line >= len(starts):
                continue
            offset = starts[line - 1] - rec_at
            if 0 <= offset < len(theirs):
                for start, end in spans:
                    if start <= offset < end:
                        cut.setdefault((start, end), (code, message))
                        break
                else:
                    unknown.append((line, code, message, "recovered class, outside any member"))
                continue
            if starts[line - 1] >= check_at:
                # The call itself does not compile: the recovered signature is not the original's.
                name = re.findall(r'Check\("(\w+)"', program[check_at:starts[line - 1]])
                if name:
                    lost.setdefault(name[-1], (code, message))
                    continue
            unknown.append((line, code, message, "outside the recovered class"))

        if not cut and not lost:
            print("the harness does not compile for a reason that is not the recovered class:")
            for line, code, message, where in unknown[:12]:
                print(f"  Generated.cs({line}) {code} {message} - {where}")
            print(output[-2500:])
            return

        for name, (code, message) in lost.items():
            uncallable.append((name, f"{code} {message}"))
        for (start, end), (code, message) in sorted(cut.items(), reverse=True):
            dropped.append((member_name(theirs[start:end]), f"{code} {message}"))
            theirs = theirs[:start] + theirs[end:]

        cases = [(name, arguments) for name, arguments in cases
                 if re.search(rf'\b{re.escape(name)}\s*\(', theirs)
                 and name not in {n for n, _ in uncallable}]
        if not cases:
            break
        if {name for name, _ in cases} == alive and not cut:
            break
    else:
        print("gave up dropping members after 40 rounds")

    if cases:
        run = subprocess.run(["dotnet", "run", "-c", "Release", "--no-build", "--project", PROJECT],
                             capture_output=True, text=True, env=environment)
        print(run.stdout)
        if run.returncode != 0:
            print(run.stderr[-2500:] or run.stdout[-2500:])

    judged = {name for name, _ in cases}
    broken = [name for name, _ in declared if name not in judged]
    if broken:
        print(f"DOES NOT COMPILE ({len(broken)}): {', '.join(broken)}")
    if dropped:
        print("  members dropped from the recovered class, with the diagnostic that named them:")
        for name, why in dropped:
            print(f"    {name:24} {why}")
    if uncallable:
        print("  recovered signature is not the original's:")
        for name, why in uncallable:
            print(f"    {name:24} {why}")
    if missing:
        print(f"NOT EXPORTED ({len(missing)}): {', '.join(missing)}")
    if skipped:
        print(f"not judged ({len(skipped)}): {', '.join(skipped)}")


if __name__ == '__main__':
    main()
