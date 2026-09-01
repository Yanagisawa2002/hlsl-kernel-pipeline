# HLSL Performance Profile Consumer

This UPM package is deliberately a one-way adapter. It reads a `profile.json` emitted by HlslKernelPipeline, validates the workload/ABI/device fingerprint, and returns the selected integer defines to project code.

It contains no benchmark runner, HLSL compiler, D3D12 backend, RGA integration, or project-specific shader logic. The project owns the final mapping from defines to shader variants through `IHlslPerfDefineSink`.

```csharp
if (HlslPerfProfileConsumer.TryResolve(
        profileTextAsset,
        runtimeFingerprint,
        "reduction-u32-v1",
        HlslPerfCompatibilityPolicy.ExactDeviceAndDriver,
        out HlslPerfResolvedProfile resolved))
{
    HlslPerfProfileConsumer.Apply(resolved, myProjectOwnedSink);
}
```

Use `ExactDeviceAndDriver` for deployment. Less strict policies are intended for explicit experiments and never make an incompatible workload or ABI acceptable.
