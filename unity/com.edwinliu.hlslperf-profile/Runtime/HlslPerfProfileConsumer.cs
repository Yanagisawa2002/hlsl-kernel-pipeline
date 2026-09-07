using System;
using System.Collections.Generic;
using UnityEngine;

namespace EdwinLiu.HlslPerf
{
    // Project-owned identities from the reviewed deployment artifact, never copied from incoming JSON.
    public sealed class HlslPerfDeploymentIdentity
    {
        public string WorkloadImplementationSha256;
        public string KernelAbiVersion;
        public string ExecutionIdentitySha256;
        public string ConfirmationSha256;
    }

    public enum HlslPerfCompatibilityPolicy
    {
        ExactDeviceAndDriver,
        ExactDevice,
        BackendAndShaderModel
    }

    public sealed class HlslPerfRuntimeFingerprint
    {
        public HlslPerfRuntimeFingerprint(
            long vendorId,
            long deviceId,
            string driverVersion,
            string backend,
            string shaderModel)
        {
            VendorId = vendorId;
            DeviceId = deviceId;
            DriverVersion = driverVersion ?? string.Empty;
            Backend = backend ?? string.Empty;
            ShaderModel = shaderModel ?? string.Empty;
        }

        public long VendorId { get; private set; }
        public long DeviceId { get; private set; }
        public string DriverVersion { get; private set; }
        public string Backend { get; private set; }
        public string ShaderModel { get; private set; }
    }

    public sealed class HlslPerfResolvedProfile
    {
        internal HlslPerfResolvedProfile(
            bool isCompatible,
            string reason,
            string workloadId,
            string candidateId,
            string compatibilityKey,
            HlslPerfDefineValue[] defines,
            double medianGpuMilliseconds,
            double p95GpuMilliseconds)
        {
            IsCompatible = isCompatible;
            Reason = reason;
            WorkloadId = workloadId;
            CandidateId = candidateId;
            CompatibilityKey = compatibilityKey;
            Defines = defines ?? new HlslPerfDefineValue[0];
            MedianGpuMilliseconds = medianGpuMilliseconds;
            P95GpuMilliseconds = p95GpuMilliseconds;
        }

        public bool IsCompatible { get; private set; }
        public string Reason { get; private set; }
        public string WorkloadId { get; private set; }
        public string CandidateId { get; private set; }
        public string CompatibilityKey { get; private set; }
        public HlslPerfDefineValue[] Defines { get; private set; }
        public double MedianGpuMilliseconds { get; private set; }
        public double P95GpuMilliseconds { get; private set; }
    }

    public interface IHlslPerfDefineSink
    {
        void SetInt(string name, int value);
    }

    public static class HlslPerfProfileConsumer
    {
        public const string SupportedSchema = "3.0";
        public const string SupportedAbi = "hlslperf.raw-buffer.v1";
        public const string PairedProtocol = "gpu-paired-abba-independent-confirmation-v2";

        public static bool TryResolve(
            TextAsset profile,
            HlslPerfRuntimeFingerprint runtime,
            string expectedWorkloadId,
            string expectedManifestSha256,
            string expectedKernelSha256,
            HlslPerfCompatibilityPolicy policy,
            out HlslPerfResolvedProfile resolved,
            HlslPerfDeploymentIdentity expectedIdentity = null,
            bool allowHistorical = false)
        {
            if (profile == null)
            {
                resolved = Failure("Profile TextAsset is null.");
                return false;
            }
            return TryResolveJson(
                profile.text,
                runtime,
                expectedWorkloadId,
                expectedManifestSha256,
                expectedKernelSha256,
                policy,
                out resolved, expectedIdentity, allowHistorical);
        }

        public static bool TryResolveJson(
            string json,
            HlslPerfRuntimeFingerprint runtime,
            string expectedWorkloadId,
            string expectedManifestSha256,
            string expectedKernelSha256,
            HlslPerfCompatibilityPolicy policy,
            out HlslPerfResolvedProfile resolved,
            HlslPerfDeploymentIdentity expectedIdentity = null,
            bool allowHistorical = false)
        {
            if (runtime == null)
            {
                resolved = Failure("Runtime fingerprint is null.");
                return false;
            }
            if (string.IsNullOrWhiteSpace(expectedWorkloadId))
            {
                resolved = Failure("Expected workload id is required.");
                return false;
            }
            if (!IsSha256(expectedManifestSha256) || !IsSha256(expectedKernelSha256))
            {
                resolved = Failure("Expected manifest and kernel SHA-256 identities are required.");
                return false;
            }
            if (string.IsNullOrWhiteSpace(json))
            {
                resolved = Failure("Profile JSON is empty.");
                return false;
            }

            HlslPerfProfileData data;
            try
            {
                data = JsonUtility.FromJson<HlslPerfProfileData>(json);
            }
            catch (ArgumentException exception)
            {
                resolved = Failure("Profile JSON is invalid: " + exception.Message);
                return false;
            }
            if (data == null)
            {
                resolved = Failure("Profile JSON did not produce a profile.");
                return false;
            }

            string validationError = Validate(
                data,
                runtime,
                expectedWorkloadId,
                expectedManifestSha256,
                expectedKernelSha256,
                policy, expectedIdentity, allowHistorical);
            if (validationError != null)
            {
                resolved = Failure(validationError);
                return false;
            }
            resolved = new HlslPerfResolvedProfile(
                true,
                data.schemaVersion == "2.0" ? "Historical profile accepted by explicit opt-in; independent confirmation is unavailable." :
                    "Profile workload, ABI, confirmation identity and runtime fingerprint are compatible.",
                data.workloadId,
                data.candidateId,
                data.compatibilityKey,
                Clone(data.defineValues),
                data.medianGpuMilliseconds,
                data.p95GpuMilliseconds);
            return true;
        }

        public static void Apply(HlslPerfResolvedProfile profile, IHlslPerfDefineSink sink)
        {
            if (profile == null)
                throw new ArgumentNullException("profile");
            if (sink == null)
                throw new ArgumentNullException("sink");
            if (!profile.IsCompatible)
                throw new InvalidOperationException("Cannot apply an incompatible HLSL performance profile.");
            foreach (HlslPerfDefineValue define in profile.Defines)
                sink.SetInt(define.name, define.value);
        }

        private static string Validate(
            HlslPerfProfileData data,
            HlslPerfRuntimeFingerprint runtime,
            string expectedWorkloadId,
            string expectedManifestSha256,
            string expectedKernelSha256,
            HlslPerfCompatibilityPolicy policy,
            HlslPerfDeploymentIdentity expectedIdentity,
            bool allowHistorical)
        {
            bool historical = data.schemaVersion == "2.0";
            if (historical && !allowHistorical)
                return "Historical profile requires explicit allowHistorical opt-in; it has no independent confirmation guarantee.";
            if (!historical && !string.Equals(data.schemaVersion, SupportedSchema, StringComparison.Ordinal))
                return "Unsupported profile schema '" + data.schemaVersion + "'.";
            if (!string.Equals(data.kernelAbiVersion, SupportedAbi, StringComparison.Ordinal) &&
                !string.Equals(data.kernelAbiVersion, "hlslperf.raw-buffer.v2", StringComparison.Ordinal))
                return "Unsupported kernel ABI '" + data.kernelAbiVersion + "'.";
            if (!historical)
            {
                if (data.measurementProtocol != PairedProtocol || data.evidenceStatus != "independently-confirmed")
                    return "Profile lacks the required paired protocol and independent confirmation.";
                if (expectedIdentity == null)
                    return "Project-owned deployment identity is required for confirmed profiles.";
                if (!SameHash(data.workloadImplementationSha256, expectedIdentity.WorkloadImplementationSha256))
                    return "Profile workload implementation hash does not match the project-owned identity.";
                if (data.kernelAbiVersion != expectedIdentity.KernelAbiVersion)
                    return "Profile ABI does not match the project-owned identity.";
                if (!SameHash(data.executionIdentitySha256, expectedIdentity.ExecutionIdentitySha256))
                    return "Profile execution identity does not match the project-owned identity.";
                if (!SameHash(data.confirmationSha256, expectedIdentity.ConfirmationSha256))
                    return "Profile confirmation identity does not match the reviewed evidence.";
                if (policy != HlslPerfCompatibilityPolicy.ExactDeviceAndDriver)
                    return "Confirmed profiles require exact device and driver policy.";
                if (string.IsNullOrWhiteSpace(runtime.DriverVersion) || runtime.DriverVersion == "unavailable")
                    return "Confirmed profiles require a known driver version.";
            }
            if (!string.Equals(data.workloadId, expectedWorkloadId, StringComparison.Ordinal))
                return "Profile workload does not match the requested workload.";
            if (!IsSha256(data.manifestSha256) ||
                !string.Equals(data.manifestSha256, expectedManifestSha256, StringComparison.OrdinalIgnoreCase))
                return "Profile manifest hash does not match the project-owned manifest.";
            if (!IsSha256(data.kernelSha256) ||
                !string.Equals(data.kernelSha256, expectedKernelSha256, StringComparison.OrdinalIgnoreCase))
                return "Profile transitive kernel hash does not match the project-owned HLSL source graph.";
            if (data.device == null)
                return "Profile device fingerprint is missing.";
            if (string.IsNullOrWhiteSpace(data.compatibilityKey) || string.IsNullOrWhiteSpace(data.candidateId))
                return "Profile identity is incomplete.";
            if (data.defineValues == null || data.defineValues.Length == 0)
                return "Profile contains no define values.";
            HashSet<string> names = new HashSet<string>(StringComparer.Ordinal);
            foreach (HlslPerfDefineValue define in data.defineValues)
            {
                if (define == null || string.IsNullOrWhiteSpace(define.name))
                    return "Profile contains an invalid define.";
                if (!names.Add(define.name))
                    return "Profile contains duplicate define '" + define.name + "'.";
            }
            if (!string.Equals(data.device.backend, runtime.Backend, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(data.device.shaderModel, runtime.ShaderModel, StringComparison.OrdinalIgnoreCase))
                return "Profile backend or shader model does not match the runtime.";
            if (policy == HlslPerfCompatibilityPolicy.BackendAndShaderModel)
                return null;
            if (data.device.vendorId != runtime.VendorId || data.device.deviceId != runtime.DeviceId)
                return "Profile GPU vendor/device id does not match the runtime.";
            if (policy == HlslPerfCompatibilityPolicy.ExactDevice)
                return null;
            if (!string.Equals(data.device.driverVersion, runtime.DriverVersion, StringComparison.OrdinalIgnoreCase))
                return "Profile driver version does not match the runtime.";
            return null;
        }

        private static bool IsSha256(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length != 64)
                return false;
            for (int index = 0; index < value.Length; ++index)
            {
                char character = value[index];
                bool hexadecimal = character >= '0' && character <= '9' ||
                    character >= 'a' && character <= 'f' ||
                    character >= 'A' && character <= 'F';
                if (!hexadecimal)
                    return false;
            }
            return true;
        }

        private static bool SameHash(string actual, string expected)
        {
            return IsSha256(actual) && IsSha256(expected) && string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
        }

        private static HlslPerfDefineValue[] Clone(HlslPerfDefineValue[] source)
        {
            HlslPerfDefineValue[] result = new HlslPerfDefineValue[source.Length];
            for (int index = 0; index < source.Length; ++index)
                result[index] = new HlslPerfDefineValue { name = source[index].name, value = source[index].value };
            return result;
        }

        private static HlslPerfResolvedProfile Failure(string reason)
        {
            return new HlslPerfResolvedProfile(
                false,
                reason,
                string.Empty,
                string.Empty,
                string.Empty,
                new HlslPerfDefineValue[0],
                0,
                0);
        }
    }
}
