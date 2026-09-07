# R9700 paired protocol smoke: correct execution, rejected timing

The native short smoke passed execution and resume checks. It did **not** establish
a deployable performance advantage. Both measured attempts failed the predeclared
5% raw timing-variation and 15% baseline-drift gates; no profile was emitted.
No samples were removed and no thresholds were relaxed.

Hardware: AMD Radeon AI PRO R9700, D3D12, driver 32.0.31041.1004. The sampled binary
was built from `dcc5baa22e06d118584e27a28f4da36f7cc28113`; raw observations retain
runner/workload/source/native-DXC/actual-DXIL hashes. Sampling occurred on
2026-09-07 13:28:55–13:29:01 UTC+08 under the shared R9700/Unity validation mutex.
Temperature, clocks, and external application interference were not controlled.

The checked-in `manifests/measurement/paired-smoke.json` declared 262,144 uint-mix
items, 8 ALU rounds, group sizes 128/256, baseline 256, 64 plans per timestamp,
at least 5 ms warmup, minimum batch 0.01 ms, three resident seed slots, and six
calibration plus six confirmation blocks. The confirmation phase sampled only the
frozen calibration selection (the retained baseline in these attempts).

| Attempt | Calibration geometric speedup, 95% CI | Confirmation baseline self-control, 95% CI | Deployment |
|---|---|---|---|
| First | 1.0381, [0.8677, 1.2420] | 0.9247, [0.6810, 1.2555] | Rejected: noise and baseline drift |
| Interrupted-checkpoint restart | 0.9744, [0.7695, 1.2339] | 1.0519, [0.7887, 1.4031] | Rejected: noise and baseline drift |

These wide intervals describe this short smoke only. The self-control is not a
claim that the baseline outperformed itself and is not gated on improvement.

Validation:

- 96 independently sampled positions across two sessions, all correctness checks passed.
- Three resident slots per position, two poison patterns per output: 576 checks passed.
- Calibration and confirmation input seeds and actual root-constant/buffer identities were disjoint.
- Complete checkpoint replay preserved the original session and sampled zero new candidates.
- A deliberately constructed partial checkpoint containing the first five genuine observations
  was archived byte-for-byte; restart created a new session and sampled all 48 positions.
  This tests interruption recovery; it was not a GPU crash or forced process termination.
- 59 unit tests passed in the native smoke build. The final code validation added the
  output-history regression test: 60/60 tests passed, Release CLI build 0 warnings/0 errors.
- Four real paired checkpoint files validate against `checkpoint.schema.v2.json`;
  all checked-in schemas pass JSON Schema 2020-12 schema validation.

The archive also preserves the preceding failed smoke. That attempt reached plan
preparation but rejected uint-mix because input hashing initially omitted its
root-constant-only seed; it also exposed an all-failure CLI summary null access.
Both defects were fixed and covered before the successful smoke. That earlier
attempt is historical failure evidence, not timed GPU evidence.

[Raw evidence archive](data/paired-protocol-r9700-20260907/raw-evidence.zip) contains
both sampled attempts, the complete replay, the five-record historical partial,
the earlier failed attempt, all logs and the final test receipt. The adjacent
SHA256SUMS file covers the archive. Existing historical published results remain
under their original protocol and acquire none of these new guarantees.

Still required from project integration: final merged candidate-set 8+8-block
calibration/confirmation runs, radix/dynamic/cache-scenario cells, measured
deployment decisions, and actual Unity consumer tests on the merged package.
The worker implementation is ready for integration; the project performance
acceptance gates are not satisfied by this smoke.
