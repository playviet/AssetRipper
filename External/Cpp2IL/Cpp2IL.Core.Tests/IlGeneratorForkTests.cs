using System;
using System.Collections.Generic;
using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using AsmResolver.PE.DotNet.Metadata.Tables;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using ReflectionMethodAttributes = System.Reflection.MethodAttributes;

namespace Cpp2IL.Core.Tests;

/// <summary>
/// What this fork adds to the IL generator, tested apart from the file it belongs to so that a later
/// version of Cpp2IL can be merged without the two sets of tests meeting.
/// </summary>
public class IlGeneratorForkTests
{
    [SetUp]
    public void Setup()
    {
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.LoadSimple2019Game();
    }

    [Test]
    public void SingleUseArgument_TravelsOnTheStack()
    {
        var appContext = Cpp2IlApi.CurrentAppContext!;
        var systemObject = appContext.SystemTypes.SystemObjectType;
        var systemVoid = appContext.SystemTypes.SystemVoidType;
        var systemInt = appContext.SystemTypes.SystemInt32Type;

        var callerContext = new InjectedMethodAnalysisContext(
            systemObject,
            "Caller",
            systemVoid,
            ReflectionMethodAttributes.Public | ReflectionMethodAttributes.Static,
            []);

        var producerContext = new InjectedMethodAnalysisContext(
            systemObject,
            "Produce",
            systemInt,
            ReflectionMethodAttributes.Public | ReflectionMethodAttributes.Static,
            []);

        var targetContext = new InjectedMethodAnalysisContext(
            systemObject,
            "TargetStatic",
            systemVoid,
            ReflectionMethodAttributes.Public | ReflectionMethodAttributes.Static,
            [systemInt, systemInt]);

        var first = new LocalVariable("first", new Register(null, "first"));
        var second = new LocalVariable("second", new Register(null, "second"));

        //The shape a call whose last argument is computed for it takes: the value is written, then the
        //earlier arguments are loaded, and only then is it read.
        var instructions = new List<Instruction>
        {
            new(0, OpCode.Call, producerContext, first),
            new(1, OpCode.Call, producerContext, second),
            new(2, OpCode.CallVoid, targetContext, first, second),
            new(3, OpCode.Return),
        };

        callerContext.ControlFlowGraph = new ISILControlFlowGraph(instructions);
        callerContext.Locals = [first, second];
        callerContext.ParameterLocals = [];
        callerContext.AnalysisWarnings = [];

        var module = new ModuleDefinition("Test.dll", new AssemblyReference("mscorlib", new Version(4, 0, 0, 0)));
        var typeDef = new TypeDefinition("Cpp2IL.Core.Tests", "IlGeneratorStackTestType", TypeAttributes.Class | TypeAttributes.Public);
        module.TopLevelTypes.Add(typeDef);

        var callerMethodDef = new MethodDefinition("Caller", MethodAttributes.Public | MethodAttributes.Static,
            MethodSignature.CreateStatic(module.CorLibTypeFactory.Void));
        typeDef.Methods.Add(callerMethodDef);

        var producerMethodDef = new MethodDefinition("Produce", MethodAttributes.Public | MethodAttributes.Static,
            MethodSignature.CreateStatic(module.CorLibTypeFactory.Int32));
        typeDef.Methods.Add(producerMethodDef);
        producerContext.PutExtraData("AsmResolverMethod", producerMethodDef);

        var targetMethodDef = new MethodDefinition("TargetStatic", MethodAttributes.Public | MethodAttributes.Static,
            MethodSignature.CreateStatic(module.CorLibTypeFactory.Void,
                [module.CorLibTypeFactory.Int32, module.CorLibTypeFactory.Int32]));
        typeDef.Methods.Add(targetMethodDef);
        targetContext.PutExtraData("AsmResolverMethod", targetMethodDef);

        IlGenerator.GenerateIl(callerContext, callerMethodDef);

        var il = callerMethodDef.CilMethodBody!.Instructions;

        Assert.That(il.Count(i => i.OpCode == CilOpCodes.Call), Is.EqualTo(3), "expected both producers and the target to be called");
        Assert.That(il.Any(i => i.OpCode == CilOpCodes.Stloc || i.OpCode == CilOpCodes.Ldloc), Is.False,
            "a value produced for one argument and read once should not need a local");
        Assert.That(callerMethodDef.CilMethodBody!.LocalVariables, Is.Empty);
    }
}
