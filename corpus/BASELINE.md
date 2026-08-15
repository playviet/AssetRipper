# The corpus baseline

Measured at **AssetRipper `9fa38918d`** ("A float constant left typed as an integer, and a check for it"),
Cpp2IL packed as **1.6.1**, export `ec432`, Unity **6000.0.78f1**, arm64 il2cpp, `Release` / `OptimizeSpeed`,
`Minimal` stripping with `link.xml` preserving Assembly-CSharp whole.

```
79 methods run, 51 behave the same, 28 do not
```

Nothing was unjudged, unexported, or too broken to compile: every shape in `Assets/Corpus.cs` got an answer.

## The number this corpus exists to state

`corpusscore.py` crosses the oracle against `markers.classify` - the same function `cfscore.py` and
`allscore.py` use, so this is their verdict, not a second opinion:

| | |
|---|---|
| shapes rated **`full`** (compiles whole, no marker) | **65** |
| of those, **compute the right answer** | **46** |
| of those, **whole and wrong** | **19** |

**29% of the bodies this project's own scorers count as recovered do not do what the original did.** The
worst of them are not subtly wrong. `Scale`, `Cross` and `Blend` recover as

```csharp
public static Pair Scale(Pair p, float factor)
{
    Pair result = default(Pair);
    return result;
}
```

- no marker, no commented statement, two statements, rated `full`, and the method does nothing.

The nineteen, by name - a body with no marker in it, that answers wrongly:

> `AsOrNull` `Blend` `ClearAndSet` `Cross` `Describe` `DivMagic` `Divide` `EventRoundTrip` `Guarded`
> `NullableSum` `Reversed` `Scale` `SharedPick` `Shifts` `Steps` `Thrown` `Using` `ValuePick` `Weight`

The other direction is worth knowing too: **5 of the 14 methods rated `partial` are right anyway**
(`Enumerated`, `Names`, `Spread`, `TickText`, `TotalSides`), so a marker is not proof of a wrong answer
either. The nine that are `partial` and wrong are `Boxed` `CastChain` `FirstPairX` `NullableChain` `OrElse`
`SumPairs` `SumSteps` `Tally` `Total`.

## What fails, and how

28 failures: **17 compute a wrong value, 11 throw, 0 fail to compile.**

| shape | kind | family, and the root where it is visible in the export |
|---|---|---|
| `Scale` `Cross` `Blend` | wrong value | **A struct returned in the vector registers is dropped.** The body is `T result = default(T); return result;` and nothing else. Rated `full`. |
| `ClearAndSet` | wrong value | A struct cleared through one vector store, then one field written - the field write is lost. |
| `Total` | throws | **`foreach` over `List<T>`.** `values.GetEnumerator();` is called and its answer thrown away, then `List<int>.Enumerator enumerator = default(...)` is what `MoveNext` runs on. `Expected I4, but got O`. |
| `Tally` | throws | Same shape over `Dictionary<K,V>`. |
| `OrElse` `NullableChain` `NullableSum` | wrong value | **`Nullable<T>` is a packed 64-bit value nothing unpacks.** `long num2 = (object?)num >> 32;` and `((_003F?)num & 0xFFL) == 0` are both commented out. |
| `Boxed` `CastChain` | wrong value / throws | **`il2cpp_vm_object_unbox` is an unresolved call target**: `_ = "Unknown call target operand: ... il2cpp_vm_object_unbox"` and the unboxed value becomes `default(int)`. |
| `AsOrNull` | throws | `as` against a boxed value: `InvalidCastException`. |
| `Describe` | throws | Boxing an **enum** for `ToString()` loses the receiver: `Enum obj = default(Enum); int num = default(int); return obj.ToString() + "=" + num;`. Same family as `Boxed`. |
| `Divide` `Guarded` `Thrown` | throws | **`try`/`catch` is not recovered.** Known: `UDIV` does not trap so clang deleted two of these handlers - but `Thrown` throws its own exception explicitly and is not covered by that. None of the 96 reference files contains a `catch`, so `cfscore` cannot see this family at all. |
| `Using` | throws | `try`/`finally`: the body and the `Dispose` are both right, and then the compiler's **exception slot** is left as `object obj = 0L;` - a boxed zero, so never null - and `if (obj2 == null) return result; throw new OutOfMemoryException();` always takes the throw. `Expected O, but got I8`. |
| `SharedPick` `ValuePick` | wrong value | A generic `Pick<T>` returns `default(T)` - both the shared (reference) and the specialised (value) instantiation. |
| `Shifts` | wrong value | A 64-bit value truncated to 32: `int num = (int)((uint)a >> b)`. `Expected I8, but got I4`. |
| `DivMagic` | wrong value | Magic division: the multiply and shifts survive, the sign correction does not. `Expected I4, but got I8`. |
| `Weight` | wrong value | A jump table with the bound lost - out-of-range input reads past the table. |
| `Reversed` | throws | The loop is rebuilt around a packed `(length << 32)` pair and indexes out of range. |
| `SumPairs` `FirstPairX` | wrong value | Element access into an array of a non-primitive struct. `SumPairs` carries `Not implemented instruction: Instruction UNIMPLEMENTED` **inside the loop** and reads `points[i].X - num9` where `num9` is `default(float)` - so this one is a **disassembler gap**, not a typing gap: the mnemonic is literally `UNIMPLEMENTED`, which is Disarm refusing the encoding, and it is the **only** one in the whole corpus export. |
| `Steps` | wrong value | The iterator's **first** yielded element is wrong (`{1,1,4,9}` for `{0,1,4,9}`); everything after it is right. |
| `SumSteps` | wrong value | The same defect, summed - always exactly one too high. |
| `EventRoundTrip` | throws | A field-like `event`'s compare-exchange accessors. |

## What passes

51 shapes, and they are worth knowing because they are families that need no work:

`AddTwo` `Clamp` `Both` `Either` `Ternary` `Mix` `Bits` `Overflow` `Narrow` `Modulo` `Kind` `WrapLevel`
`EaseInOut` `Distance` `Length3` `Luminance` `Spread` `NestedSum` `PairField` `BuildAndPass` `CountOf`
`AllNone` `IndexOfFirst` `SumJagged` `Diagonal` `Matching` `Hash` `Build` `Slice` `Areas` `Grow` `Lookup`
`TotalSides` `Names` `Enumerated` `SharedCount` `SharedMeasure` `SwapAndSum` `BoxedFloat` `Format` `Join`
`Builder` `TooShort` `Words` `Interpolated` `ParseOrDefault` `Range` `Ticks` `TickText` `Filtered` `Closure`

Notable among them: **struct arguments in the vector registers are right** (`Distance`, `Length3`,
`Luminance`, `NestedSum`, `PairField`, `BuildAndPass`) and **a big struct returned through `x8` is right**
(`Spread`) - it is only the *small* struct returned in `v0`/`v1` that is empty. Interface dispatch,
`foreach` over an interface list, `checked` arithmetic, narrowing conversions, rank-2 arrays, jagged arrays,
string formatting, `out` parameters through a wrapper, lambdas and delegate invokes are all right.

## Moving the number

At the worktree's earlier base (`9fb59bdf5`, packed as 1.6.0, export `ec431`) the same corpus scored
**49 right, 30 wrong** with `Spread` and `BoxedFloat` among the failures. The 24 commits to `9fa38918d`
bought **+2**. That is the resolution of this instrument on a stretch of work: it is a families measure, not
a percentage that drifts.

## Two things about the instrument itself

**It is reproducible, and it was not.** `Check` seeded its generator with `name.GetHashCode()`, and
`string.GetHashCode()` is randomised per process on .NET Core - so every run drew a *different* set of
inputs, two runs of the same export disagreed about which input a method failed on, and a method that fails
on only some inputs could pass one run and fail the next. The seed is computed in the harness now, and two
runs of the same export are byte-identical.

**The iteration count is load-bearing. Use at least 2000.** `Divide(a, b)` only misbehaves when `b == 0`,
which the generator draws with probability 1/180: at 200 iterations it was never drawn and the run reported
**52 right** instead of 51. 2000 and 5000 give identical verdicts, so 2000 is the floor, not a preference.

**And it does execute the recovered side.** Injecting one defect into the export - `return b + a` changed to
`return b - a` in `AddTwo` - moves the score by exactly one and names that method. Worth re-checking after
any change to the harness, because two namespaces that quietly resolve to the same class would agree on
everything.

**The backup is a corpus, not a folder of files.** `memory/tools/corpus/` was copied to an empty directory,
built with nothing else, exported and scored: **51 right, 28 wrong, the same verdict method for method**. So
if this directory is ever lost again, the restore is

```sh
rsync -a ~/.claude/projects/-Users-playviet-Documents--BZ-AssetRipper/memory/tools/corpus/ <repo>/corpus/
```

and then the Unity build below. Nothing else is needed.

## Reproducing it

```sh
# once, and again whenever a shape is added
/Applications/Unity/Hub/Editor/6000.0.78f1/Unity.app/Contents/MacOS/Unity \
    -batchmode -quit -nographics -projectPath <repo>/corpus \
    -executeMethod BuildCorpus.Build -logFile <repo>/corpus/build.log

# every round
dotnet <riprun>/riprun.dll <repo>/corpus/corpus.apk <out> <out>.log 3 fast
python3 <repo>/scratchpad-tools/autodiff.py <repo>/corpus/Assets/Corpus.cs \
    <out>/ExportedProject/Assets/Scripts/Assembly-CSharp/Corpus.cs 2000 | tee oracle.txt
python3 <repo>/scratchpad-tools/corpusscore.py \
    <out>/ExportedProject/Assets/Scripts/Assembly-CSharp/Corpus.cs oracle.txt
```
