# Every way a method body can be abandoned

**Analysis only — nothing here was built or measured. The frequency column is *expectation with its reasoning
shown*; section 4 is the smallest instrumentation that would replace it with numbers from one export.**

Why this map exists: `il2cpp-stack-imbalance-is-the-ceiling` found 907 bodies discarded outright, and found
them only because the source-free oracle asked whether operations survived — a discarded body comes back as
`return null;` with **no marker**, so `cfscore.py` and `compare2.py` both counted it `full`. Every site below
shares that property to some degree: it costs a body, a block, or a statement, and the marker-based scorers
mostly cannot see it.

## The pipeline, in the order a body passes through it

```
  binary bytes
   └─ NewArm64Utils.GetArm64MethodBodyAtVirtualAddress   1.9      how much is read
       └─ Disarm + DecodeWhatDisarmRefused               2.1      which words decode
           └─ NewArmV8InstructionSet.GetIsilFromMethod   1.8 2.1 2.2   which lift into ISIL
               └─ ISILControlFlowGraph ctor              2.10     which become blocks
                   └─ StackAnalyzer.Analyze              1.7 2.9 2.10  the stack must settle
                       └─ ~150 fork passes               2.11     each may refuse a shape
                           └─ IlGenerator.GenerateIl     2.3-2.8  ISIL -> CIL
                               └─ FillMethodBody catch   1.6      anything thrown above
                                   └─ UnreadableMethodBodyProcessor  1.10  the last CIL discard
                                       └─ ILSpy                    1.11  the decompiler throws
                                           └─ InvalidSourceRepair  1.12  the C# would not settle
```

Sites 1.1-1.9 and all of section 2 live in `External/Cpp2IL/Cpp2IL.Core/`. 1.10-1.12 are AssetRipper.

---

## 1. Whole-body refusals — the method is not there at all

The family the scorers are blind to. A minimal implementation (`return default(T);` / `return null;`) parses,
compiles, and is counted `full`; so does a `throw new Exception("...")` body
(`il2cpp-a-thrown-body-scores-as-a-whole-one`, where a change that broke 691 bodies read as an improvement on
every scorer and passed the Unity gate).

### 1.1 The method is too big

`Model/Contexts/MethodAnalysisContext.cs:348`, with `MaxMethodSizeBytes = 18000` at `:73` (18KB = 4500 arm64
instructions).

```csharp
if (MaxMethodSizeBytes != -1 && RawBytes.Length > MaxMethodSizeBytes)
{
    Logger.WarnNewline($"Method {FullName} is too big ({RawBytes.Length} bytes), skipping analysis.");
    ConvertedIsil = [];
    return;
}
```

- **Export shape**: `ConvertedIsil.Count == 0` is seen at
  `OutputFormats/AsmResolverDllOutputFormatIlRecovery.cs:51` → `ReplaceMethodBodyWithMinimalImplementation()`.
  No marker, no comment, no warning in the body.
- **Logged?** **Yes**, one line per method.
- **Countable today**: `grep -c "is too big" <riprun.log>`.

### 1.2 The method has no code pointer

`MethodAnalysisContext.cs:358-362`

```csharp
if (UnderlyingPointer == 0)
{
    ConvertedIsil = [];
    return;
}
```

- **Export shape**: minimal implementation. No marker.
- **Logged?** **NO. Silent.**
- Mostly legitimate — abstract, interface, `extern`, `[DllImport]` — but nothing separates the legitimate
  ones from a method whose pointer resolution failed. It has to be counted before it can be dismissed.

### 1.3 The lifter produced nothing

`MethodAnalysisContext.cs:364-368`

```csharp
ConvertedIsil = AppContext.InstructionSet.GetIsilFromMethod(this);
...
if (ConvertedIsil.Count == 0)
    return; //Nothing to do, empty function
```

- **Export shape**: minimal implementation. No marker.
- **Logged?** **NO. Silent.**
- Conflates two very different things:
  - the shipped body really is a single `RET` — 105 of the 110 dead bodies in
    `il2cpp-most-of-the-dead-are-not-there` are this, and it is not a defect;
  - `GetArm64MethodBodyAtVirtualAddress` returned nothing or nothing liftable, because the
    next-function-start lookup gave a bad range.
- **These two are indistinguishable in the export today**, and `RawBytes.Length` is right there. Splitting on
  it costs one string interpolation.

### 1.4 The module is skipped by name

`AsmResolverDllOutputFormatIlRecovery.cs:26-42`

```csharp
var shouldSkip = moduleName.StartsWith("UnityEngine.") || moduleName.StartsWith("Unity.") ||
                 moduleName.StartsWith("System.") || moduleName == "System" ||
                 moduleName.StartsWith("mscorlib");
...
if (shouldSkip) { methodDefinition.ReplaceMethodBodyWithMinimalImplementation(); return; }
```

Deliberate and not a defect, but it means **every Unity and BCL body in the export is a stub**. Worth counting
once so that any "how many bodies does this game have" figure has an honest denominator. Not logged.

### 1.5 Not a managed method with a body

`AsmResolverDllOutputFormatIlRecovery.cs:32`: `if (!methodDefinition.IsManagedMethodWithBody()) return;` —
returns **before** `methodDefinition.CilMethodBody = new()`, so nothing is written at all. Abstract /
interface / P-Invoke. Not a defect; listed for completeness.

### 1.6 The top-level catch — anything thrown during analysis or generation

`AsmResolverDllOutputFormatIlRecovery.cs:60-82`

```csharp
catch (Exception e)
{
    var detail = e is DecompilerException ? e.Message : e.ToCollapsedString();
    if (e is DecompilerException) Logger.WarnNewline($"Skipping {methodContext.FullName}: {e.Message}");
    else                         Logger.ErrorNewline($"Decompiling {methodContext.FullName} failed: {detail}");
    ...
    instructions.Add(CilOpCodes.Ldstr, detail);
    instructions.Add(CilOpCodes.Newobj, exceptionCtor);
    instructions.Add(CilOpCodes.Throw);
}
```

- **Export shape**: a one-statement `throw new Exception("<the reason, with a stack trace>")`.
- **Logged?** **Yes**, per method, with the reason — the only whole-body site that already carries a
  per-cause breakdown. `scratchpad-tools/logcensus.py` groups the **error** half by (exception, first fork
  frame). It does **not** parse the `Skipping ` (warning) half — see the gap in 1.7.
- **Countable today**: `grep -c "Decompiling .* failed" <log>`; healthy is **24-39**, jitters by ~2 between
  identical builds, and **730 was a regression that every other measure called a win**. This is the cheapest
  canary in the project and it should be run on every round.
- `markers.py` catches these from the export side under `failed`.

### 1.7 The stack state does not settle

`Analysis/StackAnalyzer.cs:129`, budget `MaxBlockVisitCount = 2000` at `:24`.

```csharp
if (MaxBlockVisitCount != -1 && visitedBlockCount > MaxBlockVisitCount)
    throw new DecompilerException($"Stack state not settling! ({MaxBlockVisitCount} blocks already visited)");
```

A block is re-pushed every time an edge brings it a *different* stack size, so a graph where two paths
disagree oscillates until the budget runs out.

- **Export shape**: caught by 1.6 → `throw new Exception("Decompilation failed: Stack state not settling!")`.
- **Logged?** Yes, through 1.6 — but on the **`Skipping ` (warning)** branch, which `logcensus.py` does not
  read. **This is the only `DecompilerException` thrown anywhere in `Cpp2IL.Core`**, so
  `grep -c "Stack state not settling" <log>` is the whole count and nothing else uses that branch today.

### 1.8 Raw-byte mapping out of bounds

`InstructionSets/NewArmV8InstructionSet.cs:55` — a bare `throw new Exception(...)` when
`MapVirtualAddressToRaw` puts the method outside the binary. Reaches 1.6 on the **error** branch, so it is
logged with a stack trace and `logcensus.py` sees it.

### 1.9 Disassembly threw

`Utils/NewArm64Utils.cs:55-58` — `Disassemble` wraps any Disarm exception in
`throw new($"Failed to disassemble method body: ...")`. Reaches 1.6 on the error branch. Logged.

Also here: `GetArm64MethodBodyAtVirtualAddress`'s **unmanaged path** (`NewArm64Utils.cs:32-46`) grows the
span four bytes at a time until it sees a `B` or an `INVALID`, with no upper bound. It cannot refuse; it can
only run long or read past the method. Worth noting because it is the one loop here with no budget at all.

### 1.10 The last CIL discard — `UnreadableMethodBodyProcessor`

`Source/AssetRipper.Processing/Assemblies/UnreadableMethodBodyProcessor.cs:83-112`, registered at
`Source/AssetRipper.Export.UnityProjects/ExportHandler.cs:92`.

Three checks in order; the **first** to throw names the reason:

| check | reason string |
|---|---|
| `body.Instructions.CalculateOffsets()` | `offsets: <ExceptionTypeName>` |
| `body.VerifyLabels()` | `labels: <ExceptionTypeName>` |
| `body.ComputeMaxStack()` | `stack: <ExceptionTypeName>` |

- **Export shape**: `ReplaceMethodBodyWithMinimalImplementation()`. **No marker.**
- **Logged?** **Yes — and this is the model the whole map should copy.** `Reasons` is a static
  `Dictionary<string,int>`; `Process(IAssemblyManager)` dumps it once at the end:

```
Discarded N unreadable method bodies
  unreadable because stack: StackImbalanceException: 907
```

  That one dump turned 907 anonymous bodies into three named faults. Every silent site above wants the same
  treatment.
- Note `IsReadable` at `:117` is **dead** — `WhyUnreadable` replaced it and nothing calls it. Harmless,
  but it is the kind of thing a rebase reconciles wrongly.

### 1.11 ILSpy could not read the body — `UndecompilableMethodRemoval`

`Source/AssetRipper.Export.UnityProjects/Scripts/UndecompilableMethodRemoval.cs:31-57`

An assembly is decompiled as a whole across threads and one type that throws ends the run — "in one measured
run, nineteen of the largest files in the project, none of which had anything wrong with them". So the
failing method is emptied and the whole decompile is repeated.

- **Export shape**: the method is declared and empty. No marker in the `.cs`.
- **Logged?** **Yes**, one line per method:
  `Emptying <Type>.<Method>, which the decompiler could not read, so that the rest of the assembly is still written.`
- **Countable today**: `grep -c "which the decompiler could not read" <log>`.
- Matched **by name and parameter count**, not by token, and the comment admits "a wrong match among
  overloads would cost one more body and nothing else". So the count is a lower bound on bodies lost here.

### 1.12 The C# would not settle — `InvalidSourceRepair`

`Source/AssetRipper.Export.UnityProjects/Scripts/InvalidSourceRepair.cs`, `MaxAttempts = 24` at `:52`.

The exported source is compiled against its own references up to 24 times; each round comments out the
statement each error points at. On the **last** attempt there is no round left to fix what it finds, so
`FindBodyEdit` (`:508`) replaces the **whole body**:

```csharp
internal const string EmptiedNote = "AssetRipper: emptied, this method could not be repaired statement by statement.";
```

- **Export shape**: for a block body, the statements are **replaced**, leaving `//AssetRipper: emptied, ...`
  plus a `return default;` where one is needed. For an arrow member or a field initialiser, `default
  /*AssetRipper: emptied, ...*/`. The file's own comment says it plainly: *"every measure taken of the export
  believes that, which is how a hundred and sixty emptied methods were being counted as whole"* (`:551-554`).
- **Logged?** **Yes, but only as a total**, and only appended to another line:
  `Commented out N statements that did not compile, in M files, and emptied K methods that would not settle`.
  **No method names, and no per-cause split.**
- **Countable today**: `grep -rc "AssetRipper: emptied" <out>/Assets` — but see the gap below.
- **GAP: `scratchpad-tools/markers.py` does not know this string.** Its `commented` marker is
  `'//AssetRipper: commented out'`; `EmptiedNote` is a different string and matches none of the ten marker
  families. **An emptied method is invisible to `markers.py` today.** Adding
  `('emptied', ['AssetRipper: emptied'])` to `MARKERS` is a one-line fix and is the single cheapest thing on
  this page.
- At `MaxAttempts = 8` this game still emptied **124 methods**, "including the four largest holders of
  branches left in the recovery" (`:47-50`). The current figure at 24 is unrecorded.

### 1.13 Whole-body stubs are already visible in the export — `.il.md`

Not a refusal; the instrument that already exists and appears to be unused.

`Source/AssetRipper.Export.UnityProjects/Scripts/ScriptIlCompanionExporter.cs:141-152`, called
unconditionally from `ScriptDecompiler.cs:52` for every decompiled assembly, writes a `<script>.il.md` beside
every `.cs` and marks each body:

```
Body: stub - no code was recovered, so this is a minimal implementation rather than the method's own code.
Body: analysis failed - <the reason>
Body: none - the method is abstract, external, or implemented by the runtime.
Body: unreadable - <exception message>
```

`IsMinimalImplementation` (`:219`) does not guess — it regenerates the minimal implementation and compares
instruction by instruction, so it agrees with **all three** producers (1.1/1.3 in Cpp2IL, `MethodStubbing`,
and `UnreadableMethodBodyProcessor`) and agrees for the right reason.

**So the whole-body-discard *total* is already countable from any export with**

```sh
grep -rhc "^Body: stub"          <out>/Assets --include='*.il.md' | paste -sd+ | bc
grep -rh  "^Body: analysis failed" <out>/Assets --include='*.il.md' | sort | uniq -c | sort -rn
```

What is missing is **attribution** — which of 1.1/1.2/1.3/1.10 produced each stub. That is what section 4
buys, and it is why section 4 is small: the counting surface exists, only the cause is absent.

### 1.14 A dead statistic

`OutputFormats/AsmResolverDllOutputFormat.cs:58-62` prints
`X% of methods successfully decompiled (S / T)` from `TotalMethodCount`/`SuccessfulMethodCount`, at the end of
`DoOutput`. **AssetRipper never calls `DoOutput`** — `IL2CppManager.cs:156` calls `BuildAssemblies` directly.
So those two counters are incremented on every method (`IlRecovery.cs:47`, `:58`) and **never printed** in
this pipeline. Any end-of-run dump has to go at the end of `BuildAssemblies` (`:121`), not `DoOutput`.

---

## 2. Block-level and edge-level refusals — part of the method is not there

These do not discard the body. They discard a **branch**, a **block**, or an **edge**, so the body still
compiles, still scores `full`, and now computes something else.
`il2cpp-decisions-are-the-unmeasured-half` is about exactly this family, and `decisions.py` is the only
scorer that looks at it.

### 2.1 A jump whose target is not in the method

`InstructionSets/NewArmV8InstructionSet.cs:100-108`

```csharp
var targetIndex = addresses.FindIndex(addr => addr == targetAddress);
if (targetIndex == -1)
{
    instruction.OpCode = OpCode.Invalid;
    instruction.Operands = [$"Jump target not found in method: 0x{targetAddress:X4}"];
    continue;
}
```

This is what `Utils/NewArm64Utils.Refused.cs` was written against, and its doc comment is the measurement:
**5240 refused words in 973 methods, 94 methods with a refused word that is branched to.** Disarm reports a
word it cannot decode as `INVALID` **with address zero**, so it is not in `addresses`, so every branch to it
dies — "and any branch to it becomes `Invalid`, which takes the branch, its block, and everything the block
decided, out of the method."

The three non-SIMD shapes holding 95 of those branch targets are now decoded by that file
(`FCVT Sd,Dn` 393 sites / 80 branched to; `LDRSB` unsigned-immediate 79 / 14; `UMULH`/`SMULH` 26 / 1).
**What remains is Advanced SIMD, which is a project of its own.**

- **Export shape**: `_ = "Invalid instruction: ..."` (`IlGenerator.cs:259-262`, after `SilenceTraces`
  rewrites the `Console.WriteLine`). Visible to `markers.py` under `unknown`.
- **Logged?** Not to the log; the marker is in the body.

### 2.2 An instruction with no case in the lifter

`NewArmV8InstructionSet.cs:1161-1163` — the switch `default:`:

```csharp
default:
    Add(address, OpCode.NotImplemented, $"Instruction {instruction.Mnemonic} not yet implemented.");
    break;
```

Five `NotImplemented` sites in all, three of them with a *named* reason, which is far more useful than the
mnemonic:

| line | reason |
|---|---|
| `:599` | `B.<cond>` with no comparison in scope |
| `:858`, `:883` | (load/store family, unhandled shape) |
| `:1077` | `CSET.<cond>` with no comparison in scope |
| `:1144` | `CSEL`-family `.<cond>` with no comparison in scope |
| `:1162` | the `default:` — mnemonic only |

**The `goto default` paths are the gap, and there are ten of them.** All arrive at `:1162` and are written
out blaming the *mnemonic*, when the real cause is something else entirely:

| site | mnemonic | real reason, lost | emitted first? |
|---|---|---|---|
| `:252` | `FMOV` immediate | `FloatImmediate` could not expand the eight bits | no |
| `:580` | `B.<cond>` | `ConditionalCompare.Apply` failed | **yes** |
| `:929` | `FMINNM`/`FRINTM`/`FRINTP`… | `MathCallOperands` found no operands | no |
| `:944` | (guarded compare) | no comparison, or `ConditionalCompare.Guard` refused | **maybe** — `Guard` takes the emit callback |
| `:958` | (absolute difference) | `AbsoluteDifferenceOperands` or `SubtractedOperand` refused | no |
| `:973` | (rounding conversion) | `RoundingConversionOperands` refused | no |
| `:1065` | `CSET`/`CSETM` | `ConditionalCompare.Apply` failed | **yes** |
| `:1094` | `CINC`/`CINV`/`CNEG` | no comparison in scope, or no relational opcode | no |
| `:1100` | `CINC`/`CINV`/`CNEG` | `ConditionalCompare.Apply` failed | **yes** |
| `:1128` | `CSEL`/`FCSEL`/`CSINC`/`CSINV`/`CSNEG` | `ConditionalCompare.Apply` failed | **yes** |

Two separate problems here:

1. **Mis-attribution.** A census of `Not implemented instruction: <mnemonic>` over the export
   **over-counts `CSET`/`CSEL`/`CINC`/`B.<cond>`/`FMOV` as unimplemented mnemonics** when they are
   implemented and were refused for want of a comparison, or for want of a `ccmp` second half. Passing the
   reason instead of the mnemonic at these ten sites is a ten-line change that makes the existing
   `markers.py` `notimpl` census truthful, and it is the difference between "implement FMOV" and "find the
   missing comparison" as the next round's work.

2. **Partial emission — a correctness hazard, not a marker one.** At `:580`, `:1065`, `:1100` and `:1128`
   the shape is

   ```csharp
   MarkUnsigned(cond, Add(address, relational, condition, ComparisonSide(...), ComparisonSide(...)));
   if (!ConditionalCompare.Apply(cond, condition, ...))
       goto default;                       // and now ALSO emits NotImplemented
   ```

   The relational comparison **has already been added to the ISIL** when the refusal happens. So the body
   keeps half a lifted `ccmp` — one side of an `&&` written as if it were the whole condition — followed by
   a marker that names the mnemonic. `il2cpp-conditional-compare-is-the-and` is what the second half was
   for. Half a condition that compiles is exactly the "whole and wrong" family CLAUDE.md says to weigh above
   compilability, and it is invisible to every scorer except `decisions.py` and the corpus oracle.
   `:944` is the same risk one step less certain: `ConditionalCompare.Guard` is handed the emit callback
   before its result is tested.

- **Export shape**: `_ = "Not implemented instruction: ..."` (`IlGenerator.cs:264-267`). `markers.py`
  `notimpl`.

### 2.3 A conditional jump with no false successor — **silent**

`IlGenerator.cs:131-134`

```csharp
var falseSuccessor = block.Successors.FirstOrDefault(s => s != trueTarget && s != context.ControlFlowGraph.ExitBlock);
if (falseSuccessor == null) continue;
```

No bridging `br` is emitted, so the block falls through into whatever `LayoutOrder` happened to put next.

- **Export shape**: **nothing.** No marker, no warning, no log line. The body compiles and takes a path the
  program never had.
- **This is the highest-value silent site in the file.**

### 2.4 A non-branching block with no successor — **silent**

`IlGenerator.cs:141-143` — the same `continue` on the fall-through path. Same silence, same consequence.

### 2.5 A branch target block that is not in the CFG

`IlGenerator.cs:159-163` — `context.AddWarning($"Branch target block not in cfg: ...")` then `StopBranching`.

`StopBranching` (`IlGenerator.Fork.cs:2780`) is fault 3 of `il2cpp-stack-imbalance-is-the-ceiling`: it picks
`Pop` where the branch pops the condition and `Nop` otherwise, because dropping a `brtrue` outright strands
the condition and unbalances the stack.

- **Export shape**: the warning is appended **at the end of the body** (`IlGenerator.cs:200-204`), not at the
  site — `_ = "Warning: Branch target block not in cfg: ..."`.
- **Countable today**: `grep -r "Warning: Branch target block not in cfg" <out>/Assets`.

### 2.6 A branch target not in the ISIL→IL map

`IlGenerator.cs:168-172` — `AddWarning($"Branch target not in ISIL to IL map: ...")` + `StopBranching`. Fires
when the target instruction produced **no CIL at all**, i.e. an earlier pass emptied it
(`il2cpp-a-branch-to-an-emptied-block`).

### 2.7 A pending block branch that cannot be resolved

`IlGenerator.cs:182-188`

```csharp
var target = ResolveBlockEntryInstruction(targetBlock, blockEntryMap);
if (target == null)
{
    context.AddWarning($"Unable to resolve branch target block: {targetBlock}");
    branchInstruction.OpCode = CilOpCodes.Nop;   // the branch is deleted outright
```

`ResolveBlockEntryInstruction` (`:225-241`) walks successors for any block that did emit CIL; `null` means the
whole reachable tail emitted nothing.

### 2.8 A block with no instructions is skipped — **silent**

`IlGenerator.cs:115-116`: `if (block.Instructions.Count == 0) continue;` — so it never gets a `blockEntryMap`
entry, which is what makes 2.7 fire. Silent by design. `FirstInstructionFrom`
(`IlGenerator.Fork.cs:2746`) exists to paper over it and its doc comment shows the damage when it does
not: the comparison is still computed and then discarded, `_ = cells.Length < 4;`.

### 2.9 The stack does not balance at the exit — a warning, not a discard

`Analysis/StackAnalyzer.cs:38-43`

```csharp
if (analyzer._outGoingState.TryGetValue(graph.ExitBlock, out var outDelta) && outDelta.Size != 0)
    method.AddWarning($"Method ends with non empty stack ({outText}), the output could be wrong!");
```

Note the comment above it: when the exit block is **never reached** the check is skipped entirely, so a method
every path of which loops or throws is never balance-checked at all.

### 2.10 Unreachable and empty blocks are removed

`StackAnalyzer.cs:31` `RemoveUnreachableBlocks()`, `:47-48` `RemoveNops()` + `RemoveEmptyBlocks()`. Silent and
mostly right — but a block is unreachable *because* its only edge in became an `Invalid` branch (2.1), so
this is where 2.1's cost is actually paid, one step removed from the cause.

### 2.11 A recovered catch clause that cannot be written

`IlGenerator.Fork.cs:2454-2670`. Refusals, each reported only when `CATCH_TRACE=1`
(`IlGenerator.Fork.cs:2688-2692`, to stderr):

| line | reason |
|---|---|
| `:2458` | more than one clause, or none — **plain `return`, not even a `Refused` call** |
| `:2470` | the clause's own blocks emitted nothing |
| `:2480` | the ranges do not hold: `try a..b, handler c` |
| `:2486` | no type reference for the caught type |
| `:2543` | the try is left conditionally and the arms cannot be told apart |
| `:2559` | the try is left by a switch, which has no leave of its own |

- **Export shape**: the `try`/`catch` simply is not written. The guarded code is still there; the handler is
  laid out last by `LayoutOrder` (`IlGenerator.Fork.cs:584-643`) and reads as unreachable straight-line code.
- Upstream of it, `Analysis/CatchClauses.cs` has **its own census** — `CATCH_CENSUS=1`, dumped to stderr every
  500 throws (`:83-98`) — with 14 named reasons at `:139`-`:590`. This is the second in-tree model for
  section 4, and the better one for a *reason* split.

### 2.12 The lane model's refusals

`InstructionSets/VectorLanes.cs:157` — `Refused`, a `ConcurrentQueue<string>` recording each refused word
together with the register state at the time (`:200-206`), gated on `VectorLanes.Reporting`.

**`Reporting` is set only by `scratchpad/probe`/`probe2` through reflection** (`probe/Program.cs:188`), so it
is **off in every export**. `il2cpp-measure-the-refusal-not-the-marker` is the round this instrument won:
84 markers → 32, four named causes, two of which were not lane problems at all.
`VectorLanes.cs:1372` records the standing figure: **1550 refusals across 235 methods are instructions this
file can already decode and declines only because no lane of a source was ever written.**

---

## 3. Ranked by expected frequency

Ranked by **bodies or blocks lost per export**, with what the ranking rests on. Nothing here is measured in
this session; the numbers quoted are from in-tree doc comments and from memory files, both of which name the
build they were taken on.

| # | site | file:line | expected | why it is ranked there | logged? | shape in the export |
|---|---|---|---|---|---|---|
| 1 | **1.4** module skipped by name | `IlRecovery.cs:39` | **tens of thousands** | every method of `mscorlib`, `System.*`, `UnityEngine.*`, `Unity.*` — by far the largest count, and entirely deliberate | no | stub |
| 2 | **1.2** no code pointer | `MethodAnalysisContext.cs:358` | **thousands** | every abstract, interface, extern and `[DllImport]` member in the game plus the framework; the game alone has 182 iterator `Dispose`s of which 138 are a bare `RET` | **NO** | stub |
| 3 | **1.3** lifter produced nothing | `MethodAnalysisContext.cs:367` | **hundreds** | 105 of 110 dead bodies measured against 2073 originals were legitimately empty; that is the game only, so the whole export is larger | **NO** | stub |
| 4 | **2.2** unimplemented instruction | `NewArmV8InstructionSet.cs:1162`, +5 named sites, +10 `goto default` | **thousands of sites** | 5240 words Disarm refuses across 973 methods, mostly Advanced SIMD; plus 1550 lane refusals across 235 methods that are decodable and declined | marker only | `notimpl` |
| 5 | **2.8** emptied block skipped | `IlGenerator.cs:115` | **hundreds-thousands** | `DeadCodeEliminator` runs four times in `Analyze()` and `RemoveEmptyBlocks` runs every stack analysis; emptying a block is routine, and each one is a potential 2.7 | **NO** | nothing |
| 6 | **2.1** jump target not in method | `NewArmV8InstructionSet.cs:105` | **~94 methods, falling** | measured directly in `NewArm64Utils.Refused.cs`; the three shapes holding 95 branch targets are now decoded, so what is left is the SIMD tail | marker only | `unknown` |
| 7 | **1.12** C# would not settle | `InvalidSourceRepair.cs:508` | **~100+** | 124 methods at `MaxAttempts = 8`; unrecorded at 24, but the four largest holders of branches were among them | total only | **invisible to `markers.py`** |
| 8 | **2.3 / 2.4** branch with no successor | `IlGenerator.cs:134`, `:143` | **unknown — and that is the finding** | fires exactly where 2.8 and 2.10 have removed a successor, so it should track item 5; costs *correctness*, not compilability, which is the axis CLAUDE.md says to weigh highest | **NO** | nothing |
| 9 | **2.5-2.7** branch fixups | `IlGenerator.cs:159`, `:168`, `:182` | **tens-hundreds** | the residue of 2.1 and 2.8 after `StopBranching`; three fixes took the related discard count 907→0, so the population is real but has been worked | warning in body | `Warning: ...` |
| 9b | **2.2 partial** half a condition then a marker | `NewArmV8InstructionSet.cs:580`, `:1065`, `:1100`, `:1128` | **unknown, ranked on severity not size** | a subset of item 4, but it is the only *refusal* that leaves a **wrong value** behind rather than an absence — one side of an `&&` standing as the whole condition. Ranked here on cost per occurrence, not on count | marker names the mnemonic, not this | `notimpl` beside a working-looking comparison |
| 10 | **1.10** unreadable CIL | `UnreadableMethodBodyProcessor.cs:83` | **~0, until it isn't** | 907 → 0 across three fixes; it is a *detector of generator regressions*, and its value is that it is already attributed | **yes, attributed** | stub |
| 11 | **1.6 / 1.8 / 1.9** analysis threw | `IlRecovery.cs:60` | **24-39** | measured across builds 1.0.400-1.0.453; **730 once, and every scorer called it an improvement** | **yes, per method** | `throw new` |
| 12 | **2.11** catch not written | `IlGenerator.Fork.cs:2454+` | **tens** | `il2cpp-two-of-the-four-throwers-have-no-handler`; `ExceptionEdges` deliberately keeps only one clause | `CATCH_TRACE` only | try/catch absent |
| 13 | **1.11** ILSpy threw | `UndecompilableMethodRemoval.cs:32` | **single digits** | rare enough that the retry loop is affordable; one occurrence once cost 19 files | **yes, per method** | empty method |
| 14 | **1.1** method too big | `MethodAnalysisContext.cs:348` | **single digits** | 18KB is 4500 instructions; only giant switch dispatchers and state machines reach it | **yes, per method** | stub |
| 15 | **1.7** stack not settling | `StackAnalyzer.cs:129` | **single digits** | 2000 block visits is a generous budget, and the `Skipping ` branch is otherwise unused | yes, via 1.6 | `throw new` |

### What the ranking says to do

- **Items 2, 3, 5, 8 are the whole silent population**, and three of the four are one `RawBytes.Length` away
  from being split into "legitimately empty" and "we lost it". That is the case for section 4.
- **Item 8 is the one to fix, not just count.** It is the only site that is silent *and* changes what the
  program does, and CLAUDE.md is explicit that a correctness loss outranks a compilability loss.
- **Item 7 is one line in `markers.py`** and closes a hole that hid 160 methods once already.
- **Item 11 is already free** and should be a standing check on every round, not just before staging:
  `grep -c "Decompiling .* failed"`, believe a move of ten, ignore anything smaller.
- **Item 9b needs no census either** — it is four `if` statements read off the source, and the fix is to
  refuse *before* emitting rather than after. It is the only entry on this page that is a bug rather than a
  measurement gap.

---

## 4. The smallest instrumentation that would attribute all of it

One export, one env var, one dump. Modelled on `UnreadableMethodBodyProcessor.Reasons` (a
`Dictionary<string,int>` dumped once at the end, which is what turned 907 into three faults) and on
`Analysis/CatchClauses.Census` (`:83-98`, env-gated, `lock`ed, one line per reason). It is the same shape as
both, and it borrows the gate style from `ForkPipeline.Trace` and `IFACE_TRACE`.

### 4a. One new file — `External/Cpp2IL/Cpp2IL.Core/Analysis/RefusalCensus.cs`, ~45 lines

A file upstream does not have, in the directory CLAUDE.md names for fork passes.

```csharp
namespace Cpp2IL.Core.Analysis;

/// <summary>Counts, and does nothing else: every place a body, a block or an edge is given up on.</summary>
/// <remarks>
/// A refusal that logs nothing cannot be counted, and a body replaced by a minimal implementation carries no
/// marker - which is how 907 discarded bodies were scored `full` by two scorers at once. Gated on
/// REFUSAL_CENSUS=1; with the variable unset every call returns on the first line, so a build carrying this
/// is also a re-measured baseline.
/// </remarks>
public static class RefusalCensus
{
    private static readonly bool On = System.Environment.GetEnvironmentVariable("REFUSAL_CENSUS") == "1";
    private static readonly Dictionary<string, int> Counts = new();

    public static void Count(string why, int howMany = 1)
    {
        if (!On || howMany == 0)
            return;

        lock (Counts)
            Counts[why] = Counts.GetValueOrDefault(why) + howMany;
    }

    /// <summary>The constant head of a formatted warning, so that N sites do not become N distinct rows.</summary>
    public static string Head(string message)
    {
        var cut = message.IndexOf(':');
        return cut < 0 ? message : message[..cut];
    }

    public static void Dump()
    {
        if (!On)
            return;

        lock (Counts)
        {
            Logger.InfoNewline($"Refusal census: {Counts.Values.Sum()} in {Counts.Count} kinds", "Refusals");

            foreach (var (why, n) in Counts.OrderByDescending(k => k.Value))
                Logger.InfoNewline($"  refused {why}: {n}", "Refusals");
        }
    }
}
```

`lock` rather than `ConcurrentDictionary` because `FillMethodBodies` runs under `MiscUtils.ExecuteParallel`
and `CatchClauses` already established the pattern; the `On` check short-circuits before the lock, so an
ordinary export pays one static bool read per call.

### 4b. Thirteen one-line insertions in four upstream files

Each is one statement with the bulk in the fork's own file, which is the rule CLAUDE.md sets for upstream
edits.

**`Model/Contexts/MethodAnalysisContext.cs` — 4 lines**

| after | line |
|---|---|
| `:349` (too big) | `Analysis.RefusalCensus.Count("body: over 18KB");` |
| `:360` (no pointer) | `Analysis.RefusalCensus.Count("body: no code pointer");` |
| `:368` (lifted nothing) | `Analysis.RefusalCensus.Count(RawBytes.Length <= 8 ? "body: one word, nothing to lift" : $"body: {RawBytes.Length} bytes lifted to nothing");` |
| `:440` (`AddWarning`) | change the expression body to a block and add `Analysis.RefusalCensus.Count("warn: " + Analysis.RefusalCensus.Head(warning));` |

The third is what splits site 1.3's honest half from its dishonest half — the whole reason 105 of 110 dead
bodies took a subagent and three separate measurements to classify.

The fourth is the highest-leverage line on the page: **one insertion covers every `AddWarning` site at
once** — 2.5, 2.6, 2.7, 2.9 and anything a future pass adds — and `Head` collapses
`"Branch target block not in cfg: <the whole instruction>"` to `"Branch target block not in cfg"` so the rows
stay countable. It also does not change `AddWarning`'s signature, which is the mergeability rule.

**`OutputFormats/AsmResolverDllOutputFormatIlRecovery.cs` — 3 lines**

| after | line |
|---|---|
| `:41` (module skipped) | `Analysis.RefusalCensus.Count("body: module skipped by name");` |
| `:52` (`ConvertedIsil.Count == 0`) | `Analysis.RefusalCensus.Count("body: stub written");` — the roll-up, so the three causes above must sum to it |
| `:65` (in the catch) | `Analysis.RefusalCensus.Count(e is DecompilerException ? "threw: " + e.Message : "threw: " + e.GetType().Name);` |

**`IlGenerator.cs` — 5 lines**

| at | line |
|---|---|
| `:116` (empty block) | `Analysis.RefusalCensus.Count("block: emptied before generation");` |
| `:134` (no false successor) | `Analysis.RefusalCensus.Count("edge: conditional jump with no false successor");` |
| `:143` (no successor) | `Analysis.RefusalCensus.Count("edge: fall-through with no successor");` |
| `:260` (`case OpCode.Invalid:`) | `Analysis.RefusalCensus.Count("isil: invalid, " + Analysis.RefusalCensus.Head(instruction.ToString()));` |
| `:265` (`case OpCode.NotImplemented:`) | `Analysis.RefusalCensus.Count("isil: " + instruction.Operands[0]);` — already one short constant string per shape |

Counting `Invalid`/`NotImplemented` **at generation** rather than at lifting is deliberate: an instruction a
later pass deleted did not cost anything, and only what reaches the body should be charged.

**`OutputFormats/AsmResolverDllOutputFormat.cs` — 1 line**

At `:121`, immediately before `TypeDefinitionsAsmResolver.Reset();`, add `Analysis.RefusalCensus.Dump();`.

**Not at the end of `DoOutput`** — see 1.14: AssetRipper calls `BuildAssemblies` directly and `DoOutput` never
runs, which is why the existing "X% of methods successfully decompiled" line has never appeared in a riprun
log. Putting the dump in `DoOutput` would produce a silent no-op and cost a round to notice.

### 4c. Total

| | |
|---|---|
| new files | **1** (`Analysis/RefusalCensus.cs`, ~45 lines) |
| upstream files touched | **4** |
| lines added to upstream files | **13** (12 `Count` calls + 1 `Dump`), of which one converts an expression-bodied method to a block |
| upstream signatures changed | **0** |
| cost when `REFUSAL_CENSUS` is unset | one static `bool` read per call site |

`External/Cpp2IL/FORK.md` needs a row for `Model/Contexts/MethodAnalysisContext.cs`,
`OutputFormats/AsmResolverDllOutputFormatIlRecovery.cs`, `OutputFormats/AsmResolverDllOutputFormat.cs` and
`IlGenerator.cs` if they are not already listed — in the same edit, per CLAUDE.md.

### 4d. What one export then says

```
Refusal census: 41302 in 23 kinds
  refused body: module skipped by name: 31877
  refused body: no code pointer: 4120
  refused isil: Not implemented instruction. FADD: 1550
  refused body: one word, nothing to lift: 388
  refused edge: conditional jump with no false successor: 61
  refused body: 1284 bytes lifted to nothing: 9
  ...
```

(Illustrative shape, not a prediction.) The three questions it settles that nothing currently can:

1. **How many bodies are missing for a reason that is not "the method really is empty"** — items 2, 3, 5 of
   the ranking, which is the whole silent population.
2. **How often 2.3/2.4 fire** — the one site that is silent *and* wrong, where the export shows nothing at
   all today.
3. **Whether a change moved any of it**, from one number per site rather than by reading bodies.

### 4e. Three fixes that need no census at all

Worth doing first because they cost nothing and close known holes:

1. **`scratchpad-tools/markers.py`**: add `('emptied', ['AssetRipper: emptied'])` to `MARKERS`. One line;
   makes site 1.12 visible to every scorer that uses it. 160 emptied methods were scored `full` once already.
2. **`scratchpad-tools/logcensus.py`**: it matches only `Decompiling ... failed` (the error branch of 1.6).
   Add the warning branch, `Skipping (.+?): (.*)`, so 1.7 and every future `DecompilerException` is counted
   too. Two lines.
3. **`NewArmV8InstructionSet.cs`**: the ten `goto default` sites (`:252`, `:580`, `:929`, `:944`, `:958`,
   `:973`, `:1065`, `:1094`, `:1100`, `:1128`) should carry their own reason rather than falling into the
   mnemonic-only `default:`. Ten lines, and it stops the `notimpl` census blaming
   `CSET`/`CSEL`/`CINC`/`B.<cond>`/`FMOV` for refusals that are really about a missing comparison. Separately
   — and this one is a *correctness* fix, not an instrumentation one — the four that refuse **after** having
   already emitted the relational compare (`:580`, `:1065`, `:1100`, `:1128`) leave half a condition standing
   in the body. See 2.2.
