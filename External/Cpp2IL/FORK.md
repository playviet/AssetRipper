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
| `Cpp2IL.Core/InstructionSets/NewArmV8InstructionSet.cs` | 55 arm64 mnemonics upstream does not translate, the shifted-register operand the architecture folds into a data-processing instruction, the comparison a later `b.cond` reads, the runtime method every managed method is handed, the width a store pair steps by, every register an argument could have arrived in handed to a call through a register rather than only the general purpose half, the zero register recognised as zero in the values a store pair writes, the address of a stack slot lifted as an address rather than as a read of the slot, and the register an address is indexed by kept rather than dropped at the `TODO` in the memory operand. Helpers are in `NewArmV8InstructionSet.Fork.cs`. |
| `Cpp2IL.Core/IlGenerator.cs` | A field reached through a struct field in front of it loaded one step at a time. Blocks written out in the order control takes them, values kept on the evaluation stack rather than each given a local, runtime-helper calls folded back into the CIL instruction they stand for, operands loaded as the type the place they are going expects, and an allocation fused with the constructor call belonging to it wherever along the path that is made. Bulk is in `IlGenerator.Fork.cs`. |
| `Cpp2IL.Core/Analysis/MetadataResolver.cs` | An offset that lands inside a struct held in a field resolved to the member it names (upstream's `TODO: Support nested fields`). Field offsets reached through address arithmetic, through a generic base whose layout can be closed, through a base type, and through an open generic that records no offsets at all; and the method's own runtime method argument not being taken for the one a call passes. Bulk is in `MetadataResolver.Fork.cs`. |
| `Cpp2IL.Core/Analysis/LocalVariables.cs` | A type carried through the arithmetic, the comparisons, and the choices that pass a value on, so it is not lost at the first operation; the method's own runtime method argument typed as what it is; and three passes added to the type/field fixpoint; the static-fields offsets made `internal` so a fork pass can use the same constants. |
| `Cpp2IL.Core/Il2CppApiFunctions/NewArm64KeyFunctionAddresses.cs` | Key function addresses found by looking at instructions where name matching does not reach, and the write barrier found by the store it guards. |
| `Cpp2IL.Core/Analysis/BooleanFlagSimplifier.cs` | A negated comparison written as the comparison it negates, and a sign-bit test as the comparison against zero it is. |
| `Cpp2IL.Core/Analysis/ThrowHelperRecovery.cs` | The name of the exception a helper raises, read out of the helper on both architectures. |
| `Cpp2IL.Core/Analysis/RgctxResolver.cs` | The runtime generic context belonging to a method rather than to a type, the method entries in either, and the type an allocation in a generic body names through one. |
| `Cpp2IL.Core/Analysis/MetadataInitGuardRemover.cs` | A guard recognised by which runtime structure it reads, since the offset it tests moves between il2cpp versions, and through the arithmetic that works out the flag's address where the offset is too large to ride along with the read. Bulk is in `MetadataInitGuardRemover.Fork.cs`. |
| `Cpp2IL.Core/Analysis/Simplifier.cs` | A value read from memory is not carried past anything that could have changed what is there. |
| `Cpp2IL.Core/Analysis/DelegateInvokeRecovery.cs` | Finding a delegate's `Invoke` when the delegate is a generic one - which is every `Action<...>` and `Func<...>` - and resolving its arguments against the convention the binary actually uses rather than always x64. Bulk is in `DelegateInvokeRecovery.Fork.cs`. |
| `Cpp2IL.Core/Analysis/SsaForm.cs` | A phi placed only where the register it merges can still be read, which is the pruned form of the same construction, and no copy written on an edge whose two ends hold types that cannot be reconciled - the register being reused rather than the value moving. Both tests are in `SsaForm.Fork.cs`. |
| `Cpp2IL.Core/Analysis/SsaSimplifier.cs` | A phi handed one value along every path forwarded as that value. Bulk is in `SsaSimplifier.Fork.cs`. |
| `Cpp2IL.Core/Analysis/DeadCodeEliminator.cs` | One opcode added to the pure list. |
| `Cpp2IL.Core/ISIL/OpCode.cs`, `ISIL/Instruction.cs` | A `Select` opcode, for the conditional moves arm64 uses instead of a branch. |
| `Cpp2IL.Core/Model/Contexts/MethodAnalysisContext.cs` | Five calls into `ForkPipeline`, at the five points along the pipeline where the fork's passes belong. |
| `Cpp2IL.Core/Analysis/RuntimeMethodCallRecovery.cs` | One condition: a method's code is reached through any of the three pointers at the front of its `MethodInfo`, not only the first. |
| `Cpp2IL.Core/Analysis/StackAnalyzer.cs` | Two lines, so that the address of a stack slot stays telling apart from the slot's contents: the offset correction keeps whichever of the two it was given, and the naming gives an address a register name of its own. Both go through `StackSlots` in `ISIL/StackSlotAddress.cs`. |
| `Cpp2IL.Core/Model/Contexts/ApplicationAnalysisContext.cs` | One dictionary made concurrent - it is written from the parallel analysis. |
| `LibCpp2IL/LibCpp2IlContext.cs` | A metadata usage reached through the global offset table, which is how a shared object addresses one. |
| `LibCpp2IL/Metadata/Il2CppMethodDefinition.cs` | An `RgctXs` property. Additive; nothing upstream declares changes. |
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
