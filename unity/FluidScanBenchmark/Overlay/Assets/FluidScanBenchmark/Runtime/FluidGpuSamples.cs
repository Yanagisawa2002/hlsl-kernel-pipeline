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
        public const string CoreName = "FluidBenchmark.Scan.Core";
        public static CustomSampler Scan, Sort, Spatial, Simulation, Core;
        public static bool Recording = true;

        public static void Enable()
        {
            Profiler.SetAreaEnabled(ProfilerArea.GPU, true);
            Scan = CustomSampler.Create(ScanName, true);
            Sort = CustomSampler.Create(SortName, true);
            Spatial = CustomSampler.Create(SpatialName, true);
            Simulation = CustomSampler.Create(SimulationName, true);
            Core = CustomSampler.Create(CoreName, true);
            foreach (var sampler in new[] { Scan, Sort, Spatial, Simulation, Core })
            {
                var recorder = sampler.GetRecorder();
                recorder.CollectFromAllThreads(); recorder.enabled = true;
            }
        }
        private static int Metric(CustomSampler sampler) { return sampler == Scan ? 0 : sampler == Sort ? 1 : sampler == Spatial ? 2 : sampler == Simulation ? 3 : 4; }
        public static void Begin(CommandBuffer cmd, CustomSampler sampler)
        {
            if (sampler == null || !Recording) return;
            cmd.BeginSample(sampler); FluidNativeTiming.Active?.Record(cmd, Metric(sampler), false);
        }
        public static void End(CommandBuffer cmd, CustomSampler sampler)
        {
            if (sampler == null || !Recording) return;
            FluidNativeTiming.Active?.Record(cmd, Metric(sampler), true); cmd.EndSample(sampler);
        }
    }
}
