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
        [Serializable] public sealed class Receipt {
            public string unity,result,sourceIdentity;public bool development=true,graphicsJobs,autoconnect;
            public string[] graphicsApis={"Direct3D12","Direct3D11"};
            public string d3d11Scope="diagnostic override only; benchmark remains D3D12";
        }
        public static void Build()
        {
            string output = Environment.GetEnvironmentVariable("CROSSOVER_PLAYER_PATH");
            if (string.IsNullOrWhiteSpace(output)) throw new ArgumentException("Set CROSSOVER_PLAYER_PATH");
            PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64, new[] { GraphicsDeviceType.Direct3D12, GraphicsDeviceType.Direct3D11 });
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64, false);
            PlayerSettings.SetScriptingBackend(UnityEditor.Build.NamedBuildTarget.Standalone, ScriptingImplementation.Mono2x);
            PlayerSettings.graphicsJobs = false;
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
                locationPathName = output, target = BuildTarget.StandaloneWindows64, options = BuildOptions.Development | (Environment.GetEnvironmentVariable("CROSSOVER_AUTOCONNECT") == "1" ? BuildOptions.ConnectWithProfiler : BuildOptions.None) });
            if (report.summary.result != BuildResult.Succeeded) throw new Exception("Player build failed: " + report.summary.result);
            File.WriteAllText(output + ".build.json", JsonUtility.ToJson(new Receipt {
                unity=Application.unityVersion, result=report.summary.result.ToString(), graphicsJobs=PlayerSettings.graphicsJobs,
                autoconnect=Environment.GetEnvironmentVariable("CROSSOVER_AUTOCONNECT")=="1",
                sourceIdentity=Resources.Load<TextAsset>("crossover-source-identity").text.Trim() },true));
        }
    }
}
