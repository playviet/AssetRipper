---
description: Merge the latest upstream AssetRipper into this fork and verify it still builds and exports
---

Bring this fork up to date with `https://github.com/AssetRipper/AssetRipper` (remote `upstream`, branch
`master`). Target revision: **$1** — if that is empty, use `upstream/master`.

Read `CLAUDE.md` first; the mergeability rules there are what keeps this cheap.

1. **See what is coming.** `git fetch upstream`, then `git log --oneline HEAD..<target>` and
   `git diff --stat HEAD...<target>`. Report how many commits and which of *this fork's* files upstream also
   touched — those are the only places a conflict can arise.

2. **Merge on a branch, never on `master` directly.**
   `git switch -c update-assetripper-<yyyymmdd>` then `git merge <target>`.

3. **Resolve conflicts by keeping both intentions.** This fork's changes to upstream files are deliberately
   small and additive (a case in a switch, one call at one point in a pipeline). Take upstream's version of
   the surrounding code and re-apply the fork's addition on top of it. If upstream deleted or rewrote the
   method the fork was hooking into, say so rather than guessing where the hook now belongs.

4. **Verify, in this order** — stop and report at the first failure:
   - `~/.dotnet/dotnet build Source/AssetRipper.SourceGenerated/AssetRipper.SourceGenerated.csproj -c Release`
     then build the solution. The `~/.dotnet` SDK is required; the system one fails with a misleading
     missing-member error.
   - Export the game and score it: `scratchpad/riprun` in `fast` mode, then `scratchpad/cfscore.py`. Compare
     against the numbers in `LocalPackages/README.md`. **A merge that lowers `full` is a merge that broke
     something** — find out what before going further.
   - `scratchpad/unityverify.sh` — 0 `error CS`.

5. **Report** the commit range merged, the conflicts and how each was resolved, and the before/after score.
   Do not push or merge to `master` without being asked.

If upstream has restructured something the fork depends on to the point where the hook no longer exists,
stop and describe the situation instead of inventing a new place to put it.
