# HLSL single-attempt outcome: FAILED

One implementation candidate was committed. The single frozen validation failed
at offline dependency restore; no compilation, tests, package, publish, caller
invocation or atlas generation followed. No implementation or test fixes were
made after that run, and no second attempt was started.

## Exact identities

- Base main: `723e0dd1d0fbe0912811a4bab19e8ea1a10ac630`.
- Frozen candidate: `da35f42b66788ced024ec7965e3f11160b99f50a`.
- Candidate tree: `0ecbd9400f79997d5bb3bb0c5ec249599ccb1534`.
- Branch: `codex/single-attempt-20260916-hlsl`.
- Run ID: `hlsl-export-frozen-20260916-01`.
- Run UTC: `2026-09-15T17:47:11.271751+00:00` to `2026-09-15T17:47:13.768553+00:00`.
- .NET SDK: 10.0.302; Python: 3.14.0; local platform: Windows x64.
- Raw archive SHA-256: `87554bbead4d3c33c45a5874bcb30db64475a0a10016312fca9942b88d041b66`.
- Original run.json SHA-256: `8b57146d7a52a8245dc4885a526dcd4ee1a262c6f632e965cbe76d91b496ae71`.

## What changed

Added a standalone Crowd file-to-atlas example caller: bounded JSON request and
caller-owned little-endian seed file, existing `CrowdCpuRenderer` reuse,
successive animation windows, raw RGBA and BMP export, and hash receipts. Added
nine CPU test cases covering an independent full-pixel oracle, BMP order/channels,
bad input, unsupported settings and preserving prior output. These tests were
**not executed**. Added a one-pass validation runner and release support matrix;
corrected Unity documentation to match schema 3.0 and marked the old five-test
Unity result as historical.

The existing renderer and GPU implementation are unchanged. This is an example
caller for the controlled scene, not an externally sourced production deployment.
PR #4 remains separate Draft work: its reports, runner and raw experiments were
not copied or modified. No SDK version, runtime defaults or shader changed.

## Validation

| Step | Result |
|---|---|
| SDK identity | PASS, exit 0 |
| Byte-complete checkout | PASS, exit 0 |
| Pinned upstream files | PASS, 114 files, exit 0 |
| Offline solution restore | FAIL, NU1100, exit 1 |
| Build and CPU tests | NOT RUN; no TRX, no current test count claimed |
| Package / publish / app / output checks | NOT RUN; no binary distribution or image generated |
| Requested Ubuntu RTX 5090 native D3D12 | SKIPPED; no native backend and no remote work |

The harness set `DOTNET_CLI_HOME` to a new run-local directory without binding
`NUGET_PACKAGES`. Generated `project.assets.json` confirms NuGet resolved that
new empty `.nuget/packages` directory. The supplied offline config has no sources.
Restore therefore could not resolve Vortice 3.8.3, the test SDK/xUnit packages and
the Windows SDK reference. Read-only inspection confirmed representative required
packages already exist in the user's normal cache. This is a **harness cache
binding failure**, not evidence of absent hardware, absent packages machine-wide,
a C# compilation defect or numerical failure.

The exact attempted invocation was:

```text
python tools/validate_crowd_export.py --dotnet <existing-dotnet-10.0.302>/dotnet.exe --output <new-run-directory> --max-seconds 1800
```

All resolved commands, working directories, PIDs, timestamps, exit codes and log
hashes are in the original `run.json` inside the archive. The failed restore was:

```text
dotnet restore HlslKernelPipeline.slnx --configfile examples/HlslPerf.PrimitiveApp/NuGet.offline.config --disable-parallel -p:NuGetAudit=false
```

The SDK's first-use log also reports development-certificate creation; no
explicit certificate trust command was executed. The archive excludes the CLI
home and certificate material. Checkout initially needed repository-local
`core.longpaths=true` and restoration of the new clone's incomplete checkout;
the original source working tree was preserved.

## Stop and next boundary

The frozen implementation, tests and runner remain unchanged. A future attempt
would need an explicit usable package-cache binding before its own freeze, then
compilation, CPU tests and published-caller acceptance. This campaign does not
perform that repair or rerun. This PR remains Draft.

`attempt_consumed=true`, `remote_used=false`, `remote_root=null`. No SSH,
remote lock, GPU work or shutdown was performed. All four tracked step PIDs and
their live descendants were absent at process closeout.

No GPU/application performance, FPS, p95 or energy result is available. The
Ubuntu GPU stage and external-caller/Unity acceptance remain open independently
of the local harness failure.

## Evidence

[Machine-readable summary](../evidence/hlsl-export-single-attempt-20260916/summary.json)
and [original raw records](../evidence/hlsl-export-single-attempt-20260916/raw-records.zip)
contain the unmodified run, four logs, failed restore assets, cache diagnosis,
frozen file hashes, PR #4 background snapshot and process closeout. Every archive
member was compared byte-for-byte with its local original during evidence packaging.
