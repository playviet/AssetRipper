#!/bin/zsh
# roundz.sh <old> <new> <n> - worktree-local round: bump, then export into ..._<n>
set -e
OLD=$1; NEW=$2; N=$3
[ -n "$OLD" ] && [ -n "$NEW" ] && [ -n "$N" ] || { echo "usage: roundz.sh <old> <new> <n>"; exit 1; }
SP=${0:A:h}
export DOTNET_ROOT=$HOME/.dotnet
export PATH=$HOME/.dotnet:$PATH
APK="$(cat $SP/apkpath)"
[ -f "$APK" ] || { echo "the APK named in apkpath is gone: $APK"; exit 1; }
OUT="${APK%.apk}_$N"
$SP/bumpz.sh $OLD $NEW
dotnet $SP/riprun/bin/Release/net10.0/riprun.dll "$APK" "$OUT" $SP/g$NEW.log Level3 fast
echo "exported to $OUT"
