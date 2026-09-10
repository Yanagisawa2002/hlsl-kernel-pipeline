# Roadmap

The direction is reusable GPU primitive execution, trustworthy measurement and
choosing mature backends. Internal kernels are explicit research candidates.
The [September 9 native results](results/R9700_NATIVE_CONFIRMATION_2026-09-09.md)
found one bounded tile4 win over GPUSorting FFX at `2^25` pairs and six slower
candidate comparisons. This motivates reuse and targeted investigation, not an
automatic winner policy. Native results are not SDK/Unity deployment profiles.

## Application integration — implemented, CPU checked

- [x] Public application-input scan/sort plans with explicit pinned RTS and AMD
  options, full32 semantics and stable arbitrary payloads (already present before
  September 10; not new algorithm integrations).
- [x] Capability/source/plan/runtime checks, explicit fallback and an opt-in
  unmeasured path; preserve internal compatibility defaults without a speed claim.
- [x] Borrowed-resource D3D12 recorder with caller-owned PSOs, state and fence
  lifetime (already present).
- [x] [Compilable application example](../examples/HlslPerf.PrimitiveApp/README.md)
  showing counts, material keys/draw IDs, normal RTS/AMD selection, CPU checks,
  recording/submission ownership and complete cost boundaries (September 10).
- [x] Fall back on pinned revision/byte failures and malformed lock JSON, with
  isolated CPU regression cases (September 10).

## Next integration and measurement work — not completed

- [ ] Validate a real application's borrowed-resource calls, repeated execution,
  complete outputs and full costs on its own device/runtime. Only then consider
  an exact deployment profile; no current-example performance claim is made.
- [ ] Validate Unity import/Player behavior and consumer ownership separately.
- [ ] Assess DeviceRadixSort/OneSweep for public SDK integration only after their
  operation contract, support, resource ownership and complete costs are reviewed;
  they are currently native-harness backends only.
- [ ] Investigate why wave-tiled scan and most tiled-sort cases trail mature
  implementations. Pass counts, conversion and scratch traffic are hypotheses
  to test; existing timings do not supply occupancy/cache-counter explanations.

These are future work items, not a resumed benchmark queue.

## v0.5 reusable SDK — implemented

- [x] Discover trusted external workload assemblies and directories.
- [x] Generate an out-of-tree plugin with `hlslperf new-workload`.
- [x] Ship a shared `.hlsli` kernel library with transitive include hashing.
- [x] Add manifest 3.0 conditional axes and implication constraints.
- [x] Write candidate-granular checkpoints and validate exact resume identity.
- [x] Require exact manifest and transitive-kernel hashes in the Unity adapter.
- [x] Pack SDK libraries and the CLI as NuGet/.NET tool packages.
- [x] Ship immutable manifest/profile/checkpoint schema files and a tag release workflow.

“Release-ready” means local packages and CI publishing machinery are verified.
It does not mean a public NuGet version has been published; publishing requires
the repository owner's tag and NuGet credential.

## v0.6 research algorithms — implemented, evidence scoped by workload

- [x] Generic uint scan operators: add, min, max, XOR.
- [x] Persistent segmented exclusive scan.
- [x] Scan + scatter stream compaction.
- [x] Stable 32-bit LSD radix sort with reused scratch.
- [x] Histogram plus exclusive prefix offsets.
- [x] Fused producer -> scan -> consumer compaction backend.
- [x] Wave32/64, scalar/`uint4`, persistent-group, item-scale, and LDS-replica axes.
- [x] Post-timing poison/re-execute correctness gate for repeated-plan integrity.

### v0.6 synthetic application proof harness — implemented and measured

- [x] External Crowd/VFX workload plugin over the public kernel ABI.
- [x] Complete visibility -> compaction -> tile offsets -> bin scatter -> raster plan.
- [x] Materialized/global-atomic baseline versus fused/replicated-LDS family.
- [x] Low, medium, high, and extreme pressure manifests with conditional axes.
- [x] CPU atlas oracle, strict DXC-only validation, and regression tests.
- [x] Whole-plan pressure grid and deadline/backlog GIF/MP4 compositor.
- [x] Run and independently repeat the explicit four-level R9700 GPU matrix;
  publish guarded timings, raw samples, hashes, and actual-frame media.

The two 2026-09-03 runs executed 1,224 candidates and recorded 13,464 timing
samples. Every candidate matched the CPU oracle; noisy candidates remain in the
raw evidence but could not win. See the
[R9700 Crowd/VFX evidence](results/R9700_CROWD_VFX_2026-09-03.md).

## v0.7 evidence credibility — TODO, not claimed

- [ ] AMD, NVIDIA, and Intel multi-GPU matrix.
- [ ] Multi-driver and multi-DXC-version regression jobs.
- [x] Randomized/interleaved candidate ordering, paired comparisons, independent confirmation and confidence intervals (already present before this repair).
- [ ] RGA register and live-VGPR evidence for the new single-pass/fused entry points.
- [ ] RGP/PIX/runtime-counter adapters for measured occupancy, cache misses, bandwidth, and power.
- [ ] Real-time interactive evidence explorer.

The v0.7 items remain intentionally open. Static register counts, single-GPU
timestamps, and inferred bandwidth are not presented as measured occupancy,
cross-vendor portability, or power evidence.

## Likely post-v0.7 algorithm extensions

- Wider 4/8-bit radix and payload sorting are implemented research candidates.
  The September 9 native comparisons measure the recorded tiled variants and
  workloads; current SDK/application deployments remain unconfirmed.
- Generic segmented operators and by-key segmentation.
- Hierarchical/fused histogram pipelines for larger bin counts.
- Extend capability predicates across the research candidate search space;
  the public primitive selector already checks shader model and wave support.
- Search strategies for very large conditional spaces; exhaustive enumeration remains preferable for the current small packs.
