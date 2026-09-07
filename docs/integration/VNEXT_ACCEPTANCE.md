# vNext integration acceptance declaration

This declaration is written before integrated GPU timing. It defines the scope;
exact generated manifests, candidate IDs, seeds and hashes will be frozen in an
execution ledger before the first formal cell. Any later amendment retains its
predecessor and states whether results had already been observed.

## Ownership and provenance

- Source baseline: `052cfdb033fdcf1f365514403737fdc584609dc7`, branch `codex/main`.
- Integration: `codex/opt-next-hlsl-integration-20260907`.
- Hardware scope: AMD Radeon AI PRO R9700 on this Windows host.
- Build, Unity, GPU and formal timing use the shared serialized validation runner.
- No global cache clearing, clock controls, GUI, elevation or external source import.
- Record application interference and unavailable clock/thermal controls explicitly.

## Correctness and compatibility gates

1. Release solution compilation and all core tests, including statistical rejection,
   checkpoint/resume protocol and complete identity rejection tests.
2. Native ABI v1 regression and ABI v2 zero-active, one-active, partial-group,
   maximum-active, capped-overflow count, and nonzero count/argument offset checks.
   Both poison patterns must verify every declared output against an independent
   CPU oracle. Reset, count generation, bounded argument construction, barriers and
   indirect consumption belong to the measured GPU plan.
3. Stable 1/4/8-bit sorting: keys and original-index payloads, duplicate keys,
   zero/one/partial/large counts, non-digit-aligned domains and full uint keys.
   Compare complete plans, including scratch initialization and all digit passes.
4. Run actual Unity profile-consumer tests against the integrated package. Historical
   data remains explicitly historical. New profiles require matching ABI, protocol,
   implementation and independent confirmation identities; failed confirmation
   cannot become a deployed candidate.

## Bounded formal matrix

The final ledger must cover all of these cells, using fixed input and order seeds:

- Sorting: 65,537 and 262,144 elements, uint keys and key/index payloads,
  32-bit domains, actual duplicate inputs; cache-warm cells at both sizes and
  changing-input resident rotation at the larger size.
- Binary sorting controls include the valid in-repository baseline candidate
  space at the larger size. The fastest eligible measured binary control is
  retained for comparison with 4/8-bit complete plans. No weak external control.
- Dynamic compaction/consumption: zero, partial and fully active populations;
  bounded count overflow is additionally exercised by the native correctness
  harness. Compare the available fixed and indirect consumer controls fairly.
- Working sets: an existing fused compaction or single-pass scan at 4,194,304
  and 16,777,216 elements, cache-warm and three resident input slots. Rotation
  changes real deterministic input and oracle hashes and is not called cache-cold.
- RGA: actual fused/single-pass entry analysis on gfx1201, retaining stdout,
  stderr, statistics, ISA, exact source/entrypoint/defines and executable hashes.
  Absent occupancy/bandwidth/spill metrics remain null or unavailable.

Use reproducible randomized paired blocks and a separately sampled confirmation
phase with fresh declared input seeds. Freeze the selected candidate before
confirmation; do not choose another candidate using confirmation. Preserve failed,
noisy and baseline-retained results. Use declared drift, variance, paired interval
and minimum-speedup guards without relaxing them after seeing outcomes.

Bound formal matrix residency to 1 GiB per measured comparison where supported.
Keep timings within a single process/protocol cell; never combine resumed historical
timing with new timing. Broader historical version sweeps are follow-up work.

## Evidence and handoff

Keep generated manifests, raw reports/checkpoints, stdout/stderr, build and test
logs, compiler/backend binaries, file hashes and a machine-readable acceptance
ledger. A report can conclude no benefit or unsupported functionality honestly;
it must not count an unexecuted required gate as passed. Fast-forward the original
checkout only after all required code/regression gates and the final frozen-source
checks pass. Never push.
