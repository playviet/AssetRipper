#!/usr/bin/env python3
"""srcdiff.py <export>/Assets/Scripts/Assembly-CSharp - does a recovered body DO what the original does?

    python3 srcdiff.py <export>/Assets/Scripts/Assembly-CSharp [--worst N] [--file X.cs]

Every scorer in this project asks whether a body **compiles whole**. The one that asked whether it
**computes the right answer** needed a compiled corpus, and that corpus is gone
(`il2cpp-the-execution-oracle-is-gone`). This asks a weaker question that needs nothing but the two texts,
and the ground truth for it is already on disk: 427 originals, `cfscore`'s 96 among them.

For each member it extracts three things from the original and from the recovery and compares them:

* **calls**, as a multiset of callee names - `transform.SetParent(x)` contributes `SetParent`
* **literals** - numbers and strings, which a wrong constant changes and a lost statement removes
* **decisions** - the count of `if` / `for` / `while` / `switch` / `?:` / `&&` / `||`

None of the three is sufficient and together they are still not proof: a body can keep every call and pass
them the wrong arguments, which is precisely the failure the execution oracle caught and this cannot. What
it does catch is a body that quietly does LESS than the original - the commented statement that took a call
with it, the branch that folded to a constant - and it catches it **per member**, against source, on a body
that every other scorer has already called `full`.

**READ THIS BEFORE QUOTING A NUMBER FROM IT.** The count it prints is *differences to triage*, not defects.
The compiler legitimately changes what a body says, and four such classes were measured and are discounted
here: framework inlining (`Mathf.Max`), `[Conditional]` logging removed at the call site (135 sites),
`d.Invoke(x)` against `d(x)` (166), and coroutine/async bodies that move wholesale into a generated state
machine (46 members). A fifth is only partly discounted and still dominates what is left: **il2cpp inlines
the game's own small private helpers**, so the caller loses the NAME and gains the callee's work -
`HandleLevelStateChanged` "loses" `StopBoardAnimations()` and `DisableBoardAction()` and contains both of
their bodies inline, correctly.

So the 457 members it reports as differing while scoring `full` are **not** 457 wrong bodies, and the
honest conclusion from building this is a negative one: separating a compiler transformation from a lost
statement needs semantics, which is exactly what the execution oracle supplied and text comparison cannot.
It is kept because per-file triage is still useful and because rebuilding it from scratch would cost the
same discovery again - see `scratchpad-tools/floatbits.py` for a check of the same class that IS decisive.
"""
import collections
import os
import re
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from csharp import members, without_attributes                       # noqa: E402
from markers import has_body, statements                             # noqa: E402
import allscore as A                                                 # noqa: E402

#A call is a name followed by `(`. `new Foo(` is a call too and is kept as `Foo`. Keywords that take a
#parenthesis are not calls and are counted as decisions instead.
CALL = re.compile(r'\b([A-Za-z_]\w*)\s*\(')
NOT_A_CALL = {'if', 'while', 'for', 'foreach', 'switch', 'catch', 'lock', 'using', 'fixed', 'return',
              'throw', 'sizeof', 'typeof', 'nameof', 'default', 'checked', 'unchecked', 'stackalloc'}

#il2cpp INLINES these, so the original naming one and the recovery not naming it is the same arithmetic
#written differently - not work that went missing. Leaving them in made `Mathf.Max` the single commonest
#"defect" in the game and drowned everything real. A struct constructor goes the same way: `new Vector3(a,
#b, c)` becomes three field writes.
INLINED = {'Max', 'Min', 'Clamp', 'Clamp01', 'Abs', 'Sign', 'Floor', 'Ceil', 'FloorToInt', 'CeilToInt',
           'RoundToInt', 'Round', 'Lerp', 'LerpUnclamped', 'InverseLerp', 'Approximately', 'Sqrt', 'Pow',
           'Vector2', 'Vector3', 'Vector4', 'Color', 'Color32', 'Quaternion', 'Rect', 'Mathf', 'Math',
           'get_Item', 'set_Item', 'op_Implicit', 'op_Equality', 'op_Inequality', 'op_Addition',
           'op_Subtraction', 'op_Multiply', 'op_Division',
           #Two more kinds of false positive, both measured. The game's `Logger` methods carry
           #`[Conditional(DEBUG_FLAG)]`, so the C# compiler removes the CALL SITE in a release build and
           #their absence is correct - 135 of them. And `d.Invoke(x)` and `d(x)` are the same call spelled
           #two ways, which the two sides choose differently - 166. Together they were the top three
           #"defects" in the game and every one of them was the instrument's fault.
           'Invoke', 'Log', 'LogError', 'LogWarning', 'LogAssertion', 'LogException', 'LogFormat',
           'ReferenceEquals', 'Equals', 'ToString', 'Concat', 'Format'}
NUMBER = re.compile(r'(?<![\w.])(\d+\.\d+[fdm]?|0[xX][0-9A-Fa-f]+|\d+[uUlLfdm]*)\b')
STRING = re.compile(r'"((?:[^"\\]|\\.)*)"')
DECISION = re.compile(r'\b(if|for|foreach|while|switch|case)\b|\?[^.:]|&&|\|\|')
COMMENT = re.compile(r'^\s*//')

#A coroutine or an async method keeps NONE of its body: the compiler moves the whole thing into a generated
#state machine and leaves `return new <Foo>d__12(0) { ... };` behind. Comparing the original's body against
#that is comparing against a different method, and it made `HammerAnimation` - a plain, correctly recovered
#coroutine - look like eight lost `SetEase` calls. The work is in the state machine's `MoveNext`, which is
#scored on its own. See `il2cpp-the-scorer-was-asking-for-phantoms` for the last time coroutines fooled a
#scorer here.
MOVED_TO_A_STATE_MACHINE = re.compile(r'\byield\s+(return|break)\b|\bawait\b')


def state_machine(text):
    head = without_attributes(text).split('{')[0].split('=>')[0]
    if re.search(r'\b(IEnumerator|IEnumerable|Task|UniTask|ValueTask)\b', head):
        return True
    return bool(MOVED_TO_A_STATE_MACHINE.search(text))


def live(text):
    """The body with commented-out statements and marker strings removed - what actually runs."""
    kept = []
    for line in without_attributes(text).split('\n'):
        if COMMENT.match(line):
            continue
        #`_ = "Unmanaged memory load: [...]";` is a marker, not a literal the original ever had.
        if '_ = "' in line:
            continue
        kept.append(line)
    return '\n'.join(kept)


def shape(text):
    body = live(text)
    calls = collections.Counter(n for n in CALL.findall(body)
                                if n not in NOT_A_CALL and n not in INLINED)
    #Only DISTINCTIVE constants. 0/1/2 and the like are folded, re-associated and re-spelled freely, and
    #counting them made every method that mentions a zero look like a defect.
    literals = collections.Counter(n for n in NUMBER.findall(body) if n.strip('fdmuUlL') not in
                                   ('0', '1', '2', '3', '4', '0.0', '1.0', '0.5')) + collections.Counter(
        's:' + s for s in STRING.findall(body) if s)
    return calls, literals, len(DECISION.findall(body))


def inlined_away(name, bodies, got_calls):
    """Whether a missing call to the game's OWN method is that method having been inlined into this one.

    il2cpp inlines small private helpers, so the caller loses the NAME and gains the callee's work.
    `BoardController::HandleLevelStateChanged` calls `StopBoardAnimations()` and `DisableBoardAction()` in
    the original and neither appears in the recovery - but `StopAllCoroutines`, `DisableAllSortingLayers`
    and `CancelActiveDrag`, which are what those two do, all appear inline. Nothing was lost; the call was
    flattened. Where the callee's own calls are present, treat the missing name as inlined.

    A helper that calls nothing at all cannot be told apart this way and is left counted, which is the
    conservative direction.
    """
    inner = bodies.get(name)
    return bool(inner) and not (inner - got_calls)


def compare(original, recovered, bodies=None):
    """(verdict, detail). `lost` counts what the original has and the recovery does not."""
    want_calls, want_lit, want_dec = shape(original)
    got_calls, got_lit, got_dec = shape(recovered)

    lost_calls = want_calls - got_calls
    if bodies:
        for name in list(lost_calls):
            if inlined_away(name, bodies, got_calls):
                del lost_calls[name]
    lost_lit = want_lit - got_lit
    lost_dec = max(0, want_dec - got_dec)

    if not lost_calls and not lost_lit and not lost_dec:
        #Extra calls are not a defect on their own: recovery spells `x.y` as `get_y()` and a property
        #becomes a call. Only what is MISSING is evidence.
        return 'agrees', ''

    parts = []
    if lost_calls:
        parts.append('calls ' + ','.join(f'{k}x{v}' if v > 1 else k for k, v in lost_calls.most_common(4)))
    if lost_dec:
        parts.append(f'decisions -{lost_dec}')
    if lost_lit:
        parts.append('literals ' + ','.join(str(k) for k, _ in lost_lit.most_common(3)))
    return 'differs', '; '.join(parts)


def main():
    if len(sys.argv) < 2:
        raise SystemExit(__doc__)
    export_root = sys.argv[1].rstrip('/')
    worst = int(sys.argv[sys.argv.index('--worst') + 1]) if '--worst' in sys.argv else 25
    only = sys.argv[sys.argv.index('--file') + 1] if '--file' in sys.argv else None

    roots = [os.path.join(A.AAA, name) for name in ('CF', 'CFramework', 'Common')]
    recovered = A.index_multi(export_root)
    asmdef_dirs = [d for root in roots for d in A.asmdefs(root)]

    originals = []
    for root in roots:
        for directory, _sub, files in os.walk(root):
            originals += [os.path.join(directory, n) for n in sorted(files) if n.endswith('.cs')]
    originals.sort()

    totals = collections.Counter()
    per_file = collections.Counter()
    rows = []

    #name -> the calls that method itself makes, over every original. Used to tell an inlined helper from
    #a lost one.
    bodies = {}
    for path in originals:
        try:
            for name, texts in members(path).items():
                for text in texts:
                    if has_body(text) and name not in bodies:
                        bodies[name] = shape(text)[0]
        except Exception:
            pass

    for path in originals:
        basename = os.path.basename(path)
        if only and basename != only:
            continue
        buildable, _why = A.in_the_player_assembly(path, asmdef_dirs)
        if not buildable:
            continue

        wanted = A.scorable(path, A.not_in_the_build(path))
        got, source_of, _chosen, _amb = A.recovered_members(basename, path, recovered, export_root)
        got, source_of = A.reconcile(wanted, got, source_of)
        if any(A.substituted(where) for where in set(source_of.values())):
            continue

        for key in sorted(wanted):
            if key not in got:
                totals['missing'] += 1
                continue
            if not has_body(got[key]) or not statements(wanted[key]):
                totals['nothing to compare'] += 1
                continue
            if state_machine(wanted[key]):
                totals['state machine'] += 1
                continue
            verdict, detail = compare(wanted[key], got[key], bodies)
            totals[verdict] += 1
            whole = A.classify(got[key])[0] == 'full'
            if verdict == 'differs':
                per_file[basename] += 1
                rows.append((basename, key.split('#')[0], detail, whole))
                if whole:
                    totals['whole but does less'] += 1

    judged = totals['agrees'] + totals['differs']
    print(f'members compared      : {judged}')
    print(f'  agrees              : {totals["agrees"]:5d}  ({100 * totals["agrees"] / max(judged, 1):.1f}%)')
    print(f'  differ, TO TRIAGE   : {totals["differs"]:5d}  (not defects - read the header)')
    print(f'  not compared        : {totals["missing"]} missing, {totals["nothing to compare"]} empty, '
          f'{totals["state machine"]} coroutine/async (body lives in the generated MoveNext)')
    print()
    print('This is a FLOOR. "agrees" means nothing is obviously missing - the same calls, the same')
    print('constants, at least as many decisions. It cannot see a call handed the wrong argument, which')
    print('is the failure the execution oracle existed to catch.')
    print()
    print(f'worst files ({len(per_file)} have a member that does less):')
    for name, n in per_file.most_common(12):
        print(f'  {n:4d}  {name}')
    print()
    print('of those, scored `full` by every other scorer: %d' % totals['whole but does less'])
    print('Still dominated by il2cpp inlining the game\'s own private helpers, which is a difference in')
    print('spelling and not in behaviour. Triage per file; do not quote this as a defect count.')
    print()
    for row in [r for r in rows if r[3]][:worst]:
        print(f'  full  {row[0]:28s} {row[1][:32]:34s} {row[2][:64]}')


if __name__ == '__main__':
    main()
