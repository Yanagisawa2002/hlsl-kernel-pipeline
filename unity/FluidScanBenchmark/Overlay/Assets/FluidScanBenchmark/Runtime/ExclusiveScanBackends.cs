using System;
using UnityEngine;
using UnityEngine.Rendering;
using GPUPrefixSums.Runtime;

namespace HlslPerf.FluidBenchmark
{
    public enum ScanArm { Original, HlslWaveTiled, GpuPrefixSumsRts }

    public interface IExclusiveScan : IDisposable
    {
        void Record(CommandBuffer cmd, ComputeBuffer values, int count);
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
            public void Record(CommandBuffer cmd, ComputeBuffer values, int count)
            {
                Check(values, count);
                scan.Record(cmd, values, count);
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

            public void Record(CommandBuffer cmd, ComputeBuffer values, int count)
            {
                Check(values, count);
                if (count == 0) return;
                Ensure(count);
                RecordCore(cmd, values, count);
            }

            protected abstract void Ensure(int count);
            protected abstract void RecordCore(CommandBuffer cmd, ComputeBuffer values, int count);

            protected void Bridge(CommandBuffer cmd, string entry, ComputeBuffer values, int count, int dispatchCount)
            {
                int kernel = bridge.FindKernel(entry);
                cmd.SetComputeIntParam(bridge, "LogicalCount", count);
                cmd.SetComputeIntParam(bridge, "PaddedCount", capacity);
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

            public WaveTiledScan()
            {
                if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D12)
                    throw new NotSupportedException("HLSL wave-tiled requires D3D12, DXC, SM6.6 and fixed wave32 support. No fallback is substituted.");
                shader = Load("FluidWaveTiled", "ResetWaveTiledState", "SinglePassScanWaveTiled");
                reset = shader.FindKernel("ResetWaveTiledState");
                scan = shader.FindKernel("SinglePassScanWaveTiled");
            }

            protected override void Ensure(int count)
            {
                if (count <= capacity) return;
                ReleaseData(); scratch?.Release(); capacity = count;
                input = new ComputeBuffer(capacity, 4, ComputeBufferType.Raw);
                output = new ComputeBuffer(capacity, 4, ComputeBufferType.Raw);
                scratch = new ComputeBuffer(2 + 3 * ((capacity + 4095) / 4096), 4, ComputeBufferType.Raw);
            }

            protected override void RecordCore(CommandBuffer cmd, ComputeBuffer values, int count)
            {
                Bridge(cmd, "PackRaw", values, count, count);
                int blocks = (count + 4095) / 4096;
                cmd.SetComputeIntParam(shader, "ElementCount", count);
                cmd.SetComputeIntParam(shader, "ElementsPerBlock", 4096);
                cmd.SetComputeIntParam(shader, "LogicalBlockCount", blocks);
                cmd.SetComputeBufferParam(shader, reset, "Output0", scratch);
                cmd.DispatchCompute(shader, reset, 1, 1, 1);
                cmd.SetComputeBufferParam(shader, scan, "Input0", input);
                cmd.SetComputeBufferParam(shader, scan, "Output0", output);
                cmd.SetComputeBufferParam(shader, scan, "Output1", scratch);
                // One graphics queue. Unity owns the dispatch dependencies/UAV barriers.
                cmd.DispatchCompute(shader, scan, Math.Min(blocks, 256), 1, 1);
                Bridge(cmd, "UnpackRaw", values, count, count);
            }

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
                Bridge(cmd, "PackVectors", values, count, capacity / 4);
                rts.PrefixSumExclusive(cmd, capacity, input, output, reductions);
                Bridge(cmd, "UnpackVectors", values, count, count);
            }

            public override void Dispose()
            {
                reductions?.Release(); UnityEngine.Object.Destroy(shader); base.Dispose();
            }
        }
    }
}
