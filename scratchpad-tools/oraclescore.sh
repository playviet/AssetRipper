#!/bin/zsh
# oraclescore.sh <exportRoot> [iterations] - autodiff + corpusscore over an ALREADY exported corpus.
#
# oracle.sh re-exports before it scores, which is right for a fresh round and wasteful when the export is
# already on disk - as it is whenever a corpus-only export was used to falsify a change before spending a
# game export on it. Same two tools, same numbers, no re-export.
#
#   REPO=<repo> scratchpad-tools/oraclescore.sh $SP/ec604
set -e
OUT=$1
[ -n "$OUT" ] || { echo "usage: oraclescore.sh <exportRoot> [iterations]"; exit 1; }

HERE=${0:A:h}
REPO=${REPO:-${HERE:h}}
REC=$OUT/ExportedProject/Assets/Scripts/Assembly-CSharp/Corpus.cs
[ -f "$REC" ] || { echo "no Corpus.cs under $OUT - an export directory that does not exist is silent in every scorer"; exit 1; }

# At least 2000: `Divide` only differs on b == 0, drawn with probability 1/180.
python3 "$HERE/autodiff.py" "$REPO/corpus/Assets/Corpus.cs" "$REC" ${2:-2000} | tee "$OUT.oracle"
python3 "$HERE/corpusscore.py" "$REC" "$OUT.oracle"
