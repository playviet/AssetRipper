#!/bin/zsh
# anyverify.sh <export>/ExportedProject [log] - compile the recovered scripts of ANY game, with no
# original Unity project on disk.
#
# `unityverify.sh` is the gate for Fluffy Field and only for Fluffy Field: it reaches into
# /Users/playviet/Documents/_BZ/game-hub for `Library/ScriptAssemblies`, `Library/PackageCache` and the
# Il2CppBackup, none of which exist for a game we only have an apk of. This is the same gate with those
# three legs cut off. It uses exactly three reference sets:
#
#   1. the export's own `Assets/Plugins/**/*.dll` - whatever the export itself shipped, and for most games
#      that is everything: AssetRipper writes every assembly it did not decompile into Plugins, package
#      assemblies (UnityEngine.UI, Unity.TextMeshPro, Unity.Addressables, Newtonsoft.Json) included.
#   2. the installed editor's `Managed/UnityEngine` - the engine the editor has, NOT the game's own copies,
#      which the managed linker stripped. See `il2cpp-the-engine-the-editor-has`. The version is read from
#      the export's own `ProjectSettings/ProjectVersion.txt` and matched against what is installed under
#      /Applications/Unity/Hub/Editor: exact if it is there, else the newest install sharing the major
#      version, else $ANYVERIFY_DEFAULT_UNITY. $ASSETRIPPER_UNITY_MANAGED overrides the lot with a path.
#   3. $ANYVERIFY_REFS - colon-separated extra directories, searched recursively for *.dll. This is the
#      hole through which a game that DOES have a project on disk gets its ScriptAssemblies back, and the
#      only thing anyverify.sh needs to become unityverify.sh:
#
#        ANYVERIFY_REFS=$HUB/Library/ScriptAssemblies:$HUB/Library/Bee/Android/Prj/IL2CPP/Il2CppBackup/Managed
#
# Reports the same two lines as unityverify.sh, and before them the fix queue: the top 25 error codes by
# count, and three example messages for each of the top 5. A gate that only says "no, 3714" is a gate you
# cannot act on; the ranking is what says which defect family to open next.
#
# Errors are counted DEDUPLICATED - msbuild prints every error twice, once at CoreCompile and once in the
# Build FAILED summary, so `grep -c 'error CS'` reports exactly double. unityverify.sh's numbers are that
# doubled count; anyverify.sh's are not, so halve one or double the other before comparing them.
set -e

EXPORT=$1
[ -n "$EXPORT" ] || { echo "usage: anyverify.sh <export>/ExportedProject [log]"; exit 1; }
EXPORT=${EXPORT:A}
LOG=${2:-${TMPDIR:-/tmp}/anyverify-${EXPORT:t}-$$.log}

export DOTNET_ROOT=$HOME/.dotnet
export PATH=$HOME/.dotnet:$PATH

SCRIPTS="$EXPORT/Assets/Scripts"
[ -d "$SCRIPTS" ] || { echo "compiled : no (no Assets/Scripts in $EXPORT)"; echo "error CS : -1"; exit 1; }

HUB=${ANYVERIFY_UNITY_HUB:-/Applications/Unity/Hub/Editor}
DEFAULT_UNITY=${ANYVERIFY_DEFAULT_UNITY:-6000.0.78f1}

# ---- which editor -----------------------------------------------------------------------------------
typeset -a installed
installed=(${HUB}/*/Unity.app/Contents/Managed(N:h:h:h:t))

WANT=""
[ -f "$EXPORT/ProjectSettings/ProjectVersion.txt" ] &&
  WANT=$(sed -n 's/^m_EditorVersion: *//p' "$EXPORT/ProjectSettings/ProjectVersion.txt" | head -1 | tr -d '\r')

pick_unity() {
  local want=$1 v
  if [ -n "$want" ]; then
    for v in $installed; do [ "$v" = "$want" ] && { print -r -- "$v"; return }; done
    # nothing exact: the newest install that shares the major version (6000.x, 2022.x, ...)
    local -a same
    same=(${(M)installed:#${want%%.*}.*})
    (( ${#same} )) && { print -rl -- $same | sort -V | tail -1; return }
  fi
  for v in $installed; do [ "$v" = "$DEFAULT_UNITY" ] && { print -r -- "$v"; return }; done
  (( ${#installed} )) && { print -rl -- $installed | sort -V | tail -1; return }
  return 1
}

if [ -n "$ASSETRIPPER_UNITY_MANAGED" ]; then
  UNITY=$ASSETRIPPER_UNITY_MANAGED
  UNITYVER="(\$ASSETRIPPER_UNITY_MANAGED)"
else
  UNITYVER=$(pick_unity "$WANT") || { echo "compiled : no (no Unity editor under $HUB)"; echo "error CS : -1"; exit 1; }
  UNITY="$HUB/$UNITYVER/Unity.app/Contents/Managed"
fi
[ -d "$UNITY/UnityEngine" ] || { echo "compiled : no (no $UNITY/UnityEngine)"; echo "error CS : -1"; exit 1; }

WORK=${ANYVERIFY_WORK:-${TMPDIR:-/tmp}/anyverify-gate}
rm -rf $WORK && mkdir -p $WORK

# ---- mscorlib ---------------------------------------------------------------------------------------
# Every assembly in a Unity player build is compiled against Unity's `mscorlib`, and a plugin dll whose
# metadata names a type from it - `UniTask.dll` declares `IUniTaskSource : [mscorlib]IValueTaskSource` -
# forces the compiler to resolve that typeref before it can bind a call on the plugin type. The
# netstandard2.1 reference pack does ship an `mscorlib`, but only as a facade forwarding the
# netstandard2.0-era surface, so the resolve fails and every SOURCE line touching the plugin type gets
#
#   CS7069: Reference to type 'X' claims it is defined in 'mscorlib', but it could not be found
#
# which is the reference set's fault and not the export's. ANYVERIFY.md recorded 6 of these as Fluffy
# Field's "known floor"; they were never a floor. Three routes were measured on Snacky Dash `_4`:
#
#   an EXTRA mscorlib reference (Unity's or a facade) - no effect at all. RAR resolves `mscorlib` to the
#     ref pack's own facade and silently drops the duplicate.
#   Unity's real MonoBleedingEdge mscorlib, force-swapped into @(ReferencePath) - Roslyn CRASHES
#     (NullReferenceException in PEModule.HasAttributeUsageAttribute): a second System.Object in a
#     netstandard2.1 compilation is not survivable.
#   a facade of our own, force-swapped in - closes the 3, but loses the ref pack facade's 1055
#     forwarders with it: 84 CS7069 + 57 CS0453.
#
# So the facade has to be a UNION - the ref pack's forwarders plus the types it does not forward - and
# then be swapped into @(ReferencePath) after RAR has run, because adding it as a Reference is not enough.
# `mscorlib-shim/` is that assembly: `forwards.cs` is generated, `shim.cs` is the handful of types to add.
# Measured on Snacky Dash `_4`: 50 errors -> 47, all three CS7069 gone and nothing new.
# ANYVERIFY_NO_MSCORLIB=1 turns it off, which is how you reproduce a pre-mscorlib-facade number.
MSCORLIB=""
SHIMPROJ=${0:A:h}/mscorlib-shim/mscorlib.csproj
if [ "$ANYVERIFY_NO_MSCORLIB" != "1" ] && [ -f "$SHIMPROJ" ]; then
  if dotnet build "$SHIMPROJ" -c Release -o $WORK/mscorlib \
       -p:BaseIntermediateOutputPath=$WORK/mscorlib-obj/ --nologo > $WORK/mscorlib.log 2>&1; then
    MSCORLIB=$WORK/mscorlib/mscorlib.dll
  else
    echo "warn: the mscorlib facade did not build; expect CS7069 (see $WORK/mscorlib.log)"
  fi
fi

# ---- references -------------------------------------------------------------------------------------
typeset -A seen
typeset -a refs
NPLUG=0; NUNITY=0; NEXTRA=0

# Any assembly whose SOURCE is in this compilation must not also arrive as a dll: every type would be
# declared twice and each call becomes CS0121 "ambiguous". unityverify.sh names Assembly-CSharp because
# that is the only one Fluffy Field has; here the set is whatever directories the export wrote.
typeset -A compiled_from_source
for d in "$SCRIPTS"/*(N/); do compiled_from_source[${d:t}]=1; done

add() {
  local name=${1:t:r}
  # never editor-only assemblies: the player build does not contain them and the gate must not let the
  # recovery lean on one. `UnityEditor.*Module` from the engine folder is deliberately NOT in this net -
  # unityverify.sh passes them too, and dropping them would make the two gates disagree for a reason that
  # has nothing to do with the export.
  case $name in *.Editor|*Editor) return 1;; esac
  [ -n "${compiled_from_source[$name]}" ] && return 1
  [ -n "${seen[$name]}" ] && return 1
  seen[$name]=1
  refs+=("    <Reference Include=\"$name\"><HintPath>$1</HintPath><Private>false</Private></Reference>")
  return 0
}

# The engine goes FIRST so it wins every name it declares. A game that shipped its own
# UnityEngine.CoreModule shipped a linker-stripped one, and half its methods are gone.
for dll in "$UNITY/UnityEngine"/**/*.dll(N); do
  case ${dll:t:r} in Unity.Cecil*) continue;; esac
  if add $dll; then (( NUNITY += 1 )); fi
done
for dll in "$EXPORT/Assets/Plugins"/**/*.dll(N); do if add $dll; then (( NPLUG += 1 )); fi; done
for dir in ${(s.:.)ANYVERIFY_REFS}; do
  for dll in ${~dir}/**/*.dll(N); do if add $dll; then (( NEXTRA += 1 )); fi; done
done

# ---- the project ------------------------------------------------------------------------------------
{
  echo '<Project Sdk="Microsoft.NET.Sdk">'
  echo '  <PropertyGroup>'
  # what a Unity project's API compatibility level actually is
  echo '    <TargetFramework>netstandard2.1</TargetFramework>'
  echo '    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>'
  echo '    <AssemblyName>Recovered</AssemblyName>'
  echo '    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>'
  echo '    <Nullable>disable</Nullable>'
  # the version Unity 6000 and 2022.3 both compile at. At `latest`, `field` is a contextual keyword and
  # every ordinary use of a variable called `field` becomes an error the editor never reports.
  echo '    <LangVersion>9.0</LangVersion>'
  echo '    <GenerateAssemblyInfo>false</GenerateAssemblyInfo>'
  echo '    <GenerateTargetFrameworkAttribute>false</GenerateTargetFrameworkAttribute>'
  echo '    <ProduceReferenceAssembly>false</ProduceReferenceAssembly>'
  echo '    <DebugType>none</DebugType>'
  echo '    <SatelliteResourceLanguages>en</SatelliteResourceLanguages>'
  # warnings are not the measurement; only `error CS` is
  echo '    <WarningLevel>0</WarningLevel>'
  echo '    <TreatWarningsAsErrors>false</TreatWarningsAsErrors>'
  echo '    <NoWarn>$(NoWarn);CS0067;CS0105;CS0108;CS0109;CS0114;CS0162;CS0164;CS0168;CS0169;CS0219;CS0414;CS0436;CS0465;CS0472;CS0618;CS0628;CS0649;CS0652;CS0672;CS1522;CS1717;CS3021;CS8321;NU1701</NoWarn>'
  echo '  </PropertyGroup>'
  echo '  <ItemGroup>'
  echo "    <Compile Include=\"$SCRIPTS/**/*.cs\" />"
  # An export writes an assembly it could not ship as a DLL as SOURCE, under Assets/Plugins/<AssemblyName>/.
  # Snacky Dash puts 124 files of `Assembly-CSharp-firstpass` there, including the `Unity.Scripting` types
  # that `Assembly-CSharp` uses - and dropping them showed up as 20 CS0234/CS0246 that looked like a missing
  # package and were nothing of the kind. Unity compiles firstpass ahead of Assembly-CSharp and lets the
  # latter reference it; compiling them together is a close enough approximation and is strictly better than
  # leaving them out. Any Assets/Plugins subdirectory holding .cs files is taken.
  for src in "$EXPORT/Assets/Plugins"/*(/N); do
    if [ -n "$(find "$src" -name '*.cs' -print -quit 2>/dev/null)" ]; then
      # Each assembly ships its own Properties/AssemblyInfo.cs, and Unity compiles them as separate
      # assemblies so the duplicate [assembly: AssemblyVersion] never meets itself. Merging the roots into
      # one compilation does make them meet - CS0579 - which is the harness, not the export.
      echo "    <Compile Include=\"$src/**/*.cs\" Exclude=\"$src/Properties/AssemblyInfo.cs\" />"
    fi
  done
  echo '  </ItemGroup>'
  echo '  <ItemGroup>'
  printf '%s\n' $refs
  echo '  </ItemGroup>'
  # RAR resolves `mscorlib` to the reference pack's facade and drops any duplicate, so the union facade
  # has to replace it in @(ReferencePath) after RAR has already run. See the comment above.
  if [ -n "$MSCORLIB" ]; then
    echo '  <Target Name="AnyverifySwapMscorlib" AfterTargets="ResolveAssemblyReferences">'
    echo '    <ItemGroup>'
    echo "      <ReferencePath Remove=\"@(ReferencePath)\" Condition=\"'%(Filename)'=='mscorlib'\" />"
    echo "      <ReferencePath Include=\"$MSCORLIB\" />"
    echo '    </ItemGroup>'
    echo '  </Target>'
  fi
  echo '</Project>'
} > $WORK/gate.csproj

NCS=$(find "$SCRIPTS" -name '*.cs' | wc -l | tr -d ' ')
echo "export     : $EXPORT"
echo "unity      : ${UNITYVER}${WANT:+  (project says $WANT)}"
echo "sources    : $NCS .cs under Assets/Scripts (${(k)compiled_from_source})"
echo "references : ${#refs} assemblies  ($NUNITY engine, $NPLUG plugins, $NEXTRA extra)"
echo "mscorlib   : ${MSCORLIB:-(none - CS7069 expected)}"
echo "log        : $LOG"

dotnet build $WORK/gate.csproj -c Release --nologo > $LOG 2>&1 || true

# ---- the fix queue ----------------------------------------------------------------------------------
# msbuild prints each diagnostic twice; the two copies are byte-identical, so `sort -u` is the count.
grep -E 'error CS[0-9]+' $LOG 2>/dev/null | sed 's/^[[:space:]]*//' | sort -u > $WORK/errors.txt || true
ERRORS=$(wc -l < $WORK/errors.txt | tr -d ' ')

if [ "$ERRORS" != "0" ]; then
  echo
  echo "top 25 error codes"
  grep -oE 'error CS[0-9]+' $WORK/errors.txt | sed 's/error //' | sort | uniq -c | sort -rn | head -25 |
    while read n code; do printf '  %-8s %s\n' "$code:" "$n"; done
  echo
  echo "examples (top 5 codes, 3 each)"
  for code in $(grep -oE 'error CS[0-9]+' $WORK/errors.txt | sed 's/error //' | sort | uniq -c | sort -rn | head -5 | awk '{print $2}'); do
    echo "  $code"
    grep -F "error $code:" $WORK/errors.txt | head -3 |
      sed -e "s#^$EXPORT/Assets/Scripts/##" -e 's# \[[^][]*\.csproj\]$##' -e 's/^/    /'
  done
fi

echo
if [ -f $WORK/bin/Release/netstandard2.1/Recovered.dll ]; then
  echo "compiled : yes ($(stat -f%z $WORK/bin/Release/netstandard2.1/Recovered.dll) bytes)"
else
  echo "compiled : no"
fi
echo "error CS : $ERRORS"
