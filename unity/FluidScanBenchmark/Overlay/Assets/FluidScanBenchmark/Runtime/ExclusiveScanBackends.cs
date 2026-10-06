using System;
using UnityEngine;
using UnityEngine.Rendering;
using GPUPrefixSums.Runtime;

namespace HlslPerf.FluidBenchmark
{
    public enum ScanArm { Original, HlslWaveTiled, GpuPrefixSumsRts, HlslWaveTiledDirect }

    public interface IExclusiveScan : IDisposable
    {
        // Direct mode leaves input unchanged and returns a distinct raw result buffer.
        ComputeBuffer Record(CommandBuffer cmd, ComputeBuffer values, int count);
    }

    public static class ScanBackends
    {
        public const int MaximumCount = 256 * 65535;

        public static IExclusiveScan Create(ScanArm arm)
        {
            switch (arm)
            {
                case ScanArm.Original: return new OriginalScan();
                case ScanArm.HlslWaveTiled: return new WaveTiledScan();
                case ScanArm.GpuPrefixSumsRts: return new RtsScan();
                case ScanArm.HlslWaveTiledDirect: return new WaveTiledScan(true);
                default: throw new ArgumentOutOfRangeException(nameof(arm));
            }
        }

        public static void Check(ComputeBuffer values, int count)
        {
            if (values == null || !values.IsValid() || values.stride != 4 || count < 0 ||
                count > values.count || count > MaximumCount)
                throw new ArgumentException("Expected a valid uint32 buffer and an in-range logical count.");
        }

        public static ComputeShader Load(string name, params string[] kernels)
        {
            var asset = Resources.Load<ComputeShader>(name);
            if (asset == null) throw new InvalidOperationException("Missing compute shader: " + name);
            // Each owner gets independent keyword/binding state.
            var shader = UnityEngine.Object.Instantiate(asset);
            foreach (string entry in kernels)
                if (!shader.IsSupported(shader.FindKernel(entry)))
                {
                    UnityEngine.Object.Destroy(shader);
                    throw new NotSupportedException("Unsupported compute kernel: " + name + "/" + entry);
                }
            return shader;
        }

        private sealed class OriginalScan : IExclusiveScan
        {
            private readonly Seb.GPUSorting.Scan scan = new Seb.GPUSorting.Scan();
            public ComputeBuffer Record(CommandBuffer cmd, ComputeBuffer values, int count)
            {
                Check(values, count);
                FluidGpuSamples.Begin(cmd, FluidGpuSamples.Core);
                scan.Record(cmd, values, count);
                FluidGpuSamples.End(cmd, FluidGpuSamples.Core);
                return values;
            }
            public void Dispose() { scan.Release(); }
        }

        // All adapters leave the exclusive result in the caller's original buffer.
        // The pack/unpack passes are inside the complete scan marker, including padding.
        private abstract class AdaptedScan : IExclusiveScan
        {
            protected readonly ComputeShader bridge = Load("FluidScanBridge", "PackRaw", "UnpackRaw", "PackVectors", "UnpackVectors");
            protected ComputeBuffer input, output;
            protected int capacity;

            public ComputeBuffer Record(CommandBuffer cmd, ComputeBuffer values, int count)
            {
                Check(values, count);
                if (count == 0) return values;
                Ensure(count);
                RecordCore(cmd, values, count);
                return Result(values);
            }

            protected virtual ComputeBuffer Result(ComputeBuffer values) { return values; }

            protected abstract void Ensure(int count);
            protected abstract void RecordCore(CommandBuffer cmd, ComputeBuffer values, int count);

            protected void Bridge(CommandBuffer cmd, string entry, ComputeBuffer values, int count, int dispatchCount, int paddedCount = -1)
            {
                int kernel = bridge.FindKernel(entry);
                cmd.SetComputeIntParam(bridge, "LogicalCount", count);
                cmd.SetComputeIntParam(bridge, "PaddedCount", paddedCount < 0 ? capacity : paddedCount);
                cmd.SetComputeBufferParam(bridge, kernel, "Values", values);
                bool raw = entry.EndsWith("Raw", StringComparison.Ordinal);
                bool packing = entry.StartsWith("Pack", StringComparison.Ordinal);
                cmd.SetComputeBufferParam(bridge, kernel, raw ? "RawValues" : "Vectors", packing ? input : output);
                cmd.DispatchCompute(bridge, kernel, (dispatchCount + 255) / 256, 1, 1);
            }

            protected void ReleaseData()
            {
                input?.Release(); output?.Release(); input = output = null;
            }

            public virtual void Dispose()
            {
                ReleaseData(); UnityEngine.Object.Destroy(bridge);
            }
        }

        private sealed class WaveTiledScan : AdaptedScan
        {
            private readonly ComputeShader shader;
            private readonly int reset, scan;
            private ComputeBuffer scratch;
            private readonly bool direct;

            public WaveTiledScan(bool direct = false)
            {
                this.direct = direct;
                if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D12)
                    throw new NotSupportedException("HLSL wave-tiled requires D3D12, DXC and validated native wave32. No fallback is substituted.");
                shader = Load("FluidWaveTiled", "ResetWaveTiledState", "SinglePassScanWaveTiled", "ProbeNativeWaveSize");
                using (var probe = new ComputeBuffer(256, 4, ComputeBufferType.Raw))
                {
                    int kernel = shader.FindKernel("ProbeNativeWaveSize");
                    shader.SetBuffer(kernel, "Output0", probe);
                    shader.Dispatch(kernel, 1, 1, 1);
                    var widths = new uint[256]; probe.GetData(widths);
                    foreach (uint width in widths)
                        if (width != 32) throw new NotSupportedException("Native GPU wave width is not 32. No fallback is substituted.");
                }
                reset = shader.FindKernel("ResetWaveTiledState");
                scan = shader.FindKernel("SinglePassScanWaveTiled");
            }

            protected override void Ensure(int count)
            {
                if (count <= capacity) return;
                ReleaseData(); scratch?.Release(); capacity = count;
                if (!direct) input = new ComputeBuffer(capacity, 4, ComputeBufferType.Raw);
                output = new ComputeBuffer(capacity + 4, 4, ComputeBufferType.Raw);
                scratch = new ComputeBuffer(2 + 3 * ((capacity + 4095) / 4096), 4, ComputeBufferType.Raw);
            }

            protected override void RecordCore(CommandBuffer cmd, ComputeBuffer values, int count)
            {
                if (!direct) Bridge(cmd, "PackRaw", values, count, count);
                FluidGpuSamples.Begin(cmd, FluidGpuSamples.Core);
                int blocks = (count + 4095) / 4096;
                cmd.SetComputeIntParam(shader, "ElementCount", count);
                cmd.SetComputeIntParam(shader, "ElementsPerBlock", 4096);
                cmd.SetComputeIntParam(shader, "LogicalBlockCount", blocks);
                cmd.SetComputeBufferParam(shader, reset, "Output0", scratch);
                cmd.DispatchCompute(shader, reset, 1, 1, 1);
                cmd.SetComputeBufferParam(shader, scan, "Input0", direct ? values : input);
                cmd.SetComputeBufferParam(shader, scan, "Output0", output);
                cmd.SetComputeBufferParam(shader, scan, "Output1", scratch);
                // One graphics queue. Unity owns the dispatch dependencies/UAV barriers.
                cmd.DispatchCompute(shader, scan, Math.Min(blocks, 256), 1, 1);
                FluidGpuSamples.End(cmd, FluidGpuSamples.Core);
                if (!direct) Bridge(cmd, "UnpackRaw", values, count, count);
            }

            protected override ComputeBuffer Result(ComputeBuffer values) { return direct ? output : values; }

            public override void Dispose()
            {
                scratch?.Release(); UnityEngine.Object.Destroy(shader); base.Dispose();
            }
        }

        private sealed class RtsScan : AdaptedScan
        {
            private readonly ComputeShader shader = Load("FluidRts", "Reduce", "Scan", "PropagateExclusive");
            private ReduceThenScan rts;
            private ComputeBuffer reductions;

            protected override void Ensure(int count)
            {
                if (count <= capacity) return;
                ReleaseData(); reductions?.Release(); reductions = null;
                capacity = Math.Max(4, (count + 3) & ~3);
                input = new ComputeBuffer(capacity / 4, 16);
                output = new ComputeBuffer(capacity / 4, 16);
                // Upstream requires size < allocatedSize and size > 1.
                // Execute a zero-padded multiple of four; the first N prefixes are unchanged.
                rts = new ReduceThenScan(shader, capacity + 4, ref reductions);
            }

            protected override void RecordCore(CommandBuffer cmd, ComputeBuffer values, int count)
            {
                // Allocation can stay large after shrinking, but actual work follows this logical length.
                int padded = Math.Max(4, (count + 3) & ~3);
                Bridge(cmd, "PackVectors", values, count, padded / 4, padded);
                FluidGpuSamples.Begin(cmd, FluidGpuSamples.Core);
                rts.PrefixSumExclusive(cmd, padded, input, output, reductions);
                FluidGpuSamples.End(cmd, FluidGpuSamples.Core);
                Bridge(cmd, "UnpackVectors", values, count, count, padded);
            }

            public override void Dispose()
            {
                reductions?.Release(); UnityEngine.Object.Destroy(shader); base.Dispose();
            }
        }
    }
}
