# Unguarded throw sites that cost a whole method body

`AsmResolverDllOutputFormatIlRecovery.FillMethodBody` (`OutputFormats/AsmResolverDllOutputFormatIlRecovery.cs:45`)
wraps `methodContext.Analyze()` + `IlGenerator.GenerateIl` in one `try`/`catch (Exception)`. Anything thrown
below it — a pass, the lifter, the graph builder, the resolver — does not cost the statement that failed. It
costs the **entire body**, rewritten as `throw new Exception(...)`. Every site below is therefore worth the
whole method, and the only thing that separates them is how often the input arrives.

Model fix: `Utils/GenericInstantiation.Fork.cs` — a fork-side helper, one line changed upstream.

Status: IN PROGRESS (written as found, not saved up).

---
## How the search was run

- Every `Analysis/*.cs` fork pass runs per method inside the `try` (`ForkPipeline` hooks called from
  `MethodAnalysisContext.Analyze()`, `Model/Contexts/MethodAnalysisContext.cs:346-...`). So does the lifter
  (`GetIsilFromMethod`, line 364), the graph build (370), `StackAnalyzer` (376), `DominatorInfo` (382),
  `SsaForm.Build` (385), `LocalVariables.CreateAll` (386), `MetadataResolver.ResolveAll` (397) and
  `IlGenerator.GenerateIl`. A throw in any of them is one dead body.
- Mechanical scans (scripts in the session scratchpad): all `.Operands[N]` with no reachable Count/pattern
  guard; all `Convert.To*` with no enclosing `try`; all `.First()/.Single()/.Last()/.Max()`; all
  `Dictionary[key]` reads; all hard casts of ISIL operands; all `throw new`; all `/` and `%` by a
  size/width/count.

## What the scan says about where the defects are

**The fork passes are already hardened; the upstream files are not.** Every fork pass I checked that could
have had this defect has the guard: `ExceptionTable.For` wraps the whole read in `try/catch` and answers `[]`
(`Analysis/ExceptionTable.cs:66-73`); `RgctxResolver.ResolveEntry` bounds-checks the table index
(`Analysis/RgctxResolver.cs:96`); `FieldFromItsRuntimeInfo` bounds-checks the field index
(`Analysis/FieldFromItsRuntimeInfo.cs:150`); `ArrayInitialiserHandle` bounds-checks `fieldRefs` **and**
catches the `KeyNotFoundException` out of `ResolveContextForField` (`Analysis/ArrayInitialiserHandle.cs:128,
136-143`); `StandInCopyType` compares `GenericArguments.Count` before indexing either list
(`Analysis/StandInCopyType.cs:138`); `TypeTestNarrowing`, `ConstantBranchFolding`, `ZeroOnATestedEdge`,
`ListIdiomRecovery` all check `Instructions.Count == 0` before `Instructions[^1]`; ten of the twelve bare
`Convert.ToInt64` sites are preceded by an `is (long or int or ulong or uint)` type test.

So the ranking below is dominated by **upstream Cpp2IL code that the fork now feeds shapes it was never
written for** — `LocalVariables.cs`, `DominatorInfo.cs`, `Instruction.cs`, `UnreachableBlockRemover.cs` — plus
the handful of fork sites where the guard is present on one line and missing on the neighbouring one.

---

## What the scan found overall

**The fork's own analysis passes are already hardened; the code around them is not.** Every fork pass that
could have carried this defect has the guard: `ExceptionTable.For` wraps its whole read in `try/catch` and
answers `[]` (`Analysis/ExceptionTable.cs:66-73`); `RgctxResolver.ResolveEntry` bounds-checks the table index
(`Analysis/RgctxResolver.cs:96`); `FieldFromItsRuntimeInfo` bounds-checks the field index
(`:150`); `ArrayInitialiserHandle` bounds-checks `fieldRefs` **and** catches the `KeyNotFoundException` out of
`ResolveContextForField` (`:128`, `:136-143`); `StandInCopyType` compares both `GenericArguments.Count`s
before indexing either (`:138`); `TypeTestNarrowing`, `ConstantBranchFolding`, `ZeroOnATestedEdge`,
`ListIdiomRecovery`, `ArrayStoreCheckRemover` all check `Count` before `Instructions[^1]` / `Successors[1]`;
ten of the twelve bare `Convert.ToInt64` sites are preceded by an `is (long or int or ulong or uint)` test;
and there is **no** `throw new NotImplementedException` in any `default:` arm of `IlGenerator` — every one
emits a placeholder instead.

So the ranking is dominated by five places the hardening did not reach:

0. **The disassembler boundary** — `Utils/NewArm64Utils.cs:57` re-throws every exception Disarm raises, at the
   first statement of the first thing `Analyze()` calls. Finding 1, and the only CERTAIN one on the list.
1. **`IlGenerator`**, which runs last and sees whatever ~150 passes left behind.
2. **The graph plumbing** (`Graphs/ISILControlFlowGraph.cs`, `Graphs/DominatorInfo.cs`), where an edge list and
   a block list can disagree.
3. **The type/metadata resolvers** (`Utils/Il2CppTypeToContext.cs`, `Model/Contexts/*`), whose `else` arms and
   `?? throw new(...)` tails assume metadata shapes a real build does not always have.
4. **`ISIL/Instruction.cs` itself**, whose `Sources`/`Destination` index by OpCode with no arity check.

The arm64 lifter is otherwise in better shape than expected: its `default:` arm emits `OpCode.NotImplemented`
rather than throwing (`NewArmV8InstructionSet.cs:1161-1163`), it parses no register names, and it is Disarm
rather than Capstone, so a mis-read operand yields `0`/`INVALID` instead of an exception. Its alias-arity
defects are real but **silent** — see "Wrong value, not an exception" below, where
`ConditionalCompare.cs:99` is worth a corpus round on its own because it changes what the recovered code
computes without costing a single `full`.

A recurring tell worth naming: **the guard is present on one line and missing on the next**. `IlGenerator.cs`
guards `Operands.Count > 2` at `:580` and reads `Operands[2]` unguarded at `:612`; `LocalVariables.cs` tests
`is not Register reg` at `:117` and hard-casts `(Register)` at `:131`; `CatchClauses.Detach` unhooks edges with
`RemoveAll` at `:721-724` while five other removal sites use `Remove`. Where that asymmetry appears, the
short shape has usually already been seen.

---

## Ranked findings

### 1. `Utils/NewArm64Utils.cs:57` — the disassembler's own exceptions are re-thrown, at the first statement of the method

```csharp
private static List<Arm64Instruction> Disassemble(ReadOnlySpan<byte> bytes, ulong virtAddress)
{
    try
    {
        return Disassembler.Disassemble(bytes, virtAddress, new Disassembler.Options(true, true, false)).ToList();
    }
    catch (Exception e)
    {
        throw new($"Failed to disassemble method body: …", e);
    }
}
```

Reached from `InstructionSets/NewArmV8InstructionSet.cs:71`
`var insns = NewArm64Utils.GetArm64MethodBodyAtVirtualAddress(context.AppContext.Binary, context.UnderlyingPointer);`
— the **first statement of `GetIsilFromMethod`**, which is the first thing `Analyze()` calls
(`Model/Contexts/MethodAnalysisContext.cs:364`). Nothing has been recovered yet, so this costs 100% of the
method every time, and the `catch` above turns a recoverable decode failure into exactly that.

**The lifter is Disarm, not Capstone** (`disarm 2022.1.0-master.99`). The options are
`(RemapAliases: true, ContinueOnError: true, ThrowOnUnimplemented: false)` — but Disarm's
`DisassembleSingleInstruction` only catches **`Arm64UndefinedInstructionException`**. Everything else escapes
`ContinueOnError` and is re-thrown here: **6 `NotImplementedException`, 76 raw `Exception(string)`, 4
`ArgumentOutOfRangeException`, 3 `IndexOutOfRangeException`, 1 `InvalidOperationException`** in Disarm's IL.

**The concrete instruction: `PRFM` / `PRFUM`.** Disarm's two `NotImplementedException` messages read verbatim
*"If you're seeing this, reach out, because PRFM is …"*. `prfm pldl1keep, [x0, #N]` is emitted by clang's
arm64 backend and appears in il2cpp output. Also reachable:
`Exception("Floating-point conversion to/from integer: ptype 0b10 (half precision) is not supported")`,
`Exception("Impossible cmode")`, `Exception("Impossible srcVectorElementWidth")`,
`Exception("Impossible arrangement bits")`, `ArgumentOutOfRangeException(nameof(specifier), … "16B")`.

**Cost:** whole body, and the *entire* body — no partial recovery. **Likelihood: CERTAIN.**
**Cheapest fix on this list:** answer with the instructions decoded so far (or `[]`) instead of re-throwing.
The four other callers of `GetArm64MethodBodyAtVirtualAddress` already wrap their calls in `try/catch`;
`NewArmV8InstructionSet.cs:71` does not.


### 2. `IlGenerator.cs:600-618` — arithmetic and comparison read `Operands[1]`/`[2]` right after proving they may not exist

```csharp
if (SpansSeveralRegisters(instruction.Operands.Count > 1 ? instruction.Operands[1] : null)      // :579
    || SpansSeveralRegisters(instruction.Operands.Count > 2 ? instruction.Operands[2] : null))  // :580
{ … break; }
…
&& instruction.Operands[1] is LocalVariable && instruction.Operands[2] is LocalVariable          // :600
&& LowersToNativeInt(instruction.Operands[1]) != LowersToNativeInt(instruction.Operands[2]);     // :601
var widenToInt64 = (ordered || mixedEquality)
    && (LowersToNativeInt(instruction.Operands[1]) || LowersToNativeInt(instruction.Operands[2]));// :604
var leftType  = ComparableType(TypeOfOperand(instruction.Operands[1]));                          // :611
var rightType = ComparableType(TypeOfOperand(instruction.Operands[2]));                          // :612
LoadOperand(instruction.Operands[1], …);                                                         // :614
LoadOperand(instruction.Operands[2], …);                                                         // :618
```

The case covers `Add, Subtract, Multiply, Divide, ShiftLeft, ShiftRight, And, Or, Xor` and the six `Check*`
comparisons. Lines 579-580 exist because the author found instructions with fewer than three operands;
execution falls straight through them (`SpansSeveralRegisters(null)` is false) into six unguarded reads.
`IlGenerator.Fork.cs:1788` `ComparesUnsigned(instruction.Operands[1], instruction.Operands[2])` is a seventh,
reached from `:624`.

**The input.** A vector arithmetic instruction whose second source register the disassembler leaves
`INVALID`. `InstructionSets/VectorLanes.cs:1425` `Replicate` maps `FADD/ADD→Add`, `FSUB/SUB→Subtract`,
`FMUL/MUL→Multiply`, `FDIV→Divide` (`:2103-2106`) — all three-operand ISIL opcodes — then builds its source
list with `if (register == Arm64Register.INVALID) break;` (`:1445-1446`) and rejects only the empty case
(`:1456` `if (sources.Count == 0) return false;`). With one source it emits
`operands = [Lane(dest), Lane(src)]` — a **two-operand `Add`** — at `:1467-1478`.

**Same instruction also kills `ISIL/Instruction.cs:88-92`** (`OpCode.Add … => [Operands[2], Operands[1]]`), so
in practice the first pass to read `.Sources` on it dies first — `DeadCodeEliminator`, inside the same `catch`,
at the same cost.

**Cost:** whole body. **Exception:** `ArgumentOutOfRangeException`. **Likelihood: LIKELY.** (I could not name
the exact arm64 encoding that leaves `Op2Reg == INVALID` for a vector `ADD`/`MUL`, so this is not CERTAIN —
but the guard at `:579-580` is evidence the shape was met, and the fix is a two-line `Count` check either
way.)

### 3. `Graphs/ISILControlFlowGraph.cs:205-231` — `RemoveEmptyBlocks` re-adds the block it is deleting

```csharp
foreach (var succ in block.Successors)
{
    succ.Predecessors.Remove(block);                       // :218
    foreach (var pred in block.Predecessors)
        if (!succ.Predecessors.Contains(pred))
            succ.Predecessors.Add(pred);                   // :222
}
…
foreach (var block in toRemove)
    Blocks.Remove(block);                                  // :231
```

**When `block` is its own predecessor** — an emptied self-looping block — the inner loop's `pred` *is* `block`,
`succ.Predecessors.Contains(block)` is false because line 218 just removed it, and line 222 **puts it back**.
Line 231 then takes `block` out of `Blocks`. `succ.Predecessors` now names a block the graph no longer has.

That dangling edge detonates at:

```csharp
: new HashSet<Block>(Dominators[block.Predecessors[0]]);        // Graphs/DominatorInfo.cs:74
  tempDoms.IntersectWith(Dominators[block.Predecessors[i]]);    // Graphs/DominatorInfo.cs:77
: new HashSet<Block>(PostDominators[block.Successors[0]]);      // Graphs/DominatorInfo.cs:115
  tempPostDoms.IntersectWith(PostDominators[block.Successors[i]]); // Graphs/DominatorInfo.cs:118
  while (runner != ImmediateDominators[block] && runner != null) // Graphs/DominatorInfo.cs:146
      runner = ImmediateDominators[runner];                      // Graphs/DominatorInfo.cs:149
```

all of which key on `graph.Blocks` only (`:52-58`, `:94-100`). **`KeyNotFoundException`.**

**Where it fires:** `Analysis/CatchClauses.cs:120` `var dominators = new DominatorInfo(graph);` — the one
`DominatorInfo` built *after* the pass storm. `RemoveEmptyBlocks` is called from `StackAnalyzer.cs:50`,
`SsaForm.cs:307` and `Simplifier.cs:36`, the last two well after ~40 passes have nopped instructions
(`NullCheckRemover`, `ClassInitCallRemover`, `RgctxReadRemover`, `RuntimeClassReadRemover`,
`MetadataInitGuardRemover`, `DeadCodeEliminator`). An emptied loop header is exactly what those leave.

**A second, independent route to the same lines: duplicated edges unhooked with `List.Remove`.**
`AddDirectedEdge` (`:504-508`) does not dedupe, and both arms of one conditional jump landing on the same
block is documented and handled elsewhere — `Analysis/SsaForm.cs:172-176` exists solely for "this block
appears in that block's predecessor list more than once". These six sites each remove **one** occurrence:

- `Analysis/UnreachableBlockRemover.cs:63` `successor.Predecessors.Remove(block);`
- `Analysis/UnreachableBlockRemover.cs:66` `predecessor.Successors.Remove(block);`
- `Graphs/ISILControlFlowGraph.cs:126` `successor.Predecessors.Remove(block);`
- `Graphs/ISILControlFlowGraph.cs:128` `predecessor.Successors.Remove(block);`
- `Analysis/MetadataInitGuardRemover.cs:236` and `:238`
- `Analysis/InterfaceCallRecovery.cs:577`, `Analysis/ListIdiomRecovery.cs:292`

`Analysis/CatchClauses.cs:720-724` `Detach` already uses `RemoveAll` for exactly this reason. Third route:
`Analysis/CatchClauses.cs:210` `only.Guarded.Successors.Clear();` empties one side of the edge and never
touches the successors' `Predecessors` lists.

**Cost:** whole body. **Likelihood: LIKELY.**

### 4. `InstructionSets/NewArmV8InstructionSet.cs:500` — the one unconditional `throw` inside the lifting switch

```csharp
var destRegSize = instruction.Op0Reg switch
{
    >= Arm64Register.V0 and <= Arm64Register.V31 => 16,
    >= Arm64Register.D0 and <= Arm64Register.D31 => 8,
    >= Arm64Register.S0 and <= Arm64Register.S31 => 4,
    >= Arm64Register.H0 and <= Arm64Register.H31 => 2,
    >= Arm64Register.W0 and <= Arm64Register.W31 => 4,
    >= Arm64Register.X0 and <= Arm64Register.X31 => 8,
    _ => throw new($"Unknown register size for LDP: {instruction.Op0Reg}")
};
```

Guarded by `case Arm64Mnemonic.LDP when instruction.Op2Kind == Arm64OperandKind.Memory:`. The arms cover
V/D/S/H/W/X; the two values they do **not** cover are `Arm64Register.B0..B31` and — decisively —
`Arm64Register.INVALID`, **which is `0`, the struct's default**. So any Disarm decode path that sets
`Op2Kind = Memory` and returns before assigning `Op0Reg` lands here: the reserved `opc=0b11, V=1` pair
encoding, and any `LDP`-shaped word whose destination-register picker has no case.

This is the only `throw` in the whole switch — every other unhandled shape reaches
`default: Add(address, OpCode.NotImplemented, …)` at `:1161-1163` and costs a statement, not the method.

**Cost:** whole body. **Exception:** `System.Exception`. **Likelihood: POSSIBLE**, and it is a two-word fix
(`_ => 8`).


### 5. `IlGenerator.cs:166` and `:168` — a branch target that is still a `ulong`, or is `null`

```csharp
var target = (Instruction)instruction.Operands[0];   // :166
if (!instructionMap.ContainsKey(target))             // :168
```

Two inputs, both first-class modelled states:

- **Still a raw address.** `ISIL/Instruction.cs:118` and `:121` both match `Operands[0] is ulong jumpTarget`,
  and `Graphs/ISILControlFlowGraph.cs:71-85` performs the identical cast **inside a `try/catch`** precisely
  because it fails; on failure (`:327-328`) it wires the block to `ExitBlock` and **leaves the `ulong` in
  place**. `:400` only rewrites the operand `if (… is Instruction target)`. A tail-call `b` to another
  method, or a branch into a thunk, survives to IL generation as a `ulong`. → `InvalidCastException`.
- **`null`.** `Graphs/ISILControlFlowGraph.cs:401` is `instruction.Operands[0] = FindBlockByInstruction(target)!;`
  and `FindBlockByInstruction` is declared `Block?` (`:435`) — the `!` suppresses a null the method really
  returns when the target instruction was dropped. `:159`'s `is Block` fails, `(Instruction)null` succeeds,
  and `Dictionary.ContainsKey(null)` throws `ArgumentNullException`.

Verified: `IlGenerator.cs:159` handles the `is Block` case with a warning and a `continue`, so the Block shape
is covered and the `Instruction` shape is not. A **third** throw sits two lines on —
`:175 ilBranch.Operand = new CilInstructionLabel(instructionMap[target][0]);` — where the key is checked at
`:168` but the `List<CilInstruction>` value is indexed at `[0]` without a `Count` check, and
`EnsureBranchable` (`Fork.cs:1272`) can leave an entry empty.

**Cost:** whole body. **Likelihood: LIKELY.**

### 6. `Model/Contexts/ConcreteGenericMethodAnalysisContext.cs:91` and `:94` — generic arity, the same root as the fix already made

```csharp
if (baseMethod.DeclaringType!.GenericParameters.Count != typeGenericParameters.Length)
    throw new ArgumentException("The number of type generic parameters must match the number of generic parameters on the declaring type.");
```

**The input is named verbatim in the fix already made** — `GenericInstantiation.Fork.cs` says "a generic
instance built from a shared body records fewer arguments than the definition declares". Any such instance
reaching a pass that names a method on it throws.

Four callers, all inside `Analyze()`:
`Analysis/DelegateInvokeRecovery.Fork.cs:144` `new ConcreteGenericMethodAnalysisContext(invoke, generic.GenericArguments, [])`
— **on every `Action<…>`/`Func<…>` call site in the game** — plus `Analysis/NullablePackedCompare.cs:109`
and `:137`, `Analysis/GenericSharingRecovery.cs:62`, and `Model/Contexts/MethodAnalysisContext.cs:292`.

The producer of the short instance is `Model/Contexts/GenericInstanceTypeAnalysisContext.cs:128`
`declaringType.MakeGenericInstanceType(GenericArguments.Take(declaringType.GenericParameters.Count))` —
`Take` truncates silently rather than failing, manufacturing the mismatched instance that detonates here.

**Cost:** whole body. **Exception:** `ArgumentException`. **Likelihood: LIKELY.**

### 7. `Utils/Il2CppTypeToContext.cs:29` — the `else` arm assumes every remaining type is a generic parameter

```csharp
else
    ret = context.ResolveContextForGenericParameter(type.GetGenericParameterDef()) ?? throw new($"Could not resolve type context for type {type.GetGenericParameterDef().Name}");
```

The chain handles primitives (`:20`), `CLASS`/`VALUETYPE` (`:22`), `GENERICINST` (`:24`), `PTR`/`SZARRAY`/
`ARRAY` (`:26`) — and sends **everything else** to `GetGenericParameterDef()`, which throws
`Exception("Type is not a generic parameter")` for anything that is not `VAR`/`MVAR`.

**The input:** an `Il2CppType` whose enum is `IL2CPP_TYPE_FNPTR` (0x1b — a `delegate*`, which Burst/DOTS and
`UnityEngine.Bindings` emit), `IL2CPP_TYPE_CMOD_REQD` (0x1f), `IL2CPP_TYPE_CMOD_OPT` (0x20),
`IL2CPP_TYPE_PINNED` (0x45), `IL2CPP_TYPE_BOXED` (0x51) or `IL2CPP_TYPE_SENTINEL` (0x41). `IL2CPP_TYPE_BYREF`
is guarded only by a `Debug.Assert` at `:18`, which is compiled out in Release.

**The tell:** `Utils/AsmResolver/ContextToTypeSignature.cs` already has writers for
`CustomModifierTypeAnalysisContext` (`:102`), `SentinelTypeAnalysisContext` (`:39`),
`PinnedTypeAnalysisContext` (`:92`) and `BoxedTypeAnalysisContext` (`:97`) — contexts this resolver can never
produce, because it throws instead.

**Reachable:** `Analysis/MetadataResolver.cs:65`
`instruction.Operands[1] = declaringType.AppContext.ResolveIl2CppType(typeGlobal);` — run for every
`Move local, [absoluteAddress]` in every body; also `Analysis/RgctxResolver.Fork.cs:106` and `:160`, and every
`FieldType`/`ReturnType`/`ParameterType` getter (`Model/Contexts/FieldAnalysisContext.cs:68`,
`MethodAnalysisContext.cs:149`, `ParameterAnalysisContext.cs:91`).

**Cost:** whole body. **Likelihood: LIKELY.**

### 8. `NewArmV8InstructionSet.cs:516`, `:453`, `:291` — `(Register)someMemoryOperand.Base!` where `Base` is null

```csharp
var mem2 = new MemoryOperand((Register)memInternal!.Value.Base!, addend: memInternal.Value.Addend + destRegSize);  // :516  LDP
memory = new MemoryOperand((Register)memory.Base!, addend: memory.Addend + size);                                   // :453  STP
var register = (Register)operand.Base!;                                                                             // :291  pre-indexed
```

`MemoryOperand.Base` is `object?` and `ISIL.Register` is a **struct**, so `(Register)null` is an unboxing
cast — `NullReferenceException`, not a graceful null. `ConvertOperand` returns
`new MemoryOperand(addend: offset)` with `Base == null` whenever `instruction.MemBase == Arm64Register.INVALID`
(`:1240-1242`), so a base-less `ldp`/`stp` — the literal/offset-only form, or any encoding where Disarm leaves
`MemBase` at its default — kills the body.

`:516` carries a second kill on the same line: `memInternal` is `mem as MemoryOperand?`, and
`memInternal!.Value` on a failed `as` is `InvalidOperationException: Nullable object must have a value`.

**A related correctness bug worth fixing in the same edit.** `:283`'s guard `if (instruction.MemIsPreIndexed)`
is `MemIndexMode == 0`, and `Arm64MemoryIndexMode.PreIndex == 0` **is the enum's default** — so the predicate
is true for every instruction whose decoder never sets an index mode, `MOV`/`MOVZ`/`FMOV`/`SXTW` included. It
survives today only because the following `operate is MemoryOperand` test fails. `Fork.cs:1843` gets this
right (`is not (PreIndex or PostIndex)`); `:283`, `Fork.cs:1189` (`Scaled`) and `VectorLanes.cs:1182` do not,
and the last of those refuses stores it should accept. All four `MemIsPreIndexed` uses should be audited
together.

**Cost:** whole body. **Likelihood: POSSIBLE.**


### 9. `IlGenerator.cs:981`, `:995`, `:1027` — `locals[local]` for a local shape the fix-up loop does not walk

```csharp
instructions.Add(CilOpCodes.Ldloc, locals[local]);    // :981
instructions.Add(CilOpCodes.Stloc, locals[local]);    // :995
instructions.Add(CilOpCodes.Stloc, locals[local2]);   // :1027
```

The fix-up loop at `IlGenerator.cs:56-71` — commented *"Make sure context.Locals actually has all locals (idk
why it doesn't sometimes)"*, which is itself the admission — walks only bare `LocalVariable`,
`FieldReference.Local`, and `MemoryOperand.Base is LocalVariable`. Four shapes the Fork then loads locals out
of are not walked:

- `MemoryOperand.Index is LocalVariable` → `IlGenerator.Fork.cs:511`
- `MemoryOperand.Base is FieldReference` → `Fork.cs:540` (`ArrayElement` accepts it explicitly at `Fork.cs:52`)
- `MultiDimensionalElement.Array` / `.Indices` → `Fork.cs:531`, `:511`
- `FloatStructAssembly.Parts` → `Fork.cs:1891`

`Fork.cs:1962`, `:2046`, `:2059`, `:2950`, `:3029` all use `locals.TryGetValue`; these three use the raw
indexer.

**Cost:** whole body. **Exception:** `KeyNotFoundException`. **Likelihood: LIKELY.**

### 10. `IlGenerator.cs:275`, `:283`, `:295`, `:300` — a `Move` with fewer than two operands

```csharp
LoadOperandInto(instruction.Operands[0], instruction.Operands[1], …);   // :300
```

**~20 analysis passes guard exactly this shape** — `Analysis/LocalVariables.cs:299`, `:370`, `:415`;
`LocalVariables.Fork.cs:696`, `:839`; `RgctxReadRemover.cs:41`; `FieldReadSinking.cs:42`;
`ConstantBlobStore.cs:84`; `OutParameterWriteback.cs:137`; `WideFieldStore.cs:70`; `SwitchTableRecovery.cs:109`;
`ArrayTypeInference.cs:38`, `:159`; `FloatConstantInAnInteger.cs:100`; `StackedFloatArgument.cs:206`;
`StateMachineConstructorRecovery.cs:38`; `RuntimeClassReadRemover.cs:38`; `WholeStructAtItsOwnType.cs:38`;
`ClassPointerDereference.cs:52` — and `IlGenerator.Fork.cs:169` itself writes `|| instruction.Operands.Count < 2`
for a `Move`. The generator's own `Move` case is the one place that does not. Same family:
`IlGenerator.cs:544` (`ConditionalJump` `Operands[1]`), `:669`/`:673`/`:681` (`Not`/`Negate` `Operands[1]`),
`:312`/`:314`/`:318`/`:328` (`Select` `Operands[3]`), `:419` (`Call` `Operands[0]` on an empty list, which
`Analysis/InterfaceCallRecovery.cs:401` can build), `IlGenerator.Fork.cs:2227` and `IlGenerator.cs:696`/`:340`
(`Newobj` `Operands[0]`).

**Cost:** whole body. **Exception:** `ArgumentOutOfRangeException`. **Likelihood: LIKELY as a family,
POSSIBLE per site.**

### 11. `Analysis/LocalVariables.cs:73` — a `Return` whose value is a constant

```csharp
if (instruction.OpCode != OpCode.Return || instruction.Operands.Count != 1) continue;

var returnLocal = (LocalVariable)instruction.Sources[0];
```

`Instruction.Sources` adds `Operands[0]` for a `Return` and **then filters constants out**
(`ISIL/Instruction.cs:117-119`). `IsConstantValue` (`:161-167`) returns `false` only for `Register`,
`StackOffset`, `LocalVariable` and a non-constant `MemoryOperand` — everything else is constant. So a
one-operand `Return` carrying a constant, a `FieldReference`, a constant `MemoryOperand` or a
`TypeAnalysisContext` passes the `Count != 1` guard, yields an **empty** `Sources`, and `Sources[0]` throws
`ArgumentOutOfRangeException`. If the operand is a `Register` or `StackOffset` instead, the cast throws
`InvalidCastException`.

`ForkPipeline.AfterStackAnalysis` runs `VectorReturnAssembly` and `StackedFloatArgument` immediately before
`CreateAll`, and both rewrite return-value operands.

**Cost:** whole body. **Likelihood: POSSIBLE→LIKELY.**

### 12. `Model/Contexts/ApplicationAnalysisContext.cs:195` — a nullable-returning lookup that throws instead

```csharp
public AssemblyAnalysisContext? GetAssemblyByName(string name)
{
    …
    return AssembliesByName[name];
}
```

Declared `AssemblyAnalysisContext?`; every caller writes `?.` as though a miss returns null. A miss throws
`KeyNotFoundException`. `Analysis/MathIntrinsics.cs:57-58` evaluates
`appContext.GetAssemblyByName("UnityEngine.CoreModule")?.…` **and** `GetAssemblyByName("UnityEngine")?.…`
eagerly (both inside a collection expression) for every single-precision `fabs`/`fsqrt`/`fmin`/`fmax`/
`sincos`/`exp2`/`modf` — and the `?.` proves the author expects null to be the miss result. Same pattern in
`Analysis/MaxWithZeroRecovery.cs:157-158`, `FloatBitsInAnInteger.cs:57`, `ArrayAccessRecovery.cs:453`,
`EmptyArrayRecovery.cs:58`, `RankTwoArrayAccess.cs:113`.

On a Unity 6 build both `UnityEngine.CoreModule` and the `UnityEngine` facade are normally present, so this
is a **landmine rather than a live loss on the current target** — but if either name is missing (engine-code
stripping, or a differently-packaged build) it is **CERTAIN and it kills every method containing a float
intrinsic**, which is a large share of game code. It is a one-word fix (`TryGetValue`).

**Cost:** whole body, times every float-intrinsic method. **Likelihood: UNLIKELY on this build, CERTAIN on a
build without those assemblies.**

### 13. `Analysis/MergedReturnSplitter.cs:308` and `:337` — `First()` over an all-`Nop` block

```csharp
var comparison = returnBlock.Instructions.First(i => i.OpCode != OpCode.Nop);
var result = (LocalVariable)comparison.Operands[0];
```

(identical pair in `SplitEdge` and in `Excise`.) `First(predicate)` throws
`InvalidOperationException("Sequence contains no matching element")` once every instruction in `returnBlock`
has been nopped — and nopping in place rather than removing is what this codebase does everywhere, including
`MergedReturnSplitter` itself at `:104-105` and `:111-112`, inside the same `foreach` that later revisits the
block. The cast then throws `InvalidCastException` if the comparison's destination is not a `LocalVariable`.

Neighbouring: **`Analysis/MergedReturnSplitter.cs:326`**
`predecessor.Successors[predecessor.Successors.IndexOf(returnBlock)] = landing;` — `IndexOf` answers `-1` when
`returnBlock.Predecessors` and `predecessor.Successors` disagree in multiplicity (see finding 2), and
`Successors[-1]` is an `ArgumentOutOfRangeException`.

**Cost:** whole body. **Likelihood: POSSIBLE.**

---

## Continued (14-32), in rough order

14. **`ISIL/Instruction.cs:88-108` and `:39-77`** — `Sources` and `Destination` index `Operands[1]`, `[2]`,
    `[3]` and `[0]` by OpCode with no `Count` check. Every fork pass that rewrites an OpCode currently assigns
    `Operands` in the same statement (`KeyFunctionRecovery.cs:52-53`, `InlinedConstructorRecovery.cs:69-73`,
    `SelectedFieldOffset.cs:103-104`, `ConstantFolding.cs:47-48`, `ListIdiomRecovery.cs:219-220`,
    `InterfaceCallRecovery.cs:418-419`, `:528-529`), so this is **latent, not live** — but it turns any future
    one-line pass that sets only `OpCode` into a silent whole-body loss, and it is the cheapest fix here.

15. **`Model/Contexts/MethodAnalysisContext.cs:149`** —
    `AppContext.ResolveIl2CppType(Definition?.RawReturnType) ?? throw new(…)`, dereferenced by
    `IlGenerator.cs:527`, `:529`, `:530`, `:534` and `Fork.cs:2494`, `:2723` on the `OpCode.Return` case —
    the most-executed opcode in the file. Any method whose raw return type does not resolve (finding 5 is one
    way) loses its whole body here. **LIKELY** as a consequence of 5.

16. **`Model/Contexts/GenericInstanceTypeAnalysisContext.cs:94`** — the `Lazy<T>` cache is in the default
    `ExecutionAndPublication` mode, which **caches the exception** and re-throws the same instance on every
    later `.Value`. One bad `Il2CppType` permanently poisons the entry, so one bad `List<Foo>` costs *every*
    method that mentions it rather than one. Pure amplifier; **CERTAIN if 5 or 14 ever fires**.

17. **`Model/Contexts/ApplicationAnalysisContext.cs:266`, `:271`** —
    `ResolveContextForType(typeOwner)?.GenericParameters[genericParameter.genericParameterIndexInOwner]` — a
    `ushort` metadata index into a list that is `[]` when the owner has a null `GenericContainerIndex`
    (`TypeAnalysisContext.cs:119`, `MethodAnalysisContext.cs:142`). The `Debug.Assert` at `:270` is compiled
    out in Release. **POSSIBLE.**

18. **`Utils/Il2CppTypeToContext.cs:21`** —
    `ReflectionCache.PrimitiveTypeDefinitions[type.Type]` with no `TryGetValue`. The dictionary is populated
    only from `typeDefinition.RawType.Type`, but the gate `IsIl2CppPrimitive()` also admits
    `IL2CPP_TYPE_IL2CPP_TYPE_INDEX` (0xFF, Cpp2IL's synthetic attribute-blob marker, which **no** typedef
    carries) and `IL2CPP_TYPE_TYPEDBYREF`. The sibling `_primitiveTypeCache` needed an explicit
    `TypedReference` fallback for exactly this reason; this dictionary has none. **POSSIBLE.**

19. **`Analysis/LocalVariables.cs:41`, `:49-56`, `:185-199`** — `locals[register]` keyed off a **recomputed**
    `cfg.Instructions` BFS property (`Graphs/ISILControlFlowGraph.cs:15-43`), and `(Register)memory.Base` /
    `(Register)memory.Index` hard casts on a `MemoryOperand` that is **mutated in place**. A `MemoryOperand`
    instance reachable from two instructions is an `InvalidCastException` the second time round. Nothing
    enforces the one-owner invariant. **POSSIBLE.**

20. **Ten bare `Convert.ToInt64` sites** — all preceded by a type test, so no `InvalidCastException`; but
    `Convert.ToInt64(ulong)` throws `OverflowException` at or above 2^63 and `Convert.ToInt64(double)` throws
    for NaN and out-of-range. The two that matter: **`Analysis/FloatStructBroadcast.cs:78`**
    `var bits = System.Convert.ToInt64(value);`, which reads a *bit pattern* — `0x8000000000000000` is the
    double sign mask clang materialises for `fneg`, and `0xFFFFFFFFFFFFFFFF` is `movi v0.2d, #-1`; and
    **`Analysis/MetadataResolver.Fork.cs:611`**, guarded by `.IsNumeric()`
    (`Extensions/MiscExtensions.cs:183-195`), which also admits `decimal`, `double` and `float`. The other
    eight (`FieldAddressRecovery.cs:55`, `FieldAddressThroughCopies.cs:60`, `FieldAddressSinking.cs:59`,
    `:77`, `ArrayAccessRecovery.cs:165`, `:287`, `UntypedBaseCensus.cs:69`,
    `HomogeneousFloatArguments.cs:191`) take an address distance and stay below 2^63 on an Android ELF. The
    right fix for all ten is `unchecked((long)v)`, the way `ConstantFolding.Constant`
    (`Analysis/ConstantFolding.cs:114-126`) already does it — not a `try`. **POSSIBLE for two, UNLIKELY for
    eight.**

21. **`Utils/AsmResolver/ContextToMethodDescriptor.cs:12`** —
    `context.GetExtraData<MethodDefinition>("AsmResolverMethod") ?? throw new(…)`. No method on `<Module>` or
    nested under it ever gets that key: `AsmResolverDllOutputFormat.BuildStubAssembly:197` skips the whole
    subtree. A recovered body that **calls** such a method loses the *caller's* body. This is the exact defect
    already fixed one level up for types (`ContextToTypeSignature.cs:26-29`, whose comment records "25 of them
    in `GoogleMobileAds.Android`, every one lost entirely") — the method-side twin is unfixed. Its sibling
    `ContextToTypeSignature.cs:14`/`:31` has the same gap for a type *nested under* `<Module>`, because the
    fix at `:28` matches only exact `{ Name: "<Module>" }` while `IsTypeContextModule` uses `StartsWith`.
    **POSSIBLE.**

22. **`Analysis/HomogeneousFloatArguments.cs:132`** —
    `new FieldReference(lane, (LocalVariable)instruction.Operands[first + i], 0)`. A call argument cast
    straight to `LocalVariable`; a literal float passed into a `Vector3` constructor is enough.
    `InvalidCastException`. **POSSIBLE.**

23. **`Utils/NewArm64Utils.cs:43`** — `span = allBytes.Slice(pos, span.Length + 4);` in an **unbounded** loop
    (`:38`, `count == -1`) that grows the span 4 bytes at a time until a `B`/`INVALID` appears, entered
    whenever `MiscUtils.GetAddressOfNextFunctionStart` returns 0 (the highest-addressed function, or any
    method whose successor fails `TryMapVirtualAddressToRaw` — `MiscUtils.cs:198-199`). It re-disassembles
    the whole span each iteration, so it is O(n²) before it throws. The four *other* callers of
    `GetArm64MethodBodyAtVirtualAddress` all pass an explicit `count` **and** wrap in `try/catch`;
    `NewArmV8InstructionSet.cs:70` does neither. **POSSIBLE.**

24. **`Model/Contexts/ApplicationAnalysisContext.cs:230`** —
    `ConcreteGenericMethodsByRef.TryGetValue(…) ? context : new(methodReference, this)`. The startup
    population loop wraps the identical construction in try/rethrow (`:158-177`); this call site, reached from
    `Analysis/MetadataResolver.cs:76`, has no guard, caches nothing, and the constructor chain holds three
    `?? throw new(…)` sites (`ConcreteGenericMethodAnalysisContext.cs:127-128`, `:133-134`, `:160-161`). The
    same ref re-throws for every method that references it. **POSSIBLE.**

25. **`Model/Contexts/TypeAnalysisContext.cs:108`** —
    `_interfaceContexts ??= (Definition?.RawInterfaces.Select(AppContext.ResolveIl2CppType).ToList() ?? [])!;`
    — one unresolvable interface out of a list takes down the whole property, and `??=` means the failure is
    not memoised, so it recurs for every method that asks. Read inside `Analyze()` at
    `Analysis/MostDerivedLocalType.cs:226`. **POSSIBLE.**

26. **`IlGenerator.cs:122`** `instructionMap.Add(instruction, generated);` and **`:94`**
    `locals.Add(local, ilLocal);` — `Dictionary.Add` throws `ArgumentException` on a duplicate key where the
    indexer would not. `Analysis/CatchClauses.cs:220`'s `.Distinct()` is evidence the block/handler lists do
    contain repeats. **POSSIBLE.**

27. **`Analysis/CatchClauses.cs:259`, `ExceptionEdges.cs:207`, `InterfaceCallRecovery.cs:378`,
    `MergedReturnSplitter.cs:311`** — `graph.Blocks.Max(b => b.ID) + 1` throws on an empty sequence.
    `Blocks` always retains Entry and Exit, so **UNLIKELY** — listed because `DefaultIfEmpty` is free.

28. **`IlGenerator.cs:157`** `il.First(i => i.OpCode == CilOpCodes.Br || i.OpCode == CilOpCodes.Brtrue)` —
    guarded by construction only. `EnsureBranchable` (`Fork.cs:1272`), whose own comment records this family
    costing **652 lost bodies**, can substitute a bare `Nop`. `.FirstOrDefault` + a null check costs nothing.
    **UNLIKELY today, CERTAIN under the next edit to those two cases.**

---

29. **`InstructionSets/NewArmV8InstructionSet.Fork.cs:1934`** —
    `_ => throw new ArgumentOutOfRangeException(nameof(element), $"Unknown vector element width {element.Width}")`
    in `LaneOperand`, called for every `Arm64OperandKind.VectorRegisterElement` operand
    (`NewArmV8InstructionSet.cs:1281`). `Arm64VectorElementWidth` is exactly `B=0,H=1,S=2,D=3`, so the `_` arm
    is reachable only if Disarm casts an out-of-range decoded field into the enum — which it can, since it
    constructs `Arm64VectorElement` from locals rather than constants at several sites. Trigger: a
    `mov`/`ins`/`umov`/`smov`/`dup`/`fmul`-by-element carrying `vN.<T>[i]`. **UNLIKELY**, hard throw, fix is
    `_ => 4`.

30. **`InstructionSets/NewArmV8InstructionSet.cs:100`** — `var targetAddress = (ulong)instruction.Operands[0];`
    in the branch-fixup loop: an unchecked unbox of operand 0 of every `Jump`/`ConditionalJump`, with no
    `is ulong` test and no `Count` check. Every current emitter passes `instruction.BranchTarget` (a `ulong`),
    so it holds today. **UNLIKELY now, CERTAIN on regression** — and the blast radius is "every method with a
    branch".

31. **`InstructionSets/NewArmV8InstructionSet.Fork.cs:970-971`** — `InstantiationAmong` tests
    `if (candidates.Count < 2 || …)` and then returns `candidates[0]`, so an **empty** list is not excluded.
    Reached from `GetArgumentOperandsForCall(context, callAddr)` (`:1386`) after
    `MethodsByAddress.TryGetValue` succeeded — i.e. any `bl`/`b` to an address whose entry is an empty list.
    `ArgumentOutOfRangeException`. **UNLIKELY.**

32. **`InstructionSets/VectorLanes.cs:1095`, `:890`/`:896`, `:1806`/`:1739`** — the divide-by-zero and
    empty-lane family. `:1095` `emit(OpCode.Move, [answer, read[0]])` in `Reduce` indexes a list sized by
    `LanesIn(elementWidth, q) => (q == 1 ? 16 : 8) / elementWidth` (`:2050`), which answers `0` for
    `elementWidth == 16`. `:896` `Enumerate(lanes, i => i + start / elementWidth)` and `:1806`
    `Math.Min(lanes, load.Bytes / elementWidth)` divide by the same value. Every writer of `width`
    (`Note` at `:1897`, `Write` at `:1915`) stores only `{1,2,4,8}` today, so all three are currently
    unreachable — but **nothing asserts that invariant** and `LanesIn` has no floor.
    `ArgumentOutOfRangeException` / `DivideByZeroException`. **UNLIKELY**; fix is
    `if (lanes < 1) return false;`.

## Wrong value, not an exception — the alias-arity defect in its silent form

The brief predicted that an aliased arm64 form with a different operand count would throw. In this lifter it
cannot: Disarm exposes `Op0Kind`..`Op3Kind`, `Op0Reg`..`Op3Reg`, `Op0Imm`..`Op3Imm` as plain auto-properties
on a struct, so reading `Op3Imm` on a two-operand alias yields `0`/`INVALID` rather than failing. The defect is
real and it is everywhere — it just costs statements instead of bodies:

- **`NewArmV8InstructionSet.cs:731-732`** — `var imms = (int)instruction.Op3Imm;` then
  `Add(address, OpCode.And, dest3, dest3, (1 << imms) - 1);` on the `UBFM` fallback, which is taken **exactly
  when** `Op2Kind`/`Op3Kind` are not `Immediate` (`BitfieldMove.cs:53-55`). `Op3Imm` is then `0` and the mask
  becomes `(1<<0)-1 == 0`, i.e. `dest &= 0`. For a 64-bit `ubfm`, `imms` up to 63 wraps under C#'s 5-bit shift
  mask (`1 << 32 == 1`), and `imms == 31` gives `int.MinValue - 1`. **CERTAIN as a wrong value.**
- **`ConditionalCompare.cs:99`** — `flagsWhenGuardFails = instruction.Op2Imm;` with no `Op2Kind` check. Where
  the operand layout differs, this reads `0`, `Satisfies(condition, 0)` answers a fabricated flag set, and
  `Fold` picks the wrong `&&`/`||` arm. **CERTAIN as a wrong value** — and this one silently changes what the
  recovered code *computes*, which the compilability scorers cannot see. Worth a corpus round on its own.
- **`NewArmV8InstructionSet.cs:283`** — the `MemIsPreIndexed` default-value bug, described under finding 8.

## Searched for in `InstructionSets/` and confirmed absent

- **`Details.Operands[N]`** — no such API; Disarm, not Capstone.
- **Register-name string parsing** — zero occurrences of `Substring`, `[1..]`, `int.Parse`, `Enum.Parse`,
  `Convert.*` or any indexing into a register name across all seven files. `Arm64Register` has no
  `SP`/`XZR`/`WZR`/`LR`/`FP`/`PC`/`NZCV` members at all — `sp` and `xzr` both arrive as `X31`. `RegisterFor`
  (`Fork.cs:1313`) and `Lane`/`General` (`VectorLanes.cs:2013`, `:2020`) build names from `int` and never
  parse one back.
- **A throwing `default:`** — `NewArmV8InstructionSet.cs:1161-1163` emits `OpCode.NotImplemented`. Every one
  of `ldadd`/`casal`/`ldaxr`/`stlxr`/`frinta`/`frintz`/`fmla`/`addp`/`dup`/`tbl`/`ext`/`rev`/`crc32*`/`sdot`/
  `addv`/`uaddlv`/`smax`/`umin`/`bfm`/`prfm`/`dmb`/`isb` falls there and costs a marker, not a method.
- **Lane/element tables indexed by an arrangement** — the only two (`LanesIn`, `WidthIn` at
  `VectorLanes.cs:2052`, `:2065`) are `switch` expressions with `_ =>` arms; `.2d`, `.16b`, `.8b`, `.2h` all
  land safely on the default. No array is indexed by an arrangement or an element size anywhere.
- **Operand-union confusion** — Disarm has no union; a kind/read mismatch yields `0`/`INVALID`, never a cast
  failure. `ConvertOperand` (`:1167`) dispatches on `kind` before every read.
- **`VectorLanes.cs:1541` `Emit`** — every caller's `sources` length matches its opcode's arity (`:592`,
  `:604`, `:624`, `:677`, `:687`, `:776`, `:792`, `:805`, `:1144`). Only `Replicate` (finding 2) can mismatch.
- **`NewArmV8InstructionSet.Fork.cs:1846`, `NewArmV8InstructionSet.cs:750`/`:772`** — `ShiftStack` operands
  are always `int`, so `StackAnalyzer.cs:108`'s `(int)` unbox is safe.


## Outside the per-method net — larger blast radius, same root cause

These throw where `FillMethodBody`'s `catch` cannot see them, so they cost a whole type, assembly or run:

- **`OutputFormats/AsmResolverDllOutputFormat.cs:141`** —
  `methodCtx.GetExtraData<MethodDefinition>("AsmResolverMethod") ?? throw new(…)` is thrown in the loop
  *around* `FillMethodBody`, caught at `:147` and **rethrown** at `:150` as `throw new($"Failed to process
  type …", e)`. One unresolved method kills every remaining method of the type, then the assembly. Strictly
  larger blast radius than anything above.
- **`OutputFormats/AsmResolverDllOutputFormat.cs:158`** —
  `var corlib = context.Assemblies.First(a => a.Name == "mscorlib");` — kills the entire run on any build
  without an assembly literally named `mscorlib`.
- **`Utils/AsmResolver/AsmResolverAssemblyPopulator.cs:551`, `:557`** —
  `…Properties.First(p => p.Semantics.Contains(interfaceMethodResolved.Semantics))` — il2cpp does not emit
  properties for explicit interface implementations (the comment at `:566` says so); the per-type `catch` at
  `:514-518` rethrows.
- **`Utils/AsmResolver/AsmResolverAssemblyPopulator.cs:201`, `:207`, `:129-131`** —
  `analyzedCustomAttribute.Constructor.Parameters[p.Index]`, where `p.Index` is a position read out of a v29
  attribute blob, indexed with no `Count` check.
- **`Utils/V29AttributeUtils.cs:99`, `:103`, `:31`** — `memberListGetter(typeContext)[memberIndex]` and
  `context.Metadata.methodDefs[i]` on raw blob-derived indices; one desynchronised blob turns every
  subsequent read into a garbage index.
- **`Utils/MiscUtils.cs:191`, `:193`** — `_allKnownFunctionStarts[lower]` / `[upper]` index `[0]` and `[-1]`
  on an empty list; reached by every method, so if it fires it fires for all 167k.

## Not catchable at all

- **`Utils/AsmResolver/ContextToTypeSignature.cs:50`** —
  `context.GenericArguments.Select(a => a.ToTypeSignature(parentModule))` recurses mutually with no depth cap
  and no visited set. A `GenericInstanceTypeAnalysisContext` transitively containing itself — which
  `RgctxResolver`'s inflation fixpoint (`ForkPipeline.cs:587`, `for (var again = 0; again < 8 && …)`) can
  build — overflows the stack, and `StackOverflowException` cannot be caught: it terminates the process and
  loses the entire run. Worth a depth counter regardless of likelihood.

## Checked and clean (do not re-audit)

`Analysis/ExceptionTable.cs` (fully wrapped), `Analysis/RgctxResolver.cs:96` / `.Fork.cs:95`,
`Analysis/FieldFromItsRuntimeInfo.cs:150`, `Analysis/ArrayInitialiserHandle.cs:128`/`:136-143`,
`Analysis/StandInCopyType.cs:138`, `Analysis/TypeTestNarrowing.cs:94`, `Analysis/ConstantBranchFolding.cs:69`,
`Analysis/ZeroOnATestedEdge.cs:71-74`, `Analysis/ListIdiomRecovery.cs:205-211`,
`Analysis/ArrayStoreCheckRemover.cs:59`, `Analysis/ConstantFolding.cs` (folds to `long`, `Constant` uses
`unchecked((long)v)`), `Analysis/StackAnalyzer.cs:108` (`(int)` unbox — both producers emit `int`:
`NewArmV8InstructionSet.cs:750`/`:772` and `.Fork.cs:1846`), `Analysis/StackAnalyzer.cs:98`
(`_inComingState[block]` is always set before the push), `Graphs/ISILControlFlowGraph.cs:410-412`
(`FixBlock` only runs on `Dirty` blocks, which are always jump-terminated), `Graphs/ISILControlFlowGraph.cs:424`
(`FindIndex` cannot miss after `FindBlockByInstruction` succeeded), and — **ruled out after checking** —
`Analysis/LocalVariables.cs:88` and `:131` `(Register)method.ParameterOperands[…]`: `ParametersOnTheStack.Place`
does put `StackOffset` entries in that list (`:89`), but `StackAnalyzer.cs:170-179` converts every one back
into a `Register` at `MethodAnalysisContext.cs:376`, ten lines before `CreateAll` runs. The residual defect
there is a *correctness* one, not a throw: when `AddRuntimeMethodOperand` bails at `used >= 8`
(`NewArmV8InstructionSet.Fork.cs:843-844`) while a homogeneous-float-struct parameter left extra vector
operands in the tail, `hasMethodInfo` (`LocalVariables.cs:104`) is true anyway and the last **lane** register
is named `methodInfo` and flagged `IsMethodInfo`.
