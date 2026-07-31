using AsmResolver.DotNet;
using AssetRipper.Import.Configuration;
using AssetRipper.Import.Logging;
using AssetRipper.Import.Structure.Platforms;
using Cpp2IL.Core.Api;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.OutputFormats;
using Cpp2IL.Core.ProcessingLayers;
using LibCpp2IL;
using Cpp2IlApi = Cpp2IL.Core.Cpp2IlApi;

namespace AssetRipper.Import.Structure.Assembly.Managers;

public sealed class IL2CppManager : BaseManager
{
	private static bool instructionSetsRegistered;

	/// <summary>
	/// Registers the instruction sets Cpp2IL decodes a binary with. It can only be done once per process, because
	/// <see cref="InstructionSetRegistry"/> refuses a second registration for an id.
	/// </summary>
	/// <remarks>
	/// This is not a static constructor because the Arm64 choice depends on <paramref name="level"/>, which is only
	/// known once a manager is being constructed.
	/// <para>
	/// <see cref="Arm64InstructionSet"/> produces no ISIL at all, so nothing downstream of it can recover a method
	/// body: an Arm64 game gets empty methods no matter which content level was asked for.
	/// <see cref="NewArmV8InstructionSet"/> does produce ISIL, at the cost of being the less exercised of the two, so
	/// it is used only for <see cref="ScriptContentLevel.Level3"/>, where recovery is the point and the output is
	/// already understood to be best effort.
	/// </para>
	/// </remarks>
	private static void RegisterInstructionSets(ScriptContentLevel level)
	{
		if (instructionSetsRegistered)
		{
			return;
		}
		instructionSetsRegistered = true;

		InstructionSetRegistry.RegisterInstructionSet<X86InstructionSet>(DefaultInstructionSets.X86_32);
		InstructionSetRegistry.RegisterInstructionSet<X86InstructionSet>(DefaultInstructionSets.X86_64);
		InstructionSetRegistry.RegisterInstructionSet<WasmInstructionSet>(DefaultInstructionSets.WASM);
		InstructionSetRegistry.RegisterInstructionSet<ArmV7InstructionSet>(DefaultInstructionSets.ARM_V7);
		if (level == ScriptContentLevel.Level3)
		{
			InstructionSetRegistry.RegisterInstructionSet<NewArmV8InstructionSet>(DefaultInstructionSets.ARM_V8);
		}
		else
		{
			InstructionSetRegistry.RegisterInstructionSet<Arm64InstructionSet>(DefaultInstructionSets.ARM_V8);
		}

		LibCpp2IlBinaryRegistry.RegisterBuiltInBinarySupport();
	}

	public static List<Cpp2IlProcessingLayer> DefaultProcessingLayers { get; } =
	[
		new AttributeAnalysisProcessingLayer(),
		new MethodOverrideNameFixer(),
	];

	public static AsmResolverDllOutputFormatDefault DefaultOutputFormat { get; } = new();

	/// <summary>
	/// Used for <see cref="ScriptContentLevel.Level3"/>.
	/// </summary>
	/// <remarks>
	/// <see cref="CallAnalysisProcessingLayer"/> is deliberately absent. It only annotates methods with who calls
	/// them, which is of no use once the bodies themselves are recovered, and on an obfuscated assembly it can
	/// produce an attribute referring to the module pseudo type, which fails to convert and takes the whole assembly
	/// manager down with it. Call targets are resolved during method analysis, not by that layer.
	/// </remarks>
	public static List<Cpp2IlProcessingLayer>? RecoveryProcessingLayers { get; set; } =
	[
		new AttributeAnalysisProcessingLayer(),
		new MethodOverrideNameFixer(),
	];

	/// <summary>
	/// Used for <see cref="ScriptContentLevel.Level3"/>.
	/// Analyzes each method and emits recovered CIL, falling back to a throwing stub when analysis fails.
	/// </summary>
	public static AsmResolverDllOutputFormat? RecoveryOutputFormat { get; set; } = new AsmResolverDllOutputFormatIlRecovery();

	public static event Action? ClearStaticState;

	public string? GameAssemblyPath { get; private set; }
	public string? UnityPlayerPath { get; private set; }
	public string? GameDataPath { get; private set; }
	public string? MetaDataPath { get; private set; }
	public UnityVersion UnityVersion { get; private set; }
	/// <summary>
	/// For when analysis is reimplimented in Cpp2IL.
	/// </summary>
	private readonly ScriptContentLevel contentLevel;

	public IL2CppManager(Action<string> requestAssemblyCallback, ScriptContentLevel level) : base(requestAssemblyCallback)
	{
		contentLevel = level;
		RegisterInstructionSets(level);
	}

	public override ScriptingBackend ScriptingBackend => ScriptingBackend.IL2Cpp;

	public override void Initialize(PlatformGameStructure gameStructure)
	{
		string? gameDataPath = gameStructure.GameDataPath;
		if (string.IsNullOrWhiteSpace(gameDataPath))
		{
			throw new ArgumentException($"{nameof(gameStructure.GameDataPath)} cannot be null or whitespace.", nameof(gameStructure));
		}

		GameDataPath = gameDataPath;
		GameAssemblyPath = gameStructure.Il2CppGameAssemblyPath;
		UnityPlayerPath = gameStructure.UnityPlayerPath;
		MetaDataPath = gameStructure.Il2CppMetaDataPath;

		UnityVersion = gameStructure.Version ?? Cpp2IlApi.DetermineUnityVersion(UnityPlayerPath, GameDataPath);

		if (UnityVersion == default)
		{
			throw new Exception("Could not determine the unity version");
		}
		else
		{
			Logger.Info(LogCategory.Import, $"During Il2Cpp initialization, found Unity version: {UnityVersion}");
		}

		Logger.SendStatusChange("loading_step_parse_il2cpp_metadata");

		ClearStaticState?.Invoke();

		Cpp2IlApi.InitializeLibCpp2Il(GameAssemblyPath!, MetaDataPath!, UnityVersion, false);

		Logger.SendStatusChange("loading_step_generate_dummy_dll");

		List<Cpp2IlProcessingLayer> processingLayers = contentLevel == ScriptContentLevel.Level3
			? RecoveryProcessingLayers ?? DefaultProcessingLayers
			: DefaultProcessingLayers;

		foreach (Cpp2IlProcessingLayer cpp2IlProcessingLayer in processingLayers)
		{
			cpp2IlProcessingLayer.PreProcess(Cpp2IlApi.CurrentAppContext, processingLayers);
		}

		foreach (Cpp2IlProcessingLayer cpp2IlProcessingLayer in processingLayers)
		{
			cpp2IlProcessingLayer.Process(Cpp2IlApi.CurrentAppContext);
		}

		AsmResolverDllOutputFormat outputFormat = contentLevel == ScriptContentLevel.Level3
			? RecoveryOutputFormat ?? DefaultOutputFormat
			: DefaultOutputFormat;

		List<AssemblyDefinition> assemblies = outputFormat.BuildAssemblies(Cpp2IlApi.CurrentAppContext);

		foreach (AssemblyDefinition assembly in assemblies)
		{
			Add(assembly);
		}
	}

	~IL2CppManager()
	{
		Dispose(false);
	}
}
