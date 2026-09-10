# GPU primitives: integration and evidence

The project demonstrates reusable GPU primitive execution, trustworthy
measurement and choosing mature backends. Comparing my kernels with external
implementations exposed substantial gaps. The resulting work combines explicit
RTS/AMD integration with research on specific internal candidates, while keeping
source identities, full operation costs and fallback visible to an application.

The [application example](../../examples/HlslPerf.PrimitiveApp/README.md) builds
real count and draw-order plans through public APIs on the CPU, and includes
compiled application-owned D3D12 recording/fence code. It has no measured GPU
performance claim. [SDK first use](../SDK.md#first-use-application-data-to-an-explicit-plan)
and [September 10 change record](../integration/POSITIONING_2026-09-10.md) describe
what is available and what remains unverified.

The latest [September 9 native report](../results/R9700_NATIVE_CONFIRMATION_2026-09-09.md)
retains all seven comparisons: tile4 beats GPUSorting's vendored FFX only on its
`2^25` workload; the other six favor the external baseline. DeviceRadixSort and
OneSweep are native-harness options, not SDK options. These results neither
promote a default nor confirm SDK/Unity deployment.

## Preserved September 7 figure

`overview.svg`, `overview.png` and `figure.json` are unchanged historical
artifacts. They show the single-pass execution schematic and two September 7
Scan comparisons against an internal baseline, with their 95% intervals. They
do not depict the later external results or the current application example.

## Reproduce the historical figure

From the repository root:

```bash
python -m pip install -r docs/portfolio/requirements.txt
python docs/portfolio/render.py
```

The renderer verifies source SHA-256 hashes (CRLF normalized to LF) before plotting the reviewed values in `figure.json`. If a source changes, review and refresh the snapshot before regenerating. It writes SVG and PNG with matching content.

## Sources

- [docs/results/R9700_VNEXT_INTEGRATION_2026-09-07.md](../../docs/results/R9700_VNEXT_INTEGRATION_2026-09-07.md)

The flow/memory/timing illustrations are schematics. Only explicitly labeled measurements represent recorded experiments. Confidence intervals are copied from source reports, not recomputed.
