using HlslPerf.Core;
using Xunit;

namespace HlslPerf.Core.Tests;

public sealed class TuningCheckpointTests
{
    [Fact]
    public void CheckpointRoundTripsAndRejectsKernelDrift()
    {
        string root = Path.Combine(Path.GetTempPath(), "hlslperf-checkpoint-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "checkpoint.json");
            DeviceFingerprint device = Device();
            CandidateResult candidate = Candidate("candidate-a");
            string workloadImplementation = new string('c', 64);
            TuningCheckpointStore.Write(
                path,
                new TuningCheckpoint(
                    TuningCheckpointStore.Schema,
                    HlslPerfSdk.Version,
                    HlslPerfSdk.MeasurementProtocol,
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow,
                    false,
                    new string('a', 64),
                    new string('b', 64),
                    device,
                    "test-v1",
                    workloadImplementation,
                    KernelAbiV1.Id,
                    new Dictionary<string, CandidateResult> { [candidate.CandidateId] = candidate }));

            TuningCheckpoint checkpoint = TuningCheckpointStore.LoadAndValidate(
                path,
                new string('a', 64),
                new string('b', 64),
                device,
                "test-v1",
                workloadImplementation,
                KernelAbiV1.Id,
                new HashSet<string> { "candidate-a" });

            Assert.Single(checkpoint.CompletedCandidates);
            Assert.Throws<InvalidDataException>(() => TuningCheckpointStore.LoadAndValidate(
                path,
                new string('a', 64),
                new string('c', 64),
                device,
                "test-v1",
                workloadImplementation,
                KernelAbiV1.Id,
                new HashSet<string> { "candidate-a" }));
            Assert.Throws<InvalidDataException>(() => TuningCheckpointStore.LoadAndValidate(
                path,
                new string('a', 64),
                new string('b', 64),
                device,
                "test-v1",
                new string('d', 64),
                KernelAbiV1.Id,
                new HashSet<string> { "candidate-a" }));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static CandidateResult Candidate(string id)
    {
        DistributionSummary timing = new(1, 1, 1, 1, 1, 0, 0);
        return new CandidateResult(
            id,
            new Dictionary<string, int>(),
            true,
            null,
            new CorrectnessResult(true, new string('a', 64), new string('a', 64), "correct"),
            [1],
            1,
            timing,
            1,
            true,
            null);
    }

    private static DeviceFingerprint Device() => new(
        "GPU",
        1,
        2,
        3,
        4,
        "luid",
        "driver",
        "D3D12",
        "6_0",
        "compiler",
        "Windows");
}
