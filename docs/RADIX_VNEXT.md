# Stable wide radix candidates

The original `manifests/radix-sort.json` remains the binary key-only baseline.
`radix-wide.json` retains its entire candidate set and adds genuine 4-bit and
8-bit LSD passes through `HLSLPERF_RADIX_BITS=1|4|8`. No unmeasured wide candidate
becomes a production default. All shader code is independently authored in this
repository; no external AMD/SUMMIT/employer source was imported.

Each wide digit builds a **bin-major block histogram**, performs the existing
hierarchical exclusive addition scan over that histogram, and scatters records.
The scan yields the sum of lower bins plus preceding blocks in the current bin.
The local rank counts matching earlier records in the block. Atomic arrival
order is used only for counts and never for stable ordering. This portable
ranking implementation costs O(block size squared) per block: fewer digit passes
do not establish a performance advantage. Group size, records per thread and
scan backend remain explicit candidate axes. Unused tail bins are overwritten
with zeros and the final digit uses exactly the remaining key bits.

## Data and ABI contracts

- `radix-sort-u32-v1`: unsigned keys, one uint per record.
- `radix-sort-pairs-u32-v1`: interleaved `[key, payload]` uint records during
  sorting. Payloads in generated workloads are original input indices, so an
  independent comparison-sort oracle detects loss, duplication and instability.
- `RadixSortContract.Pack` and `StableOracle` also accept arbitrary uint payloads;
  payload numeric value never determines equal-key ordering.
- `bitCount=1..32`; `keyDomain=1` (default) constrains generated keys to that bit
  domain, preserving historical inputs. `keyDomain=2` preserves all 32 key bits
  and sorts stably by the selected low bits. No signed-key reinterpretation.
- `keyPattern=1` random, `2` seven duplicate values, `3` all equal, `4` descending,
  `5` zero / UINT_MAX / sign-bit / one extremes. `seed` changes actual random
  inputs, not just metadata.
- ABI v1 positive-count compatibility hashes the entire packed output including
  payloads. ABI v2 emits separate `sorted-keys` and `sorted-payloads` in a final
  split pass and verifies both with independent hashes. Its passes have explicit
  producer dependencies. Existing root bindings and constructors are unchanged.
- Zero elements require v2. A single empty pass writes a deterministic physical
  sentinel (one zero uint for keys or two for pairs) while LogicalItemCount stays
  zero. The v2 split emits one zero uint per physical output. The sentinel is not
  a logical record. Positive counts include partial blocks and singleton inputs.
- Buffer lengths use signed int bytes: maximum record count is floor(INT_MAX/4)
  for keys and floor(INT_MAX/8) for pairs. Wide histograms additionally require
  `ceil(count/blockSize) * 2^radixBits * 4 <= INT_MAX`. Each wide block supports
  at most 1024 records. Reject invalid dimensions before allocating the oracle.
  These are address limits, not a promise that maximum allocations fit VRAM/RAM.

## Accounting and executable validation

`RadixSortContract.Describe` reports digit iterations, actual dispatch pass count,
total allocated bytes and scratch bytes. Scratch excludes initialized input and
all verified outputs, but includes ping-pong records, histograms and every scan
level. For split v2 output, both packed buffers count as scratch. GPU timestamp
scope includes histogram, scan hierarchy/offset propagation, scatter, empty or
split passes and transitions for the whole plan. CPU oracle, allocation, upload,
poison and readback are outside timing; there is no omitted GPU reset/setup.

Run from the repository root **inside the shared validation mutex**:

```powershell
dotnet test tests/HlslPerf.Core.Tests/HlslPerf.Core.Tests.csproj -c Release
dotnet run --project tools/RadixSmoke/RadixSmoke.csproj -c Release --no-launch-profile -- . artifacts/radix-smoke
```

The harness writes a predeclared `matrix.json`, exact per-cell manifests, raw
TuningRunReports, device/compiler/source identity, binary hashes and `summary.json`
with costs and diagnostic complete-plan times. Required smoke cells include
0/1/127/128/129/257/4097/262147/1048579 records; bits 1/4/5/9/31/32; random,
duplicates, equal, descending and extreme keys; both key-only and key/payload;
binary/4/8 digits and v1 packed compatibility. It fails on any compile/hash error
and requires both v2 pair outputs on both poison attempts. Timing is explicitly
historical correctness smoke, not paired performance or deployment evidence.

## Formal comparison gate, owned by project integration

Use the merged measurement protocol, never reinterpret historical smoke as paired
data. Freeze an informative grid before calibration: keys and pairs at 4097 and
262147 records with 32-bit random keys, plus 262147 duplicate-key pairs. Use fresh
deterministic seeds for independent confirmation. Run all binary candidates in
the corresponding wide manifest to identify the fastest valid in-repository
binary baseline; then compare wide candidates against that frozen baseline using
the same complete-plan scope, ABI/output format, inputs and protocol. Do not use
the manifest's historical default baseline as proof it is fastest. Do not compare
key-only time with key/payload time or omit the v2 split from either side.

The new manifests use the current manifest timing protocol settings; integrators
must stamp the merged paired protocol and its calibration/confirmation settings
explicitly. Required evidence is raw paired blocks, drift checks, confidence
intervals, independent confirmation and exact source/binary/driver identity.
If no wide candidate clears the gates, retain the fastest valid binary path and
report that outcome. Broad historical matrices and an external FidelityFX adapter
are not prerequisites. No measured benefit is claimed by this document.
