# RTX 4090 marker-isolated scan closeout

Captured September 18¨C19, 2026 (Asia/Singapore). This compact evidence bundle preserves the exact commands, provenance, stdout/stderr, exit status, original tab-separated Nsight exports (despite their `.xls` suffix), named marker metrics, and RSP shader names/hashes.

All values used in the diagnosis come from `HlslPerf.ScanInclusive.ProfileRange`: duration from `BASE/D3DPERF_EVENTS.xls`, counters from the matching `flattened_event_name` row in `BASE/GPUTRACE_REGIMES.xls`. `GPUTRACE_FRAME.xls` is retained for audit only and is not used as marker evidence.

Raw binary traces remain at the local paths recorded in `raw-traces.json`, with size and SHA256; they are not embedded in this compact Git bundle. `files.json` hashes every other file in this bundle (excluding itself).

RSP shader relative contributions normalize each named shader active-warp value by the sum of named shader active-warp values in that marker. They are not raw sample counts or duration shares. Tile-fused exported all-zero marker PCSampler values despite nonzero hardware occupancy: preserve the zeros, but treat its sampled stall breakdown and relative shader contributions as unavailable. No AddInput column is present for tile-fused; this alone is not proof of absence of execution. No multi-pass captures were attempted in these rounds.
