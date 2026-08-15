#!/bin/zsh
# oracle.sh <exportRoot> [iterations] - the execution oracle, end to end.
#
# The only measure in this loop that asks whether a recovered body computes the RIGHT ANSWER rather than
# whether it compiles whole. Everything else - cfscore, compare2, roundtrip, decisions, allscore - asks the
# second question, and 19 of the 65 corpus shapes they rate `full` answer wrongly.
#
#   REPO=<repo> RIPRUN=<path to riprun.dll> scratchpad-tools/oracle.sh $SP/ec500
#
# Assumes corpus/corpus.apk is already built - see corpus/README.md. That step is Unity, takes ~4 minutes,
# and is only needed when a shape is added.
set -e
OUT=$1
ITERATIONS=${2:-2000}
[ -n "$OUT" ] || { echo "usage: oracle.sh <exportRoot> [iterations]"; exit 1; }

HERE=${0:A:h}
REPO=${REPO:-${HERE:h}}
RIPRUN=${RIPRUN:-$SP/riprun/bin/Release/net10.0/riprun.dll}
export DOTNET_ROOT=$HOME/.dotnet
export PATH=$HOME/.dotnet:$PATH

[ -f "$REPO/corpus/corpus.apk" ] || { echo "no $REPO/corpus/corpus.apk - run the Unity build first"; exit 1; }
[ -f "$RIPRUN" ] || { echo "no riprun at $RIPRUN - set RIPRUN"; exit 1; }

dotnet "$RIPRUN" "$REPO/corpus/corpus.apk" "$OUT" "$OUT.log" 3 fast | tail -2

# The second argument to autodiff is the FILE, not the export root. And an export directory that does not
# exist is silent in every scorer - os.walk yields nothing and a scan reports zero, which reads as perfect.
REC=$OUT/ExportedProject/Assets/Scripts/Assembly-CSharp/Corpus.cs
[ -f "$REC" ] || { echo "the export produced no Corpus.cs"; exit 1; }

# At least 2000: `Divide` only misbehaves on b == 0, drawn with probability 1/180, and at 200 iterations the
# run reported one more agreement than there is.
python3 "$HERE/autodiff.py" "$REPO/corpus/Assets/Corpus.cs" "$REC" $ITERATIONS | tee "$OUT.oracle"
python3 "$HERE/corpusscore.py" "$REC" "$OUT.oracle"
