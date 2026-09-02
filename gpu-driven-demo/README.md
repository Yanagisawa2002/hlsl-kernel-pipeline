# GPU-driven Crowd/VFX application demo

This standalone demo turns the v0.6 primitives into a complete, picture-producing
application pipeline. It remains outside Unity and can be loaded as an external
HlslPerf workload plugin. The scene is generated entirely from deterministic
integer seeds; it contains no employer/client source, art, captures, or project
configuration.

Every submitted scene frame performs:

1. crowd/particle visibility classification;
2. stable visible-list compaction;
3. screen-tile histogram and exclusive prefix offsets;
4. scatter into per-tile seed lists;
5. deterministic tiled compute rasterization into an RGBA frame atlas.

The declared baseline materializes an `N`-element flag buffer, performs a
hierarchical scan, scatters visible seeds, and uses globally contended tile
atomics. The optimized family fuses producer -> scan -> scatter through
decoupled look-back and evaluates replicated LDS tile histograms before merging
them globally. Both paths render the same resolution, particles, colors, and
frames. A CPU oracle gates the final atlas by SHA-256 after the backend poisons
and re-executes the already-timed plan.

## Safety gate

`validate` is CPU-only: it loads manifests, expands candidates, creates plans,
and verifies the deterministic oracle without creating a D3D12 device.

    dotnet run --project src/HlslPerf.GpuDrivenDemo -c Release -- validate

GPU execution is an explicit command and should only be used when the device is
available:

    dotnet run --project src/HlslPerf.GpuDrivenDemo -c Release -- run --level all

The four planned measurement levels are low (262K agents), medium (1M), high (4M), and
extreme (8M), each with twelve application frames. Their alive/LOD masks keep
roughly 4K instances in the viewport, so world-processing pressure rises without
turning the visual comparison into an overdraw or quality comparison. The runner writes raw reports,
a pressure-grid summary, and an actual-frame GIF/MP4 for the strongest guarded
budget crossing.

No Crowd/VFX performance result is claimed in the repository until that command
has completed on a named GPU/driver and passed correctness plus stability gates.
