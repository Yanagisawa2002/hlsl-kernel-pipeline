# Crowd export single-attempt receipt

## Frozen scope

Base: `723e0dd1d0fbe0912811a4bab19e8ea1a10ac630`, refreshed from GitHub main.
One candidate: standalone CPU file-to-atlas caller and release documentation.
No changes to the existing renderers, kernels, primitive APIs or PR #4.

The bounded validation runner is `tools/validate_crowd_export.py`. It restores
from the local cache, builds the solution, runs the CPU tests and existing
PrimitiveApp, packs and inspects the workload package, publishes the new caller,
then executes it once outside the checkout and checks every exported preview pixel.
The run stops at the first failure, without repairs or reruns.

## Hardware and adoption

Requested Ubuntu RTX 5090 native D3D12 stage: **SKIPPED**. No remote connection,
GPU dispatch, new backend or hardware measurement is part of this candidate.
The new application is a standalone controlled-scene example. No external
production host was supplied, so external GPU-caller acceptance remains open.
CPU validation cannot close that gate. No performance benefit is claimed.

## Validation outcome

Pending the one frozen run. Evidence and exact candidate SHA are appended during
documentation-only closeout; implementation and test files remain frozen.
