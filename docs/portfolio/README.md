# Portfolio figure

Single-pass execution schematic with the two independently confirmed Scan results and their 95% intervals.

## Reproduce

From the repository root:

```bash
python -m pip install -r docs/portfolio/requirements.txt
python docs/portfolio/render.py
```

The renderer verifies source SHA-256 hashes (CRLF normalized to LF) before plotting the reviewed values in `figure.json`. If a source changes, review and refresh the snapshot before regenerating. It writes SVG and PNG with matching content.

## Sources

- [docs/results/R9700_VNEXT_INTEGRATION_2026-09-07.md](../../docs/results/R9700_VNEXT_INTEGRATION_2026-09-07.md)

The flow/memory/timing illustrations are schematics. Only explicitly labeled measurements represent recorded experiments. Confidence intervals are copied from source reports, not recomputed.
