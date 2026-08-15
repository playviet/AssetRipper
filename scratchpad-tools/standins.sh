#!/bin/zsh
# standins.sh <n> - how many `default(...)` stand-ins an export still has.
#
# The measure for a family no scorer can see. A wrong parameter register, a wrong argument slot, a value the
# recovery could not produce at all - none of them is a marker and none of them is a commented statement, so
# `full` and `commented` sit still. What moves is the number of places the generator had to write
# `default(...)` because it had nothing to write. `il2cpp-a-run-of-eight-then-the-stack` measured its whole
# family this way.
#
# A FALLING COUNT IS ONLY A WIN IF THE VALUES ARE RIGHT: the call-side attempt cut it by 59 by turning 59
# stand-ins into confident wrong values. Read the diff.
SP=${0:A:h}
N=$1
[ -n "$N" ] || { echo "usage: standins.sh <n>"; exit 1; }
APK="$(cat $SP/apkpath)"
ROOT="${APK%.apk}_$N/ExportedProject/Assets/Scripts/Assembly-CSharp"
[ -d "$ROOT" ] || { echo "no export $N"; exit 1; }
echo "default(...) in export $N: $(grep -rho 'default([A-Za-z0-9_.<>\`, ]*)' "$ROOT" | wc -l | tr -d ' ')"
