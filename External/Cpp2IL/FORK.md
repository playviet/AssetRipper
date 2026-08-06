# The fork, and how to move it onto a newer Cpp2IL

This tree is a fork of [Cpp2IL](https://github.com/SamboyCoding/Cpp2IL). What it adds is described in
`../../LocalPackages/README.md`, fix by fix. This file is about keeping it mergeable: where it starts from,
what it touches, and what to run when upstream moves.

## Where it starts from

| | |
|---|---|
| upstream | `https://github.com/SamboyCoding/Cpp2IL`, default branch `development` |
| base commit | `b20ca0d04f9d4deaf04636428b3c116589b2a167` |
| then | `97566c8`, `800cc39`, `e3aa824` from `https://github.com/AssetRipper/Cpp2IL` - the packaging commits that give the projects their `AssetRipper.` package ids |

Those four commits reconstruct the tree this fork was written against. `rebase-onto-upstream.sh` does it for
you and replays the fork on top of whatever upstream revision you ask for.

## How the fork is arranged

Almost everything it adds lives in files upstream does not have, so upstream can add, move and rewrite its own
code without meeting any of it:

* `Cpp2IL.Core/Analysis/*.cs` - the passes that turn lifted code back into something resembling the method
  that was written. `ForkPipeline.cs` is where they are ordered, and why each one runs where it does.
* `*.Fork.cs` - the fork's half of a class upstream also has. The upstream file gains the word `partial` and
  loses nothing else; every member the fork adds is in the sibling file.
* `Cpp2IL.Core.Tests/IlGeneratorForkTests.cs` - the fork's own tests.

What is left in upstream's own files is code that has to be there: a case in a switch, a call at the one point
in a pipeline where it belongs, a field read one way rather than another.

## What it changes in upstream's files

| file | what and why |
|---|---|
| `Cpp2IL.Core/InstructionSets/NewArmV8InstructionSet.cs` | 55 arm64 mnemonics upstream does not translate, the shifted-register operand the architecture folds into a data-processing instruction, the comparison a later `b.cond` reads - taken *before* the arithmetic where `subs` writes the very register it compares, since `RecordComparison` emits moves rather than capturing values and would otherwise record a left side one subtraction too small (guarded by `Op0Reg == Op1Reg`: reordering it unconditionally costs seventeen of the corpus's twenty-five methods), the runtime method every managed method is handed, the width a store pair steps by, every register an argument could have arrived in handed to a call through a register rather than only the general purpose half, the zero register recognised as zero in the values a store pair writes, the address of a stack slot lifted as an address rather than as a read of the slot, and the register an address is indexed by kept rather than dropped at the `TODO` in the memory operand. Helpers are in `NewArmV8InstructionSet.Fork.cs`. Also two hooks: `ConditionalCompare` at the conditional compare and at each of the three places a held comparison becomes a value, and `VectorLanes` ahead of the mnemonic switch. One line at `UBFM`, handing the bitfield move to `BitfieldMove` (which is a shift in whichever direction its two immediates put it, and upstream translates only one of the two). Two further lines where an ADRP page is folded into a load: the offset scaled, because that fold never reaches `MemoryOperandFor` where every other addressing form is scaled, and the operand built by `ConstantBlobStore.Note` so the width of the load survives into the pass that needs it. Also the narrowing conversion the architecture folds into a data-processing operand - `add w8, w8, w0, uxtb` adds the low byte, not the word - materialised in `ShiftedOperand` via `ExtendedTo` in the fork, narrowing what goes in and widening straight back so the instruction's result stays a full word. |
| `Cpp2IL.Core/Analysis/SwitchTableRecovery.cs` | **New.** A `switch` whose cases are dense and whose arms are constants becomes a table in the binary's own data, which resolves to nothing - so the method answered with its default for every input. The address is a constant once the page and offset are folded, the count is in the comparison guarding the read, and the width is the index scale; the answers are read back and become a chain of choices. |
| `Cpp2IL.Core/Analysis/ConstantFolding.cs` | **New.** A constant too wide for one instruction is built a sixteen bit field at a time, so what reaches the analysis is a chain of masks and ors. The decompiler folds it at the end, which is why the output never looked wrong - but no pass in between could read the number. Folds only where every source is already a constant, carrying them along the block itself so it does not depend on which propagation runs next. |
| `Cpp2IL.Core/Analysis/LogicalShift.cs` | **New.** arm64 has two right shifts - `LSRV`/`UBFM` bring in zeroes, `ASRV`/`SBFM` bring in the sign - and ISIL has one `ShiftRight` for both, which the generator emitted as `Shr`. So every logical shift came back arithmetic: identical on a non-negative value, wrong on every negative one, with no marker and no commented statement. `probeg shifts` counts **93 logical against 17 arithmetic** in Assembly-CSharp (4153 against 620 binary-wide). Marks are held in a `ConditionalWeakTable` rather than as an operand, because `ConstantFolding` refuses anything whose operand count is not three. |
| `Cpp2IL.Core/InstructionSets/BitfieldMove.cs` | Its `emit` delegate hands back the instruction it created, so the extract half can be marked as logical when the mnemonic is `UBFM`. |
| `Cpp2IL.Core/InstructionSets/NewArmV8InstructionSet.cs` | Also: `LSRV` split out of the case it shared with `ASRV`, so the logical one can be marked. |
| `Cpp2IL.Core/Analysis/LocalVariables.cs` | One line beyond what is listed elsewhere: the call-argument typing uses `SetTypeFromParameter` (fork) rather than `SetTypeIfUnknown`. A register reused across a join is typed by whichever edge the inference reached first, and once it has *any* type the parameter it is passed as cannot correct it - one local written from six places, three arrays and three lists, came out as `Il2CppMethodInfo`, which managed code never holds. The fork overrules a runtime stand-in with the declared parameter type, **except inside a generic body**, where the method's own `MethodInfo*` is what every runtime generic context entry is reached through: overruling it there emptied four generic extension methods. |
| `Cpp2IL.Core/Analysis/InlinedConstructorRecovery.cs` | **New.** A constructor small enough to inline leaves the allocation, the base constructor call and the field writes its body did - and those fields are the callee's own and often `readonly`, so no C# expresses them. The allocation still compiles, so nothing marks it and the object simply holds the wrong values: `Corpus::Areas` answered 13 where the original answers 83200. Matches each write to the parameter that stands for it (field name without `_`/`m_`, plus type) and folds only when every parameter is matched exactly once. Built **at the last write**, not at the allocation, because an argument is often computed between the two; and the base constructor call is nopped, since the generator only looks forward for one to fuse. |
| `Cpp2IL.Core/Analysis/UnboxRecovery.cs` | **New.** The unboxing helper and the cast helper have the same call shape, so `CastHelperRecovery` names both `isinst` - and adding an object to an int is not C#. What separates them is that **nobody dereferences the result of a cast**: a result only ever read at offset zero, for a value type, is a box being opened. Retypes the result, replaces `[result]` with `result`, and renames the call so the generator writes `unbox.any`. Only when every use is such a read. |
| `Cpp2IL.Core/IlGenerator.cs` | Two lines beyond what is listed elsewhere: a `case` for the unboxing helper, and reading one constructor argument per parameter off an allocation rather than only the single one the state machine needs (`CarriedConstructorArguments` in the fork; the one-parameter case reads identically either way). |
| `Cpp2IL.Core/Analysis/InaccessibleFieldRecovery.cs` | Two halves of one defect, and they only work together. **Visibility was answered by assembly**, so anything compiled alongside the caller was taken to be reachable - but `private` is per *type*, and `TimersManager` cannot name `LevelManager._currentState`. Now answered per the language: enclosure either way, then public / assembly / family / their combinations. On its own that change is **completely inert**, because these fields have no property to be named by. The other half is `ForwardingGetter`: a type that hands a private field out through a plain `public ELevelState GetCurrentState()` rather than a property, which il2cpp inlines into every caller. **Reads only** - a `SetX` is not the mirror, since `SetState` assigns *and* raises an event, and calling it where the code assigned a field would put back something the program never did. |
| `Cpp2IL.Core/Analysis/UnusedLengthRead.cs` | **New.** il2cpp bounds-checks every indexer, so once the access itself is recovered the length its check read is left feeding nothing - and it cannot be written down either, because `List<T>._items` is private to another assembly, so each indexed access left a commented `_ = list._items.Length;`. `DeadCodeEliminator` will not take it: by here the read is a `Call` to `Array.get_Length`, and that pass excludes calls, correctly, since it cannot know what a callee does. This knows what that one does - it reads a length the header already holds, has no side effect, and can fail only on a null array, which recovered C# checks for itself. Nothing else is taken. In seven of the ninety-six methods this was the only thing keeping the method from scoring whole. |
| `Cpp2IL.Core/Analysis/ConstantBranchFolding.cs` | **New.** `RuntimeClassReadRemover` answers the bit a class carries saying it has been prepared, because by the time recovered code could run the answer is always yes - which leaves a branch on `1 != 0` whose other arm holds il2cpp's initialisation call, and that call is not something the language can write, so it came out as a commented statement inside `if (1 == 0)`. Its own comment said the collection afterwards removed the arm; nothing did, because `UnreachableBlockRemover` decides by who points at a block and a conditional jump points at both of its arms. This settles the branch and detaches the arm, immediately above that collection. Acts **only** on comparisons a pass registered through `HasSettledAnswer`: a comparison between two constants is just as often what a read that resolved to nothing left behind, and folding one of those deleted nine of `SubCellVisual::UpdateEyeTracking`'s thirteen decisions. 96 guards in the game, none left. |
| `Cpp2IL.Core/Analysis/WideFieldStore.cs` | **New.** One store writing two adjacent fields resolves to the first of them and is given the whole of what was written. The store's width is not carried this far and does not need to be: a four byte store cannot hold bits above the thirty-second, and a field has to exist at the next offset too. |
| `Cpp2IL.Core/Model/Contexts/MethodAnalysisContext.cs` | One line: a fork hook where the constants have reached their uses and before the float literals are read as floats. |
| `Cpp2IL.Core/Analysis/FloatLiteralRecovery.cs` | Made `partial`, and one line: a constant computed with rather than stored or passed is reinterpreted where something else in the same expression is known to be floating point. Bulk in `FloatLiteralRecovery.Fork.cs`. |
| `Cpp2IL.Core/ISIL/ConversionTarget.cs` | **New.** The type an arm64 conversion produces, carried as a third operand of the move that stands for it - because ISIL has no width, so every converting instruction was lifted as a plain move and the value came out carrying the type of what it was converted *from*. Marked rather than given an opcode of its own: an opcode is one line in the enum and fifty places that match `OpCode.Move` silently not matching. |
| `Cpp2IL.Core/InstructionSets/ConditionalCompare.cs` | **New.** `ccmp`/`ccmn`/`fccmp` - the comparison the architecture only makes when an earlier one came out a particular way, which is what a branchless `&&` or `||` compiles to. The guard is folded into whichever of the three readers of the flags comes next. |
| `Cpp2IL.Core/InstructionSets/UndecodedScalar.cs` | **New.** The one-source floating point instructions the disassembler refuses, which are not vector at all - `fcvt s0, d0` above all, 387 of them, which the switch has handled all along and never saw because the mnemonic arrives as `INVALID`. A precision change is lifted as a conversion rather than a move. |
| `Cpp2IL.Core/InstructionSets/VectorLanes.cs` | **New.** The lanes of a vector register lifted as registers of their own, so scalar arithmetic the compiler packed two or four at a time comes back as what the source had. Decodes the Advanced SIMD copy, single-lane load/store, three-same and two-register-miscellaneous encodings from the raw word, because the disassembler package refuses all of them. |
| `Cpp2IL.Core/IlGenerator.cs` | A field reached through a struct field in front of it loaded one step at a time. Blocks written out in the order control takes them, values kept on the evaluation stack rather than each given a local, runtime-helper calls folded back into the CIL instruction they stand for, operands loaded as the type the place they are going expects, and an allocation fused with the constructor call belonging to it wherever along the path that is made. Bulk is in `IlGenerator.Fork.cs`. One further case in the operand-loading switch, for `MultiDimensionalElement` - an array of more than one dimension is indexed through methods rather than by `ldelem`, and the reference for those is built in `IlGenerator.Fork.cs`. One further case beside it for `FieldToken`, so an array initialiser's handle is named with `ldtoken` at the call that copies it. |
| `Cpp2IL.Core/Analysis/MetadataResolver.cs` | An offset that lands inside a struct held in a field resolved to the member it names (upstream's `TODO: Support nested fields`). Field offsets reached through address arithmetic, through a generic base whose layout can be closed, through a base type, and through an open generic that records no offsets at all; and the method's own runtime method argument not being taken for the one a call passes. Bulk is in `MetadataResolver.Fork.cs`. A conversion emits the conversion it performs rather than reinterpreting the bits. Also one line where a usage address is taken: position-independent code names a global through a slot in the global offset table, so the usage is at what the slot holds rather than at the slot - `MetadataResolver.Fork.ThroughGlobalOffsetTable` follows it. |
| `Cpp2IL.Core/Analysis/LocalVariables.cs` | A type carried through the arithmetic, the comparisons, and the choices that pass a value on, so it is not lost at the first operation; the method's own runtime method argument typed as what it is; and three passes added to the type/field fixpoint; the static-fields offsets made `internal` so a fork pass can use the same constants. Also one line calling `SeedDereferencedClasses`, which types the class a metadata slot points at - the slot names where the class is kept, not the class, and until that one read is typed nothing past it resolves. And one line calling `ArrayWalkerTyping`, which gives back the subscript a loop that walks an array with a pointer never kept. |
| `Cpp2IL.Core/Analysis/Simplifier.cs` | Two conditions: a move that is a conversion is not forwarded into its uses, because forwarding it deletes both the type it pins and the arithmetic it performs. |
| `Cpp2IL.Core/Analysis/SsaSimplifier.cs` | The same condition, for the same reason. |
| `Cpp2IL.Core/Il2CppApiFunctions/NewArm64KeyFunctionAddresses.cs` | Key function addresses found by looking at instructions where name matching does not reach, and the write barrier found by the store it guards. A conversion pins the type it produces instead of copying one across itself. |
| `Cpp2IL.Core/Analysis/BooleanFlagSimplifier.cs` | A negated comparison written as the comparison it negates, and a sign-bit test as the comparison against zero it is. |
| `Cpp2IL.Core/Analysis/ThrowHelperRecovery.cs` | The name of the exception a helper raises, read out of the helper on both architectures. `ResolveName` also asks the fork whether a search may go ahead and whether its answer is worth keeping - the cache is shared across the application and used to record a search that had merely given up as a settled negative, so the answer depended on which call site asked first. Policy in `ThrowHelperRecovery.Fork.cs`. |
| `Cpp2IL.Core/Analysis/RgctxResolver.cs` | The runtime generic context belonging to a method rather than to a type, the method entries in either, and the type an allocation in a generic body names through one. |
| `Cpp2IL.Core/Analysis/MetadataInitGuardRemover.cs` | A guard recognised by which runtime structure it reads, since the offset it tests moves between il2cpp versions, and through the arithmetic that works out the flag's address where the offset is too large to ride along with the read. Bulk is in `MetadataInitGuardRemover.Fork.cs`. |
| `Cpp2IL.Core/Analysis/Simplifier.cs` | A value read from memory is not carried past anything that could have changed what is there. |
| `Cpp2IL.Core/Analysis/DelegateInvokeRecovery.cs` | Finding a delegate's `Invoke` when the delegate is a generic one - which is every `Action<...>` and `Func<...>` - and resolving its arguments against the convention the binary actually uses rather than always x64. Bulk is in `DelegateInvokeRecovery.Fork.cs`. |
| `Cpp2IL.Core/Analysis/SsaForm.cs` | A phi placed only where the register it merges can still be read, which is the pruned form of the same construction, and no copy written on an edge whose two ends hold types that cannot be reconciled - the register being reused rather than the value moving. Both tests are in `SsaForm.Fork.cs`. Also every operand slot a predecessor owns filled in `Rename`, not just the first `IndexOf` finds: both arms of a branch can land on the same block, and the slots left holding the skeleton placeholder destroyed into copies from registers nothing ever wrote, clobbering the loop-carried value on the edge that did carry it. |
| `Cpp2IL.Core/Analysis/SsaSimplifier.cs` | A phi handed one value along every path forwarded as that value. Bulk is in `SsaSimplifier.Fork.cs`. |
| `Cpp2IL.Core/Analysis/DeadCodeEliminator.cs` | One opcode added to the pure list. |
| `Cpp2IL.Core/ISIL/OpCode.cs`, `ISIL/Instruction.cs` | A `Select` opcode, for the conditional moves arm64 uses instead of a branch. |
| `Cpp2IL.Core/Model/Contexts/MethodAnalysisContext.cs` | Five calls into `ForkPipeline`, at the five points along the pipeline where the fork's passes belong. |
| `Cpp2IL.Core/Analysis/RuntimeMethodCallRecovery.cs` | One condition: a method's code is reached through any of the three pointers at the front of its `MethodInfo`, not only the first. |
| `Cpp2IL.Core/Analysis/StackAnalyzer.cs` | Two lines, so that the address of a stack slot stays telling apart from the slot's contents: the offset correction keeps whichever of the two it was given, and the naming gives an address a register name of its own. Both go through `StackSlots` in `ISIL/StackSlotAddress.cs`. |
| `Cpp2IL.Core/Model/Contexts/ApplicationAnalysisContext.cs` | One dictionary made concurrent - it is written from the parallel analysis. |
| `LibCpp2IL/LibCpp2IlContext.cs` | A metadata usage reached through the global offset table, which is how a shared object addresses one. |
| `LibCpp2IL/Metadata/Il2CppMethodDefinition.cs` | An `RgctXs` property. Additive; nothing upstream declares changes. |
| `LibCpp2IL/Elf/ElfFile.cs` | One word: `partial`, so `ElfFile.Fork.cs` can add `IsReadOnlyAddress`, which is what lets a load out of the binary be answered as a constant only where the data cannot change. Nothing else in the file changes. |
| `Cpp2IL.Core.Tests/IlGeneratorTests.cs` | One assertion, which counted the locals a call's arguments used to need. |
| `Cpp2IL.Core/Utils/AsmResolver/ContextToTypeSignature.cs` | One line: the fork's `MethodRgctxTableTypeAnalysisContext` added to the runtime handles that lower to `IntPtr`. Without it the fork does not compile. |
| `Cpp2IL.Core/Cpp2IL.Core.csproj`, `LibCpp2IL/LibCpp2IL.csproj` | The version the packages carry, and `AsmResolver.DotNet` 6.0.0 to 6.0.1. |

Where the fork needed a different signature it added a method beside the original, so that a merge does not
have to reconcile two versions of one declaration. **Two private ones escaped that rule and are still to be
undone**: `Analysis/MetadataInitGuardRemover.cs::TryRemoveGuard` gained `float metadataVersion, bool is32Bit`,
and `Analysis/LocalVariables.cs::InstantiatedType` gained an optional `definitions` dictionary with its one
upstream call site rewritten to pass it. Both are `private static`, so a rebase conflict in either is confined
to its own file, but neither should have been edited in place.

## Moving onto a newer upstream

```sh
External/Cpp2IL/rebase-onto-upstream.sh /tmp/cpp2il-rebase           # onto upstream's development
External/Cpp2IL/rebase-onto-upstream.sh /tmp/cpp2il-rebase v2026.1.0 # or onto a tag or commit
```

The script clones upstream, reconstructs the base commit above, commits this tree on top of it as a single
commit, and rebases that commit onto the revision you asked for. Whatever conflicts are left are ordinary
rebase conflicts in the files listed in the table - resolve them there, then:

```sh
cd /tmp/cpp2il-rebase
git rebase --continue
dotnet test -c Release
rsync -a --delete --exclude .git --exclude TestFiles --exclude bin --exclude obj \
    --exclude FORK.md --exclude rebase-onto-upstream.sh \
    /tmp/cpp2il-rebase/ External/Cpp2IL/
```

`FORK.md` and `rebase-onto-upstream.sh` are this repository's, not the fork's, so they have to be excluded -
`--delete` removes anything the source tree does not have, and it has neither of them.

Then raise `VersionPrefix` in both csproj files and the `Version` in
`Source/AssetRipper.Import/AssetRipper.Import.csproj`, repack into `LocalPackages`, and record the new base
commit at the top of this file.
