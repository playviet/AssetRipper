#!/bin/zsh
# why.sh <n> - export into ..._<n> with REPAIR_WHY set, using the build already in place.
#
# The dump says what the FIRST compilation objected to behind every statement the repair commented out.
# It needs its own export because the environment variable is read while the export runs.
set -e
SP=${0:A:h}
N=$1
[ -n "$N" ] || { echo "usage: why.sh <n>"; exit 1; }
export DOTNET_ROOT=$HOME/.dotnet
export PATH=$HOME/.dotnet:$PATH
APK="$(cat $SP/apkpath)"
OUT="${APK%.apk}_$N"
export REPAIR_WHY=$SP/why$N.tsv
rm -f $REPAIR_WHY
dotnet $SP/riprun/bin/Release/net10.0/riprun.dll "$APK" "$OUT" $SP/g$N.log Level3 fast | tail -2
wc -l $REPAIR_WHY
