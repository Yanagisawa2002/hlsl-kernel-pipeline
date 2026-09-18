using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace HlslPerf.Crossover
{
    public static class BuildCrossover
    {
        public static void Build()
        {
            string output = Environment.GetEnvironmentVariable("CROSSOVER_PLAYER_PATH");
            if (string.IsNullOrWhiteSpace(output)) throw new ArgumentException("Set CROSSOVER_PLAYER_PATH");
            PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64, new[] { GraphicsDeviceType.Direct3D12 });
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64, false);
            PlayerSettings.SetScriptingBackend(UnityEditor.Build.NamedBuildTarget.Standalone, ScriptingImplementation.Mono2x);
            PlayerSettings.gpuSkinning = false; PlayerSettings.colorSpace = ColorSpace.Linear;
            PlayerSettings.enableFrameTimingStats = true; PlayerSettings.runInBackground = true;
            PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
            PlayerSettings.defaultScreenWidth = 1280; PlayerSettings.defaultScreenHeight = 720;
            QualitySettings.vSyncCount = 0; QualitySettings.antiAliasing = 0;
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var instance = new GameObject("Crossover").AddComponent<CrossoverBenchmark>();
            instance.culling = AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/GpuDrivenCrowdBenchmark/CullAgents.compute");
            instance.drawing = AssetDatabase.LoadAssetAtPath<Shader>("Assets/GpuDrivenCrowdBenchmark/Crowd.shader");
            if (instance.culling == null || instance.drawing == null) throw new Exception("Missing shader assets");
            EditorSceneManager.SaveScene(scene, "Assets/Crossover.unity");
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions { scenes = new[] { "Assets/Crossover.unity" },
                locationPathName = output, target = BuildTarget.StandaloneWindows64, options = BuildOptions.Development });
            if (report.summary.result != BuildResult.Succeeded) throw new Exception("Player build failed: " + report.summary.result);
            File.WriteAllText(output + ".build.json", "{\"unity\":\"" + Application.unityVersion + "\",\"result\":\"Succeeded\",\"development\":true}");
        }
    }
}
