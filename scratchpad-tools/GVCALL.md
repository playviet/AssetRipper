# GVCALL — the generic-virtual call, found by its idiom and folded into one call

`CALLGAP.md` §5 is the evidence this is written against: **one helper Cpp2IL does not name is 72% of
Snacky Dash's `notfound` + `indirect` combined (~2,590 sites)** — the generic-virtual-method resolver.
This file is the design, what the finder actually selects on both binaries, and what changed.

**Nothing here was built.** An export was running from `scratchpad/riprun/bin` throughout; every reading
below comes from the two `.so` files and from `probe`, which reads them directly.

---

## 1. What the idiom is, named from Unity's own source

Unity ships the generator's helper inline. `6000.0.78f1/.../libil2cpp/codegen/il2cpp-codegen.h:731`:

```c
IL2CPP_FORCE_INLINE const RuntimeMethod* il2cpp_codegen_get_generic_virtual_method(
        const RuntimeMethod* method, const RuntimeObject* obj)
{
    uint16_t slot = method->slot;
    const RuntimeMethod* methodDefinition = obj->klass->vtable[slot].method;
    return il2cpp_codegen_get_generic_virtual_method_internal(methodDefinition, method);
}

IL2CPP_FORCE_INLINE void il2cpp_codegen_get_generic_virtual_invoke_data(
        const RuntimeMethod* method, const RuntimeObject* obj, VirtualInvokeData* invokeData)
{
    invokeData->method  = il2cpp_codegen_get_generic_virtual_method(method, obj);
    invokeData->methodPtr = invokeData->method->virtualMethodPointer;
}
```

`..._internal` is `GenericMethod::GetGenericVirtualMethod(vtableSlotMethod, genericVirtualMethod)`
(`metadata/GenericMethod.cpp:146`). Everything above the `BL` is inlined at the call site; only that one
function is out of line, and **it is the address nothing names.**

### The two struct offsets, settled from the header rather than inferred

`Unity.app/Contents/il2cpp/libil2cpp/il2cpp-class-internals.h` — the same header
`Cpp2IL.Core/Il2CppClassLayout.cs` was computed from, so both constants are **already in the fork** and
need no new literal:

| what | where | in the fork |
|---|---|---|
| `MethodInfo::slot` (`uint16_t`) | **0x50** | `Il2CppMethodInfoLayout.Slot` |
| `Il2CppClass::vtable` | 0x138 | `Il2CppClassLayout.Vtable` |
| `VirtualInvokeData { methodPtr; method; }` | `.method` at +8 | — |
| ⇒ `klass->vtable[slot].method` | **0x138 + 8 + slot*0x10** = `+0x140` at slot 0 | derived |
| `MethodInfo::virtualMethodPointer` | **0x08** | `Il2CppMethodInfoLayout.VirtualMethodPointer` |

**Version stability.** `0x50` and `0x140` are *not* free constants and are *not* hard-coded by this pass:
`0x50` is `Il2CppMethodInfoLayout.Slot`, and `0x140` is
`Il2CppClassLayout.Vtable + 8`. They move only if `MethodInfo` or `Il2CppClass` gains a field before them.
`MethodInfo` is stable from metadata 27 onward for the first eight pointers; `slot` sits after
`token`/`flags`/`iflags`, which have not moved in the metadata 24→31 range this fork targets. The two binaries
here agree exactly, which is the check that matters. If a future build disagreed,
the **finder would simply elect nothing** — the vote requires the feed — and the pass would be inert. That
is the safe direction.

Note the offsets are used **only inside the vote**, never at a rewrite site: the rewrite keys on the call
target the vote elected and on the resolver's own two-argument signature.

---

## 2. The idiom in the binary — Snacky Dash

`0x03CB7418`, one of 7,741 sites (`probe at`):

```
LDR   X9, [X20]              ; klass = obj->klass          (Il2CppObject + 0)
LDR   X1, [X8]               ; the MethodInfo, from a metadata usage or the rgctx
LDRH  W8, [X1 + 0x50]        ; method->slot
ADD   X8, X9, X8, LSL #4     ; klass + slot*sizeof(VirtualInvokeData)
LDR   X0, [X8 + 0x140]       ; vtable[slot].method
BL    0x3AE432C              ; -> b 0x3B02AC0, GetGenericVirtualMethod(x0=def, x1=method)
LDR   X8, [X0 + 0x8]         ; resolved->virtualMethodPointer
MOV   X2, X0                 ; the resolved MethodInfo becomes the hidden last argument
MOV   X0, X20                ; the receiver
MOV   X1, X19                ; the argument
BLR   X8
```

`0x3B02AC0` disassembles to exactly `GetGenericVirtualMethod`: `LDRB W8,[X0+0x53]` (the bitfield byte
holding `is_inflated`), `TBNZ #1`, `LDR X9,[X0+0x40]` (`genericMethod`), `LDP X0,X8,[X9]`
(`methodDefinition` and `context.class_inst`), `LDR X9,[X1+0x40]` / `LDR X9,[X9+0x10]`
(`genericVirtualMethod->genericMethod->context.method_inst`), then the tail call into `GetMethod`.

**The lifter already follows the thunk.** `NewArmV8InstructionSet.Fork.ImportedCall` calls `FollowThunks`
on a target that is not a key function, so `BL 0x3AE432C` is lifted as `Call 0x3B02AC0` — which is why the
census names `3B02AC0` and not the thunk. The pass therefore has to accept **both** addresses.

### The same site in ISIL — `ES3Reader::ReadProperty<T>(ES3Type)`, `probe dump`

```
27 Call ES3Reader.ReadPropertyName, v37 (String), this @ X0 (ES3Reader)
32 Move v50 @ X1_v2 (Il2CppMethodInfo), methodof(ES3Reader::Read)
36 ShiftLeft v54, [v50 (Il2CppMethodInfo)+50], 4
37 Add  v55 @ X8_v5, [this @ X0 (ES3Reader)], v54
38 Move v56 @ X0_v5, [v55 @ X8_v5+140]
40 Call 3B02AC0, v58 @ X0_v6, v56, methodof(ES3Reader::Read), methodInfo @ X2, v24 @ X3, … v28 @ X7
43 Move v61 @ X3_v1, [v58 @ X0_v6+8]
54 IndirectCall v61, returnVal1 @ X0_v8 (T), this @ X0 (ES3Reader), type @ X1 (ES3Type),
                v58 @ X2, v61 @ X3, v25 @ X4 … v28 @ X7, v38 @ V0 … v45 @ V7
55 Return returnVal1
```

Six statements — one `notfound`, one `indirect`, three unmanaged reads — for `return Read<T>(type);`.

**The resolver call's second argument is already `methodof(ES3Reader::Read)`.** That is the whole answer:
the runtime's own signature says argument 1 is `genericVirtualMethod`, i.e. the method the *source* named
at this call site. Naming it is not a guess — it is the same argument `VirtualCallRecovery` makes about a
vtable slot, one level up.

---

## 3. The finder — by idiom, voted, never by address

`gvfind.py` in the session scratchpad is the prototype of exactly what the C# does. Over **every** `BL` in
the generated section (not a sample), a target scores a vote when:

1. the target is outside the managed region (it is a runtime helper, not a managed body); **and**
2. within 6 instructions after the `BL`, and before any other call, there is `LDR Xd, [X0, #8]`; **and**
3. within 6 instructions after that, `BLR Xd` on the same register.

A target scores a *full* vote when, in addition, the 24 instructions above the `BL` — stopping at the
previous call, per `il2cpp-a-call-ends-the-register-search` — contain both a halfword load at
`Il2CppMethodInfoLayout.Slot` (0x50) and a load or address computation at `Vtable + 8` (0x140).

Nothing else in a generated body has that shape: it needs a result that is *only* consumed as a code
pointer at `+8` and then called, with a vtable entry and a `MethodInfo` slot feeding it.

### What it selects — both binaries, measured

```
=== SNACKY DASH   (libil2cpp.so 149 MB, Unity 6000.0.66f2, metadata 31,1)
text 0x3598e20 0x670924 | il2cpp 0x3c09744 0x47cea98
BL sites in scanned section: 2366124 ... targeting .text: 1614975

result loaded at +8 and BLR-ed      0x3AE432C   7741   of which with the slot+vtable feed: 7552
full idiom                          0x3AE432C   7552   sites: 3cb7430 3d87614 3d878a8 3d87dec
                                    -- no second candidate at any vote count --

=== FLUFFY FIELD  (libil2cpp.so 84 MB, the fork's original target)
text 0x1e7bb20 0x3fb0e4 | il2cpp 0x2276c04 0x2884e44
BL sites in scanned section: 1340931 ... targeting .text: 909694

result loaded at +8 and BLR-ed      0x2184654    352   of which with the slot+vtable feed: 147
full idiom                          0x2184654    147   sites: 25f2d1c 25f2e3c 25f2f6c 2690ffc
                                    -- no second candidate at any vote count --
```

**Exactly one candidate on each binary, and no runner-up at all.** Both elected addresses are thunks:

```
0x3AE432C  B 0x3B02AC0     (Snacky Dash)
0x2184654  B 0x21A2A7C     (Fluffy Field)
```

and `0x21A2A7C` is **byte-for-byte the same function** as `0x3B02AC0` — same `LDRB W8,[X0+0x53]`, same
`TBNZ #1`, same `[X0+0x40]` / `LDP` / `[X1+0x40]` / `[X9+0x10]`. Two independent games and two independent Unity versions, one function, elected without an address ever being written down.

A Fluffy Field call site, for the record (`0x25F2D04`) — the same ten instructions in the same order.

So the answer to "does it select the right thing on Fluffy Field" is **yes, the same function**, and the
answer to "does it change Fluffy Field's export" is **almost not at all**: 147 full-idiom sites exist
binary-wide, and none of Fluffy Field's 18 `notfound` addresses is this one, so nearly all of them are in
corelib and DOTween rather than in the assembly the export writes. Fluffy Field is the *control*: the pass
must measure ≈ neutral there.

### `0x3B1D778` is not this, and is deliberately left alone

`CALLGAP.md` lists a second address, 18 sites (1% of the family). Disassembled it takes **three**
arguments — `(x0, x1, w2)`, with the slot in `w2` — and reads `[x0]` then calls a walker: it is the
*interface* form (`ClassInlines::GetInterfaceInvokeDataFromVTable`), not the vtable form. The finder gives
it no votes, and the pass will not touch it. Its marker stays, which is the right answer for 18 sites.

### The vote's acceptance rule

* the leader must have at least **24** full-idiom votes, and
* at least **four times** the runner-up's.

Both games pass by an infinite margin (runner-up 0). A binary that does not is a binary where the pass
declines and nothing changes — the same safety property `AttemptInstructionAnalysisToFillGaps` has.

Both the thunk and the address it lands on are recorded, because the lifter may present either.

---

## 4. The rewrite

At a site, all of these must hold, or the marker stays:

1. `OpCode.Call` whose `Operands[0]` is a `ulong` in the elected set.
2. `Operands[3]` (register x1, the resolver's `genericVirtualMethod`) names a method — either directly as a
   `RuntimeMethodInfoAnalysisContext`, or as a local whose `Type` is one, or through that local's
   defining `Move`. **Where it does not, decline.** A generic virtual call whose callee cannot be named is
   exactly the case `CLAUDE.md` says to leave as a marker.
3. The call's result (`Operands[1]`) is read by exactly one `Move dest, [result + VirtualMethodPointer]`.
4. That `dest` is the target of an `IndirectCall` — as the operand itself, or folded in as
   `[result + 8]`.
5. The callee is an instance method and `Aapcs64.ArgumentsOf` can lay its arguments out.

Then the `IndirectCall` becomes `Call callee, result, receiver, args…` (or `CallVoid`), modelled line for
line on `VirtualCallRecovery` and `RuntimeMethodCallRecovery`, which is what makes the generator emit an
ordinary virtual call.

**And the machinery is then swept.** Otherwise the round trades one `notfound` and one `indirect` for
three `unmanaged` reads and measures worse. The sweep is *not* a general dead-code pass: it starts from
the instructions this pass identified (the resolver call, the `+8` load, and transitively what fed the
resolver's x0 and x1) and drops only those whose destination no longer has a single use anywhere in the
graph, to a fixpoint. On `ReadProperty` that is instructions 32, 36, 37, 38, 40 and 43 — every one of the
six statements gone, leaving `return Read<T>(type);`.

### Where it runs, and why

`ForkPipeline.BeforeUnusedLocalsAreDropped`, **directly after `InterfaceCallRecovery.Run(method)`.**

* *Not earlier.* The `MethodInfo` operand is a metadata usage or a runtime-generic-context entry, and only
  `RgctxResolver` / `LocalVariables.ResolveTypesAndFields` — which have run several times by this point in
  that hook — put a `RuntimeMethodInfoAnalysisContext` on it. Before that the pass would decline on every
  shared body, which is most of the population.
* *Not later.* The hook ends with `LocalVariables.ResolveTypesAndFields(method)`, and a recovered call has
  to be there for it: the callee's return type is what types the value the call produces, and everything
  read off that value — a field, an array's length, the loop over it — depends on it. That is the reason
  `InterfaceCallRecovery` is at this position, and this is the same family of defect.
* *Beside `InterfaceCallRecovery` specifically* because they are the two halves of "dispatch that names no
  method": one where il2cpp inlined the whole walk, one where it called out to a helper. Neither can see
  the other's shape, and running them adjacently keeps the reason in one place.

**On `il2cpp-the-dump-is-not-where-the-pass-runs`:** the dump above is the body as analysis *finished*
with it, and the pass runs earlier. Three of the five things it keys on are created by the lifter and
never rewritten (the unresolved `Call`, the `+8` `Move`, the `IndirectCall`); the fourth — the operand
being a folded `methodof(…)` rather than a local — is handled **both ways**, so the fold's position in the
pipeline cannot decide the outcome; the fifth, the `RuntimeMethodInfoAnalysisContext` type, is the one
that argues for the position above. The pass carries `GVCALL_TRACE` (see below) so that the first export
answers what a build could not be run to answer here.

### The trace

`GVCALL_TRACE=1` prints, per method, one line per site — elected addresses, whether the MethodInfo named a
method, whether the `+8` load and the indirect call were found, and what was emitted or why it declined.
`GVCALL_METHOD=<substring>` narrows it, in the shape `RGCTX_TRACE` / `IFACE_TRACE` already use. Counting
`GVCALL fold` against `GVCALL decline` in the export log is the measurement of what this moved.

---

## 5. Files changed

| file | what | upstream? |
|---|---|---|
| `External/Cpp2IL/Cpp2IL.Core/Analysis/GenericVirtualCallRecovery.cs` | **new** — the finder and the rewrite | no |
| `External/Cpp2IL/Cpp2IL.Core/Analysis/ForkPipeline.cs` | **one line** + its reason, in `BeforeUnusedLocalsAreDropped` | no (fork file) |

No upstream Cpp2IL file is touched, so `FORK.md` needs no new row. No signature is changed. No virtual
address appears anywhere in the code.

## 6. What it should move, and how to tell

| | round 2 | expected |
|---|---|---|
| `notfound` sites | 1648 | −853 → ~795 |
| `indirect` sites | 1965 | −~1743 → ~220 |
| `unmanaged` sites | 13457 | falls, if the sweep works; **rises by ~2500 if it does not** |
| `full` | 12861 | up |
| Fluffy Field, everything | — | **flat** — that is the control, and a move there is a regression to explain |

Run `callcensus.py` after the export: `3B02AC0` should be gone from the histogram entirely, and the
`indirect` X8 count (1606 of 1965) should collapse. `gamescore.py` decides keep-or-revert; per CLAUDE.md,
count generation failures beside it — this turns a non-call into a call, which is exactly the case
`il2cpp-a-thrown-body-scores-as-a-whole-one` warns about.
