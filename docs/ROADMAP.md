# Roadmap

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

## v0.6 lower-level algorithm value — implemented

- [x] Generic uint scan operators: add, min, max, XOR.
- [x] Persistent segmented exclusive scan.
- [x] Scan + scatter stream compaction.
- [x] Stable 32-bit LSD radix sort with reused scratch.
- [x] Histogram plus exclusive prefix offsets.
- [x] Fused producer -> scan -> consumer compaction backend.
- [x] Wave32/64, scalar/`uint4`, persistent-group, item-scale, and LDS-replica axes.
- [x] Post-timing poison/re-execute correctness gate for repeated-plan integrity.

## v0.7 evidence credibility — TODO, not claimed

- [ ] AMD, NVIDIA, and Intel multi-GPU matrix.
- [ ] Multi-driver and multi-DXC-version regression jobs.
- [ ] Randomized/interleaved candidate ordering, paired comparisons, and confidence intervals.
- [ ] RGA register and live-VGPR evidence for the new single-pass/fused entry points.
- [ ] RGP/PIX/runtime-counter adapters for measured occupancy, cache misses, bandwidth, and power.
- [ ] Real-time interactive evidence explorer.

The v0.7 items remain intentionally open. Static register counts, single-GPU
timestamps, and inferred bandwidth are not presented as measured occupancy,
cross-vendor portability, or power evidence.

## Likely post-v0.7 algorithm extensions

- Wider 4/8-bit radix passes and key/value payload sorting.
- Generic segmented operators and by-key segmentation.
- Hierarchical/fused histogram pipelines for larger bin counts.
- Backend capability predicates so unsupported wave sizes are eliminated before compilation.
- Search strategies for very large conditional spaces; exhaustive enumeration remains preferable for the current small packs.
