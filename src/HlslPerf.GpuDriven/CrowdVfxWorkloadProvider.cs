using HlslPerf.Core;

namespace HlslPerf.GpuDriven;

/// <summary>
/// External-workload entry point. The assembly can be passed directly to
/// hlslperf through --plugin without changing the core workload pack.
/// </summary>
public sealed class CrowdVfxWorkloadProvider : IKernelWorkloadProvider
{
    public const string CrowdVfxWorkloadId = "crowd-vfx-gpu-driven-v1";

    public IReadOnlyCollection<string> WorkloadIds { get; } = [CrowdVfxWorkloadId];

    public IKernelWorkload Create(string workloadId) => workloadId == CrowdVfxWorkloadId
        ? new CrowdVfxWorkload()
        : throw new InvalidDataException($"Unknown GPU-driven workload '{workloadId}'.");
}
