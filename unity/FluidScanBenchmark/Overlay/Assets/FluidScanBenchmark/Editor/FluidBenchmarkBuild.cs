using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
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
