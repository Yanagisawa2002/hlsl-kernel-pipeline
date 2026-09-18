# Live GPU-driven Crowd hardware evidence — 2026-09-17

Hardware validation of commit `715dd13b0aef9049095e6530be213bf18bf23986` on an NVIDIA GeForce RTX 4090. See the [validation report](../../results/LIVE_GPU_DRIVEN_VALIDATION_2026-09-17.md) for conclusions and limits.

| Evidence | What it records |
| --- | --- |
| [Native fused](native-fused.png), [native wave-tiled](native-wave-tiled.png) | Successful live windows at 1,048,576 agents; instantaneous overlay readings, not benchmark estimates. |
| [Unity 160,000](unity-160000.png), [Unity 1,000,000](unity-1000000.png) | Game View captures at two total agent counts; only the culled subset is drawn. |
| [Unity runtime samples](unity-runtime.txt) | Changing view positions and visible-count telemetry at two times per agent count. |
| [RenderDoc GPU chain](renderdoc-gpu-chain.png) | Actual captured dispatch, append counter, CopyStructureCount and indirect draw. |
| [Pipeline extraction](pipeline-evidence.txt) | Event IDs, resource identities, argument bytes and draw bindings. |
| [Cull shader](cull-shader.txt), [draw shader](draw-shader.txt) | Captured compiled shader instructions for append and visible-ID consumption. |
| [Native fused log](logs/native-fused.log), [native wave-tiled log](logs/native-wave-tiled.log) | Initial SDK-resolution failures only; these are not successful-run logs. |
| [Unity Editor log](logs/unity-editor.log) | Environment and runtime diagnostics, including the Editor Search exception; sanitized as described below. |

## Provenance and handling

Files were collected from `D:/CodexValidation/hlsl-live-evidence-20260917/`. Screenshots are copied byte-for-byte. Extracted text content is preserved; trailing whitespace in `draw-shader.txt` and the sanitized Unity log is trimmed, and Git may normalize text line endings. The Unity log preserves diagnostics but redacts access-token fragments, licensing/session/machine identifiers, local player-connection details and user/host names. The unmodified log remains in the local evidence directory.

The original RenderDoc 1.46 capture, `unity-gpu_frame17666.rdc`, is **not committed**: 8,886,071 bytes (8.47 MiB) is unnecessary binary weight for this lightweight documentation package. It was retained locally in the directory above; that local path is not a public download. Its SHA-256 is:

```text
8593acfe9166b18ef962a4489515d55a94b8b07ed22d20821c742685e631a107
```

Independent replay requires the original capture. The committed screenshot, extraction and disassembly make the recorded command/data chain inspectable without claiming to replace replay.

Empty successful native rerun logs (`native-*-sdk302.log`), duplicate first screenshots, temporary scripts/configuration and failed screenshot-layout/replay diagnostics are omitted. SDK installation resolved the initial native launch failure; the successful windows are evidenced by the PNGs.

**This package establishes architecture execution on hardware, not a CPU-vs-GPU performance advantage.** Native samples are instantaneous; Unity telemetry is asynchronous and may describe a different frame from the RenderDoc capture.
