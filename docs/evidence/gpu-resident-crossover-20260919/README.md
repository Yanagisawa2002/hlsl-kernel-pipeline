# Crossover feasibility evidence — 2026-09-19 (Singapore)

**STOP: this package contains correctness and a failed timing pilot, not benchmark results.** See the [result/stop report](../../results/GPU_RESIDENT_CROSSOVER_2026-09-19.md).

- [Machine-readable diagnostic summary](diagnostic-summary.json) is derived from the raw JSON. Null costs/ratios mean unavailable.
- [Final smoke correctness](smoke-v2/validation-100000-0.25.json): 10/10 exact visible-set/count checks and non-black RGBA image comparisons passed at 100,000 agents, target visibility 25%.
- [Frozen calibration](smoke-v2/calibrate-100000-0.25.json): all 1000 views/counts and actual mean visible ratio 0.25000744.
- [Timing pilot](smoke-v2/pilot-100000-0.25-pair0-cpu.json): failed after 121 submitted measured CPU frames, zero resolved GPU range samples; arrays contain unexecuted slots and must not be averaged.
- [Pilot launch receipt](smoke-v2/pilot-100000-0.25-pair0-cpu.launch.json): exact command, process exit 2, executable/managed assembly hashes and NVIDIA snapshots. GPU utilization was 33% before and 15% after; this was not a quiet performance run.
- [Pilot player log](smoke-v2/pilot-100000-0.25-pair0-cpu.log): D3D12 adapter/driver identity and unresolved-query exception.
- [Initial failed correctness](smoke-v1/validation-100000-0.25.json): identical visible sets but black images; the non-black gate correctly rejected the result. The fixed logical depth clear is documented in the report.
- [Player build receipt](player-build.json): Unity version, Development configuration and successful build result.
- [File manifest](manifest.json): original and committed SHA-256 hashes plus sanitization rules.

Source identity for smoke-v2 and the pilot: `f7365d1009a51fa8b9bcde8cf7955fafbbf7eeee0cb92dab443630b8fe2eb785`. The runtime/shader code was not tuned after the pilot. The analyzer and collection gates were hardened afterwards; no primary evidence was collected with either tool version.

Raw JSON/launch receipts are copied byte-for-byte. Player logs redact local connection identifiers and user/host names, with trailing whitespace trimmed. Original logs remain in `D:/CodexValidation/hlsl-crossover-smoke-20260919/` and `D:/CodexValidation/hlsl-crossover-smoke-v2-20260919/`. Unity project/player/build log remain local under the corresponding `hlsl-crossover-*20260919` directories; large generated binaries, Library and licensing logs are not committed.

The runtime JSON's `driver` extractor returned unavailable; the player's own log records **32.0.15.9186**, and the external NVIDIA runtime snapshot records **591.86**. Do not interpret `graphicsDeviceVersion` as a driver version. The pilot timestamp is September 18 UTC / September 19 Singapore; folder dates use Singapore time.

No architecture-validation capture was repeated. No controlled performance plots, CPU/GPU architecture ratios, intervals or crossover conclusions can be derived from this package.
