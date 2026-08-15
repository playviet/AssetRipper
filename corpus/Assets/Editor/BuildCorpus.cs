using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

// Builds the corpus to an arm64 il2cpp Android apk. Run headless:
//
//   /Applications/Unity/Hub/Editor/6000.0.78f1/Unity.app/Contents/MacOS/Unity \
//       -batchmode -quit -nographics -projectPath <repo>/corpus \
//       -executeMethod BuildCorpus.Build -logFile <repo>/corpus/build.log
//
// Produces <repo>/corpus/corpus.apk. Everything the recovery cares about is inside it:
// lib/arm64-v8a/libil2cpp.so and assets/bin/Data/Managed/Metadata/global-metadata.dat.
public static class BuildCorpus
{
	const string ScenePath = "Assets/Corpus.unity";

	public static void Build()
	{
		string output = Path.Combine(Directory.GetCurrentDirectory(), "corpus.apk");

		EnsureScene();

		NamedBuildTarget target = NamedBuildTarget.Android;
		PlayerSettings.SetScriptingBackend(target, ScriptingImplementation.IL2CPP);
		PlayerSettings.SetApiCompatibilityLevel(target, ApiCompatibilityLevel.NET_Unity_4_8);
		// Minimal is the weakest level il2cpp offers; link.xml keeps Assembly-CSharp whole on top of it.
		PlayerSettings.SetManagedStrippingLevel(target, ManagedStrippingLevel.Minimal);
		PlayerSettings.SetIl2CppCompilerConfiguration(target, Il2CppCompilerConfiguration.Release);
		PlayerSettings.SetIl2CppCodeGeneration(target, Il2CppCodeGeneration.OptimizeSpeed);
		PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
		PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel23;
		PlayerSettings.stripEngineCode = false;
		PlayerSettings.applicationIdentifier = "com.assetripper.corpus";
		PlayerSettings.productName = "corpus";

		EditorUserBuildSettings.androidBuildSystem = AndroidBuildSystem.Gradle;
		EditorUserBuildSettings.buildAppBundle = false;
		EditorUserBuildSettings.androidCreateSymbols = AndroidCreateSymbols.Disabled;
		EditorUserBuildSettings.development = false;
		EditorUserBuildSettings.exportAsGoogleAndroidProject = false;
		EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android);

		BuildPlayerOptions options = new BuildPlayerOptions
		{
			scenes = new[] { ScenePath },
			locationPathName = output,
			target = BuildTarget.Android,
			targetGroup = BuildTargetGroup.Android,
			options = BuildOptions.None,
		};

		var report = BuildPipeline.BuildPlayer(options);
		var summary = report.summary;
		Debug.Log($"CORPUS BUILD {summary.result} -> {output} ({summary.totalSize} bytes, {summary.totalErrors} errors)");

		if (summary.result != UnityEditor.Build.Reporting.BuildResult.Succeeded)
		{
			foreach (var step in report.steps)
			{
				foreach (var message in step.messages)
				{
					if (message.type == LogType.Error || message.type == LogType.Exception)
					{
						Debug.Log($"CORPUS BUILD ERROR [{step.name}] {message.content}");
					}
				}
			}

			EditorApplication.Exit(1);
		}

		EditorApplication.Exit(0);
	}

	static void EnsureScene()
	{
		if (File.Exists(ScenePath))
		{
			return;
		}

		Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
		GameObject holder = new GameObject("Driver");
		holder.AddComponent<Driver>();
		EditorSceneManager.SaveScene(scene, ScenePath);
		AssetDatabase.Refresh();
	}
}
