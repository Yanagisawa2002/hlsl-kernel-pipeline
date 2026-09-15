# Crowd atlas export caller

A standalone file-based application that consumes the public, existing
`CrowdCpuRenderer` and produces usable RGBA animation atlases and BMP previews.
The caller reads your seed file, prepares one renderer, advances successive
frame windows, exports every pixel, and saves hashes after verifying file
readback. It runs after publishing, without the source checkout or shaders.

This is an example asset-export application for the repository's controlled
Crowd scene. It is **CPU execution**, not a CPU oracle, GPU fallback, external
production integration or proof of GPU speedup. `CrowdCpuRenderer` and the GPU
Crowd runtime already existed on main. This addition supplies file input,
bounded requests, consumable previews, deployment instructions and receipts.

## Try it

Build with the SDK selected by repository `global.json` (10.0.302), Python 3
for the optional demo input, and a .NET 10 runtime for the published app.
From the repository root:

```powershell
dotnet publish examples/HlslPerf.CrowdExport -c Release --self-contained false -p:UseAppHost=false -o artifacts/crowd-export
python examples/HlslPerf.CrowdExport/make_demo_input.py artifacts/crowd-input
dotnet artifacts/crowd-export/HlslPerf.CrowdExport.dll artifacts/crowd-input/request.json artifacts/crowd-output
```

Use new directories for input generation and every export. The synthetic demo
has 4,097 seeds and exports two groups of four 128x72 frames. Open
`atlas-00.bmp` and `atlas-01.bmp`; each is a horizontal strip in chronological
order. `receipt.json` names and hashes both previews and both raw atlases.
No timing or comparative performance conclusion is emitted.

To deploy, copy the **entire publish directory**, including `.deps.json`,
`.runtimeconfig.json`, both library DLLs, README, LICENSE and NOTICE. Invoke
the DLL from any directory with your request and a new output path. The caller
has no external NuGet package dependencies beyond its source project references.
No official release or package publication is implied by local publishing.

## Your input contract

```json
{
  "schemaVersion": 1,
  "backend": "cpu",
  "seedsFile": "seeds.u32",
  "width": 128,
  "height": 72,
  "framesPerAtlas": 4,
  "atlasCount": 2,
  "firstFrame": 0,
  "visibilityMask": 15,
  "workers": 1
}
```

- `seedsFile` is resolved relative to the request JSON, not the working directory.
  It contains little-endian uint32 agent seeds, at least one and at most
  16,777,216. Truncated words and oversized files are rejected before rendering.
- The input is used directly. The exporter never regenerates seeds, invokes
  `CrowdVfxWorkload` or requires expected output hashes. The demo generator is
  optional; it is separate from the application.
- Width/height: 1–512; frames per atlas: 1–12; atlas count: 1–4. The last
  emitted frame must be at most 100,000. Worker count is explicit and cannot
  exceed frames per atlas or available logical processors. Mask is `2^k-1`.
- Rendering has a 256 MiB **logical renderer storage** cap; this is not a cap
  on total process RSS. Preview encoding and readback use additional memory.
  The bounded dimensions limit each raw atlas to 12 MiB.
- Unknown JSON properties, unsupported backends and existing output directories
  fail. There is no implicit worker reduction or GPU-to-CPU substitution.

Raw `.rgba` files are frame-major, then top-down rows, then left-to-right RGBA8
pixels with opaque alpha. BMP previews use top-down 32-bit BGRX pixels; frame
`f` occupies rectangle `(f * width, 0, width, height)`. The JSON receipt includes
input/request hashes, application and renderer assembly hashes, runtime identity,
frame starts, output hashes and `gpuExecuted: false` / `performanceStatus: unmeasured`.
Hashes establish file identity, not independent numerical correctness.

Outputs use create-new files. A failed export may leave partial files for
inspection; `receipt.json` exists only after all atlases pass byte-for-byte file
readback. Do not treat an output directory alone as success. Writes include
close and readback, without a durable-storage flush guarantee.

## Validation and hardware boundary

CPU integration tests compare all pixels of two emitted windows with the
independent existing oracle, check BMP channels/order, and exercise malformed
inputs, frame bounds, unsupported backends and preservation of previous output.
The separate frozen run also publishes and executes the app outside the checkout.

The requested Ubuntu/RTX 5090 native D3D12 stage is **SKIPPED**. This application
does not add a Linux GPU backend, and its CPU output cannot validate D3D12 or
Unity. Linux execution of this new caller is also unverified in this attempt.
See the repository's `docs/RELEASE_READINESS_2026-09-16.md` for the release scope.
