# Provenance and ownership

HlslKernelPipeline was started on 2026-09-01 as independent personal work by
Edwin Liu.

The repository is clean-room:

- no employer, client, or unrelated project source, assets, configuration,
  benchmark captures, or Git history are included;
- the bundled workloads are synthetic and authored for this repository;
- the reduction, exclusive-scan, transpose providers, CPU oracles, ABI, D3D12
  executor, RGA adapter, and Unity profile consumer were authored here;
- external compiler and API bindings are consumed as packages and documented
  in NOTICE.md;
- generated device profiles contain hardware and toolchain fingerprints, not
  project content.

The Unity directory is a standalone UPM package and does not contain or point to
an employer/client Unity project. Optional RGA binaries used for validation were
downloaded from AMD's official release into the ignored `.hlslperf` tool cache
and are not redistributed or committed.

The initial license intentionally grants no external rights. Choose an
open-source license only when the public-release and contribution policy is
deliberate.
