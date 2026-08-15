# The ground-truth corpus

A Unity project whose whole point is to be recovered. `Assets/Corpus.cs` holds one `public static` method per
shape the recovery is known or suspected to get wrong; the build turns it into an **arm64 il2cpp Android
apk**; the fork exports that apk back to C#; and `scratchpad-tools/autodiff.py` **executes both sides against
the same inputs and compares the answers**.

It is the only instrument in this project that asks whether a recovered body computes the right answer.
Everything else - `cfscore.py`, `compare2.py`, `roundtrip.py`, `decisions.py`, `allscore.py` - asks whether it
compiles whole, and a body can compile whole and be wrong. When this last ran it found **9 of 10 methods
rated `full` and 2 of 10 that actually worked**.

It has been lost three times. The sources are in git now for that reason. `Library/`, `Temp/`, `Logs/`,
`UserSettings/` and `corpus.apk` are ignored: they are all rebuildable from what is tracked.

## Build it

```sh
/Applications/Unity/Hub/Editor/6000.0.78f1/Unity.app/Contents/MacOS/Unity \
    -batchmode -quit -nographics \
    -projectPath <repo>/corpus \
    -executeMethod BuildCorpus.Build \
    -logFile <repo>/corpus/build.log
grep "CORPUS BUILD" <repo>/corpus/build.log     # -> "CORPUS BUILD Succeeded ... 0 errors"
```

About 4 minutes cold, and it produces `<repo>/corpus/corpus.apk` (~21 MB) holding
`lib/arm64-v8a/libil2cpp.so` and `assets/bin/Data/Managed/Metadata/global-metadata.dat`. `BuildCorpus.Build`
sets il2cpp, ARM64-only, `Minimal` stripping, `Release` / `OptimizeSpeed`, and creates `Assets/Corpus.unity`
with the one `Driver` GameObject on first run. Unity **6000.0.78f1** is deliberate: it is the version the game
under recovery was built with.

Repeat this step only when a shape is added or changed.

## Run the oracle

```sh
dotnet <riprun>/riprun.dll <repo>/corpus/corpus.apk <out> <out>.log 3 fast
python3 <repo>/scratchpad-tools/autodiff.py \
    <repo>/corpus/Assets/Corpus.cs \
    <out>/ExportedProject/Assets/Scripts/Assembly-CSharp/Corpus.cs 2000
```

The second argument to `autodiff.py` is the **file**, not the export root. The export takes about a minute -
the corpus apk is a hundredth of the game's size.

It prints one line per method: `agrees`, or `DIFFERS` with the first input the two disagree on. Then, below
the run, three lists that are the other failure kinds:

| line | means |
|---|---|
| `DIFFERS` | recovered, compiles, **computes a different answer** |
| `DOES NOT COMPILE` | the recovered member had to be dropped for the harness to build; the members and their error ids follow |
| `NOT EXPORTED` | the method is not in the recovered `Corpus.cs` at all |
| `not judged` | autodiff has no way to make a value of some parameter type - add one to its `GENERATORS` |

Then cross the answer against the project's own compile-whole score, which is the number none of the other
scorers can state:

```sh
python3 <repo>/scratchpad-tools/autodiff.py ... | tee oracle.txt
python3 <repo>/scratchpad-tools/corpusscore.py \
    <out>/ExportedProject/Assets/Scripts/Assembly-CSharp/Corpus.cs oracle.txt
```

`corpusscore.py` classifies every recovered body with `markers.classify` - the same function `cfscore.py`
and `allscore.py` use - and prints how many of the shapes rated `full` actually compute the right answer.
`BASELINE.md` beside this file is the last measurement.

## Adding a shape

Add a method. `autodiff.py` picks up any `public static` method of `Corpus` whose parameter types it knows,
zero-argument ones included, and generates the comparison itself. There is no per-method table anywhere.

The rules it imposes, each of which fails quietly if broken:

* the subject is `public static class Corpus` at column 0, and autodiff lifts the **whole class** into two
  namespaces - so anything a method calls must be inside the class or a top-level type in this file.
* a judged method is `public static <ret> <Name>(` on **one line** and **never returns `void`**.
* every parameter type must be a key in autodiff's `GENERATORS`. `out` / `ref` / `params` parameters are not
  judged - wrap them in a method that returns the answer, the way `ParseOrDefault` and `Range` do.
* supporting types (`Pair`, `Colour`, `Shape`, ...) are taken from **this file for both sides**, so what this
  measures is method bodies, not a struct's own recovered layout. They need a `ToString()` override or every
  value of them describes as its type name and every comparison passes vacuously.
* **do not mutate an argument.** autodiff hands the same object to both sides; `Grow` copies its list first
  for exactly this reason, and reported a false difference until it did.
* **never put a shape in a static field initialiser.** One throw there takes the class initialiser down and
  every method on the class fails at once.
* no `UnityEngine` types: the harness is a plain console app. `Pair`/`Triple`/`Quad` are the corpus's stand-ins
  for `Vector2`/`Vector3`/`Color`, and they are the same thing to the ABI - a homogeneous float aggregate in
  the vector registers.

`Assets/Driver.cs` is not judged. It exists to be a linker root and, in `Keepalive.Touch`, to **name the
library members the corpus relies on being present**: an il2cpp build runs the managed linker over mscorlib
and the engine assemblies, so a member nothing calls is deleted, recovery cannot then name an intrinsic back
to a method that no longer exists, and the corpus reports a defect that is really an absence. That mistake
cost a wrong diagnosis of the struct-in-registers family once already (`Distance` / `Mathf.Sqrt`).

## Reading a failure

A corpus failure names a **shape**, not a defect count. It is a catalogue of what recovery cannot do,
weighted by what someone chose to write, not by what the game's binary contains. Check the shape's frequency
in the game before working on it.
