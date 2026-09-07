using System.Collections.Generic;
using NUnit.Framework;

namespace EdwinLiu.HlslPerf.Tests
{
    public sealed class HlslPerfProfileConsumerTests
    {
        [Test]
        public void HistoricalProfilesRequireExplicitOptIn()
        {
            var runtime = new HlslPerfRuntimeFingerprint(4098, 30033, "32.0.1", "D3D12", "6_0");
            Assert.That(HlslPerfProfileConsumer.TryResolveJson(ProfileJson("32.0.1"), runtime,
                "reduction-u32-v1", Hash('a'), Hash('b'), HlslPerfCompatibilityPolicy.ExactDeviceAndDriver,
                out HlslPerfResolvedProfile resolved), Is.False);
            StringAssert.Contains("Historical", resolved.Reason);
        }

        [Test]
        public void ConfirmedProfileRequiresEveryProjectOwnedIdentity()
        {
            var runtime = new HlslPerfRuntimeFingerprint(4098, 30033, "32.0.1", "D3D12", "6_0");
            var identity = new HlslPerfDeploymentIdentity {
                WorkloadImplementationSha256 = Hash('c'), KernelAbiVersion = "hlslperf.raw-buffer.v1",
                ExecutionIdentitySha256 = Hash('d'), ConfirmationSha256 = Hash('e'), CandidateId = "winner",
                DefinesSha256 = HlslPerfProfileConsumer.ComputeDefinesSha256(new[] {
                    new HlslPerfDefineValue { name = "HLSLPERF_GROUP_SIZE", value = 256 },
                    new HlslPerfDefineValue { name = "HLSLPERF_ELEMENTS_PER_THREAD", value = 8 } }) };
            string json = ProfileJson("32.0.1").Replace("\"schemaVersion\":\"2.0\"", "\"schemaVersion\":\"3.0\"");
            json = json.Substring(0, json.Length - 1) + ",\"measurementProtocol\":\"" + HlslPerfProfileConsumer.PairedProtocol +
                "\",\"evidenceStatus\":\"independently-confirmed\",\"workloadImplementationSha256\":\"" + Hash('c') +
                "\",\"executionIdentitySha256\":\"" + Hash('d') + "\",\"confirmationSha256\":\"" + Hash('e') +
                "\",\"definesSha256\":\"" + identity.DefinesSha256 + "\",\"medianGpuMilliseconds\":1,\"p95GpuMilliseconds\":1.1}";
            Assert.That(HlslPerfProfileConsumer.TryResolveJson(json, runtime, "reduction-u32-v1", Hash('a'), Hash('b'),
                HlslPerfCompatibilityPolicy.ExactDeviceAndDriver, out HlslPerfResolvedProfile resolved, identity), Is.True, resolved.Reason);
            Assert.That(HlslPerfProfileConsumer.TryResolveJson(json.Replace("\"value\":256", "\"value\":128"), runtime,
                "reduction-u32-v1", Hash('a'), Hash('b'), HlslPerfCompatibilityPolicy.ExactDeviceAndDriver, out resolved, identity), Is.False);
            StringAssert.Contains("defines", resolved.Reason);
            Assert.That(HlslPerfProfileConsumer.TryResolveJson(json.Replace("\"winner\"", "\"substitute\""), runtime,
                "reduction-u32-v1", Hash('a'), Hash('b'), HlslPerfCompatibilityPolicy.ExactDeviceAndDriver, out resolved, identity), Is.False);
            StringAssert.Contains("candidate", resolved.Reason);
            identity.ConfirmationSha256 = Hash('f');
            Assert.That(HlslPerfProfileConsumer.TryResolveJson(json, runtime, "reduction-u32-v1", Hash('a'), Hash('b'),
                HlslPerfCompatibilityPolicy.ExactDeviceAndDriver, out resolved, identity), Is.False);
            StringAssert.Contains("confirmation", resolved.Reason);
            identity.ConfirmationSha256 = Hash('e');
            identity.WorkloadImplementationSha256 = Hash('f');
            Assert.That(HlslPerfProfileConsumer.TryResolveJson(json, runtime, "reduction-u32-v1", Hash('a'), Hash('b'),
                HlslPerfCompatibilityPolicy.ExactDeviceAndDriver, out resolved, identity), Is.False);
            StringAssert.Contains("implementation", resolved.Reason);
        }

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
                out HlslPerfResolvedProfile resolved, allowHistorical: true);
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
                out HlslPerfResolvedProfile resolved, allowHistorical: true);

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
                out HlslPerfResolvedProfile resolved, allowHistorical: true);

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
                out HlslPerfResolvedProfile resolved, allowHistorical: true);

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
                out HlslPerfResolvedProfile resolved, allowHistorical: true);

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

