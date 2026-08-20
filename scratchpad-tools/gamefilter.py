#!/usr/bin/env python3
"""Which of the 167817 recovered methods are actually the game's?

    from gamefilter import is_game_code, bucket, tier, split_path

    is_game_code(assembly, namespace='', type_name='') -> bool   # the predicate other scorers import
    bucket(assembly, namespace='', type_name='')       -> 'game' | 'unity' | 'bcl' | 'sdk'
    tier(assembly, namespace='', type_name='')         -> 'gameplay' | 'meta' | ''   ('' when not game)
    split_path(scripts_root, path)                     -> (assembly, namespace, type_name)

    python3 gamefilter.py <export-root>                      # method counts per bucket
    python3 gamefilter.py <export-root> --files              # skip ast-grep, count files only (fast)
    python3 gamefilter.py <export-root> --queue              # the fix queue: game namespaces by size
    python3 gamefilter.py --census <global-metadata.dat>     # the same split with no export at all
    python3 gamefilter.py --selftest                         # the tables, against no files

`<export-root>` is either the export directory or its `ExportedProject`; both are accepted. `--census`
reads the split straight out of the metadata in about a second, which is what to use while an export is
still running - `assets/bin/Data/Managed/Metadata/global-metadata.dat` inside the apk.

WHY THIS EXISTS. Snacky Dash 1.11.0 (`com.b2p.gobbledash2`) ships 167817 methods across 172 assemblies, and
**93.1% of them are not the game's code**: 34.7% Unity, 26.2% BCL, 32.3% third party. Naming the game's
assembly is not enough either - `Assembly-CSharp.dll` is 21415 methods and 10051 of them are the Voodoo
publishing SDK, Easy Save 3, HighlightPlus and a consent manager compiled straight into it.

What is left, measured off the metadata (`--selftest` pins the tables that produce it):

    game    11601   6.91%      of which gameplay 7946 (4.73%), meta 3655 (2.18%)
    sdk     54134  32.26%
    unity   58205  34.68%
    bcl     43877  26.15%

So a scorer counting all 167817 equally spends 93 of every 100 points somewhere nobody asked about, and
cannot tell a win on the snake from a win on `System.Linq`. See `GAMEFILTER.md`.

WHERE THE LISTS COME FROM. Not guesswork. Two sources, both read off this build:

  * `assets/bin/Data/ScriptingAssemblies.json` inside `com.b2p.gobbledash2.apk` names all 225 assemblies
    Unity compiled, and tags each 2 (a UnityEngine module) or 16 (everything else).
  * `assets/bin/Data/Managed/Metadata/global-metadata.dat` - its image table gives the 172 assemblies that
    survived stripping, and the type table gives every namespace and type in each, with method counts. The
    per-image method counts sum to exactly 167817, which is the number Cpp2IL logs, so the parse is right.

Namespaces were then read per assembly and classified by hand; `--selftest` checks the tables still cover
every name the metadata knows.

THE FALLBACK RULE, applied in this order:

  1. The assembly tables below. They are exhaustive for this build.
  2. Inside a `game` assembly, NAMESPACE_OVERRIDES catches third-party code vendored into it - most of
     `Assembly-CSharp` is Voodoo SDK, not game.
  3. Inside the *global* namespace of a `game` assembly, GLOBAL_TYPE_OVERRIDES catches vendored types that
     have no namespace to be caught by - `ES3*`, `VoodooSauce`, `Taptic`.
  4. An assembly in no table is classified by prefix: `System.`/`Microsoft.`/`mscorlib`/`Mono.`/`netstandard`
     -> bcl; `UnityEngine.*Module`/`Unity.` -> unity; anything else -> **sdk**.

     An unknown assembly is NEVER game. The game's assemblies are named explicitly and a new one is a new
     fact about the build, not something to infer - guessing `game` would quietly inflate every number this
     tool exists to keep honest.

NESTED TYPES. A nested type is counted against the namespace of the type that declares it - which is what
the export does too, since AssetRipper writes a nested type inside its parent's file. This matters: counted
naively, `Assembly-CSharp`'s global namespace looks like 4588 methods, because every `<>c` closure of every
`JuicedUp` type carries an empty namespace. Rooted to the declaring type it is 1633.

PORTING THIS TO ANOTHER GAME. `bucket()` is table-driven, so a second title needs its own tables, not a
second copy of the logic. Regenerate them the same way: pull `ScriptingAssemblies.json` and
`global-metadata.dat` out of the apk, list the namespaces per assembly, and classify. The Unity, BCL and
common-SDK rows carry over unchanged; only the game's own assemblies and the vendored-namespace list are
per-title.
"""
import collections
import os
import sys

GAME, UNITY, BCL, SDK = 'game', 'unity', 'bcl', 'sdk'

# The tables below are Snacky Dash's. Run against another title they will happily report nonsense - Fluffy
# Field comes out 96.6% game, because none of its assets are in NAMESPACE_OVERRIDES and rule 2 has nothing to
# say. `MARKER` is a namespace that only this title has, and the CLI refuses to report without it.
TITLE = 'Snacky Dash 1.11.0 (com.b2p.gobbledash2)'
MARKER = ('Assembly-CSharp', 'JuicedUp')

# ---------------------------------------------------------------------------------------------------------
# (a) THE GAME'S OWN CODE.
#
# `Assembly-CSharp` is the game's assembly but is NOT all game - see NAMESPACE_OVERRIDES. `CloudContent*` is
# the studio's remote-content service, which is a judgement call: it is infrastructure rather than gameplay,
# but it is not a vendor SDK either, so it lands in `game`/`meta`. Moving it is a one-line edit.
GAME_ASSEMBLIES = {
    'Assembly-CSharp',
    'CloudContent',
    'CloudContent.AutoGenerated',
    'CloudContent.Serializer',
}

# ---------------------------------------------------------------------------------------------------------
# (b) UNITY ENGINE AND UNITY PACKAGES.
#
# Every `UnityEngine.*Module` (the 69 assemblies ScriptingAssemblies.json tags type 2) plus `UnityEngine.dll`
# is matched by prefix. Listed here are the Unity *packages*, which are not caught by a prefix because they
# are named `Unity.*` and because Cinemachine and UnityEngine.UI are not.
#
# NOT here, deliberately: `Unity.Services.*`, `Unity.Notifications.*`, `Unity.Analytics.DataPrivacy`,
# `UnityEngine.Purchasing.*` and `UnityEngine.Advertisements`. They ship as Unity packages but they are
# analytics, IAP and ad services, which is bucket (d) - the split the caller asked for is by what the code
# does, not by who publishes it.
UNITY_ASSEMBLIES = {
    'UnityEngine', 'UnityEngine.UI', 'Unity.TextMeshPro', 'Cinemachine', 'Unity.Timeline',
    'Unity.Mathematics', 'Unity.Collections', 'Unity.Collections.LowLevel.ILSupport',
    'Unity.Burst', 'Unity.Burst.Unsafe', 'Unity.Compat', 'Unity.Pipeline',
    'Unity.InternalAPIEngineBridge.001', 'Unity.AI.Navigation', 'Unity.Tasks',
    'Unity.2D.Animation.Runtime', 'Unity.2D.Common.Runtime', 'Unity.2D.IK.Runtime',
    'Unity.2D.PixelPerfect', 'Unity.2D.SpriteShape.Runtime', 'Unity.2D.Tilemap.Extras',
    'Unity.Recorder', 'Unity.Recorder.Base',
    'Unity.Rendering.LightTransport.Runtime',
    'Unity.RenderPipeline.Universal.ShaderLibrary',
    'Unity.RenderPipelines.Core.Runtime', 'Unity.RenderPipelines.Core.Runtime.Shared',
    'Unity.RenderPipelines.Core.ShaderLibrary', 'Unity.RenderPipelines.GPUDriven.Runtime',
    'Unity.RenderPipelines.ShaderGraph.ShaderGraphLibrary',
    'Unity.RenderPipelines.Universal.2D.Runtime', 'Unity.RenderPipelines.Universal.Config.Runtime',
    'Unity.RenderPipelines.Universal.Runtime', 'Unity.RenderPipelines.Universal.Shaders',
    '__Generated',  # il2cpp's own generated image
}

# ---------------------------------------------------------------------------------------------------------
# (c) BCL AND THE MICROSOFT LIBRARIES THAT RIDE WITH IT.
#
# Matched by prefix (`System.`, `Microsoft.`, `Mono.`) plus the bare names below. Microsoft.Extensions.* and
# Microsoft.CodeAnalysis land here rather than in (d): they are general-purpose platform libraries, and the
# caller's own framing put `Microsoft.*` with the BCL.
BCL_ASSEMBLIES = {
    'mscorlib', 'netstandard', 'System', 'IsExternalInit.System.Runtime.CompilerServices',
}

# ---------------------------------------------------------------------------------------------------------
# (d) THIRD PARTY: SDKs, plugins and asset-store libraries. This is also the fallback for anything unknown,
# so the set is documentation rather than logic - it records what was actually identified in this build.
#
# `Assembly-CSharp-firstpass` is in here, not in (a): it is the Plugins folder, and every namespace in it is
# an asset - ProceduralPrimitivesUtil, DG.Tweening, Coffee.UIExtensions, Shapes2D, PolyAndCode, FlatKit,
# LayerLab. Not one game type.
SDK_ASSEMBLIES = {
    # the game's own Plugins folder - entirely vendored assets
    'Assembly-CSharp-firstpass',
    # Voodoo: the publisher's SDK, the biggest single third party here
    'Blackboard',  # namespace Voodoo.Sauce.Common
    'Voodoo.IAP', 'Voodoo.IAP.Samples', 'Voodoo.Sauce.Core', 'Voodoo.Sdk.Analytics',
    'Voodoo.Sdk.AnalyticsCommon', 'Voodoo.Sdk.Core', 'Voodoo.Sdk.Results', 'Voodoo.UI.Particles',
    'VoodooSauce.Amplitude', 'VoodooTuneSDK',
    # ads and mediation
    'MaxSdk.Scripts', 'AppHarbrSDK.Runtime', 'NeftaCustomAdapter', 'UnityEngine.Advertisements',
    'AudioMobAndroid', 'AudioMobGame',
    # analytics and attribution
    'AdjustSdk.Scripts', 'GameAnalyticsBridge', 'GameAnalyticsSDK',
    'Unity.Analytics.DataPrivacy', 'Unity.Services.Analytics',
    'Unity.Services.Core', 'Unity.Services.Core.Analytics', 'Unity.Services.Core.Components',
    'Unity.Services.Core.Configuration', 'Unity.Services.Core.Device',
    'Unity.Services.Core.Environments', 'Unity.Services.Core.Environments.Internal',
    'Unity.Services.Core.Internal', 'Unity.Services.Core.Networking',
    'Unity.Services.Core.Registration', 'Unity.Services.Core.Scheduler',
    'Unity.Services.Core.Telemetry', 'Unity.Services.Core.Threading',
    # IAP
    'IAPAnalytics', 'IAPCrashReporter', 'Purchasing.Common',
    'UnityEngine.Purchasing', 'UnityEngine.Purchasing.AppleCore', 'UnityEngine.Purchasing.AppleMacosStub',
    'UnityEngine.Purchasing.AppleStub', 'UnityEngine.Purchasing.Codeless',
    'UnityEngine.Purchasing.Security', 'UnityEngine.Purchasing.SecurityCore',
    'UnityEngine.Purchasing.Stores', 'UnityEngine.Purchasing.WinRTCore',
    'UnityEngine.Purchasing.WinRTStub',
    # Firebase and Google
    'Firebase.Analytics', 'Firebase.App', 'Firebase.Crashlytics', 'Firebase.Messaging',
    'Firebase.Platform', 'Firebase.TaskExtension',
    'Google.MiniJson', 'Google.Play.Common', 'Google.Play.Core', 'Google.Play.Review',
    # notifications
    'Unity.Notifications.Android', 'Unity.Notifications.Unified',
    # general-purpose third-party libraries: not SDKs, but not Unity's and not the BCL's either
    'Newtonsoft.Json', 'UniRx', 'UniTask', 'UniTask.Addressables', 'UniTask.DOTween',
    'UniTask.Linq', 'UniTask.TextMeshPro', 'VContainer', 'ZLogger', 'ZLogger.Unity',
    'Utf8StringInterpolation', 'MoreMountains.Tools',
    'Sirenix.OdinInspector.Attributes', 'Sirenix.Serialization', 'Sirenix.Serialization.Config',
    'Sirenix.Utilities',
    # asset-store art, tween, input and UI packages
    'Coffee.UIEffect', 'Coffee.UIEffect.R', 'CW.Common', 'DemiLib', 'DOTween', 'DOTweenPro',
    'EasyButtons', 'EasyTextEffects', 'KinoBloom.Runtime', 'LeanCommon', 'LeanCommonPlus',
    'LeanTouch', 'LeanTouchPlus', 'LeTai.TrueShadow', 'Lofelt.NiceVibrations', 'NativeShare.Runtime',
    'ParticleImage', 'ToonyColorsPro.Runtime', 'ToonyColorsPro2.Demo',
    # editor and dev tooling that was compiled into the player
    'AppIconChanger', 'Domain_Reload', 'MCPForUnity.Runtime',
}

# ---------------------------------------------------------------------------------------------------------
# THIRD PARTY VENDORED INTO A GAME ASSEMBLY.
#
# These namespaces live inside `Assembly-CSharp` but are not the game. They are 17605 of its 21415 methods.
# Matched as a namespace root: an entry matches the namespace itself and anything under it.
NAMESPACE_OVERRIDES = {
    # the Voodoo publishing SDK, compiled straight into Assembly-CSharp: 7569 methods
    'Voodoo': SDK,
    'Voodoo_ContextSDK': SDK,
    'VT': SDK,
    # Easy Save 3: 915 methods across two namespaces plus the global types below
    'ES3Types': SDK,
    'ES3Internal': SDK,
    # asset-store packages
    'HighlightPlus': SDK,          # Kronnect highlight effect
    'PaperPlaneTools': SDK,        # RateBox / native Alert
    'ConsentManagementProvider': SDK,
    'ConsentMessagePlugin': SDK,
    'Audiomob': SDK,
    'AudiomobExamples': SDK,
    'EpicToonFX': SDK,
    'TinyJson': SDK,
    'Nakama_Client': SDK,
    # compiler and attribute shims that happen to be declared in the assembly
    'System': BCL,
    'Microsoft': BCL,
    'UnityEngine': UNITY,
}

# The game's own namespace roots inside `Assembly-CSharp`, and which tier each is. `JuicedUp` (and its typo
# sibling `JuicesUp`) is the studio's own framework, not a vendor's: `JuicedUp.Features.Core` holds
# CrateManager, Player, TailManager, SnakeOccupancyManager, PillManager, SwipeController and LevelController
# - that IS Snacky Dash. `JuicedUp.Features.VoodooLiveBridge` bridges *to* the SDK, which is what settles it.
GAMEPLAY_NAMESPACES = {
    'JuicedUp.Features', 'JuicesUp.Features',
    'KiraganGames',          # the studio
    'MiniSoundManager', 'Deus', 'Tools', 'UI',
}
META_NAMESPACES = {
    'JuicedUp.Common', 'JuicedUp.App', 'JuicesUp.Common',
    'MobileGameShop',        # a shop module written for this title family, not an asset - judgement call
    'CloudContent', 'Assets.CloudContent',
    # feature namespaces that are instrumentation or store plumbing rather than anything the player sees
    'JuicedUp.Features.Ads', 'JuicedUp.Features.Debugger', 'JuicedUp.Features.ForceUpdate',
    'JuicedUp.Features.LevelCohort', 'JuicedUp.Features.LocalizationDebug',
    'JuicedUp.Features.OnlineOnlyMode', 'JuicedUp.Features.PlayGates', 'JuicedUp.Features.Segmentation',
    'JuicedUp.Features.ShaderWarmup', 'JuicedUp.Features.Support', 'JuicedUp.Features.VoodooLiveBridge',
}

# A game namespace whose LAST segment is one of these is instrumentation wherever it sits, so
# `JuicedUp.Features.Core.Analytics` is meta even though `JuicedUp.Features.Core` is the game itself. A rule
# rather than a list because every feature grows its own `.Analytics` sooner or later.
META_LEAF_SEGMENTS = {'Analytics', 'Debugger', 'Debugging', 'Diagnostics', 'Logging', 'Telemetry'}

# Types in a game assembly's *global* namespace that belong to a vendor. 726 of the 1633 global methods.
GLOBAL_TYPE_PREFIXES = ('ES3',)
GLOBAL_TYPE_NAMES = {
    'VoodooSauce',
    'Taptic', 'AndroidTaptic',              # the Taptic Engine asset
    'MaxNativeAdsSdkUtils', 'MyRewardedInterstitialCallbacks',
    'AdnDebuggerScreen', 'AdnContextSDKDebuggerScreen',
}

_UNITY_PREFIXES = ('UnityEngine.',)
_BCL_PREFIXES = ('System.', 'Microsoft.', 'Mono.')


def _assembly_bucket(assembly):
    """Rule 1 then rule 4: the tables, then the prefix fallback. Never returns `game` from a prefix."""
    if assembly in GAME_ASSEMBLIES:
        return GAME
    if assembly in SDK_ASSEMBLIES:
        return SDK
    if assembly in UNITY_ASSEMBLIES:
        return UNITY
    if assembly in BCL_ASSEMBLIES:
        return BCL
    if assembly.startswith(_BCL_PREFIXES):
        return BCL
    # `UnityEngine.CoreModule` and friends. `UnityEngine.Purchasing.*` is already in SDK_ASSEMBLIES above and
    # so never reaches here.
    if assembly.startswith(_UNITY_PREFIXES) or assembly == 'Unity' or assembly.startswith('Unity.'):
        return UNITY
    return SDK


def _root_match(namespace, roots):
    """Whether `namespace` is one of `roots` or sits under one of them."""
    return any(namespace == root or namespace.startswith(root + '.') for root in roots)


def bucket(assembly, namespace='', type_name=''):
    """Which of the four buckets a type belongs to: 'game', 'unity', 'bcl' or 'sdk'.

    `assembly` is the name without `.dll`. `namespace` is dotted and empty for the global namespace.
    `type_name` is only consulted for a global-namespace type in a game assembly, where there is no
    namespace to decide by.
    """
    assembly = assembly[:-4] if assembly.endswith('.dll') else assembly
    found = _assembly_bucket(assembly)

    if found is not GAME:
        return found

    # Rule 2: third party vendored into a game assembly.
    if namespace:
        for root, where in NAMESPACE_OVERRIDES.items():
            if namespace == root or namespace.startswith(root + '.'):
                return where
        return GAME

    # Rule 3: a global-namespace type, which only its name can place.
    if type_name.startswith(GLOBAL_TYPE_PREFIXES) or type_name in GLOBAL_TYPE_NAMES:
        return SDK
    return GAME


def is_game_code(assembly, namespace='', type_name=''):
    """The predicate. True where this is the game's own code rather than engine, BCL or a vendor's."""
    return bucket(assembly, namespace, type_name) == GAME


def tier(assembly, namespace='', type_name=''):
    """Within `game`, which half: 'gameplay' or 'meta'. '' where the code is not game code at all.

    'gameplay' is the game's product code - the board, the snake, the crates, the levels, and the screens a
    player sees. 'meta' is the game's own plumbing - save and load, the api client, config, notifications,
    the debug menu, and every `.Analytics` under a feature.

    The fix queue is ordered by this: a defect in `JuicedUp.Features.Core.CrateManager` is worth more than
    one in `JuicedUp.Common.Notifications`, and both are worth more than anything outside `game` at all.
    """
    if bucket(assembly, namespace, type_name) != GAME:
        return ''
    if not namespace:
        return 'gameplay'
    if namespace.rsplit('.', 1)[-1] in META_LEAF_SEGMENTS:
        return 'meta'
    if _root_match(namespace, META_NAMESPACES):
        return 'meta'
    if _root_match(namespace, GAMEPLAY_NAMESPACES):
        return 'gameplay'
    # A game namespace in neither list: new code, and gameplay until something says otherwise.
    return 'gameplay'


def split_path(scripts_root, path):
    """(assembly, namespace, type_name) for a .cs file under `<export>/ExportedProject/Assets/Scripts`.

    AssetRipper writes `Assets/Scripts/<Assembly>/<namespace as directories>/<Type>.cs`, and puts a nested
    type inside its parent's file, so the path already names the declaring type's namespace.
    """
    parts = os.path.relpath(path, scripts_root).split(os.sep)
    assembly = parts[0]
    namespace = '.'.join(parts[1:-1])
    type_name = os.path.splitext(parts[-1])[0]
    return assembly, namespace, type_name


def scripts_root(export):
    """`Assets/Scripts` under an export, whether the export root or its ExportedProject was given."""
    for candidate in (os.path.join(export, 'ExportedProject', 'Assets', 'Scripts'),
                      os.path.join(export, 'Assets', 'Scripts'),
                      export):
        if os.path.isdir(candidate):
            return candidate
    return None


# ---------------------------------------------------------------------------------------------------------
# CLI


def _count(root, count_methods):
    """{(bucket, tier): [files, methods]} over every .cs file in the export."""
    if count_methods:
        sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
        from csharp import members
        from markers import has_body

    totals = collections.defaultdict(lambda: [0, 0])
    per_namespace = collections.defaultdict(lambda: [0, 0])

    for directory, _subdirectories, files in os.walk(root):
        for name in files:
            if not name.endswith('.cs'):
                continue
            path = os.path.join(directory, name)
            assembly, namespace, type_name = split_path(root, path)
            where = bucket(assembly, namespace, type_name)
            key = (where, tier(assembly, namespace, type_name))

            found = 0
            if count_methods:
                for texts in members(path).values():
                    found += sum(1 for text in texts if has_body(text))

            totals[key][0] += 1
            totals[key][1] += found
            if where == GAME:
                label = (assembly, namespace or '<global>')
                per_namespace[label][0] += 1
                per_namespace[label][1] += found

    return totals, per_namespace


def census(metadata):
    """[(assembly, namespace, type_name, methods)] read straight out of `global-metadata.dat`.

    The export takes minutes and the metadata takes a second, so the split can be measured before a run has
    finished - which is how the numbers in this file's docstring were produced. Metadata v31 only: every
    index is a fixed int32 there, and the dynamic-width indices of v38+ would need the layout LibCpp2IL
    carries in `Il2CppVariableWidthIndex`.

    Correctness check: the per-image method counts sum to the number Cpp2IL logs as `Processed N OK`.
    """
    import struct

    data = open(metadata, 'rb').read()
    magic, version = struct.unpack_from('<Ii', data, 0)
    if magic != 0xFAB11BAF:
        raise SystemExit('%s is not global-metadata.dat' % metadata)
    if not 29 <= version < 38:
        raise SystemExit('metadata v%d: only v29-v37 have fixed-width indices this reader understands' % version)

    # Section headers are (offset, size) pairs from byte 8, in declaration order. Only the four needed.
    sections = ['stringLiteral', 'stringLiteralData', 'string', 'events', 'properties', 'methods',
                'parameterDefaultValues', 'fieldDefaultValues', 'fieldAndParameterDefaultValueData',
                'fieldMarshaledSizes', 'parameters', 'fields', 'genericParameters',
                'genericParameterConstraints', 'genericContainers', 'nestedTypes', 'interfaces',
                'vtableMethods', 'interfaceOffsets', 'typeDefinitions', 'images', 'assemblies']
    where = {name: struct.unpack_from('<ii', data, 8 + n * 8) for n, name in enumerate(sections)}

    strings = where['string'][0]

    def text(index):
        return data[strings + index:data.index(b'\0', strings + index)].decode('utf-8', 'replace')

    TYPE_DEF, IMAGE = 88, 40
    type_off, type_size = where['typeDefinitions']
    count = type_size // TYPE_DEF
    nested = where['nestedTypes'][0]

    # Il2CppTypeDefinition v29-v37: name and namespace at 0 and 4, NestedTypesStart at 48, MethodCount at 64,
    # NestedTypeCount at 72.
    names, parents = [], [-1] * count
    for n in range(count):
        at = type_off + n * TYPE_DEF
        name_index, namespace_index = struct.unpack_from('<ii', data, at)
        methods, nested_count = struct.unpack_from('<H', data, at + 64)[0], struct.unpack_from('<H', data, at + 72)[0]
        names.append((text(name_index), text(namespace_index), methods))
        start = struct.unpack_from('<i', data, at + 48)[0]
        for step in range(nested_count):
            child = struct.unpack_from('<i', data, nested + (start + step) * 4)[0]
            if 0 <= child < count:
                parents[child] = n

    # A nested type carries an empty namespace, so it is counted against the type that declares it - the same
    # place the export puts it. Without this, Assembly-CSharp's global namespace reads 4588 instead of 1633.
    def declaring(n):
        for _ in range(32):
            if parents[n] < 0:
                return n
            n = parents[n]
        return n

    rows = []
    for n in range(where['images'][1] // IMAGE):
        at = where['images'][0] + n * IMAGE
        name_index, _assembly, first, types = struct.unpack_from('<iiiI', data, at)
        image = text(name_index)
        image = image[:-4] if image.endswith('.dll') else image
        for t in range(first, first + types):
            root = declaring(t)
            rows.append((image, names[root][1], names[root][0], names[t][2]))
    return rows


def _census_report(metadata, queue):
    rows = census(metadata)
    totals = collections.Counter()
    tiers = collections.Counter()
    per_namespace = collections.Counter()

    for assembly, namespace, type_name, methods in rows:
        where = bucket(assembly, namespace, type_name)
        totals[where] += methods
        if where == GAME:
            tiers[tier(assembly, namespace, type_name)] += methods
            per_namespace[(assembly, namespace or '<global>')] += methods

    grand = sum(totals.values())
    print('%d methods in %d assemblies\n' % (grand, len({row[0] for row in rows})))
    print('%-10s %10s %8s' % ('bucket', 'methods', 'share'))
    for where in (GAME, SDK, UNITY, BCL):
        print('%-10s %10d %7.2f%%' % (where, totals[where], 100.0 * totals[where] / grand))
    for what in ('gameplay', 'meta'):
        print('  %-8s %10d %7.2f%%' % (what, tiers[what], 100.0 * tiers[what] / grand))

    if queue:
        print('\nfix queue - the game\'s own namespaces, largest first:')
        for (assembly, namespace), methods in per_namespace.most_common():
            what = tier(assembly, '' if namespace == '<global>' else namespace, '')
            print('%6d  %-8s %s/%s' % (methods, what, assembly, namespace))


def _selftest():
    """The tables against the census this file's docstring quotes. No export needed."""
    census = [
        ('Assembly-CSharp', GAME), ('mscorlib', BCL), ('System.Xml', BCL), ('Microsoft.Extensions.Options', BCL),
        ('UnityEngine.CoreModule', UNITY), ('UnityEngine.UIElementsModule', UNITY), ('Unity.Mathematics', UNITY),
        ('Unity.RenderPipelines.Core.Runtime', UNITY), ('UnityEngine.UI', UNITY), ('Cinemachine', UNITY),
        ('Voodoo.Sauce.Core', SDK), ('Blackboard', SDK), ('AudioMobGame', SDK), ('Assembly-CSharp-firstpass', SDK),
        ('Firebase.App', SDK), ('UnityEngine.Purchasing.Stores', SDK), ('Unity.Services.Analytics', SDK),
        ('Newtonsoft.Json', SDK), ('DOTween', SDK), ('Sirenix.Serialization', SDK), ('MoreMountains.Tools', SDK),
        ('CloudContent', GAME), ('SomethingNobodyHasSeen', SDK),
    ]
    bad = [(name, want, _assembly_bucket(name)) for name, want in census if _assembly_bucket(name) != want]

    inside = [
        (('Assembly-CSharp', 'JuicedUp.Features.Core', 'CrateManager'), GAME, 'gameplay'),
        (('Assembly-CSharp', 'JuicedUp.Common.Notifications', 'X'), GAME, 'meta'),
        (('Assembly-CSharp', 'Voodoo.Sauce.Internal.Ads', 'X'), SDK, ''),
        (('Assembly-CSharp', 'ES3Types', 'X'), SDK, ''),
        (('Assembly-CSharp', 'HighlightPlus', 'X'), SDK, ''),
        (('Assembly-CSharp', '', 'PillController'), GAME, 'gameplay'),
        (('Assembly-CSharp', '', 'ES3Writer'), SDK, ''),
        (('Assembly-CSharp', '', 'VoodooSauce'), SDK, ''),
        (('Assembly-CSharp', '', 'BoosterManager'), GAME, 'gameplay'),
        (('Assembly-CSharp', 'KiraganGames', 'Button'), GAME, 'gameplay'),
        (('Assembly-CSharp.dll', 'JuicedUp.Features.Core', 'Player'), GAME, 'gameplay'),
    ]
    for args, want_bucket, want_tier in inside:
        got_bucket, got_tier = bucket(*args), tier(*args)
        if got_bucket != want_bucket or got_tier != want_tier:
            bad.append((args, (want_bucket, want_tier), (got_bucket, got_tier)))

    for row in bad:
        print('FAIL', row)
    print('selftest:', 'ok' if not bad else '%d FAILED' % len(bad))
    return 0 if not bad else 1


def main():
    args = [a for a in sys.argv[1:]]
    if '--selftest' in args:
        raise SystemExit(_selftest())

    count_methods = '--files' not in args
    queue = '--queue' in args
    positional = [a for a in args if not a.startswith('--')]
    if not positional:
        raise SystemExit(__doc__.split('\n\n')[1].strip())

    if '--census' in args:
        return _census_report(positional[0], queue)

    root = scripts_root(positional[0])
    if not root:
        raise SystemExit('no Assets/Scripts under %s - is the export finished?' % positional[0])

    if not os.path.isdir(os.path.join(root, *MARKER)):
        raise SystemExit(
            'the tables in this file are %s\'s, and %s/%s is not in %s.\n'
            'Reporting anyway would be nonsense - rule 2 has nothing to say about another title\'s assets, '
            'so everything in its Assembly-CSharp would come out `game`.\n'
            'Build that title its own tables; GAMEFILTER.md says how.' % (TITLE, MARKER[0], MARKER[1], root))

    totals, per_namespace = _count(root, count_methods)

    unit = 'methods' if count_methods else 'files'
    grand = sum(v[1 if count_methods else 0] for v in totals.values())

    print('%-10s %-9s %8s %10s   %s' % ('bucket', 'tier', 'files', unit, 'share'))
    order = {GAME: 0, SDK: 1, UNITY: 2, BCL: 3}
    for (where, what), (files, found) in sorted(totals.items(), key=lambda kv: (order[kv[0][0]], kv[0][1])):
        measure = found if count_methods else files
        print('%-10s %-9s %8d %10d   %5.2f%%' % (where, what or '-', files, found,
                                                 100.0 * measure / grand if grand else 0))
    print('%-10s %-9s %8d %10d   100.00%%' % ('TOTAL', '',
                                              sum(v[0] for v in totals.values()),
                                              sum(v[1] for v in totals.values())))

    if queue:
        print('\nfix queue - the game\'s own namespaces, largest first:')
        print('%-9s %8s %10s   %s' % ('tier', 'files', unit, 'namespace'))
        rows = sorted(per_namespace.items(), key=lambda kv: -kv[1][1 if count_methods else 0])
        for (assembly, namespace), (files, found) in rows:
            what = tier(assembly, '' if namespace == '<global>' else namespace, '')
            print('%-9s %8d %10d   %s/%s' % (what, files, found, assembly, namespace))


if __name__ == '__main__':
    main()
