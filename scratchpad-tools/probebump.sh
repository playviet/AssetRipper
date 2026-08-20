#!/bin/zsh
# probebump.sh <old> <new> - rebuild Cpp2IL and *only* probe at a new version.
# For diagnosing, where riprun is not going to be run: skips the riprun build, which is the slow half.
# NuGet caches by version, so the version still has to change.
set -e
OLD=$1; NEW=$2
[ -n "$OLD" ] && [ -n "$NEW" ] || { echo "usage: probebump.sh <old> <new>"; exit 1; }
[ "$OLD" != "$NEW" ] || { echo "old and new must differ - bump.sh deletes *.\$OLD.nupkg, so old==new removes what it just built"; exit 1; }
SP=${0:A:h}
AR=/Users/playviet/Documents/_BZ/AssetRipper
export DOTNET_ROOT=$HOME/.dotnet
export PATH=$HOME/.dotnet:$PATH

sed -i '' "s|<VersionPrefix>$OLD</VersionPrefix>|<VersionPrefix>$NEW</VersionPrefix>|" \
  $AR/External/Cpp2IL/Cpp2IL.Core/Cpp2IL.Core.csproj $AR/External/Cpp2IL/LibCpp2IL/LibCpp2IL.csproj
# Version-agnostic, like bump.sh: matched on $OLD this drifted silently and probe sat 75 versions behind.
sed -i '' -E "s|(\"AssetRipper.Cpp2IL.Core\" Version=)\"[0-9.]*\"|\\1\"$NEW\"|" \
  $AR/Source/AssetRipper.Import/AssetRipper.Import.csproj $SP/probe/probe.csproj

grep -q "<VersionPrefix>$NEW<" $AR/External/Cpp2IL/Cpp2IL.Core/Cpp2IL.Core.csproj || { echo "version did not change"; exit 1; }

dotnet build $AR/External/Cpp2IL/LibCpp2IL/LibCpp2IL.csproj     -c Release 2>&1 | grep -E "error CS" | head -5
dotnet build $AR/External/Cpp2IL/Cpp2IL.Core/Cpp2IL.Core.csproj -c Release 2>&1 | grep -E "error CS" | head -5
cp $AR/External/Cpp2IL/LibCpp2IL/bin/Release/AssetRipper.LibCpp2IL.$NEW.nupkg     $AR/LocalPackages/
cp $AR/External/Cpp2IL/Cpp2IL.Core/bin/Release/AssetRipper.Cpp2IL.Core.$NEW.nupkg $AR/LocalPackages/
rm -f $AR/LocalPackages/*.$OLD.nupkg

rm -rf ~/.nuget/packages/assetripper.cpp2il.core/$NEW ~/.nuget/packages/assetripper.libcpp2il/$NEW $SP/probe/obj $SP/probe/bin
for attempt in 1 2 3; do
  dotnet build $SP/probe/probe.csproj -c Release 2>&1 | grep -E "error CS|error NU" | head -3
  [ -f $SP/probe/bin/Release/net10.0/probe.dll ] && break
  echo "restore was stale, retrying ($attempt)"
done
[ -f $SP/probe/bin/Release/net10.0/probe.dll ] || { echo "probe did not build"; exit 1; }
echo "probe $NEW ok"
