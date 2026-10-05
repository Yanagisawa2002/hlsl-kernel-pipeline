using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.Rendering;

namespace HlslPerf.FluidBenchmark.Editor
{
    // Explicit build entry point only. Importing the project does not build or run it.
    public static class FluidBenchmarkBuild
    {
        public static void Build()
        {
            string[] args = Environment.GetCommandLineArgs();
            int index = Array.IndexOf(args, "--fluid-build-output");
            if (index < 0 || index + 1 >= args.Length) throw new ArgumentException("--fluid-build-output is required.");
            string target = Path.GetFullPath(args[index + 1]);
            if (File.Exists(target) || Directory.Exists(Path.GetDirectoryName(target)))
                throw new IOException("Choose an executable inside a new output directory.");
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64, false);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64, new[] { GraphicsDeviceType.Direct3D12 });
            PlayerSettings.enableFrameTimingStats = true;
            // Unity disables GPU Recorder when Graphics Jobs are enabled. Common to all arms.
            PlayerSettings.graphicsJobs = false;
            // The author's blur helpers locate these by name, so scene references do not retain them.
            var graphics = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/GraphicsSettings.asset")[0]);
            var included = graphics.FindProperty("m_AlwaysIncludedShaders");
            foreach (string name in new[] { "Hidden/GaussSmooth", "Hidden/BilateralFilter1D", "Hidden/BilateralFilter2D" })
            {
                Shader shader = Shader.Find(name);
                if (shader == null) throw new InvalidOperationException("Missing scene blur shader: " + name);
                bool exists = false;
                for (int i = 0; i < included.arraySize; ++i)
                    if (included.GetArrayElementAtIndex(i).objectReferenceValue == shader) exists = true;
                if (!exists)
                {
                    int indexToAdd = included.arraySize++;
                    included.GetArrayElementAtIndex(indexToAdd).objectReferenceValue = shader;
                }
            }
            graphics.ApplyModifiedPropertiesWithoutUndo();
            AssetDatabase.SaveAssets();
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            BuildReport report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { "Assets/Scenes/Fluid ScreenSpace 2.unity" },
                locationPathName = target,
                target = BuildTarget.StandaloneWindows64,
                options = BuildOptions.Development | BuildOptions.StrictMode
            });
            if (report.summary.result != BuildResult.Succeeded)
                throw new InvalidOperationException("Fluid Player build failed: " + report.summary.result);
        }
    }
}
