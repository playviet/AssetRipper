#!/bin/zsh
# census.sh <n> - game export with CATCH_CENSUS on.
#
# The whole stream is kept and the census is grepped OUT OF THE FILE, never out of the pipe. Filtering the
# process's output with `grep` and then reading the result as "did it work" hid a stack overflow for a whole
# round: the export exited 0, wrote DONE, produced no scripts, and every scorer reported zeroes. See
# memory/il2cpp-score-only-a-finished-export.
SP=${0:A:h}
export DOTNET_ROOT=$HOME/.dotnet
export PATH=$HOME/.dotnet:$PATH
export CATCH_CENSUS=1
export CATCH_TRACE=1
APK="$(cat $SP/apkpath)"
OUT="${APK%.apk}_$1"
RAW=$SP/raw$1.txt
dotnet $SP/riprun/bin/Release/net10.0/riprun.dll "$APK" "$OUT" $SP/g$1.log Level3 fast > $RAW 2>&1
grep -E "^CATCH" $RAW > $SP/census$1.txt

# Did it actually finish? Asked of the unfiltered stream and of the tree, not of the census.
grep -q "^DONE" $RAW || { echo "*** the export did not reach DONE - last of $RAW:"; tail -20 $RAW; exit 1; }
FILES=$(find "$OUT/ExportedProject" -name '*.cs' 2>/dev/null | wc -l)
[ "$FILES" -gt 100 ] || { echo "*** only $FILES .cs files - last of $RAW:"; tail -20 $RAW; exit 1; }
grep -qiE "Unhandled exception|StackOverflow" $RAW && { echo "*** the run threw:"; grep -iE -A 4 "Unhandled exception|StackOverflow" $RAW | head -12; }

echo "exported $FILES .cs files"
echo "--- last census line ---"
grep "CATCH CENSUS" $SP/census$1.txt | tail -1
echo "--- clauses written ---"
grep -c "wrote catch" $SP/census$1.txt
