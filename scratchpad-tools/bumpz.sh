#!/bin/zsh
# bumpz.sh <old> <new> - worktree-local bump. Never touches the main tree.
set -e
OLD=$1; NEW=$2
[ -n "$OLD" ] && [ -n "$NEW" ] || { echo "usage: bumpz.sh <old> <new>"; exit 1; }
[ "$OLD" != "$NEW" ] || { echo "old and new must differ"; exit 1; }
SP=${0:A:h}
AR=${SP:h}
export DOTNET_ROOT=$HOME/.dotnet
export PATH=$HOME/.dotnet:$PATH

sed -i '' "s|<VersionPrefix>$OLD</VersionPrefix>|<VersionPrefix>$NEW</VersionPrefix>|" \
  $AR/External/Cpp2IL/Cpp2IL.Core/Cpp2IL.Core.csproj $AR/External/Cpp2IL/LibCpp2IL/LibCpp2IL.csproj
sed -i '' "s|\"AssetRipper.Cpp2IL.Core\" Version=\"$OLD\"|\"AssetRipper.Cpp2IL.Core\" Version=\"$NEW\"|" \
  $AR/Source/AssetRipper.Import/AssetRipper.Import.csproj
sed -i '' -E "s|(\"AssetRipper.Cpp2IL.Core\" Version=)\"[0-9.]*\"|\1\"$NEW\"|" $SP/probe2/probe.csproj || true

grep -q "<VersionPrefix>$NEW<" $AR/External/Cpp2IL/Cpp2IL.Core/Cpp2IL.Core.csproj || { echo "version did not change"; exit 1; }

dotnet build $AR/External/Cpp2IL/LibCpp2IL/LibCpp2IL.csproj     -c Release 2>&1 | grep -E "error CS" | head -5
dotnet build $AR/External/Cpp2IL/Cpp2IL.Core/Cpp2IL.Core.csproj -c Release 2>&1 | grep -E "error CS" | head -5
cp $AR/External/Cpp2IL/LibCpp2IL/bin/Release/AssetRipper.LibCpp2IL.$NEW.nupkg     $AR/LocalPackages/
cp $AR/External/Cpp2IL/Cpp2IL.Core/bin/Release/AssetRipper.Cpp2IL.Core.$NEW.nupkg $AR/LocalPackages/
[ -f "$AR/LocalPackages/AssetRipper.Cpp2IL.Core.$NEW.nupkg" ] || { echo "pack failed"; exit 1; }

rm -rf ~/.nuget/packages/assetripper.cpp2il.core/$NEW ~/.nuget/packages/assetripper.libcpp2il/$NEW
rm -rf $SP/riprun/obj $SP/riprun/bin $SP/probe2/obj $SP/probe2/bin
rm -rf $AR/Source/AssetRipper.Import/obj $AR/Source/AssetRipper.Import/bin
for attempt in 1 2 3; do
  dotnet build $SP/riprun/riprun.csproj -c Release 2>&1 | grep -E "error CS" | head -5
  [ -f $SP/riprun/bin/Release/net10.0/Cpp2IL.Core.dll ] && break
  echo "restore was stale, retrying ($attempt)"
done
[ -f $SP/riprun/bin/Release/net10.0/Cpp2IL.Core.dll ] || { echo "riprun did not build"; exit 1; }
dotnet build $SP/probe2/probe.csproj -c Release 2>&1 | grep -E "error CS" | head -5

# the only thing that proves which build is about to be measured
echo "deps: $(grep -o 'AssetRipper.Cpp2IL.Core/[0-9.]*' $SP/riprun/bin/Release/net10.0/riprun.deps.json | head -1)"
grep -q "AssetRipper.Cpp2IL.Core/$NEW" $SP/riprun/bin/Release/net10.0/riprun.deps.json || { echo "WRONG PACKAGE VERSION RESTORED"; exit 1; }
cmp -s $SP/probe2/bin/Release/net10.0/Cpp2IL.Core.dll $SP/riprun/bin/Release/net10.0/Cpp2IL.Core.dll \
  && echo "probe2  $NEW: same build as riprun" || echo "probe2  $NEW: DIFFERENT BUILD from riprun"
echo "bumped $OLD -> $NEW"
