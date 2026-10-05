using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace HlslPerf.FluidBenchmark
{
    [Serializable]
    public sealed class ValidationReceipt
    {
        public string schema = "hlslperf.fluid-scan.correctness.v1";
        public string status = "failed";
        public string arm, device, api, failure;
        public int scanCases, sortCases, spatialOffsetCases;
        public bool fullOutputValidated, timed = false;
    }

    public static class FluidScanValidation
    {
        public static ValidationReceipt Run(ScanArm arm)
        {
            var receipt = new ValidationReceipt { arm = arm.ToString(), device = SystemInfo.graphicsDeviceName,
                api = SystemInfo.graphicsDeviceType.ToString() };
            string fatalLog = null;
            Application.LogCallback captureLog = (message, trace, type) =>
            {
                if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)
                    fatalLog = message + "\n" + trace;
            };
            Application.logMessageReceived += captureLog;
            try
            {
                if (!SystemInfo.supportsComputeShaders || SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D12)
                    throw new NotSupportedException("This comparison requires a graphics-enabled D3D12 Player.");
                // A single owner is reused through growth/shrink, preserving scratch-reset coverage.
                using (var scan = ScanBackends.Create(arm))
                using (var cmd = new CommandBuffer())
                {
                    int[] sizes = { 0, 1, 2, 3, 4, 511, 512, 513, 3071, 3072, 3073, 4095, 4096, 4097, 65537, 262147, 3, 4097, 1, 0 };
                    foreach (int count in sizes)
                    for (int pattern = 0; pattern < 4; pattern++)
                    {
                        uint[] input = new uint[Math.Max(1, count) + 4];
                        for (int i = 0; i < count; i++) input[i] = Pattern(i, pattern);
                        for (int i = count; i < input.Length; i++) input[i] = 0xa5a5a5a5;
                        uint[] expected = (uint[])input.Clone();
                        uint prefix = 0;
                        for (int i = 0; i < count; i++) { expected[i] = prefix; prefix = unchecked(prefix + input[i]); }
                        using (var buffer = new ComputeBuffer(input.Length, 4))
                        {
                            buffer.SetData(input); cmd.Clear(); scan.Record(cmd, buffer, count);
                            Graphics.ExecuteCommandBuffer(cmd);
                            uint[] actual = new uint[input.Length]; buffer.GetData(actual);
                            for (int i = 0; i < actual.Length; i++)
                                if (actual[i] != expected[i]) throw new InvalidOperationException("Scan mismatch: N=" + count + ", pattern=" + pattern + ", index=" + i);
                        }
                        receipt.scanCases++;
                    }
                }
                using (var cmd = new CommandBuffer())
                {
                    var sort = new Seb.GPUSorting.GPUCountSort();
                    var offsetBuilder = new Seb.Helpers.Internal.SpatialOffsetCalculator();
                    try
                    {
                        foreach (int count in new[] { 1, 3, 513, 4097, 65537, 3 })
                        for (int pattern = 0; pattern < 3; pattern++)
                        using (var indices = new ComputeBuffer(count, 4))
                        using (var keys = new ComputeBuffer(count, 4))
                        using (var offsets = new ComputeBuffer(count, 4))
                        {
                            uint[] source = new uint[count];
                            for (int i = 0; i < count; i++) source[i] = pattern == 0 ? 0u : pattern == 1 ? (uint)(count - 1) : unchecked((uint)i * 2654435761u) % (uint)count;
                            keys.SetData(source); cmd.Clear(); sort.Record(cmd, indices, keys, (uint)(count - 1), arm);
                            offsetBuilder.Record(cmd, keys, offsets);
                            Graphics.ExecuteCommandBuffer(cmd);
                            uint[] actualKeys = new uint[count], actualIndices = new uint[count];
                            keys.GetData(actualKeys); indices.GetData(actualIndices);
                            bool[] seen = new bool[count];
                            uint[] actualOffsets = new uint[count], expectedOffsets = new uint[count];
                            for (int i = 0; i < count; i++) expectedOffsets[i] = (uint)count;
                            offsets.GetData(actualOffsets);
                            for (int i = 0; i < count; i++)
                            {
                                uint index = actualIndices[i];
                                if (index >= count || seen[index] || actualKeys[i] != source[index] ||
                                    (i > 0 && actualKeys[i - 1] > actualKeys[i]))
                                    throw new InvalidOperationException("Sort/permutation mismatch: N=" + count + ", index=" + i);
                                seen[index] = true;
                                if (i == 0 || actualKeys[i - 1] != actualKeys[i]) expectedOffsets[actualKeys[i]] = (uint)i;
                            }
                            for (int i = 0; i < count; i++)
                                if (actualOffsets[i] != expectedOffsets[i])
                                    throw new InvalidOperationException("Spatial offset mismatch: N=" + count + ", key=" + i);
                            receipt.sortCases++;
                            receipt.spatialOffsetCases++;
                        }
                    }
                    finally { sort.Release(); }
                }
                if (fatalLog != null) throw new InvalidOperationException("GPU validation logged an error: " + fatalLog);
                receipt.status = "passed"; receipt.fullOutputValidated = true;
            }
            catch (Exception error) { receipt.failure = error.ToString(); }
            finally { Application.logMessageReceived -= captureLog; }
            return receipt;
        }

        private static uint Pattern(int i, int pattern)
        {
            switch (pattern)
            {
                case 0: return 0;
                case 1: return 1;
                case 2: return i % 2 == 0 ? uint.MaxValue : 1;
                default: return unchecked((uint)i * 747796405u + 2891336453u);
            }
        }
    }
}
