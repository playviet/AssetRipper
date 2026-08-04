---
description: Rebase the vendored Cpp2IL fork onto the latest upstream Cpp2IL and verify it still builds and exports
---

Bring `External/Cpp2IL` up to date with `https://github.com/SamboyCoding/Cpp2IL` (branch `development`).
Target revision: **$1** — if that is empty, use `development`.

`External/Cpp2IL/FORK.md` is the record of where this fork starts from and every upstream file it touches.
Read it and `CLAUDE.md` before starting. The fork is vendored, not a submodule, so this is a replay rather
than a merge — `rebase-onto-upstream.sh` does the mechanical part:

```sh
External/Cpp2IL/rebase-onto-upstream.sh /tmp/cpp2il-rebase $1
```

It clones upstream, reconstructs the base commit recorded in `FORK.md` **plus AssetRipper's three packaging
commits** (without those the replayed tree fails to build for netstandard2.0), commits this tree on top as a
single commit, and rebases it onto the target.

1. **Resolve whatever conflicts it leaves** in `/tmp/cpp2il-rebase`. They can only be in the files listed in
   the `FORK.md` table — everything else the fork adds lives in files upstream does not have (`Analysis/*.cs`
   and the `*.Fork.cs` siblings), which is the whole point of that arrangement. Keep upstream's version of
   the surrounding code and re-apply the fork's addition on top. Then `git rebase --continue`.

2. **Test where the fork is edited**, not in the repo:
   `cd /tmp/cpp2il-rebase && ~/.dotnet/dotnet test -c Release` — 73 + 5 tests must pass.

3. **Measure before adopting it.** Point the scratchpad working copy at the rebased tree, `bump.sh` to a new
   version (NuGet caches by version, so it must change), run a `fast` export and `cfscore.py`. Compare against
   the numbers in `LocalPackages/README.md`. A rebase that lowers `full` has lost one of the fork's changes in
   a conflict — find which before going further.

4. **Adopt it** only once the score holds:
   ```sh
   rsync -a --delete --exclude .git --exclude TestFiles --exclude bin --exclude obj \
       /tmp/cpp2il-rebase/ External/Cpp2IL/
   ```
   Then run the full (non-`fast`) export and `unityverify.sh` — 0 `error CS`.

5. **Update the record**: the new base commit at the top of `FORK.md`, the file table if the set of upstream
   files the fork touches changed, and the measured paragraph in `LocalPackages/README.md` if the numbers
   moved.

6. **Report** the revision moved onto, the conflicts and how each was resolved, and the before/after score.
   Do not commit or push without being asked.
