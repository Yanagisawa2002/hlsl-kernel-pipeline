using EdwinLiu.HlslPerf;
using UnityEngine;

public sealed class ProfileConsumerExample : MonoBehaviour, IHlslPerfDefineSink
{
    [SerializeField] private TextAsset profile;

    private void Start()
    {
        // A production integration should build this fingerprint from its own graphics-device/driver service.
        HlslPerfRuntimeFingerprint runtime = new HlslPerfRuntimeFingerprint(
            4098,
            30033,
            "replace-with-runtime-driver",
            "D3D12",
            "6_0");
        if (HlslPerfProfileConsumer.TryResolve(
                profile,
                runtime,
                "reduction-u32-v1",
                "replace-with-build-owned-manifest-sha256",
                "replace-with-build-owned-transitive-kernel-sha256",
                HlslPerfCompatibilityPolicy.ExactDeviceAndDriver,
                out HlslPerfResolvedProfile resolved))
            HlslPerfProfileConsumer.Apply(resolved, this);
        else
            Debug.LogWarning(resolved.Reason);
    }

    public void SetInt(string name, int value)
    {
        // Map the ABI define to your project's shader variant system here.
        Debug.Log(name + "=" + value);
    }
}
