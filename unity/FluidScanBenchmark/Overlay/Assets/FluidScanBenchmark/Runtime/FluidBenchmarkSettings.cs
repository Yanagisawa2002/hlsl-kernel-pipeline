using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace HlslPerf.FluidBenchmark
{
    [Serializable]
    public sealed class FluidBenchmarkSettings
    {
        public static FluidBenchmarkSettings Active { get; private set; }
        public static bool Enabled => Active != null && !Active.validateOnly;
        public ScanArm arm;
        public bool validateOnly, captureOnly;
        public bool nativeGpuTiming;
        public int seed = 42, warmupFrames = 120, measureFrames = 600, spawnDensity = 600;
        public float fixedDeltaTime = 1f / 60f;
        public string outputDirectory;

        public static FluidBenchmarkSettings Parse(string[] args)
        {
            bool benchmark = Array.IndexOf(args, "--fluid-benchmark") >= 0;
            bool validate = Array.IndexOf(args, "--fluid-validate-only") >= 0;
            bool capture = Array.IndexOf(args, "--fluid-capture") >= 0;
            if (!benchmark && !validate && !capture) return null;
            if ((benchmark ? 1 : 0) + (validate ? 1 : 0) + (capture ? 1 : 0) != 1)
                throw new ArgumentException("Choose exactly one of benchmark, validation-only or capture.");
            var options = new Dictionary<string, string>(StringComparer.Ordinal);
            var known = new HashSet<string> { "--fluid-arm", "--fluid-output", "--fluid-seed", "--fluid-warmup",
                "--fluid-frames", "--fluid-spawn-density", "--fluid-dt", "--fluid-gpu-timing" };
            for (int i = 0; i < args.Length; i++)
            {
                string key = args[i];
                if (!key.StartsWith("--fluid-", StringComparison.Ordinal) || key == "--fluid-benchmark" || key == "--fluid-validate-only" || key == "--fluid-capture") continue;
                if (!known.Contains(key) || i + 1 == args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal) || options.ContainsKey(key))
                    throw new ArgumentException("Unknown, duplicate or missing fluid option: " + key);
                options.Add(key, args[++i]);
            }
            if (!options.TryGetValue("--fluid-arm", out string armName) || !options.TryGetValue("--fluid-output", out string destination))
                throw new ArgumentException("Explicit --fluid-arm and a new --fluid-output directory are required.");
            var settings = new FluidBenchmarkSettings { validateOnly = validate, captureOnly = capture, outputDirectory = Path.GetFullPath(destination) };
            switch (armName)
            {
                case "original": settings.arm = ScanArm.Original; break;
                case "hlsl-wave-tiled": settings.arm = ScanArm.HlslWaveTiled; break;
                case "gpuprefixsums-rts": settings.arm = ScanArm.GpuPrefixSumsRts; break;
                default: throw new ArgumentException("Unknown scan arm: " + armName);
            }
            settings.seed = Integer(options, "--fluid-seed", settings.seed);
            settings.warmupFrames = Integer(options, "--fluid-warmup", settings.warmupFrames);
            settings.measureFrames = Integer(options, "--fluid-frames", settings.measureFrames);
            settings.spawnDensity = Integer(options, "--fluid-spawn-density", settings.spawnDensity);
            if (options.TryGetValue("--fluid-gpu-timing", out string timing))
            {
                if (timing != "recorder" && timing != "d3d12-query") throw new ArgumentException("Unknown GPU timing method.");
                settings.nativeGpuTiming = timing == "d3d12-query";
                if (settings.nativeGpuTiming && (capture || validate)) throw new ArgumentException("GPU timing is only available in benchmark mode.");
            }
            if (options.TryGetValue("--fluid-dt", out string dt)) settings.fixedDeltaTime = float.Parse(dt, CultureInfo.InvariantCulture);
            if (settings.warmupFrames < 10 || settings.measureFrames < 1 || settings.measureFrames > 100000 ||
                settings.spawnDensity < 1 || float.IsNaN(settings.fixedDeltaTime) || float.IsInfinity(settings.fixedDeltaTime) ||
                settings.fixedDeltaTime <= 0 || settings.fixedDeltaTime > 1f / 60f + 1e-8f)
                throw new ArgumentOutOfRangeException(nameof(args), "Invalid warmup, frame count, density or timestep.");
            if (Directory.Exists(settings.outputDirectory) || File.Exists(settings.outputDirectory))
                throw new IOException("The output path already exists; previous evidence is never overwritten.");
            return settings;
        }

        public static void Activate(FluidBenchmarkSettings settings) { Active = settings; }
        private static int Integer(Dictionary<string, string> options, string key, int fallback)
        {
            return options.TryGetValue(key, out string value) ? int.Parse(value, CultureInfo.InvariantCulture) : fallback;
        }
    }
}
