#!/bin/sh
# Replays this fork onto a revision of upstream Cpp2IL.
#
#   rebase-onto-upstream.sh <workdir> [<upstream-ref>]
#
# Clones upstream into <workdir>, builds the target - the revision you asked for plus AssetRipper's packaging
# commits - reconstructs the commit this fork was written against, commits this tree on top of that, and
# rebases it onto the target. What is left is an ordinary rebase you finish by hand; FORK.md says which files
# can conflict and why.
set -e

FORK=$(cd "$(dirname "$0")" && pwd)
WORK=${1:?usage: rebase-onto-upstream.sh <workdir> [<upstream-ref>]}
REF=${2:-origin/development}

# The tree the fork was written against, and the AssetRipper commits that give the projects their package
# ids - see FORK.md. The packages are built from those, so they belong to the target as much as upstream does.
BASE=b20ca0d04f9d4deaf04636428b3c116589b2a167
PICKS="97566c8 800cc39 e3aa824"

if [ -e "$WORK" ]; then
    echo "$WORK already exists; give a path that does not." >&2
    exit 1
fi

git clone https://github.com/SamboyCoding/Cpp2IL.git "$WORK"
cd "$WORK"
git remote add assetripper https://github.com/AssetRipper/Cpp2IL.git
git fetch --quiet assetripper

echo
echo "Building the target: $REF plus AssetRipper's packaging commits ..."
git checkout --quiet -b target "$REF"
git cherry-pick $PICKS || {
    echo
    echo "A packaging commit did not apply to $REF. Resolve it, 'git cherry-pick --continue', then re-run"
    echo "the rest of this script by hand - it is the three commands after this one."
    exit 1
}

echo
echo "Reconstructing the base the fork was written against ..."
git checkout --quiet -b fork "$BASE"
git cherry-pick $PICKS
git tag fork-base

# The fork as one commit on top of that base. TestFiles is upstream's sample binaries, which this tree does
# not carry and must not delete.
rsync -a --delete --exclude .git --exclude TestFiles --exclude bin --exclude obj "$FORK/" .
git add -A
git commit --quiet -m "AssetRipper Il2Cpp recovery fork"

echo
echo "Rebasing the fork onto the target ..."
git rebase --onto target fork-base fork || cat <<EOF

The rebase stopped on a conflict. Resolve it, 'git add' the file, and 'git rebase --continue'.
FORK.md lists every file the fork touches and what it changes there.
EOF

echo
echo "When the rebase is finished and 'dotnet test -c Release' passes, copy the result back:"
echo "  rsync -a --delete --exclude .git --exclude TestFiles --exclude bin --exclude obj $WORK/ $FORK/"
