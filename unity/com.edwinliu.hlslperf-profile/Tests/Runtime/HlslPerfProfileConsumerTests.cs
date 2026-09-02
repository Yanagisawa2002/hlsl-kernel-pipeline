using System.Collections.Generic;
using NUnit.Framework;

namespace EdwinLiu.HlslPerf.Tests
{
    public sealed class HlslPerfProfileConsumerTests
    {
        [Test]
        public void ExactProfileResolvesAndAppliesDefines()
        {
            HlslPerfRuntimeFingerprint runtime = new HlslPerfRuntimeFingerprint(4098, 30033, "32.0.1", "D3D12", "6_0");

            bool success = HlslPerfProfileConsumer.TryResolveJson(
                ProfileJson("32.0.1"),
                runtime,
                "reduction-u32-v1",
                Hash('a'),
                Hash('b'),
                HlslPerfCompatibilityPolicy.ExactDeviceAndDriver,
                out HlslPerfResolvedProfile resolved);
            RecordingSink sink = new RecordingSink();
            HlslPerfProfileConsumer.Apply(resolved, sink);

            Assert.That(success, Is.True, resolved.Reason);
            Assert.That(sink.Values["HLSLPERF_GROUP_SIZE"], Is.EqualTo(256));
            Assert.That(sink.Values["HLSLPERF_ELEMENTS_PER_THREAD"], Is.EqualTo(8));
        }

        [Test]
        public void StrictPolicyRejectsDriverDrift()
        {
            HlslPerfRuntimeFingerprint runtime = new HlslPerfRuntimeFingerprint(4098, 30033, "32.0.2", "D3D12", "6_0");

            bool success = HlslPerfProfileConsumer.TryResolveJson(
                ProfileJson("32.0.1"),
                runtime,
                "reduction-u32-v1",
                Hash('a'),
                Hash('b'),
                HlslPerfCompatibilityPolicy.ExactDeviceAndDriver,
                out HlslPerfResolvedProfile resolved);

            Assert.That(success, Is.False);
            StringAssert.Contains("driver", resolved.Reason.ToLowerInvariant());
        }

        [Test]
        public void WorkloadMismatchIsNeverRelaxed()
        {
            HlslPerfRuntimeFingerprint runtime = new HlslPerfRuntimeFingerprint(1, 2, "any", "D3D12", "6_0");

            bool success = HlslPerfProfileConsumer.TryResolveJson(
                ProfileJson("32.0.1"),
                runtime,
                "exclusive-scan-u32-v1",
                Hash('a'),
                Hash('b'),
                HlslPerfCompatibilityPolicy.BackendAndShaderModel,
                out HlslPerfResolvedProfile resolved);

            Assert.That(success, Is.False);
            StringAssert.Contains("workload", resolved.Reason.ToLowerInvariant());
        }

        [Test]
        public void KernelDriftIsNeverRelaxed()
        {
            HlslPerfRuntimeFingerprint runtime = new HlslPerfRuntimeFingerprint(1, 2, "any", "D3D12", "6_0");

            bool success = HlslPerfProfileConsumer.TryResolveJson(
                ProfileJson("any"),
                runtime,
                "reduction-u32-v1",
                Hash('a'),
                Hash('c'),
                HlslPerfCompatibilityPolicy.BackendAndShaderModel,
                out HlslPerfResolvedProfile resolved);

            Assert.That(success, Is.False);
            StringAssert.Contains("kernel", resolved.Reason.ToLowerInvariant());
        }

        [Test]
        public void ManifestDriftIsNeverRelaxed()
        {
            HlslPerfRuntimeFingerprint runtime = new HlslPerfRuntimeFingerprint(1, 2, "any", "D3D12", "6_0");

            bool success = HlslPerfProfileConsumer.TryResolveJson(
                ProfileJson("any"),
                runtime,
                "reduction-u32-v1",
                Hash('c'),
                Hash('b'),
                HlslPerfCompatibilityPolicy.BackendAndShaderModel,
                out HlslPerfResolvedProfile resolved);

            Assert.That(success, Is.False);
            StringAssert.Contains("manifest", resolved.Reason.ToLowerInvariant());
        }

        private static string ProfileJson(string driver)
        {
            return "{\"schemaVersion\":\"2.0\",\"compatibilityKey\":\"abc\",\"candidateId\":\"winner\"," +
                   "\"workloadId\":\"reduction-u32-v1\",\"kernelAbiVersion\":\"hlslperf.raw-buffer.v1\"," +
                   "\"manifestSha256\":\"" + Hash('a') + "\",\"kernelSha256\":\"" + Hash('b') + "\"," +
                   "\"device\":{\"vendorId\":4098,\"deviceId\":30033,\"driverVersion\":\"" + driver +
                   "\",\"backend\":\"D3D12\",\"shaderModel\":\"6_0\"}," +
                   "\"defineValues\":[{\"name\":\"HLSLPERF_GROUP_SIZE\",\"value\":256}," +
                   "{\"name\":\"HLSLPERF_ELEMENTS_PER_THREAD\",\"value\":8}]}";
        }

        private static string Hash(char value) { return new string(value, 64); }

        private sealed class RecordingSink : IHlslPerfDefineSink
        {
            public readonly Dictionary<string, int> Values = new Dictionary<string, int>();
            public void SetInt(string name, int value) { Values[name] = value; }
        }
    }
}
