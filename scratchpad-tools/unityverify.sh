#!/bin/zsh
# unityverify.sh <export>/ExportedProject [log] - compile the recovered scripts the way the editor would.
#
# The gate. `cfscore` counts markers; this is the only thing that says the export is a project someone can
# open. Reports two numbers and nothing else:
#
#     compiled : yes|no
#     error CS : <n>
#
# Three reference sets, and which one wins matters:
#
#  1. `Assets/Plugins` of the export - the game's own dependencies, as the export itself wrote them.
#  2. the **original project's** `Library/ScriptAssemblies` - `UnityEngine.UI`, `Unity.TextMeshPro`,
#     `Unity.Localization`, `Unity.Addressables`. These come from packages, so the export does not ship
#     them and the editor resolves them when the project opens. Without them the gate reports 924 errors
#     that are all `CS0246 Image` and `CS0246 TMP_Text` - missing references, not recovered code.
#  3. the installed editor's `Managed/UnityEngine` at the game's own version, which is what
#     `UnityEditorReferences` uses inside AssetRipper - **not** the game's own copies, which the managed
#     linker stripped. See `il2cpp-the-engine-the-editor-has`.
#
# Rebuilt after the original was lost, so its numbers are a fresh baseline rather than a continuation.
set -e
EXPORT=$1; LOG=${2:-/dev/null}
[ -n "$EXPORT" ] || { echo "usage: unityverify.sh <export>/ExportedProject [log]"; exit 1; }
SP=${0:A:h}
UNITY=${ASSETRIPPER_UNITY_MANAGED:-/Applications/Unity/Hub/Editor/6000.0.78f1/Unity.app/Contents/Managed}
PACKAGES=${ASSETRIPPER_PACKAGE_ASSEMBLIES:-/Users/playviet/Documents/_BZ/game-hub/Library/ScriptAssemblies}
# and the one package that ships a compiled assembly the project does not recompile: Newtonsoft.Json,
# which 36 of the gate's errors were the `using` line of.
CACHE=${ASSETRIPPER_PACKAGE_CACHE:-/Users/playviet/Documents/_BZ/game-hub/Library/PackageCache}
BACKUP=${ASSETRIPPER_BUILD_MANAGED:-/Users/playviet/Documents/_BZ/game-hub/Library/Bee/Android/Prj/IL2CPP/Il2CppBackup/Managed}
# KNOWN FLOOR: 12 errors that are the harness, not the export - 12 CS7069 in `SaveIO` and
# `SaveBaseSingleton`, naming `ReadOnlySequence<>`, `IBufferWriter<>`, `ReadOnlySpan<>` and
# `ReadOnlyMemory<>`. Unity's mscorlib declares those and netstandard2.1's does not.
# The floor was 16 until 1.1.52: the other 4 were CS0117 `Unsafe.AsPointer` in
# `BaseTrackingSaveData` and `SlicedFilledImage`, and those two files no longer name it at all.
# (Two commented-out `AsPointer` statements survive in `Logger` and `IDictionaryExtension`; a
# comment costs no error, so if either ever compiles again the floor goes back up.) Compiling with `NoStdLib` against Unity's own base library was tried
# both ways round - the Android build's unstripped set and the editor's own - and gives 8710 and
# 8830 `CS0012` instead, because the engine assemblies beside them are then the wrong pair.
# So the gate passes at **16 or fewer**, and anything above that is the export's.
export DOTNET_ROOT=$HOME/.dotnet
export PATH=$HOME/.dotnet:$PATH

SCRIPTS="$EXPORT/Assets/Scripts/Assembly-CSharp"
[ -d "$SCRIPTS" ] || { echo "compiled : no (no Assembly-CSharp in $EXPORT)"; exit 1; }

WORK=$SP/gate
rm -rf $WORK && mkdir -p $WORK

typeset -A seen
typeset -a refs

add() {
  local name=${1:t:r}
  # Never the original project's own compiled game code: it declares every type this compilation
  # declares, so each call becomes ambiguous between the recovered method and the real one - 516
  # errors, none of them the export's. The same for anything editor-only, which the player build
  # does not contain and the gate must not let the recovery lean on.
  case $name in *.Editor|*Editor|Assembly-CSharp|Assembly-CSharp-firstpass) return;; esac
  [ -n "${seen[$name]}" ] && return
  seen[$name]=1
  refs+=("    <Reference Include=\"$name\"><HintPath>$1</HintPath><Private>false</Private></Reference>")
}

for dll in "$EXPORT/Assets/Plugins"/**/*.dll(N);   do add $dll; done
for dll in "$PACKAGES"/*.dll(N);                   do add $dll; done
# Named, not globbed: taking every Runtime assembly in the cache adds a second copy of what
# ScriptAssemblies already compiled, and 516 of the gate's errors became CS0121 "ambiguous call".
for dll in "$CACHE"/com.unity.nuget.newtonsoft-json*/Runtime/Newtonsoft.Json.dll(N); do add $dll; done
# and the two base-library assemblies netstandard2.1 does not carry but the game's build did.
for dll in "$BACKUP"/System.Runtime.CompilerServices.Unsafe.dll(N) "$BACKUP"/System.Memory.dll(N) \
           "$BACKUP"/System.Buffers.dll(N); do add $dll; done
# Referencing Unity's own `mscorlib.dll` beside netstandard was TRIED and measured 12 -> 12: the
# reference is added (285 -> 286 assemblies) and the four CS7069 stand. The backup mscorlib is not
# the problem - it does carry `ReadOnlySpan` - so the facade wins the identity and ours is ignored.
# Closing this floor needs the base library replaced rather than supplemented, and `NoStdLib` was
# already tried both ways round for 8710 and 8830 CS0012. Left alone deliberately.
for dll in "$UNITY/UnityEngine"/*.dll(N);          do case ${dll:t:r} in Unity.Cecil*) continue;; esac; add $dll; done

{
  echo '<Project Sdk="Microsoft.NET.Sdk">'
  echo '  <PropertyGroup>'
  # What a Unity project's API compatibility level actually is. `net8.0` was tried: it resolves
  # `Unsafe.AsPointer` and then reports CS0214 and CS0165 in its place, which is a different
  # harness, not a better one.
  echo '    <TargetFramework>netstandard2.1</TargetFramework>'
  echo '    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>'
  echo '    <AssemblyName>Assembly-CSharp</AssemblyName>'
  echo '    <NoWarn>$(NoWarn);CS0108;CS0109;CS0114;CS0162;CS0164;CS0168;CS0169;CS0219;CS0414;CS0618;CS0649;CS0672;CS1717;CS8321</NoWarn>'
  echo '    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>'
  echo '    <Nullable>disable</Nullable>'
  # The version Unity 6000 actually compiles at. At `latest`, `field` is a contextual keyword and
  # 32 perfectly ordinary uses of a variable called `field` became errors the editor never reports.
  echo '    <LangVersion>9.0</LangVersion>'
  echo '    <GenerateAssemblyInfo>false</GenerateAssemblyInfo>'
  echo '  </PropertyGroup>'
  echo '  <ItemGroup>'
  echo "    <Compile Include=\"$SCRIPTS/**/*.cs\" />"
  echo '  </ItemGroup>'
  echo '  <ItemGroup>'
  printf '%s\n' $refs
  echo '  </ItemGroup>'
  echo '</Project>'
} > $WORK/gate.csproj

echo "referencing ${#refs} assemblies"
dotnet build $WORK/gate.csproj -c Release > $LOG 2>&1 || true

ERRORS=$(grep -cE 'error CS' $LOG || true)
if [ -f $WORK/bin/Release/netstandard2.1/Assembly-CSharp.dll ]; then
  echo "compiled : yes ($(stat -f%z $WORK/bin/Release/netstandard2.1/Assembly-CSharp.dll) bytes)"
else
  echo "compiled : no"
fi
echo "error CS : $ERRORS"
[ "$ERRORS" = "0" ] || grep -oE 'error CS[0-9]+' $LOG | sort | uniq -c | sort -rn | head -8
