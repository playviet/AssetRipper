# The repair commented a closing brace — root cause and fix

Evidence: `Assembly-CSharp/Tools/CameraTools.cs` from the Snacky Dash round-1 export. 238 lines,
3 opening braces and 1 closing brace over the non-`//` lines, one error in the whole 2787-file
export: `CameraTools.cs(238,2): error CS1513: } expected`.

## Root cause — `InvalidSourceRepair.ApplyEdits`, `InvalidSourceRepair.cs:1552`

`ApplyEdits` comments a span out with **`//` line comments**: it writes the `Marker` at `span.Start`,
then one `//`-prefixed copy of each line of the span, and then resumes copying the untouched source
from `span.End`:

```csharp
builder.Append(Marker);
foreach (string line in text.Substring(span.Start, span.Length).Split('\n'))
{
    builder.Append('\n');
    if (!trimmed.TrimStart().StartsWith("//")) { builder.Append(indentation).Append("//"); indentation = ""; }
    builder.Append(trimmed);
}
...
position = span.End;              // <- and the next iteration/tail appends straight onto that line
```

A `//` comment runs to the end of the **physical line**. The tail resumed at `span.End` is appended
onto the builder immediately after the last `//`-prefixed line, with no newline between them — so
**whatever followed the commented span on the same source line is swallowed by that `//`**. Nothing
in the code notices, because the loss is textual, not syntactic: no span ever covered it.

That normally does no harm, because the decompiler writes one statement per line. It does harm the
moment a *rewrite* has put several statements on one line. `InvalidSourceRepair.Conversions.cs:144`
is exactly that rewrite — `RewriteStructPropertyMember` turns `worldBounds.center.x = x3;` into a
one-line read-modify-write block:

```
{ UnityEngine.Vector3 repairCopy = worldBounds.center; repairCopy.x = x3; worldBounds.center = repairCopy; }
```

On a later attempt `repairCopy.x = x3;` — a statement whose parent is that synthetic `BlockSyntax`,
so `FindStatement` happily returns it — fails to compile and is commented out. `ApplyEdits` produces:

```
						{ UnityEngine.Vector3 repairCopy = worldBounds.center; //AssetRipper: commented out, this could not be kept as code.
//repairCopy.x = x3; worldBounds.center = repairCopy; }
```

which is byte-for-byte lines 176-177 of the exported file. The block's **`}` is now inside a comment
while its `{` is not**. The file is one closing brace short from that point on.

Everything after is consequence, not a second bug:

* Roslyn re-parses the file in error recovery. The method body block no longer ends where it did, so
  the statement spans it hands out legitimately run past the method — and the next round's comment
  edit covers the method's `}` (2 tabs, line 200) and the class's `}` (1 tab, line 201). The tree was
  not wrong; the text it was built from was.
* The method can then never settle. `FindBodyEdit` (`:508`) empties it and inserts
  `//{EmptiedNote}\r\n\t\t\treturn default;`; the next attempt finds that inserted `return default;`
  is still an error and **comments out the repair's own insertion**; the attempt after that sees a
  body with zero statements, walks out to the enclosing body and empties again. One
  emptied/commented/`return default` quartet per attempt, `MaxAttempts` of them, which is the
  repeated tail from line 202 to the end of the file.

So: one root cause (`ApplyEdits` swallowing the tail of a line into a `//` comment), one amplifier
(the multi-statement rewrite at `Conversions.cs:144` that puts a `}` after a commentable statement),
and one loop defect that turns a broken file into 36 lines of noise instead of one.

## The fix

1. **`ApplyEdits` (`InvalidSourceRepair.cs`)** — after writing the commented copy of a span, if any
   non-whitespace remains on that source line after `span.End`, start a new line before resuming.
   The swallowed code goes back to being code. This is the actual bug and the whole of the CS1513.
2. **`FindEdits`** — refuse a comment edit whose span text is not brace-balanced as code. A whole
   statement always balances; a span that does not is a span that has picked up a brace belonging to
   something outside it. This is invariant 1, enforced rather than argued.
3. **`FindEdits`** — refuse to comment a statement the repair itself inserted (its text or leading
   trivia carries `EmptiedNote`). This is invariant 2, and it stops the empty/comment/empty cycle.
4. **The attempt loop** — a per-file brace-balance check around `ApplyEdits`: if a round would change
   the file's code brace balance, the edits are dropped and the file and the offending span are
   logged. Belt and braces, ~10 lines on top of the balance counter already needed for (2).

The balance counter, `HasCodeAfterOnLine` and the file check live in a new file,
`Source/AssetRipper.Export.UnityProjects/Scripts/InvalidSourceRepair.Braces.cs`, per the fork's
mergeability rule; the change to `InvalidSourceRepair.cs` itself is four short call sites.

## Invariants

* **The repair can never comment a token outside the body it is repairing.** Holds. Enforced by (2)
  and (4): an edit whose span carries an unmatched brace is dropped before it is applied, and a round
  that would change a file's brace balance is dropped before it is written.
* **The repair loop never comments out text it inserted itself.** Holds. Enforced by (3).

## Verification, without a build

`ApplyEdits`' comment path was replayed in Python against the one line in question (the replica is a
line-for-line transcription of the C# loop, `/tmp/sim.py` in the session that wrote this):

```
before   balance +1
						{ UnityEngine.Vector3 repairCopy = worldBounds.center; //AssetRipper: commented out, this could not be kept as code.
//repairCopy.x = x3; worldBounds.center = repairCopy; }

fixed    balance  0
						{ UnityEngine.Vector3 repairCopy = worldBounds.center; //AssetRipper: commented out, this could not be kept as code.
//repairCopy.x = x3;
 worldBounds.center = repairCopy; }
```

The "before" output is **lines 176-177 of the exported file byte for byte**, which is what settles the
diagnosis: the mechanism is not merely capable of producing that text, it produces exactly it.

`HasCodeAfterOnLine` fires only where a span ends with code still to come on the same line. For the one
statement per line the decompiler writes, and for the emptied-body edit (whose span ends at the last
statement, with only `\r\n\t\t}` after it), it is false and nothing changes. The blast radius is the
multi-statement line and nothing else.

Two cases were added to `Source/AssetRipper.Tests/InvalidSourceRepairTests.cs`
(`CommentingOneOfSeveralStatementsOnALineKeepsTheRestOfTheLine`, `TheRepairDoesNotCommentOutWhatItInserted`),
each asserting brace balance and that the repaired file still parses without errors. They have **not been
run** - an export was building and this session was told not to build.

## Cross-check over the exports on disk

A brace-balance scan (comments and literals skipped) over every `.cs` file of every finished export still on
disk:

| export | files | unbalanced |
|---|---|---|
| Fluffy Field 1.0.3 `_527` | 533 | **0** |
| CarSort 1.2.3 | 1544 | **0** |
| Goods Puzzle 5.22A | - | 8, **all in `Library/PackageCache`** - Unity's own sources, unbalanced only to a counter that does not follow `#if`; no recovered file |

So `CameraTools.cs` is the only recovered file known to have been damaged. That fits the cause: it needs a
multi-statement rewrite *and* a later failure of a statement inside it. Only one rewrite in the whole repair
puts several statements on one line - `RewriteStructPropertyMember`, `InvalidSourceRepair.Conversions.cs:144`
- so the family is small, but it is a family and not an accident, and the fix is in `ApplyEdits` rather than
in the rewrite for that reason.

## Files changed

| file | change |
|---|---|
| `Scripts/InvalidSourceRepair.Braces.cs` | **new.** `HasCodeAfterOnLine`, `CodeBraceBalance`, `SkipQuoted`, `SpanKeepsBraces`, `IsOwnInsertion`, `RepairedText` |
| `Scripts/InvalidSourceRepair.cs` | four call sites: the newline in `ApplyEdits`, the `IsOwnInsertion` branch and the `SpanKeepsBraces` guard in `FindEdits`, the all-stand-ins guard in `FindBodyEdit`, and `RepairedText` in the attempt loop |
| `AssetRipper.Tests/InvalidSourceRepairTests.cs` | two cases plus a `Balance` helper |

Nothing in Cpp2IL, nothing in `scratchpad/`, `LocalPackages/` or `External/`.
