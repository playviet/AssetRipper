using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Metadata.Tables;
using AssetRipper.Processing.Assemblies;

namespace AssetRipper.Tests;

internal class GeneratedCodeRemovalTests
{
	/// <summary>
	/// A module shaped like one Netcode for GameObjects has generated into: a behaviour carrying the generated members
	/// alongside its own, a nested type that also carries one, and the per assembly script map.
	/// </summary>
	private static ModuleDefinition CreateModule()
	{
		ModuleDefinition module = new("Assembly-CSharp.dll", KnownCorLibs.SystemRuntime_v9_0_0_0);

		TypeDefinition behaviour = new("Game", "PlayerBehaviour", TypeAttributes.Public);
		behaviour.Methods.Add(CreateMethod(module, "__initializeVariables"));
		behaviour.Methods.Add(CreateMethod(module, "__rpc_handler_2246990417"));
		behaviour.Methods.Add(CreateMethod(module, "__getTypeName"));
		behaviour.Methods.Add(CreateMethod(module, "Update"));
		behaviour.Methods.Add(CreateMethod(module, "ShootServerRpc"));
		module.TopLevelTypes.Add(behaviour);

		TypeDefinition nested = new(null, "Inner", TypeAttributes.NestedPublic);
		nested.Methods.Add(CreateMethod(module, "__initializeVariables"));
		nested.Methods.Add(CreateMethod(module, "Tick"));
		behaviour.NestedTypes.Add(nested);

		TypeDefinition initializer = new(null, "NetworkBehaviourILPP", TypeAttributes.Public);
		initializer.Methods.Add(CreateMethod(module, "InitializeRPCS_Assembly_CSharp"));
		module.TopLevelTypes.Add(initializer);

		TypeDefinition scriptTypes = new(null, "UnitySourceGeneratedAssemblyMonoScriptTypes_v1", TypeAttributes.Public);
		scriptTypes.Methods.Add(CreateMethod(module, "Get"));
		module.TopLevelTypes.Add(scriptTypes);

		return module;
	}

	private static MethodDefinition CreateMethod(ModuleDefinition module, string name)
	{
		return new MethodDefinition(name, MethodAttributes.Public, MethodSignature.CreateInstance(module.CorLibTypeFactory.Void));
	}

	[Test]
	public void GeneratedMembersAreRemoved()
	{
		ModuleDefinition module = CreateModule();

		(int methods, int types) = GeneratedCodeRemovalProcessor.Process(module);

		using (Assert.EnterMultipleScope())
		{
			Assert.That(methods, Is.EqualTo(5));
			Assert.That(types, Is.EqualTo(1));
			Assert.That(module.TopLevelTypes.Select(t => t.Name?.ToString()), Does.Not.Contain("UnitySourceGeneratedAssemblyMonoScriptTypes_v1"));
			Assert.That(module.GetAllTypes().SelectMany(t => t.Methods).Select(m => m.Name?.ToString()),
				Is.EquivalentTo(new[] { "Update", "ShootServerRpc", "Tick" }));
		}
	}

	[Test]
	public void HandWrittenMembersAreKept()
	{
		ModuleDefinition module = new("Assembly-CSharp.dll", KnownCorLibs.SystemRuntime_v9_0_0_0);

		// Names that share a prefix with a generated one without being generated themselves.
		TypeDefinition type = new("Game", "Player", TypeAttributes.Public);
		type.Methods.Add(CreateMethod(module, "__getTypeNameCore"));
		type.Methods.Add(CreateMethod(module, "InitializeRPC"));
		type.Methods.Add(CreateMethod(module, "_initializeVariables"));
		module.TopLevelTypes.Add(type);

		TypeDefinition lookalike = new(null, "UnitySourceGeneratedAssemblyMonoScriptTypes_v2", TypeAttributes.Public);
		module.TopLevelTypes.Add(lookalike);

		(int methods, int types) = GeneratedCodeRemovalProcessor.Process(module);

		using (Assert.EnterMultipleScope())
		{
			Assert.That(methods, Is.Zero);
			Assert.That(types, Is.Zero);
		}
	}
}
