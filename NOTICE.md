# Third-party notices

HlslKernelPipeline uses, but does not copy or modify:

- [Vortice.Windows](https://github.com/amerkoleci/Vortice.Windows), MIT License,
  for maintained .NET bindings to Direct3D 12, DXGI, and DXC.
- [DirectX Shader Compiler](https://github.com/microsoft/DirectXShaderCompiler),
  distributed by Microsoft under its repository license.

The project intentionally does not reimplement a compiler, Direct3D binding,
vendor ISA analyzer, or general-purpose autotuning search framework. Optional
future integrations may consume reports from AMD Radeon GPU Analyzer, PIX, or
other vendor tools without redistributing those tools.
