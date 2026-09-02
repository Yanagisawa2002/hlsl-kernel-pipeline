# Versioned schemas

These files are the public wire-format contracts shipped in the
`EdwinLiu.HlslPerf.Core` NuGet package under `schemas/`.

- `manifest.schema.v3.json`: conditional axes and implication constraints.
- `profile.schema.v2.json`: the only deployable profile format accepted by the
  current Unity consumer.
- `checkpoint.schema.v1.json`: candidate-granular resumable measurement state.

Schema versions are independent of the SDK package version. A future breaking
wire-format change gets a new file and version; an SDK release does not rewrite
old schema files in place.
