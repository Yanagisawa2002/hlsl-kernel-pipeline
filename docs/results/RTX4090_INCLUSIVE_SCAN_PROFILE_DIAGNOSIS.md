# RTX 4090 inclusive scan: hardware-profile diagnosis

Status: **timing-confirmed; marker-isolated throughput and shader attribution complete (with the tile-fused RSP sampling limitation documented); case closed 2026-09-19**.

¡°Complete¡± closes the planned capture and attribution work; it does not claim that tile-fused has a valid sampled stall distribution, that every child dispatch was reconstructed, or that exact DRAM-byte savings were measured. Those limits are frozen below and are not outstanding release gates for this case.

## Frozen timing result

The September 15 paired timing/correctness result remains the performance anchor:

| Arm | Operation | Complete GPU operation |
|---|---|---:|
| `tile` | exclusive scan followed by AddInput conversion | 6.376 ms |
| `tile-fused` | native inclusive output in the local GPUPrefixSums-derived adaptation | 2.904 ms |
| `rts` | pinned GPUPrefixSums ReduceThenScan | 3.584 ms |

At `268435456` (`2^28`) uint32 elements, fused reduced complete operation time by **54.45%** versus tile and used **18.97% less time** than RTS. These figures come from the frozen paired experiment, not the single-operation profiling captures below. Reproduction and correctness gates: [RTX4090_INCLUSIVE_SCAN_2026-09-15.md](RTX4090_INCLUSIVE_SCAN_2026-09-15.md).

Source accounting identifies a separate full-array AddInput conversion in tile: reading input and output and rewriting output amounts to **3 GiB of logical full-array traffic** at this size. This is source accounting, not measured DRAM traffic.

## Final capture identity and scope

The September 18 Throughput Metrics round and September 19 Real-Time Shader Profiler (RSP) round used:

- source commit `b8df865819662f0f0b57f4c90b1d3def2898d3f3`;
- NVIDIA GeForce RTX 4090, LUID `743848209`, driver `32.0.15.9186`;
- Nsight Graphics `2026.3.1.0`, build `38722833`;
- GPU Trace Profiler, `Windows (x86_64)`, Ada, Throughput Metrics, base GPU clocks;
- `profile-once <arm> 268435456 743848209 "NVIDIA GeForce RTX 4090"`;
- existing `.scratch/native-profile/scan/scan.exe`, launched with its own directory as working directory and the adjacent PIX runtime;
- `--start-after-submits=2 --limit-to-submits=4 --auto-export --verbose`;
- marker `HlslPerf.ScanInclusive.ProfileRange` around the warmed inclusive scan operation;
- shader pipeline collection enabled; the RSP round adds only `--real-time-shader-profiler` and changes output directory. No multi-pass metrics.

The six-submit process excludes warmup submits 1¨C2; capture submits 3¨C6 include input initialization, marked scan, and validation/readback. **The submit window is not itself scan-isolated.** All reported durations use the named event in `D3DPERF_EVENTS.xls`; all counters use the named marker row in `GPUTRACE_REGIMES.xls`, never whole-capture `GPUTRACE_FRAME.xls` averages. Range attribution does not establish process-exclusive hardware counters or exact DRAM bytes.

Unchanged binary SHA256 values:

- scan.exe: `230179D2CC2E5B5A344D9E6190A95E691E4CF5A0995331EC0E674494F45C3568`
- WinPixEventRuntime.dll: `81ADCFD8253C3489BE720DA7E30F16004DC9A1F02A8B418C6C3AEF4993032E6D`

All six successful captures exited **0**, saved `.ngfx-gputrace` files and auto-exported data. The logs report a connection error during teardown after successful save/export, followed by `Running result: 0`. The first throughput launch rejected platform `Windows` before application launch; the successful command uses `Windows (x86_64)`. No source changes or rebuild occurred during either round.

Versioned evidence: [compact capture bundle](../evidence/rtx4090-scan-marker-20260919/README.md), [raw trace paths and SHA256](../evidence/rtx4090-scan-marker-20260919/raw-traces.json), [bundle file hashes](../evidence/rtx4090-scan-marker-20260919/files.json). Local raw captures remain under `.scratch/nsight-isolated/{tile,tile-fused,rts}` and `.scratch/nsight-isolated-rsp/{tile,tile-fused,rts}`.

## Marker-isolated Throughput Metrics

| metric | tile | tile-fused | rts |
|---|---:|---:|---:|
| duration_ms | 7.30486 | 3.39322 | 3.98349 |
| SM_throughput_pct | 3.39695 | 5.12492 | 4.3442 |
| L1TEX_throughput_pct | 6.36813 | 11.264 | 11.4771 |
| L1TEX_hit_rate_pct | 59.5289 | 69.6222 | 71.3265 |
| L2_throughput_pct | 22.7144 | 27.4815 | 30.286 |
| L2_hit_rate_pct | 47.3763 | 62.7993 | 48.168 |
| DRAM_throughput_pct | 72.9589 | 61.8576 | 80.037 |
| DRAM_read_throughput_pct | 44.1089 | 31.4464 | 54.128 |
| DRAM_write_throughput_pct | 28.85 | 30.4112 | 25.909 |
| compute_active_warps_occupancy_pct | 31.1459 | 33.0528 | 81.5105 |

Throughput and occupancy counters use `% of peak sustained elapsed`; hit rates use their exported percentage denominators. These exact single-capture values establish range-specific observations, not repeated-trial means or hardware-byte accounting. No causal conclusion is inferred from these percentages alone.

## Marker-isolated RSP counters

| metric | tile | tile-fused | rts |
|---|---:|---:|---:|
| duration_ms | 6.90723 | 3.40435 | 3.61901 |
| long_scoreboard_pipe_l1tex_pct_peak | 18.7741 | 0 | 38.9424 |
| short_scoreboard_pct_peak | 0.68821 | 0 | 12.0656 |
| barrier_pct_peak | 11.3668 | 0 | 11.9965 |
| lg_throttle_pct_peak | 0.0288165 | 0 | 6.83713 |
| mio_throttle_pipe_mio_pct_peak | 0.0133473 | 0 | 15.6962 |
| wait_pct_peak | 0.983806 | 0 | 1.04507 |
| membar_pct_peak | 0.503545 | 0 | 0 |
| register_allocation_launch_stall_pct_peak | 24.4348 | 49.5409 | 0 |
| compute_occupancy_pct_peak | 32.9299 | 33.0616 | 90.7008 |

All `_pct_peak` values retain Nsight's peak-sustained-elapsed denominator. They are **not** percentages of a normalized stall distribution. Register-allocation launch stalls are not registers per thread, and compute occupancy is an exported active-warp signal, not a per-shader occupancy proof.

**Tile-fused limitation:** every marker PCSampler field is zero despite hardware compute occupancy of `33.0616%`. The raw exported zeros above are preserved for audit, but its sampled stall distribution and relative shader contributions are **unavailable**, not confirmed zero. The cause of the missing/non-interpretable marker samples was not established. The independent hardware register-allocation counter remains reportable. No extra capture or settings change was made to hide this limitation.

## Shader attribution and relative contributions

| arm | shader | hash | raw marker active warp % peak | relative contribution % |
|---|---|---|---:|---:|
| tile | AddInput | 0xb073da1f6bffeb24 | 16.6215 | 50.48698500 |
| tile | ClearErrorCount | 0x0fecd3cdf21b7e05 | 6.71427e-06 | 0.00002039 |
| tile | InitOne | 0x5ca1d4584f974a0c | 0 | 0.00000000 |
| tile | ResetWaveTiledState | 0x6022434002d41376 | 0.000139508 | 0.00042375 |
| tile | SinglePassScanWaveTiled | 0x4924d7febe7b2012 | 16.3007 | 49.51257085 |
| tile | ValidateOneInclusive | 0x78f4c9f9bc7ffbe5 | 0 | 0.00000000 |
| tile-fused | ClearErrorCount | 0x0fecd3cdf21b7e05 | 0 | unavailable |
| tile-fused | InitOne | 0x5ca1d4584f974a0c | 0 | unavailable |
| tile-fused | ResetWaveTiledState | 0x6022434002d41376 | 0 | unavailable |
| tile-fused | SinglePassScanWaveTiled | 0x5263b0f2d200a619 | 0 | unavailable |
| tile-fused | ValidateOneInclusive | 0x78f4c9f9bc7ffbe5 | 0 | unavailable |
| rts | ClearErrorCount | 0x0fecd3cdf21b7e05 | 0 | 0.00000000 |
| rts | InitOne | 0x5ca1d4584f974a0c | 0 | 0.00000000 |
| rts | PropagateInclusive | 0xe54982ecfebc14d5 | 59.8092 | 65.94652578 |
| rts | Reduce | 0xa9c62cc972c5d945 | 30.8797 | 34.04842285 |
| rts | Scan | 0xe086a782cc29f0a1 | 0.00458126 | 0.00505137 |
| rts | ValidateOneInclusive | 0x78f4c9f9bc7ffbe5 | 0 | 0.00000000 |

Relative contribution is `100 * shader marker active-warp metric / sum of named shader marker active-warp metrics`. The displayed percentages are derived and rounded to eight decimal places; raw exported values and hashes are preserved. They are not measured duration shares or raw sample-count fractions. A shader column can exist with zero marker activity; its presence alone does not establish execution inside the marker.

- **tile:** `AddInput` is now identified by name/hash with nonzero marker activity (`16.6215%` peak; `50.48698500%` of named shader active-warp contribution). `SinglePassScanWaveTiled` and the small reset contribution are also resolved.
- **tile-fused:** no AddInput shader column is identified. The fused `SinglePassScanWaveTiled` hash is `0x5263b0f2d200a619`, distinct from tile's `0x4924d7febe7b2012`. Because all marker sampling values are zero, this round cannot independently prove execution absence or give its shader shares. Source structure and the earlier shader inventory support the absence of the conversion stage.
- **RTS:** `Reduce`, `Scan`, and `PropagateInclusive` are resolved with nonzero marker activity and relative contributions of `34.04842285%`, `0.00505137%`, and `65.94652578%` respectively.
- The tiny tile `ClearErrorCount` contribution (`6.71427e-06%` peak) is retained without asserting that validation executed inside the marker. Input initialization and validation exports otherwise show zero marker contribution. This small attribution artifact limits exact boundary claims.

The source-recorded order is `ResetWaveTiledState -> SinglePassScanWaveTiled -> AddInput` for tile, reset then fused scan for tile-fused, and `Reduce -> Scan -> PropagateInclusive` for RTS. Exported aggregate shader contributions do **not** independently reconstruct child-dispatch ordering or multiplicity. Numeric PSO IDs, per-shader stall breakdowns, and registers per thread were not exposed by the auto exports; raw traces and exported shader hashes are preserved instead of guessing.

## Historical evidence and final diagnosis

The [September 17 evidence](../evidence/rtx4090-scan-nsight-20260917/README.md) is retained as whole-capture context. Its counters are superseded for scan-range attribution by the marker tables above and must not be mixed into them. The September 17 unprofiled means (tile `6.90786 ms`, fused `3.09687 ms`, RTS `3.61031 ms`) preserve the ordering but do not replace the September 15 paired result.

The established claim is a paired complete-operation timing improvement, a source-level elimination of the separate full-array conversion stage, and marker-level runtime evidence identifying AddInput in tile with lower marker DRAM throughput/read signals in fused. This supports the pass-elimination explanation without assigning a numerical fraction of the timing win to a counter. It does not establish exact DRAM-byte savings, universal superiority over RTS, or an improved occupancy mechanism. In the new throughput round fused active-warps signal is slightly higher than tile (`33.0528%` versus `31.1459%`), so the older whole-capture statement that it falls is not a marker-level conclusion.

Interviewer-safe statement:

> Paired GPU timestamps and correctness gates established a 54.45% complete-operation improvement for this RTX 4090 inclusive uint32 scan at 2^28 elements. The change removes an AddInput conversion representing 3 GiB of logical full-array traffic. Subsequent marker-isolated profiling identifies AddInput in the old path and records the three arms' scan-range throughput counters and resolved shader identities. Exact DRAM-byte savings remain unmeasured, and the fused RSP run's all-zero marker samples prevent interpreting its stall mix or relative shader contribution.

## Closure

The planned marker-isolated Throughput + RSP capture rounds and evidence write-up are complete. **This case is closed; no further profiling is required for this release.** The frozen performance claim is unchanged. Fused RSP sampling, precise child-dispatch reconstruction, per-shader stall attribution, exact DRAM-byte accounting, and repeated profiling statistics remain explicit limits of the evidence, not completed measurements. No multi-pass result is claimed.
