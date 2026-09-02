# Unity profile consumer boundary

The UPM package at `unity/com.edwinliu.hlslperf-profile` is intentionally a
consumer, not an embedded tuner.

It contains:

- serializable schema 2.0 profile fields;
- workload and `hlslperf.raw-buffer.v1` ABI checks;
- exact 64-hex manifest and transitive-kernel hash checks supplied by the caller;
- backend, shader model, vendor/device, and optional exact-driver policies;
- immutable resolved define values;
- `IHlslPerfDefineSink`, implemented by project-owned integration code.

It does not contain DXC, Vortice, D3D12 commands, workload kernels, CPU oracles,
RGA code, benchmarking, or a reference to `HlslPerf.Core`.

## Install as a local package

Add this dependency to a Unity project's `Packages/manifest.json`:

    "com.edwinliu.hlslperf-profile": "file:C:/path/to/HlslKernelPipeline/unity/com.edwinliu.hlslperf-profile"

Load an emitted `profile.json` as a `TextAsset`, construct the runtime
fingerprint through a project-owned hardware service, and call
`HlslPerfProfileConsumer.TryResolve`. Deployment should use
`ExactDeviceAndDriver`; looser modes are for deliberate experiments.

Even the loosest mode never relaxes workload, ABI, backend, or shader-model
matching. It also never relaxes either content hash. An incompatible or stale
result cannot be passed to `Apply`.

The package was compiled and tested with Unity 6000.5.3f1 in a clean temporary
project. All five runtime tests passed on 2026-09-02, including independent
manifest- and kernel-hash drift regressions.
