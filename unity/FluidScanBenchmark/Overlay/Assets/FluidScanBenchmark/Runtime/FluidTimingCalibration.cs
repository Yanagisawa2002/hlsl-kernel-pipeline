using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;

namespace HlslPerf.FluidBenchmark
{
    // Warmup-only timestamp association probe, independent of simulation buffers.
    public sealed class FluidTimingCalibration : IDisposable
    {
        private readonly CustomSampler sampler = CustomSampler.Create("FluidBenchmark.GpuDelayProbe", true);
        private readonly ComputeShader shader = ScanBackends.Load("FluidScanBridge", "PackRaw");
        private readonly ComputeBuffer values = new ComputeBuffer(256, 4);
        private readonly ComputeBuffer raw = new ComputeBuffer(256, 4, ComputeBufferType.Raw);
        private readonly CommandBuffer cmd = new CommandBuffer { name = "Fluid GPU timing warmup probe" };
        private readonly Dictionary<int, int> expected = new Dictionary<int, int>();
        public readonly Receipt result = new Receipt();
        private bool disposed;

        public FluidTimingCalibration()
        {
            values.SetData(new uint[256]);
            var recorder = sampler.GetRecorder();
            recorder.CollectFromAllThreads(); recorder.enabled = true;
        }

        public void Tick(int frame, int first)
        {
            int age = frame - first;
            if (age >= 12 && age < 72)
            {
                var recorder = sampler.GetRecorder();
                result.observations.Add(new Observation { observedFrame = frame, gpuBlocks = recorder.gpuSampleBlockCount,
                    gpuNanoseconds = recorder.gpuElapsedNanoseconds });
            }
            if (age < 72)
            {
                // A fixed, aperiodic 1..4 block sequence distinguishes candidate frame offsets.
                uint mixed = unchecked((uint)age * 747796405u + 2891336453u);
                mixed = unchecked(((mixed >> (int)((mixed >> 28) + 4)) ^ mixed) * 277803737u);
                int blocks = 1 + (int)(((mixed >> 22) ^ mixed) & 3);
                expected.Add(frame, blocks);
                result.emissions.Add(new Emission { frame = frame, blocks = blocks });
                cmd.Clear();
                int kernel = shader.FindKernel("PackRaw");
                cmd.SetComputeIntParam(shader, "LogicalCount", 256);
                cmd.SetComputeBufferParam(shader, kernel, "Values", values);
                cmd.SetComputeBufferParam(shader, kernel, "RawValues", raw);
                for (int i = 0; i < blocks; ++i)
                {
                    cmd.BeginSample(sampler); cmd.DispatchCompute(shader, kernel, 1, 1, 1); cmd.EndSample(sampler);
                }
                Graphics.ExecuteCommandBuffer(cmd);
            }
            if (age == 96)
            {
                int matches = 0;
                for (int delay = 0; delay <= 8; ++delay)
                {
                    bool equal = result.observations.Count >= 48;
                    foreach (var row in result.observations)
                        if (!expected.TryGetValue(row.observedFrame - delay, out int count) ||
                            row.gpuBlocks != count || row.gpuNanoseconds <= 0) equal = false;
                    if (equal) { result.delayFrames = delay; ++matches; }
                }
                result.validated = matches == 1;
                result.status = result.validated ? "unique_constant_delay_observed_in_warmup" : "unresolved_no_unique_constant_delay";
                Dispose();
            }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true; sampler.GetRecorder().enabled = false;
            cmd.Release(); values.Release(); raw.Release(); UnityEngine.Object.Destroy(shader);
        }

        [Serializable] public sealed class Receipt
        {
            public string schema = "hlslperf.fluid-scan.gpu-delay.v1", status = "warmup_not_completed";
            public bool validated;
            public int delayFrames = 3;
            public List<Emission> emissions = new List<Emission>();
            public List<Observation> observations = new List<Observation>();
        }
        [Serializable] public sealed class Emission { public int frame, blocks; }
        [Serializable] public sealed class Observation { public int observedFrame, gpuBlocks; public long gpuNanoseconds; }
    }
}
