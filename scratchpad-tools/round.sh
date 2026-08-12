#!/bin/zsh
# round.sh <old> <new> <n> - build at a new version and export into the follow folder `..._<n>`.
# The APK is the one pinned in `apkpath`; the export roots are the numbered siblings beside it, which is
# where the user follows the work.
set -e
OLD=$1; NEW=$2; N=$3
[ -n "$OLD" ] && [ -n "$NEW" ] && [ -n "$N" ] || { echo "usage: round.sh <old> <new> <n>"; exit 1; }
SP=${0:A:h}
export DOTNET_ROOT=$HOME/.dotnet
export PATH=$HOME/.dotnet:$PATH

APK="$(cat $SP/apkpath)"
[ -f "$APK" ] || { echo "the APK named in apkpath is gone: $APK"; exit 1; }
OUT="${APK%.apk}_$N"

$SP/bump.sh $OLD $NEW
dotnet $SP/riprun/bin/Release/net10.0/riprun.dll "$APK" "$OUT" $SP/g$NEW.log Level3 fast
echo "exported to $OUT"
