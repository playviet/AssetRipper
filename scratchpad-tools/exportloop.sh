#!/bin/bash
# exportloop.sh <apk> <exportRoot> <logdir> [attempts]
#
# Run one riprun export to completion, classify what happened, and say so in one line. This is the harness
# for "run it until it works" on a build big enough that the run is the expensive part: Snacky Dash is a
# 149 MB libil2cpp.so with 167817 methods, and a full export of it is measured in tens of minutes on a
# 16 GB machine. Losing one to a second export started by hand, or to an OOM nobody was watching for, costs
# an evening.
#
# What it does that a bare invocation does not:
#
#   1. Refuses to start while another riprun is alive (`pgrep -f riprun`) or while the machine is already
#      short of memory. Two exports of this size on 16 GB do not both finish; they both die. This guard is
#      the point of the script - there is no flag that skips it.
#   2. Samples the export's RSS every 15s into <logdir>/rss-<n>.tsv, so an OOM leaves evidence of how far
#      it got and how big it was when it died, rather than an empty export directory.
#   3. Classifies the outcome as one of ok / crashed / oom / truncated and prints exactly one line
#      beginning `verdict: `. Everything else on stdout is context; that line is the answer.
#   4. Retries ONLY an oom, and only with `DOTNET_gcServer=0 DOTNET_GCConserveMemory=9`, which is the one
#      thing that changes an OOM outcome without a code change. A crash is deterministic - retrying it
#      burns hours to reproduce a stack trace we already have - so the default is `attempts 1`.
#   5. Always finishes with logcensus.py, success or failure. A run that "worked" can still have thrown
#      away thousands of method bodies, and that census is the only place it shows.
#
# The export is always FULL (no `fast`) and RIPRUN_ORIGIN is always `none`. `none` is mandatory for any
# game that is not Fluffy Field: riprun's default ScriptOriginalSourceDirectories point at Fluffy Field's
# DOTween/Dreamteck/EnhancedScroller source on this machine, and without `none` that source is substituted
# into this game's export - a wrong body that compiles whole and that no scorer flags. See memory
# `il2cpp-riprun-injects-the-other-games-source`.
#
# Environment:
#   RIPRUN_BIN                 override the riprun binary (default scratchpad/riprun/bin/Release/net10.0/riprun)
#   EXPORTLOOP_LEVEL           ScriptContentLevel (default Level3)
#   EXPORTLOOP_MIN_FREE_MB     refuse below this much available memory (default 3072; 0 disables)
#   EXPORTLOOP_MIN_FREE_PCT    refuse below this system free percentage (default 12; 0 disables)
#   EXPORTLOOP_SAMPLE_SECS     RSS sample interval (default 15)
#
# Exit status: 0 only on `verdict: ok`. 2 usage, 3 another riprun alive, 4 not enough memory,
# 5 riprun binary or apk missing, 1 the export failed.

set -u
set -o pipefail

SELF_DIR=$(cd -- "$(dirname -- "$0")" && pwd)
REPO=$(cd -- "$SELF_DIR/.." && pwd)

RIPRUN=${RIPRUN_BIN:-$REPO/scratchpad/riprun/bin/Release/net10.0/riprun}
LEVEL=${EXPORTLOOP_LEVEL:-Level3}
MIN_FREE_MB=${EXPORTLOOP_MIN_FREE_MB:-3072}
MIN_FREE_PCT=${EXPORTLOOP_MIN_FREE_PCT:-12}
SAMPLE_SECS=${EXPORTLOOP_SAMPLE_SECS:-15}

# these three are compared with `-gt`/`-lt`, which dies on anything that is not a number
for _v in MIN_FREE_MB MIN_FREE_PCT SAMPLE_SECS; do
    eval "_x=\$$_v"
    case "$_x" in
        ''|*[!0-9]*) echo "exportloop: $_v must be a whole number, got '$_x'" >&2; exit 2 ;;
    esac
done
[ "$SAMPLE_SECS" -ge 1 ] || SAMPLE_SECS=15

usage() {
    cat >&2 <<USAGE
usage: exportloop.sh <apk> <exportRoot> <logdir> [attempts]

  Runs ONE full riprun export (Level3, RIPRUN_ORIGIN=none), refusing to start if another riprun is
  alive or the machine is short of memory, and prints one line beginning "verdict: " that is one of
  ok / crashed: <exception> at <frame> / oom / truncated. Retries only an oom, only if attempts > 1,
  and only under DOTNET_gcServer=0 DOTNET_GCConserveMemory=9. Finishes with logcensus.py either way.

  attempts defaults to 1 on purpose: a crash is deterministic and a retry of one costs hours.

  logs   <logdir>/export-<n>.log   riprun's FileLogger
         <logdir>/export-<n>.out   stdout, which is where DONE and any fatal stack land
         <logdir>/rss-<n>.tsv      RSS sampled every ${EXPORTLOOP_SAMPLE_SECS:-15}s

  see the header of $0 for the whole story, and exportwatch.sh to check on a run in flight
USAGE
}

if [ "$#" -lt 3 ]; then
    usage
    exit 2
fi
case "${1:-}" in
    -h|--help) usage; exit 0 ;;
esac

APK=$1
EXPORT_ROOT=$2
LOGDIR=$3
ATTEMPTS=${4:-1}

case "$ATTEMPTS" in
    ''|*[!0-9]*) echo "exportloop: attempts must be a number, got '$ATTEMPTS'" >&2; exit 2 ;;
esac
[ "$ATTEMPTS" -ge 1 ] || ATTEMPTS=1

# ---------------------------------------------------------------- memory readings

# macOS has no /proc/meminfo and no single honest "available" number: the compressor and the file cache
# both hold pages that a big allocation can have back. free+inactive+speculative+purgeable is the usual
# approximation, and `memory_pressure`'s own percentage is the kernel's view of the same question. Both are
# printed and either one can refuse the run, because they disagree by gigabytes under load.
avail_mb() {
    vm_stat 2>/dev/null | awk '
        /page size of/     { for (i = 1; i <= NF; i++) if ($i == "of") ps = $(i + 1) }
        /^Pages free/        { gsub(/\./, "", $3); f  = $3 }
        /^Pages inactive/    { gsub(/\./, "", $3); ia = $3 }
        /^Pages speculative/ { gsub(/\./, "", $3); s  = $3 }
        /^Pages purgeable/   { gsub(/\./, "", $3); p  = $3 }
        END { if (ps == "") ps = 4096; printf "%d", (f + ia + s + p) * ps / 1048576 }'
}

free_pct() {
    memory_pressure 2>/dev/null |
        awk -F: '/free percentage/ { gsub(/[^0-9]/, "", $2); print $2; found = 1 }
                 END { if (!found) print 100 }' | tail -n 1
}

phys_mb() {
    awk -v b="$(sysctl -n hw.memsize 2>/dev/null || echo 0)" 'BEGIN { printf "%d", b / 1048576 }'
}

# ---------------------------------------------------------------- guards

# Two things count as "an export is in flight", and nothing else does:
#
#   exec    the process's own executable IS riprun - argv[0], field 5 below. That is a running export.
#   launch  the arguments invoke a riprun binary (".../riprun/bin/<cfg>/riprun <args>"). That is the shell
#           that is about to exec one, in the second before it does.
#
# `pgrep -f riprun` on its own is too coarse to be useful here: this repo keeps its il2cpp scratch under
# scratchpad/riprun/bin/, so probe/objdump/disassembly commands from other work name that directory
# constantly, and a guard that blocked on those would block forever and be turned off within a day. A guard
# that is switched off protects nothing.
guard_no_riprun() {
    local table n
    table=$(ps -Ao pid=,rss=,etime=,state=,args= 2>/dev/null | LC_ALL=C awk -v self="$$" -v par="$PPID" '
        $1 == self || $1 == par                { next }
        $5 ~ /(^|\/)riprun$/                   { print "exec  " $0; next }
        /riprun\/bin\/[^ ]*\/riprun([ "]|$)/  { print "launch " $0; next }')
    [ -n "$table" ] || return 0
    n=$(printf '%s\n' "$table" | grep -c .)
    echo "exportloop: REFUSING to start - $n riprun process(es) already running." >&2
    echo "            Two exports of this size do not both finish on 16 GB; they both die." >&2
    printf '%s\n' "$table" | cut -c1-150 | sed 's/^/            /' >&2
    echo "            watch it with scratchpad-tools/exportwatch.sh <log>, or kill it, then re-run." >&2
    return 1
}

guard_memory() {
    local a p phys
    a=$(avail_mb)
    p=$(free_pct)
    phys=$(phys_mb)
    printf 'memory: %s MB available of %s MB physical, %s%% free (floor %s MB / %s%%)\n' \
        "$a" "$phys" "$p" "$MIN_FREE_MB" "$MIN_FREE_PCT"
    if [ "$MIN_FREE_MB" -gt 0 ] && [ "$a" -lt "$MIN_FREE_MB" ]; then
        echo "exportloop: REFUSING to start - only ${a} MB available, floor is ${MIN_FREE_MB} MB." >&2
        echo "            close something, or set EXPORTLOOP_MIN_FREE_MB if you mean it." >&2
        return 1
    fi
    if [ "$MIN_FREE_PCT" -gt 0 ] && [ "$p" -lt "$MIN_FREE_PCT" ]; then
        echo "exportloop: REFUSING to start - system free memory is ${p}%, floor is ${MIN_FREE_PCT}%." >&2
        echo "            close something, or set EXPORTLOOP_MIN_FREE_PCT if you mean it." >&2
        return 1
    fi
    return 0
}

# ---------------------------------------------------------------- the fatal-exception scanner

# Cpp2IL logs one `Decompiling <T>::<M> failed: <Exception>` per method it could not recover, each with a
# full stack. Those are NOT the crash - the export carries on past them and logcensus.py counts them. Only
# a `Unhandled exception.` / `Fatal error.` / `Stack overflow.` line means the process died, so the scanner
# arms on those and disarms on every per-method failure.
# Prints one TSV line: <exception type> <TAB> <first fork frame> <TAB> <0|1 out-of-memory signature>
scan_fatal() {
    [ -f "$1" ] || { printf '\t?\t0\n'; return; }
    LC_ALL=C awk '
        BEGIN { MEM = "OutOfMemory|Out of memory|Insufficient memory|Cannot allocate memory|GC heap hard limit" }
        /Decompiling .* failed:/ { armed = 0; next }
        /^Unhandled [Ee]xception/ {
            armed = 1
            line = $0
            sub(/^Unhandled [Ee]xception[.:][ ]*/, "", line)
            if (match(line, /[A-Za-z_][A-Za-z0-9_.`+]*(Exception|Error)/))
                exc = substr(line, RSTART, RLENGTH)
            if (line ~ MEM) oom = 1
            next
        }
        /^Fatal error/    { armed = 1; if (exc == "") exc = "FatalError";    if ($0 ~ MEM) oom = 1; next }
        /^Stack overflow/ { armed = 1; if (exc == "") exc = "StackOverflow"; next }
        {
            # An out-of-memory signature only counts INSIDE the fatal block. Cpp2IL catches a per-method
            # OutOfMemoryException, logs it, and carries on - reading that as the cause of death would
            # label every later crash an oom and send the harness into a retry loop it cannot win.
            if (armed && $0 ~ MEM) oom = 1
            if (armed && $0 ~ /^[[:space:]]+at /) {
                if (frame == "" && match($0, /(Cpp2IL|LibCpp2IL|AssetRipper)[A-Za-z0-9_.`<>+]*\.[A-Za-z0-9_`<>]+/))
                    frame = substr($0, RSTART, RLENGTH)
                if (anyframe == "") { l = $0; sub(/^[[:space:]]+at /, "", l); sub(/\(.*/, "", l); anyframe = l }
            }
        }
        END {
            if (frame == "") frame = anyframe
            if (frame == "") frame = "?"
            printf "%s\t%s\t%d\n", exc, frame, oom + 0
        }' "$1" 2>/dev/null
}

# the fatal exception and its stack, for printing. Bounded: a stack is tens of lines, not thousands.
print_fatal_stack() {
    [ -f "$1" ] || return 0
    LC_ALL=C awk '
        /Decompiling .* failed:/ { p = 0; next }
        /^Unhandled [Ee]xception|^Fatal error|^Stack overflow/ { p = 1; n = 0 }
        p { print; n++; if (n > 60) p = 0 }' "$1"
}

# ---------------------------------------------------------------- the verdict

# classify_run <exit code> <0|1 DONE seen> <exception type> <first fork frame> <0|1 oom signature>
#
# The order is the whole content of this function, so it is written out rather than nested:
#
#   ok        DONE on stdout AND exit 0. riprun writes DONE last and only on the success path, so a DONE
#             with a non-zero exit is not a success - it is something failing on the way out, and falls
#             through to be classified like any other failure.
#   crashed   something was thrown and it was not an allocation failure. Deterministic: retrying it costs
#             another export and produces the same stack.
#   oom       exit 137/139/134 (SIGKILL from jetsam, SIGSEGV, abort), or an out-of-memory signature in the
#             output, or any other death-by-signal with nothing said. This is the only verdict worth a
#             retry, because it is the only one a GC setting can change. SIGINT and SIGTERM are carved out
#             of it: 130 and 143 are a person stopping the run, and restarting a run someone just killed
#             is the worst thing this script could do.
#   truncated ran, said nothing, threw nothing, and did not finish. A deliberate stop, a full disk, or an
#             end the process itself never noticed.
classify_run() {
    local rc=$1 done_seen=$2 exc=$3 frame=$4 oom=$5
    if [ "$rc" -eq 0 ] && [ "$done_seen" -eq 1 ]; then
        echo "ok"
    elif [ -n "$exc" ] && [ "$oom" != "1" ]; then
        echo "crashed: $exc at $frame"
    elif [ "$rc" -eq 130 ] || [ "$rc" -eq 143 ]; then
        # SIGINT/SIGTERM is a person stopping it, not the kernel reclaiming it. Never retry that.
        echo "truncated"
    elif [ "$rc" -eq 137 ] || [ "$rc" -eq 139 ] || [ "$rc" -eq 134 ] || [ "$oom" = "1" ]; then
        echo "oom"
    elif [ "$rc" -ge 128 ]; then
        echo "oom"
    elif [ -n "$exc" ]; then
        echo "crashed: $exc at $frame"
    else
        echo "truncated"
    fi
}

# ---------------------------------------------------------------- the RSS sampler

SAMPLER_PID=""
stop_sampler() {
    if [ -n "$SAMPLER_PID" ]; then
        kill "$SAMPLER_PID" 2>/dev/null
        wait "$SAMPLER_PID" 2>/dev/null
        SAMPLER_PID=""
    fi
}
trap 'stop_sampler' EXIT INT TERM

start_sampler() {
    local pid=$1 tsv=$2 t0
    t0=$(date +%s)
    printf 'epoch\telapsed_s\trss_kb\trss_mb\tavail_mb\tfree_pct\n' > "$tsv"
    (
        while kill -0 "$pid" 2>/dev/null; do
            rss=$(ps -o rss= -p "$pid" 2>/dev/null | tr -d ' ')
            [ -n "$rss" ] || break
            now=$(date +%s)
            printf '%s\t%s\t%s\t%s\t%s\t%s\n' \
                "$now" "$((now - t0))" "$rss" "$((rss / 1024))" "$(avail_mb)" "$(free_pct)" >> "$tsv"
            sleep "$SAMPLE_SECS"
        done
    ) &
    SAMPLER_PID=$!
}

peak_rss_mb() {
    [ -f "$1" ] || { echo 0; return; }
    awk -F'\t' 'NR > 1 && $3 + 0 > m { m = $3 + 0 } END { printf "%d", m / 1024 }' "$1"
}

# ---------------------------------------------------------------- preflight

[ -x "$RIPRUN" ] || {
    echo "exportloop: no riprun binary at $RIPRUN" >&2
    echo "            build it first (scratchpad/bump.sh, then the riprun project) - this script never builds." >&2
    exit 5
}
[ -e "$APK" ] || { echo "exportloop: no apk at $APK" >&2; exit 5; }

mkdir -p -- "$LOGDIR" || exit 5
LOGDIR=$(cd -- "$LOGDIR" && pwd)

echo "exportloop: riprun     $RIPRUN"
echo "exportloop: apk        $APK"
echo "exportloop: exportRoot $EXPORT_ROOT"
echo "exportloop: logdir     $LOGDIR"
echo "exportloop: level      $LEVEL (full export, RIPRUN_ORIGIN=none)"
echo "exportloop: attempts   $ATTEMPTS"
echo

# ---------------------------------------------------------------- the loop

VERDICT=""
LAST_LOG=""
CONSERVE=0
n=1
while [ "$n" -le "$ATTEMPTS" ]; do
    LOG="$LOGDIR/export-$n.log"
    OUT="$LOGDIR/export-$n.out"
    TSV="$LOGDIR/rss-$n.tsv"
    LAST_LOG=$LOG

    echo "=== attempt $n/$ATTEMPTS  $(date '+%Y-%m-%d %H:%M:%S') ==="
    guard_no_riprun || exit 3
    guard_memory    || exit 4

    started=$(date +%s)
    if [ "$CONSERVE" -eq 1 ]; then
        echo "exportloop: retrying under DOTNET_gcServer=0 DOTNET_GCConserveMemory=9"
        DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" RIPRUN_ORIGIN=none \
        DOTNET_gcServer=0 DOTNET_GCConserveMemory=9 \
            "$RIPRUN" "$APK" "$EXPORT_ROOT" "$LOG" "$LEVEL" > "$OUT" 2>&1 &
    else
        DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" RIPRUN_ORIGIN=none \
            "$RIPRUN" "$APK" "$EXPORT_ROOT" "$LOG" "$LEVEL" > "$OUT" 2>&1 &
    fi
    RIP_PID=$!
    echo "exportloop: riprun pid $RIP_PID, sampling RSS every ${SAMPLE_SECS}s into $TSV"

    start_sampler "$RIP_PID" "$TSV"
    wait "$RIP_PID"
    RC=$?
    stop_sampler
    elapsed=$(( $(date +%s) - started ))

    # --- classify
    HAS_DONE=0
    if [ -f "$OUT" ] && tail -n 20 -- "$OUT" | grep -q '^DONE[[:space:]]*$'; then
        HAS_DONE=1
    fi
    FATAL=$(scan_fatal "$OUT")
    F_EXC=$(printf '%s' "$FATAL" | cut -f1)
    F_FRAME=$(printf '%s' "$FATAL" | cut -f2)
    F_OOM=$(printf '%s' "$FATAL" | cut -f3)
    # the log gets the same exception when AssetRipper logs it before dying
    if [ -z "$F_EXC" ]; then
        FATAL=$(scan_fatal "$LOG")
        F_EXC=$(printf '%s' "$FATAL" | cut -f1)
        F_FRAME=$(printf '%s' "$FATAL" | cut -f2)
        [ "$(printf '%s' "$FATAL" | cut -f3)" = "1" ] && F_OOM=1
    fi
    PEAK=$(peak_rss_mb "$TSV")
    PHYS=$(phys_mb)

    VERDICT=$(classify_run "$RC" "$HAS_DONE" "$F_EXC" "$F_FRAME" "$F_OOM")

    echo
    printf 'exportloop: exit %s after %s (%ss), peak RSS %s MB of %s MB physical\n' \
        "$RC" "$(awk -v s="$elapsed" 'BEGIN { printf "%dh%02dm%02ds", s/3600, (s%3600)/60, s%60 }')" \
        "$elapsed" "$PEAK" "$PHYS"

    if [ "$VERDICT" != "ok" ]; then
        echo
        echo "--- last 40 lines of $LOG ---"
        if [ -f "$LOG" ]; then tail -n 40 -- "$LOG"; else echo "(no log written)"; fi
        echo
        echo "--- fatal exception and stack ---"
        stack=$(print_fatal_stack "$OUT")
        [ -n "$stack" ] || stack=$(print_fatal_stack "$LOG")
        if [ -n "$stack" ]; then printf '%s\n' "$stack"; else echo "(none - nothing was thrown; the process stopped)"; fi
        echo
        echo "--- RSS ---"
        printf 'peak %s MB of %s MB physical; samples in %s\n' "$PEAK" "$PHYS" "$TSV"
        if [ -f "$TSV" ]; then tail -n 5 -- "$TSV"; fi
    fi

    if [ "$VERDICT" = "ok" ]; then
        # the next thing anyone does is score this directory, so say where it is and that it has scripts
        printf 'exportloop: export at %s\n' "$EXPORT_ROOT"
        for _sub in ExportedProject/Assets/Scripts ExportedProject/Assets Assets; do
            if [ -d "$EXPORT_ROOT/$_sub" ]; then
                printf 'exportloop: %s holds %s entries\n' "$_sub" "$(ls -1 "$EXPORT_ROOT/$_sub" 2>/dev/null | grep -c .)"
                break
            fi
        done
    fi

    echo
    echo "verdict: $VERDICT"

    [ "$VERDICT" = "ok" ] && break

    # only an oom is worth another run: a crash is deterministic and a retry just reproduces the stack.
    if [ "$VERDICT" = "oom" ] && [ "$n" -lt "$ATTEMPTS" ]; then
        CONSERVE=1
        n=$((n + 1))
        echo
        continue
    fi
    break
done

# ---------------------------------------------------------------- the census, win or lose

echo
echo "=== logcensus $LAST_LOG ==="
if [ -f "$LAST_LOG" ]; then
    python3 "$SELF_DIR/logcensus.py" "$LAST_LOG" 15
else
    echo "(no log to census - riprun never opened $LAST_LOG)"
fi

[ "$VERDICT" = "ok" ] && exit 0
exit 1
