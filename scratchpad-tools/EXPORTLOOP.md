# exportloop.sh / exportwatch.sh — running an export that is too big to babysit

Snacky Dash 1.11.0 is a 149 MB `libil2cpp.so` with 167817 methods. A full export of it on a 16 GB machine
runs for tens of minutes, and every way it can fail looks the same from outside: no export directory, and a
log that stopped. These two scripts turn "run it until it works" into something mechanical.

    scratchpad-tools/exportloop.sh  <apk> <exportRoot> <logdir> [attempts]   # run it, classify it
    scratchpad-tools/exportwatch.sh <log> [stdout]                           # probe one already running

Neither builds anything. `exportloop.sh` refuses if the riprun binary is not already there, because a build
during a running export is exactly the thing that makes the export die.

## What decides the verdict

`exportloop.sh` prints exactly one line beginning `verdict: `, and everything else on stdout is context.
Grep for `^verdict: ` and you have the answer.

| verdict | what happened | retried? |
|---|---|---|
| `ok` | `DONE` on the last line of stdout **and** exit 0 | — |
| `crashed: <exception> at <first fork frame>` | something was thrown and it was not an allocation failure | no |
| `oom` | exit 137/139/134, an out-of-memory signature in the fatal block, or any other death by signal | yes |
| `truncated` | ran, threw nothing, said no `DONE`; or exit 130/143, a person stopping it | no |

`DONE` is riprun's own success flag and it goes to **stdout only** — never to the `FileLogger` log. That is
why every attempt keeps two files: `export-<n>.log` is what `logcensus.py` reads, `export-<n>.out` is where
`DONE` and any fatal stack actually land. A tool that only looks at the log can never tell a finished export
from a dead one.

### The trap the classifier is built around

Cpp2IL catches a generator failure **per method**, logs

    Cpp2IL [Error] : Decompiling <Type>::<Method> failed: <ExceptionType>: <message>

with a full stack, and carries on. There are eleven of those in the live Snacky log after twenty minutes and
there will be thousands by the end. **None of them is the crash.** So the fatal scanner arms only on
`Unhandled exception.` / `Fatal error.` / `Stack overflow.`, and disarms again on every `Decompiling …
failed:` line. Without that it would report the first per-method stack as the cause of death every time.

The same trap runs the other way for `oom`. A per-method `OutOfMemoryException` is caught and survivable, so
an out-of-memory signature only counts when it appears **inside** the fatal block. Reading a caught one as
the cause would label every later crash an `oom` and send the harness into a retry loop it cannot win.
Fixture `caught-oom-then-crash` in the unit test below is exactly this case.

## Why `attempts` defaults to 1

A crash is deterministic. Retrying one costs another twenty minutes to reproduce a stack trace already
printed. Only `oom` gets a second run, and only under

    DOTNET_gcServer=0 DOTNET_GCConserveMemory=9

which is the one thing that changes an OOM outcome without a code change. Server GC keeps a heap per core
and will not give memory back; `GCConserveMemory=9` is the most aggressive setting there is.

SIGINT (130) and SIGTERM (143) are carved out of `oom` deliberately — those are a person stopping the run,
and restarting a run someone just killed is the worst thing this script could do.

## The guard, and why it is not `pgrep -f riprun`

Refusing to start a second export is the whole point of the script, so there is no flag that skips it. But
the obvious implementation is unusable. `pgrep -f riprun` matches **any** command line containing the
string, and this repo keeps its il2cpp scratch under `scratchpad/riprun/bin/`, so probe and objdump work
from other tasks names that directory constantly. The first version of this guard blocked on another
agent's `llvm-objdump -d --section=il2cpp .../riprun/bin/Release/net10.0/temp/10f3/libil2cpp.so`. A guard
that blocks on unrelated work gets switched off within a day, and a guard that is switched off protects
nothing.

So two things count and nothing else does:

    exec    argv[0] IS riprun                              -> an export is running
    launch  the args invoke ".../riprun/bin/<cfg>/riprun "  -> a shell about to exec one

`exportwatch.sh` uses the same discipline in reverse: it identifies *which* riprun by matching the **log
path** in the process arguments, because riprun takes the log as its third argument. Matching on the string
`riprun` alone would just as happily report the launching shell's 1 MB of RSS as the export's.

## Memory

macOS has no `/proc/meminfo` and no honest single "available" number — the compressor and the file cache
both hold pages a big allocation can have back. Both readings are taken and either can refuse the run:

* `vm_stat`: free + inactive + speculative + purgeable
* `memory_pressure`: the kernel's own "System-wide memory free percentage"

They disagree by gigabytes under load. With the Snacky export running, `vm_stat` said 2777 MB available
while `memory_pressure` said 34% free of 16 GB — a factor of two. Floors are `EXPORTLOOP_MIN_FREE_MB`
(3072) and `EXPORTLOOP_MIN_FREE_PCT` (12); set either to 0 to disable that half.

**RSS is not a reliable instantaneous reading on this machine.** Three `ps` samples of the same live export
inside ten minutes gave 316 MB, 138 MB, 54 MB and 117 MB — it is swapping hard (4.7M pages swapped out).
That is why `exportloop.sh` samples every 15s into `rss-<n>.tsv` and reports the **peak**: a single reading
at the moment of death says nothing about how close the run came to the ceiling.

## Testing it without running an export

`exportloop.sh` cannot be tested end to end while an export is alive — its own guard stops it, correctly.
The parts that can go wrong are all pure functions and can be pulled straight out of the shipped file:

    { echo '#!/bin/bash'; echo 'set -u'
      sed -n '/^scan_fatal() {/,/^}/p'        scratchpad-tools/exportloop.sh
      sed -n '/^print_fatal_stack() {/,/^}/p' scratchpad-tools/exportloop.sh
      sed -n '/^classify_run() {/,/^}/p'      scratchpad-tools/exportloop.sh
      echo '"$@"'; } > /tmp/fns.sh && chmod +x /tmp/fns.sh

    /tmp/fns.sh scan_fatal    some-export.out       # -> <exception> TAB <frame> TAB <0|1 oom>
    /tmp/fns.sh classify_run  <rc> <done> <exc> <frame> <oom>

Read the three fields with `cut -f1/-f2/-f3`, never with `read -r a b c` under `IFS=$'\t'`: a tab is IFS
whitespace, so `read` silently eats the leading empty field and shifts everything left by one. That is what
made `truncated` come out as `crashed: ? at 0` the first time this was tested — a bug in the test, not the
script, and it would have looked like a bug in the script for as long as anyone believed it.

## What exportwatch says, against the live run

    log       : /Users/playviet/Documents/_APK/Snacky Dash/snacky_1.log (24363 bytes, last written 468s ago)
    alive     : YES  pid 2985  state R  [matched by this log]
    rss       : 106 MB of 16384 MB physical (0.6%)
    elapsed   : 0h18m26s (1106s, from process etime)
    phase     : Cpp2IL : Application model created in 3518,204ms
    failures  : 11 methods failed to decompile so far
    DONE      : no
    memory    : 2915 MB available, 36% free system-wide

`last written 468s ago` with `state R` is the normal shape of this export, not a hang: after "Application
model created" Cpp2IL analyses 167817 methods and logs nothing until one of them fails. The phase line is
the last real progress line — per-method failures and their stack frames are excluded from it, because
there can be tens of thousands of them in a row and not one says where the export has got to.

`exportwatch.sh` exits 0 if a riprun is alive and 1 if not, so it works as a wait condition:

    until ! scratchpad-tools/exportwatch.sh "$LOG" >/dev/null; do sleep 60; done
