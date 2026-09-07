# HLSL Performance Profile Consumer

This UPM package is deliberately a one-way adapter. It reads a `profile.json` emitted by HlslKernelPipeline, validates the workload/ABI/device fingerprint, and returns the selected integer defines to project code.

It contains no benchmark runner, HLSL compiler, D3D12 backend, RGA integration, or project-specific shader logic. The project owns the final mapping from defines to shader variants through `IHlslPerfDefineSink`.

```csharp
if (HlslPerfProfileConsumer.TryResolve(
        profileTextAsset,
        runtimeFingerprint,
        "reduction-u32-v1",
        expectedManifestSha256,
        expectedTransitiveKernelSha256,
        HlslPerfCompatibilityPolicy.ExactDeviceAndDriver,
        out HlslPerfResolvedProfile resolved,
        reviewedDeploymentIdentity))
{
    HlslPerfProfileConsumer.Apply(resolved, myProjectOwnedSink);
}
```

Use `ExactDeviceAndDriver` for deployment. Less strict policies are intended for explicit experiments and never make an incompatible workload or ABI acceptable.

The two expected hashes must come from project-owned build metadata. The kernel
hash covers the root HLSL file and every recursively included `.hlsli`; an old
profile is rejected after either the manifest or any include changes.

Schema 3 additionally requires `reviewedDeploymentIdentity`, a project-owned
`HlslPerfDeploymentIdentity` containing `WorkloadImplementationSha256`,
`KernelAbiVersion`, `ExecutionIdentitySha256`, `ConfirmationSha256`, `CandidateId`
and `DefinesSha256`. Copy these from a reviewed build artifact into trusted project
metadata, never from the incoming JSON while resolving it. The consumer recomputes
the defines hash over actual defineValues and requires exact device/driver policy.
Schema 2 is historical and is rejected unless `allowHistorical: true` is explicitly
supplied. That compatibility mode has no paired/independent-confirmation guarantee.
