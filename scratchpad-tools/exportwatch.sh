#!/bin/bash
# exportwatch.sh <log> [stdout] - one-shot status of an export that is already running.
#
# A full export of a 149 MB libil2cpp.so takes tens of minutes and says nothing while it works, so the only
# question anyone actually has is "is it alive, where has it got to, and is it about to run the machine out
# of memory". This answers that in one screen and then exits. It is a probe, not a monitor: run it again
# when you want a newer answer (`watch -n30` if you insist).
#
# Everything it does is read-only. It never truncates, locks, or rewrites the log, so it is safe to point at
# a file riprun is still appending to.
#
# The riprun process is found by matching the LOG PATH in the process arguments - riprun is invoked with the
# log as its third argument, so that identifies the one run you asked about even when several are alive. If
# nothing matches, it falls back to any riprun on the machine and says so.
#
# The stdout capture is optional and only used to spot `DONE`, which riprun writes to stdout and never to
# the log. If not given, `<log without .log>.out` and `<log>.out` are tried.

set -u

usage() { sed -n '2p' "$0" | sed 's/^# \{0,1\}//'; }

if [ "$#" -lt 1 ]; then usage >&2; exit 2; fi
case "$1" in -h|--help) usage; exit 0 ;; esac

LOG=$1
OUT=${2:-}
if [ -z "$OUT" ]; then
    for cand in "${LOG%.log}.out" "$LOG.out"; do
        [ -f "$cand" ] && { OUT=$cand; break; }
    done
fi

[ -f "$LOG" ] || { echo "exportwatch: no log at $LOG" >&2; exit 2; }

NOW=$(date +%s)

# ---------------------------------------------------------------- find the process

# `pgrep -f riprun` and a plain grep both match the SHELL that launched the export, and any shell running a
# command that merely names riprun - including this one. The only honest test is that the process's own
# executable, its first argv token, is riprun. Field 5 of the ps line below is exactly that.
PS_ALL=$(ps -Ao pid=,rss=,etime=,state=,args= 2>/dev/null)

# tier 1: the process's own executable IS riprun (field 5 is argv[0]).
PSTAB=$(printf '%s\n' "$PS_ALL" | LC_ALL=C awk '$5 ~ /(^|\/)riprun$/ { print }')
# tier 2: riprun appears somewhere in the arguments and argv[0] is not an interpreter or a text tool.
# This is the `dotnet riprun.dll` shape. Shells are excluded by name because the shell that LAUNCHED the
# export names riprun too, and reporting its 1 MB of RSS as the export's would be worse than saying nothing.
[ -n "$PSTAB" ] || PSTAB=$(printf '%s\n' "$PS_ALL" | LC_ALL=C awk '
    $5 ~ /(^|\/)(sh|bash|zsh|ksh|dash|fish|csh|tcsh|grep|pgrep|awk|sed|ps|tail|head|cat|python|python3|watch)$/ { next }
    /riprun/ { print }')

PS_LINE=$(printf '%s\n' "$PSTAB" | LC_ALL=C grep -F -- "$LOG" | head -n 1)
MATCH_KIND="this log"
if [ -z "$PS_LINE" ]; then
    PS_LINE=$(printf '%s\n' "$PSTAB" | LC_ALL=C grep -v '^[[:space:]]*$' | sort -k2,2nr | head -n 1)
    MATCH_KIND="some riprun (NOT matched to this log)"
fi
[ -n "$PS_LINE" ] || MATCH_KIND="-"

PID=""; RSS_KB=0; ETIME=""; STATE=""
if [ -n "$PS_LINE" ]; then
    PID=$(printf '%s' "$PS_LINE"  | awk '{ print $1 }')
    RSS_KB=$(printf '%s' "$PS_LINE" | awk '{ print $2 }')
    ETIME=$(printf '%s' "$PS_LINE"  | awk '{ print $3 }')
    STATE=$(printf '%s' "$PS_LINE"  | awk '{ print $4 }')
fi

# ---------------------------------------------------------------- elapsed

# ps etime is [[dd-]hh:]mm:ss
etime_secs() {
    printf '%s' "$1" | awk '
        { s = $0; d = 0
          if (index(s, "-")) { d = substr(s, 1, index(s, "-") - 1); s = substr(s, index(s, "-") + 1) }
          n = split(s, p, ":")
          t = 0
          for (i = 1; i <= n; i++) t = t * 60 + p[i]
          printf "%d", t + d * 86400 }'
}
hms() { awk -v s="$1" 'BEGIN { printf "%dh%02dm%02ds", s/3600, (s%3600)/60, s%60 }'; }

LOG_BIRTH=$(stat -f '%B' -- "$LOG" 2>/dev/null || echo "$NOW")
LOG_MTIME=$(stat -f '%m' -- "$LOG" 2>/dev/null || echo "$NOW")
LOG_SIZE=$(stat -f '%z' -- "$LOG" 2>/dev/null || echo 0)

if [ -n "$PID" ] && [ "$MATCH_KIND" = "this log" ]; then
    ELAPSED=$(etime_secs "$ETIME")
    ELAPSED_SRC="process etime"
else
    ELAPSED=$((LOG_MTIME - LOG_BIRTH))
    ELAPSED_SRC="log birth to last write"
fi
IDLE=$((NOW - LOG_MTIME))

# ---------------------------------------------------------------- phase

# The last line that is a real progress line: `General : ...`, `Import : ...`, `Cpp2IL : ...`. Per-method
# decompile failures and their stack frames are excluded - there can be tens of thousands of them in a row,
# and none of them says where the export has got to. Read from the tail in widening windows so a live log
# of any size costs a bounded read.
phase_from() {
    tail -n "$1" -- "$LOG" 2>/dev/null | LC_ALL=C awk '
        /Decompiling .* failed:/ { next }
        /^[[:space:]]/           { next }
        /\[Error\]/              { next }
        /^[A-Za-z][A-Za-z0-9_]* : ./ { clean = $0 }
        /./                      { any = $0 }
        END { if (clean != "") print clean; else print any }'
}
PHASE=$(phase_from 2000)
[ -n "$PHASE" ] || PHASE=$(phase_from 50000)
[ -n "$PHASE" ] || PHASE="(nothing but errors in the log)"

# ---------------------------------------------------------------- failures and DONE

FAILS=$(LC_ALL=C grep -cF -- 'Decompiling ' "$LOG" 2>/dev/null)
[ -n "$FAILS" ] || FAILS=0

DONE_STATE="no"
if [ -n "$OUT" ] && [ -f "$OUT" ]; then
    if tail -n 20 -- "$OUT" 2>/dev/null | grep -q '^DONE[[:space:]]*$'; then
        DONE_STATE="YES - the export finished"
    fi
else
    DONE_STATE="unknown (no stdout capture given; DONE is never written to the log)"
fi

# ---------------------------------------------------------------- peak, if exportloop is driving

# RSS is a snapshot, and on a machine this deep into swap it swings by hundreds of MB between two reads.
# When exportloop.sh launched the run it has been sampling every 15s into a sibling rss-<n>.tsv; the peak
# in there is the number that decides whether the next OOM was close.
PEAK_NOTE=""
case "$LOG" in
    */export-*.log)
        _stem=${LOG##*/export-}
        _tsv="${LOG%/*}/rss-${_stem%.log}.tsv"
        if [ -f "$_tsv" ]; then
            _peak=$(awk -F'\t' 'NR > 1 && $3 + 0 > m { m = $3 + 0 } END { printf "%d", m / 1024 }' "$_tsv")
            PEAK_NOTE="  peak so far ${_peak} MB"
        fi
        ;;
esac

# ---------------------------------------------------------------- memory

PHYS_MB=$(awk -v b="$(sysctl -n hw.memsize 2>/dev/null || echo 0)" 'BEGIN { printf "%d", b / 1048576 }')
AVAIL_MB=$(vm_stat 2>/dev/null | awk '
    /page size of/       { for (i = 1; i <= NF; i++) if ($i == "of") ps = $(i + 1) }
    /^Pages free/        { gsub(/\./, "", $3); f  = $3 }
    /^Pages inactive/    { gsub(/\./, "", $3); ia = $3 }
    /^Pages speculative/ { gsub(/\./, "", $3); s  = $3 }
    /^Pages purgeable/   { gsub(/\./, "", $3); p  = $3 }
    END { if (ps == "") ps = 4096; printf "%d", (f + ia + s + p) * ps / 1048576 }')
FREE_PCT=$(memory_pressure 2>/dev/null |
    awk -F: '/free percentage/ { gsub(/[^0-9]/, "", $2); print $2; found = 1 }
             END { if (!found) print 100 }' | tail -n 1)

# ---------------------------------------------------------------- report

printf 'log       : %s (%s bytes, last written %ss ago)\n' "$LOG" "$LOG_SIZE" "$IDLE"
if [ -n "$PID" ]; then
    printf 'alive     : YES  pid %s  state %s  [matched by %s]\n' "$PID" "$STATE" "$MATCH_KIND"
    printf 'rss       : %s MB of %s MB physical (%s%%)%s\n' \
        "$((RSS_KB / 1024))" "$PHYS_MB" \
        "$(awk -v r="$RSS_KB" -v p="$PHYS_MB" 'BEGIN { printf "%.1f", p ? r / 1024 * 100 / p : 0 }')" \
        "$PEAK_NOTE"
else
    printf 'alive     : NO   - no riprun process is running\n'
    printf 'rss       : -\n'
fi
printf 'elapsed   : %s (%ss, from %s)\n' "$(hms "$ELAPSED")" "$ELAPSED" "$ELAPSED_SRC"
printf 'phase     : %s\n' "$PHASE"
printf 'failures  : %s methods failed to decompile so far\n' "$FAILS"
printf 'DONE      : %s\n' "$DONE_STATE"
printf 'memory    : %s MB available, %s%% free system-wide\n' "$AVAIL_MB" "$FREE_PCT"

[ -n "$PID" ] && exit 0
exit 1
