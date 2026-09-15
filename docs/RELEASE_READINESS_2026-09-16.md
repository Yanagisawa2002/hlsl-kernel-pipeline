# September 16 release preparation

This is a reviewable source change and a local distribution rehearsal. It does
not publish an official Release, tag, NuGet package or production deployment.
The candidate starts from refreshed GitHub main
`723e0dd1d0fbe0912811a4bab19e8ea1a10ac630`.

## One useful entry point

[CrowdExport](../examples/HlslPerf.CrowdExport/README.md) accepts a caller-owned
uint32 seed file and JSON scene request. It reuses one existing conventional CPU
renderer across successive animation windows, writes complete RGBA atlases and
viewable BMP strips, and publishes a receipt only after all file readbacks match.
The framework-dependent publish folder runs independently of the source tree.

The delivered caller is a standalone **example asset export application** using
the controlled Crowd scene. It establishes a concrete CPU consumption path.
No named external host application was supplied or integrated in this attempt;
external product adoption and real GPU-caller acceptance remain open. The demo
input is synthetic and is not evidence of deployment or application speedup.

## What was already present

| Surface | Status at the refreshed base | This candidate |
|---|---|---|
| `PrimitiveOperations`, `D3D12OperationRecorder` | Public primitive planning, explicit selection, borrowed-resource recording | Unchanged; no new GPU evidence |
| `HlslPerf.PrimitiveApp` | CPU plan checks plus a compiled recording example | Existing CPU check retained in validation |
| `CrowdApplication`, `CrowdCpuRenderer` | GPU runtime with oracle-independent scheduling and conventional CPU renderer, merged in PR #3 | Reuses the CPU renderer, adds file/preview/export lifecycle |
| `CrowdVfxWorkload` | Independent CPU oracle plus older controlled workload | Used only by integration tests, never by the new application |
| Draft [PR #4](https://github.com/Yanagisawa2002/hlsl-kernel-pipeline/pull/4) | Complete-task protocol, evidence and report; head `30446fac6f895ad70c621bbae5e97f64d7d3b136`, unmerged when inspected | Linked as separate work; no commits or raw results copied |
| Unity profile consumer | Schema 3.0 implementation, older schema 2.0 documentation | Corrects documentation; current Player/import validation still open |

The existing [inclusive scan result](results/RTX4090_INCLUSIVE_SCAN_2026-09-15.md)
is scoped to its RTX 4090 native GPU operation. It does not measure this export
caller. PR #4's complete-task report and its CPU conclusion remain in that Draft;
this candidate does not retest, supersede or imply that report has merged.

## Distribution and acceptance gates

| Gate | Scope / boundary |
|---|---|
| CPU source build and tests | One frozen run; raw exit codes and TRX determine pass/fail |
| Published caller | One invocation outside the source checkout, two sequential atlas windows, exact file hashes and independent BMP decode |
| Local workload `.nupkg` | Pack existing 0.6.0 workload package and verify shader/upstream/license bytes; not package installation or NuGet publication |
| CPU correctness | Independent pixel oracle and exporter contract tests; not CPU/GPU comparative performance |
| Requested Ubuntu RTX 5090 D3D12 | **SKIPPED**: no native D3D12 backend on this target; no SSH or remote work used |
| New Linux GPU backend | Excluded; no D3D12/Vulkan/CUDA port |
| Linux execution of new CPU caller | Unverified; local validation uses Windows |
| External host, Unity Player, SDK GPU deployment | Unverified |
| GPU/app speedup, FPS, p95, energy | Unavailable; new receipts say performance `unmeasured` |

Use `tools/validate_crowd_export.py --dotnet <matching-dotnet> --output <new-directory>`
for the bounded source/build/test/publish/export/package check. The output must
not exist, steps run once and stop on the first failure, and a run manifest binds
the Git revision, commands, exit codes and evidence hashes. This command never
creates a GPU device or runs a benchmark. Dependency restore uses the existing
offline config; missing cached packages fail rather than being installed.

The single-attempt outcome and exact validation identities are recorded in
[the result receipt](results/CROWD_EXPORT_SINGLE_ATTEMPT_2026-09-16.md).
Any later hardware or external-caller acceptance requires a separately scoped
attempt. The once-only campaign does not permit repairing and rerunning a failed
frozen candidate.

## Ownership and licensing

The caller, BMP export code and receipts are project code. The CPU renderer was
already part of main. GPUPrefixSums/AMD shader algorithms remain attributed to
their pinned upstream authors and are not executed by the CPU caller. SDK
packages must retain the project's LICENSE and upstream source/license tree;
the publish folder includes LICENSE and NOTICE. The repository's limited
benchmark reproduction permission is not a general MIT/open-source grant.
