using System.Diagnostics;
using HlslPerf.Core;
using Vortice.Direct3D12;

namespace HlslPerf.D3D12;

public sealed partial class D3D12Tuner
{
    public sealed partial class UnifiedSession
    {
        /// <summary>
        /// Executes one already-prepared operation with optional per-pass root-constant overrides,
        /// then reads one output buffer for interactive presentation. This is intentionally separate
        /// from benchmark timing: the synchronous readback/presentation contract is a demo path.
        /// </summary>
        public byte[] ExecuteLiveFrame(
            string outputResource,
            IReadOnlyDictionary<string, uint[]>? constantOverrides,
            out UnifiedLiveFrameTiming timing)
        {
            if (string.IsNullOrWhiteSpace(outputResource))
                throw new ArgumentException("A live output resource is required.", nameof(outputResource));

            GpuBuffer output = resources.Get(outputResource);
            using ID3D12Resource readback = owner.device.CreateCommittedResource(
                HeapType.Readback,
                ResourceDescription.Buffer((ulong)output.ByteLength, ResourceFlags.None, 0),
                ResourceStates.CopyDest,
                null);

            long cpuRecordStart = Stopwatch.GetTimestamp();
            Stamp(0);
            foreach (UnifiedPass pass in plan.Passes)
            {
                UnifiedPass active = pass;
                if (constantOverrides is not null && constantOverrides.TryGetValue(pass.Name, out uint[]? constants))
                {
                    if (constants.Length > 8)
                        throw new InvalidDataException($"Live override for '{pass.Name}' exceeds eight root constants.");
                    active = pass with { Constants = constants };
                }
                Execute(active);
            }
            Stamp(1);

            owner.Transition(output, ResourceStates.CopySource);
            owner.commandList.CopyResource(readback, output.Resource);
            Stamp(2);
            double cpuRecordMilliseconds = Stopwatch.GetElapsedTime(cpuRecordStart).TotalMilliseconds;

            ulong[] timestamps = Resolve(3, out UnifiedSubmissionTiming submission);
            long cpuCopyStart = Stopwatch.GetTimestamp();
            byte[] bytes = readback.Map<byte>(0, output.ByteLength).ToArray();
            readback.Unmap(0);
            double cpuCopyMilliseconds = Stopwatch.GetElapsedTime(cpuCopyStart).TotalMilliseconds;

            timing = new UnifiedLiveFrameTiming(
                Elapsed(timestamps, 0, 1),
                Elapsed(timestamps, 1, 2),
                cpuRecordMilliseconds,
                cpuCopyMilliseconds,
                submission);
            return bytes;
        }
    }
}

public sealed record UnifiedLiveFrameTiming(
    double GpuRenderMilliseconds,
    double GpuReadbackMilliseconds,
    double CpuRecordMilliseconds,
    double CpuCopyMilliseconds,
    UnifiedSubmissionTiming Submission)
{
    public double CpuSynchronizedMilliseconds =>
        CpuRecordMilliseconds + Submission.CpuCloseMilliseconds + Submission.CpuSubmitMilliseconds +
        Submission.CpuFenceWaitMilliseconds + Submission.CpuResetMilliseconds + CpuCopyMilliseconds;
}
