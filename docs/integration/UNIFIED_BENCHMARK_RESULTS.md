# Official R9700 external benchmark results

RTS and AMD Parallel Sort pass the required full-width semantics. Four of the 96 preregistered directional comparisons satisfy every confirmation gate; 68 remain inconclusive and 24 involving the fallback scan are not comparable because of correctness. No default implementation is changed.

The fixed 24-cell matrix completed all 120 independent processes from source `824cfafc9a07ade0a3cf440c1f5b3a19af2e8bf0`, between 2026-09-07 16:17:29 and 16:30:53 UTC. The declaration SHA-256 is `6ac4edf5dae1e5056438c0da09bf3f6c41eb9bf0e1f240339438313ab23c61e7`.

## Confirmed comparisons

Ratios are numerator whole-operation GPU time / denominator time. A value above one favors the denominator. Each interval uses five process-level paired log ratios; it does not treat repeated operations as independent samples.

| Cell | Numerator | Denominator | Ratio [95% CI] | Maximum CV | Minimum p95 ratio |
|---|---|---|---|---:|---:|
| scan-8mi-keys-uniform-1slots | internal-scan-single | gps-reduce-then-scan | 2.21124 [2.19819, 2.22436] | 4.22% | 1.9235 |
| scan-16mi-keys-uniform-1slots | internal-scan-baseline | gps-reduce-then-scan | 1.31885 [1.31365, 1.32407] | 2.12% | 1.3141 |
| scan-16mi-keys-uniform-3slots | internal-scan-baseline | gps-reduce-then-scan | 1.30439 [1.29815, 1.31066] | 4.29% | 1.1473 |
| radix-1mi-pairs-uniform-1slots | internal-radix-8 | amd-parallel-sort | 2.41331 [2.39863, 2.42808] | 4.33% | 2.2931 |

## Current internal implementation versus official baseline

The complete matrix is retained below. Inconclusive means the full preregistered gate was not satisfied, even when the mean estimate or confidence interval favors an implementation. The all-pairs table, process CV/drift/tails and every failed check are delivered in report.md, metrics.csv and audit.json.

| Cell | Official baseline | Current / official [95% CI] | Confirmation |
|---|---|---|---|
| scan-4mi-keys-uniform-1slots | gps-reduce-then-scan | 2.36246 [2.22076, 2.51320] | inconclusive |
| scan-4mi-keys-uniform-3slots | gps-reduce-then-scan | 1.51190 [1.43195, 1.59633] | inconclusive |
| scan-8mi-keys-uniform-1slots | gps-reduce-then-scan | 2.21124 [2.19819, 2.22436] | accepted |
| scan-8mi-keys-uniform-3slots | gps-reduce-then-scan | 1.58169 [1.29315, 1.93461] | inconclusive |
| scan-12mi-keys-uniform-1slots | gps-reduce-then-scan | 1.28906 [1.25097, 1.32832] | inconclusive |
| scan-12mi-keys-uniform-3slots | gps-reduce-then-scan | 1.09133 [1.03784, 1.14759] | inconclusive |
| scan-16mi-keys-uniform-1slots | gps-reduce-then-scan | 0.92202 [0.87944, 0.96666] | inconclusive |
| scan-16mi-keys-uniform-3slots | gps-reduce-then-scan | 0.94275 [0.86600, 1.02630] | inconclusive |
| radix-1mi-keys-uniform-1slots | amd-parallel-sort | 2.79896 [2.69794, 2.90377] | inconclusive |
| radix-1mi-keys-uniform-3slots | amd-parallel-sort | 2.83623 [2.79046, 2.88276] | inconclusive |
| radix-1mi-keys-duplicate-1slots | amd-parallel-sort | 2.51124 [2.41311, 2.61335] | inconclusive |
| radix-1mi-keys-duplicate-3slots | amd-parallel-sort | 2.47290 [2.36774, 2.58274] | inconclusive |
| radix-1mi-pairs-uniform-1slots | amd-parallel-sort | 2.41331 [2.39863, 2.42808] | accepted |
| radix-1mi-pairs-uniform-3slots | amd-parallel-sort | 2.28801 [2.23093, 2.34656] | inconclusive |
| radix-1mi-pairs-duplicate-1slots | amd-parallel-sort | 2.27168 [2.25826, 2.28519] | inconclusive |
| radix-1mi-pairs-duplicate-3slots | amd-parallel-sort | 2.20731 [2.15367, 2.26229] | inconclusive |
| radix-4mi-keys-uniform-1slots | amd-parallel-sort | 3.87231 [3.72185, 4.02886] | inconclusive |
| radix-4mi-keys-uniform-3slots | amd-parallel-sort | 3.71409 [3.67457, 3.75404] | inconclusive |
| radix-4mi-keys-duplicate-1slots | amd-parallel-sort | 3.27271 [3.19487, 3.35244] | inconclusive |
| radix-4mi-keys-duplicate-3slots | amd-parallel-sort | 3.12065 [2.99137, 3.25552] | inconclusive |
| radix-4mi-pairs-uniform-1slots | amd-parallel-sort | 2.76682 [2.68056, 2.85584] | inconclusive |
| radix-4mi-pairs-uniform-3slots | amd-parallel-sort | 2.41593 [2.35764, 2.47566] | inconclusive |
| radix-4mi-pairs-duplicate-1slots | amd-parallel-sort | 2.90301 [2.84970, 2.95731] | inconclusive |
| radix-4mi-pairs-duplicate-3slots | amd-parallel-sort | 2.62441 [2.56336, 2.68691] | inconclusive |

## Correctness and accounting

* Development: 101 Core tests, three synthetic protocol tests, and 246 GPU boundary cases. Of the boundary cases, 241 pass; five fallback cases fail at full32 cross-partition sums. Internal Scan and RTS each pass all 24 cases; AMD and both internal Radix arms each pass all 50 cases.
* Formal: 400 arm/process results, including 360 measured-correct results and 40 fallback correctness failures across all eight Scan cells and five processes. All native processes complete without a runtime error or device removal.
* The 4,000 full-output checks contain 3,840 passes and 160 fallback mismatches. There are 9,600 planned observations: 8,640 measured batches and 960 explicit missing timings for the incorrect arm. Measured batches contain 155,520 whole operations; 17,280 warmup and 3,040 correctness operations are separate.
* Source/binary/oracle/accounting audit verifies 38 fixed shader identities, 19,800 compilation-source hash references, all resident-slot counts, poison attempts, timestamp accounting, and one consistent device/binary identity across the 120 processes. The 50-file upstream source lock also passes independently.

The fallback source packs a prefix payload and two flag bits into uint32. A retained 6,145-element diagnostic matches until index 6,144, where expected 3,595,723,458 becomes 1,448,239,810 (difference 2^31). Small/single-partition and low-value cases pass. This is incompatibility with this benchmark's exclusive uint32 modulo-2^32 contract, not a claim that the algorithm fails for every input domain. No vendor code was changed and no input range was narrowed.

## Cost boundaries and limitations

The measured operation includes every dispatch/barrier, required scratch reset, AMD input restoration, pair AoS-to-SoA adaptation and consumer conversion. AMD uses its official four-bit default, including the capability-selected wave64 permutation; it is not a reimplementation of the algorithm. Internal implementations retain the fixed previous one-bit/eight-bit and baseline/single-pass configurations. GPUPrefixSums retains its upstream 256-thread/three-uint4 partition defaults.

For the confirmed 1 Mi pair/uniform/one-slot comparison, mean whole-operation GPU time is 0.704771 ms for internal eight-bit sorting and 0.291990 ms for AMD. The AMD total includes 0.019861 ms in input restoration/deinterleaving and 0.271324 ms in its algorithm stage. CPU recording is separately 0.044793 and 0.045245 ms/op, and amortized CPU submission 0.001205 and 0.001243 ms/op respectively. These CPU fields are not GPU times.

Stage measurements retain marker cost, including nonzero empty stages; they must not be mistaken for nonexistent data conversions. The outer batch total includes inter-operation marker gaps. Upload, readback, fixture/oracle, plan construction, PSO compilation, preparation, CPU record/close/submit/wait/reset are preserved separately in raw records. CommittedBytes covers the DEFAULT operation buffers and dummy binding; transient upload/readback/query resources are not included in that field.

Hardware is AMD Radeon AI PRO R9700, driver 32.0.31041.1004, Windows 10.0.26200. A read-only native-module capture confirms the frozen x64 DXC/DXIL 1.9.2602.17 paths and hashes. Each process records all runtime file identities. Internal shaders use HLSL 2018/SM6.6/strictness; GPS uses upstream HLSL 2021/SM6.7/no added strictness; AMD uses HLSL 2021/SM6.6 and its pinned CMake defines. Every compiled DXIL hash is retained.

Early development device failures, the missing optional debug SDK, and a shader-cache identity guard failure are preserved. Restoring the GPS upstream language/target/strictness settings resolved its propagation diagnostic; those three changes were made together, so no sole-cause attribution is made. Formal data use only the later clean source and fresh binary output.

Five processes, balanced blocks, CV<=5%, drift<=15%, all five p95 ratios>=1.01 and lower95>=1.01 were fixed before sampling. No failed cell was resampled. Tail timestamps are correlated descriptive observations; comparisons have no familywise multiplicity correction. Slot rotation uses real separate resources, but GPU clocks, global cache state, residency and other user workloads were not controlled or measured with hardware counters. Conclusions apply to this recorded workload/device/protocol, not a universal ranking.

## Reproduction and evidence

Use UNIFIED_BENCHMARK_PROTOCOL.md and the committed unified-declaration.json. The native runner and matrix wrapper are in tools/Invoke-UnifiedExperiment.ps1 and tools/Invoke-UnifiedMatrix.ps1. Recompute statistics with tools/unified_protocol.py and validate provenance/accounting with tools/audit_unified_evidence.py. The latter audit was added after sampling and does not change the frozen measurement/statistical rules.

The delivery contains raw-evidence.zip (550 files, including all native failures, boundaries, pilots, formal records and audits), frozen-runtime.zip (24 files), per-member SHA-256 evidence-manifest.json, upstream-lock.json, binary-lock.json, audit.json, supplemental-audit.json, metrics.csv and the complete all-pairs report.md.

* raw-evidence.zip SHA-256: `b6c3b54ff7ff730f2df64a29e9a872cd1ffbf5080b57f1d685c8380a357abdd4`
* frozen-runtime.zip SHA-256: `20f47de78190ad4c645506499d0139c30568905270f24725e7ebc083c2383ab4`

Official source identity and independent third-party licenses are detailed in EXTERNAL_BASELINES.md and upstream-lock.json. The coordinating integration must retain its already-authorized project benchmark-reproduction license commit `34b0533def0fca2d1c882cd99e25e58cadbe41e0`; this work does not replace project licensing with MIT or change repository visibility.
