#!/bin/zsh
# score.sh <n> - every scorer on export <n>, in one go. Rebuilt here; README lists it but it was not committed.
SP=${0:A:h}
N=$1
[ -n "$N" ] || { echo "usage: score.sh <n>"; exit 1; }
APK="$(cat $SP/apkpath)"
E="${APK%.apk}_$N"
[ -d "$E" ] || { echo "no export $E"; exit 1; }

echo "===== export $N ====="
echo "--- genfail ---"
grep -c 'Decompiling.*failed' $SP/g*.log 2>/dev/null | tail -3
echo "--- cfscore ---"
python3 $SP/cfscore.py "$E" 2>&1 | tail -8
echo "--- compare2 ---"
python3 $SP/compare2.py "$E/ExportedProject" 2>&1 | tail -8
echo "--- allscore ---"
python3 $SP/allscore.py "$E/ExportedProject/Assets/Scripts/Assembly-CSharp" 2>&1 | grep -E "totals|full|partial|files" | tail -8
echo "--- decisions ---"
python3 $SP/decisions.py "$E" 2>&1 | tail -6
echo "--- roundtrip ---"
python3 $SP/roundtrip.py "$E/ExportedProject" $SP/rt.jsonl 2>&1 | tail -6
echo "--- notecensus ---"
python3 $SP/notecensus.py "$E/ExportedProject" 2>&1 | head -4
echo "--- floatbits ---"
python3 $SP/floatbits.py "$E/ExportedProject/Assets/Scripts/Assembly-CSharp" 2>&1 | grep -E "decode|sites|of|=" | tail -12
