# Changelog

## 0.6.0

- Require callers to supply the exact manifest SHA-256 and transitive HLSL
  source-graph SHA-256 before resolving a profile.
- Reject malformed hashes and kernel/include drift under every device policy.
- Keep the package a read-only adapter with no tuner or Unity-project coupling.

## 0.2.0

- Added schema 2.0 profile loading.
- Added workload, ABI, device, backend, shader model, and driver compatibility checks.
- Added a project-owned define sink boundary.
