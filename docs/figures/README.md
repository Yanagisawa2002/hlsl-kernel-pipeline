# Reproducible portfolio figures

From the repository root:

```sh
python -m pip install -r tools/requirements-portfolio.txt
python tools/generate_portfolio_figures.py --check
python tools/generate_portfolio_figures.py
```

Python 3.14, matplotlib 3.10.7, NumPy 2.5.2 and Pillow 12.0.0 were used. Matplotlib's bundled DejaVu Sans is embedded as paths in SVG. Fixed geometry, an SVG hash salt and no timestamp metadata make repeat generation in the same environment byte deterministic. No seaborn or remote data.

Both charts start at zero and directly label point means; no error bars are invented. Paired uncertainty is in the linked source reports. The scan figure is complete GPU-operation latency, not application latency. The Crowd figure is lifecycle cost for the CPU-input / buffered-export caller, not FPS or a claim about all GPU rendering. It displays five selected arms from the full nine-configuration cohort.

SVG is used in the repository README, with PNG equivalents for viewers without SVG support. Original result documents and evidence are unchanged. Numeric provenance is in [the data index](../data/README.md).
