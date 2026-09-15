# Unity profile consumer boundary

The UPM package at `unity/com.edwinliu.hlslperf-profile` is intentionally a
consumer, not an embedded tuner.

It contains:

- serializable schema 3.0 profile fields (schema 2.0 requires explicit historical opt-in);
- workload and `hlslperf.raw-buffer.v1` / `hlslperf.raw-buffer.v2` ABI checks;
- exact 64-hex manifest and transitive-kernel hash checks supplied by the caller;
- backend, shader model, vendor/device, and exact-driver checks for confirmed profiles;
- project-owned workload, execution, confirmation, candidate and define identities;
- immutable resolved define values;
- `IHlslPerfDefineSink`, implemented by project-owned integration code.

It does not contain DXC, Vortice, D3D12 commands, workload kernels, CPU oracles,
RGA code, benchmarking, or a reference to `HlslPerf.Core`.

## Install as a local package

Add this dependency to a Unity project's `Packages/manifest.json`:

    "com.edwinliu.hlslperf-profile": "file:C:/path/to/HlslKernelPipeline/unity/com.edwinliu.hlslperf-profile"

Load an emitted `profile.json` as a `TextAsset`, construct the runtime
fingerprint through a project-owned hardware service, and call
`HlslPerfProfileConsumer.TryResolve` with a separately trusted
`HlslPerfDeploymentIdentity`. Schema 3.0 confirmed profiles require
`ExactDeviceAndDriver`, a known driver and the independent paired-confirmation
protocol. Do not populate the trusted identity from the incoming profile itself.
Looser device policies apply only to explicitly accepted historical profiles.

Even the loosest mode never relaxes workload, ABI, backend, or shader-model
matching. It also never relaxes either content hash. An incompatible or stale
result cannot be passed to `Apply`.

Historical validation: the package was compiled and tested with Unity
6000.5.3f1 in a clean temporary project on 2026-09-02. All five tests recorded
at that time passed, including manifest- and kernel-hash drift regressions.
Those results predate the current schema 3.0 consumer and do not validate its
current Unity import or Player execution. Neither gate was rerun in the
September 16 release preparation. Current code is
`unity/com.edwinliu.hlslperf-profile/Runtime/HlslPerfProfileConsumer.cs`.
