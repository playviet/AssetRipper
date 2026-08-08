#!/bin/zsh
# bump.sh <old> <new> - rebuild Cpp2IL at a new version and make riprun pick it up.
# NuGet caches by version, so the version has to change or a rebuild is invisible.
set -e
OLD=$1; NEW=$2
[ -n "$OLD" ] && [ -n "$NEW" ] || { echo "usage: bump.sh <old> <new>"; exit 1; }
# old==new would sed nothing and then `rm -f *.$OLD.nupkg` would delete the package just built
[ "$OLD" != "$NEW" ] || { echo "old and new must differ"; exit 1; }
SP=${0:A:h}
AR=/Users/playviet/Documents/_BZ/AssetRipper
export DOTNET_ROOT=$HOME/.dotnet
export PATH=$HOME/.dotnet:$PATH

sed -i '' "s|<VersionPrefix>$OLD</VersionPrefix>|<VersionPrefix>$NEW</VersionPrefix>|" \
  /Users/playviet/Documents/_BZ/AssetRipper/External/Cpp2IL/Cpp2IL.Core/Cpp2IL.Core.csproj /Users/playviet/Documents/_BZ/AssetRipper/External/Cpp2IL/LibCpp2IL/LibCpp2IL.csproj
sed -i '' "s|\"AssetRipper.Cpp2IL.Core\" Version=\"$OLD\"|\"AssetRipper.Cpp2IL.Core\" Version=\"$NEW\"|" \
  $AR/Source/AssetRipper.Import/AssetRipper.Import.csproj
# probe loads its own copy; left behind it reports on a build that is no longer the one being measured.
# Matched on $OLD this drifted silently: probe sat at 1.0.388 while the export was at 1.0.461, so every
# ISIL reading in between was of code that had since changed. Set it to $NEW from whatever it says.
[ -f $SP/probe/probe.csproj ] && sed -i '' -E "s|(\"AssetRipper.Cpp2IL.Core\" Version=)\"[0-9.]*\"|\1\"$NEW\"|" $SP/probe/probe.csproj

grep -q "<VersionPrefix>$NEW<" /Users/playviet/Documents/_BZ/AssetRipper/External/Cpp2IL/Cpp2IL.Core/Cpp2IL.Core.csproj || { echo "version did not change"; exit 1; }

dotnet build /Users/playviet/Documents/_BZ/AssetRipper/External/Cpp2IL/LibCpp2IL/LibCpp2IL.csproj     -c Release 2>&1 | grep -E "error CS" | head -5
dotnet build /Users/playviet/Documents/_BZ/AssetRipper/External/Cpp2IL/Cpp2IL.Core/Cpp2IL.Core.csproj -c Release 2>&1 | grep -E "error CS" | head -5
cp /Users/playviet/Documents/_BZ/AssetRipper/External/Cpp2IL/LibCpp2IL/bin/Release/AssetRipper.LibCpp2IL.$NEW.nupkg     $AR/LocalPackages/
cp /Users/playviet/Documents/_BZ/AssetRipper/External/Cpp2IL/Cpp2IL.Core/bin/Release/AssetRipper.Cpp2IL.Core.$NEW.nupkg $AR/LocalPackages/
[ -f "$AR/LocalPackages/AssetRipper.Cpp2IL.Core.$NEW.nupkg" ] || { echo "pack failed"; exit 1; }
# (N) so zsh does not abort the script when the old package is already gone
rm -f $AR/LocalPackages/*.$OLD.nupkg(N)

# the stale-restore trap: nuget will happily reuse the old extracted package
rm -rf ~/.nuget/packages/assetripper.cpp2il.core/$NEW ~/.nuget/packages/assetripper.libcpp2il/$NEW
rm -rf $SP/riprun/obj $SP/riprun/bin $SP/probe/obj $SP/probe/bin
for attempt in 1 2 3; do
  dotnet build $SP/riprun/riprun.csproj -c Release 2>&1 | grep -E "error CS" | head -5
  [ -f $SP/riprun/bin/Release/net10.0/Cpp2IL.Core.dll ] && break
  echo "restore was stale, retrying ($attempt)"
done
[ -f $SP/riprun/bin/Release/net10.0/Cpp2IL.Core.dll ] || { echo "riprun did not build"; exit 1; }
[ -f $SP/probe/probe.csproj ] && dotnet build $SP/probe/probe.csproj -c Release 2>&1 | grep -E "error CS" | head -5

# probe and riprun each load their own copy; an unrefreshed one has cost a wrong conclusion before
echo "riprun  $NEW: $(strings $SP/riprun/bin/Release/net10.0/Cpp2IL.Core.dll | grep -c RankTwoArrayAccess) rank2 marks"

# The two copies must be the same build, or a probe reading is about different code than the export runs.
[ -f $SP/probe/bin/Release/net10.0/Cpp2IL.Core.dll ] || { echo "probe   $NEW: not built"; exit 1; }
cmp -s $SP/probe/bin/Release/net10.0/Cpp2IL.Core.dll $SP/riprun/bin/Release/net10.0/Cpp2IL.Core.dll \
  && echo "probe   $NEW: same build as riprun" \
  || { echo "probe   $NEW: DIFFERENT BUILD from riprun - a probe reading would be about other code"; exit 1; }
echo "bumped $OLD -> $NEW"
