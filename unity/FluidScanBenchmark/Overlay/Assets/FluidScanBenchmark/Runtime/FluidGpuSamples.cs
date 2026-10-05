using UnityEngine.Profiling;
using UnityEngine.Rendering;

namespace HlslPerf.FluidBenchmark
{
    public static class FluidGpuSamples
    {
        public const string ScanName = "FluidBenchmark.Scan.Complete";
        public const string SortName = "FluidBenchmark.CountSort.Complete";
        public const string SpatialName = "FluidBenchmark.SpatialHash.Complete";
        public const string SimulationName = "FluidBenchmark.Simulation.Complete";
        public static CustomSampler Scan, Sort, Spatial, Simulation;

        public static void Enable()
        {
            Scan = CustomSampler.Create(ScanName, true);
            Sort = CustomSampler.Create(SortName, true);
            Spatial = CustomSampler.Create(SpatialName, true);
            Simulation = CustomSampler.Create(SimulationName, true);
            foreach (var sampler in new[] { Scan, Sort, Spatial, Simulation }) sampler.GetRecorder().enabled = true;
        }
        public static void Begin(CommandBuffer cmd, CustomSampler sampler) { if (sampler != null) cmd.BeginSample(sampler); }
        public static void End(CommandBuffer cmd, CustomSampler sampler) { if (sampler != null) cmd.EndSample(sampler); }
    }
}
