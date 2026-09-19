# Draw ramp mechanism diagnostic (frozen before hardware execution)

Diagnostic only; eligibleForPerformanceAcceptance=false. Formal v4 runner, gate, timestamp ring and source files remain unchanged. A generated isolated Player reuses the actual CPU all-agent visibility/list/upload/direct draw method, shader, population, 1280x720 RGBA8/D32 target and depth semantics. The generator asserts patch anchors and records original/generated hashes.

Existing 1000-view calibration is reused byte-for-byte; no recalibration. Stage A: CPU forward, 100k/.25, seed69501203, warmup300, measured96 (views0..95). If instrumentation IDs/stats/fence/telemetry checks pass, exactly three processes in order: forward, frozen, reverse; warmup300, measured1000. Forward maps i->i; reverse i->999-i; frozen i->500. Warmup uses the same mapping at warmup index0..299, so frozen has constant input through warmup and measurement. This is a declared diagnostic difference, not an acceptance protocol amendment.

Pipeline stats use a separate 32-entry heap/readback, sharing explicit timestamp submission IDs and completion fence. Begin after T1, End before T2, Resolve after draw; timestamp call locations surrounding the draw are unchanged. Stats add instrumentation work inside the diagnostic timing range, so durations cannot be used for overhead or crossover. No synchronous readback or slot waits. All 11 standard pipeline counters retained.

Proxies precomputed before warmup from the exact C# population/predicate/calibration: visible count/ID hash, sum full world quad area, partial clipping count, continuous projected quad area before clipping/overlap, and clipped projected area before overlap/depth. Pixel-area is a geometric estimate, not rasterized invocation count.

Telemetry: persistent NVML ctypes sidecar, 100ms target cadence, UTC and monotonic timestamps, query latency and missed cadence reported; all available GPUs anonymously indexed. Locally inspect supported NVIDIA query names first. Fields: GPU util, graphics/SM/memory clocks, P-state, board power, temperature; unavailable values null with return codes. No process names/paths queried. Actual cadence/latency retained; sensor internal averaging can be slower than polling. Sidecar starts and records before Player launch, ends after Player exit. CPU frame submission UTC times align 100-frame blocks approximately; fence-resolve UTC recorded but no fabricated exact GPU execution UTC.

No quality/overhead acceptance, retries, extra trajectories, formal matrix, or correctness expansion. Pre/post load retained but not an acceptance designation. A failed Stage A stops the three-run sequence. A failed trajectory is retained and stops, never replaced. Block telemetry statistics and associations are descriptive, not causal or independent-frame inference.

Pipeline counter definitions: https://learn.microsoft.com/en-us/windows/win32/api/d3d12/ns-d3d12-d3d12_query_data_pipeline_statistics
