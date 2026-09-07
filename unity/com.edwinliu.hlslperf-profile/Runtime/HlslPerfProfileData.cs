using System;

namespace EdwinLiu.HlslPerf
{
    [Serializable]
    public sealed class HlslPerfProfileData
    {
        public string schemaVersion;
        public string compatibilityKey;
        public HlslPerfDeviceData device;
        public string manifestSha256;
        public string kernelSha256;
        public string candidateId;
        public double medianGpuMilliseconds;
        public double p95GpuMilliseconds;
        public double throughputMillionItemsPerSecond;
        public double speedupOverBaseline;
        public string workloadId;
        public string kernelAbiVersion;
        public HlslPerfDefineValue[] defineValues;
        public string measurementProtocol;
        public string evidenceStatus;
        public string workloadImplementationSha256;
        public string executionIdentitySha256;
        public string confirmationSha256;
    }

    [Serializable]
    public sealed class HlslPerfDeviceData
    {
        public string adapterName;
        public long vendorId;
        public long deviceId;
        public long subsystemId;
        public long revision;
        public string adapterLuid;
        public string driverVersion;
        public string backend;
        public string shaderModel;
        public string compilerVersion;
        public string operatingSystem;
    }

    [Serializable]
    public sealed class HlslPerfDefineValue
    {
        public string name;
        public int value;
    }
}
