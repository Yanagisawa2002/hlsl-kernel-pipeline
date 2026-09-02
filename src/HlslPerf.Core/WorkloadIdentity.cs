using System.Reflection;

namespace HlslPerf.Core;

public static class WorkloadIdentity
{
    /// <summary>
    /// Identifies the actual workload implementation, not only its public id.
    /// A rebuilt built-in or external workload assembly therefore invalidates
    /// checkpoints even when its manifest and HLSL source are unchanged.
    /// </summary>
    public static string Compute(IKernelWorkload workload)
    {
        ArgumentNullException.ThrowIfNull(workload);
        Type type = workload.GetType();
        Assembly assembly = type.Assembly;
        string assemblyIdentity;
        if (!string.IsNullOrWhiteSpace(assembly.Location) && File.Exists(assembly.Location))
            assemblyIdentity = ContentHash.Sha256(File.ReadAllBytes(assembly.Location));
        else
            assemblyIdentity = assembly.ManifestModule.ModuleVersionId.ToString("D");

        return ContentHash.Sha256(
            "hlslperf-workload-implementation-v1\n" +
            (type.FullName ?? type.Name) + "\n" +
            assemblyIdentity);
    }
}
