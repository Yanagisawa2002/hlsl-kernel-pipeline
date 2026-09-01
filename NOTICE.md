# Third-party notices

HlslKernelPipeline uses, but does not copy or modify:

- [Vortice.Windows](https://github.com/amerkoleci/Vortice.Windows), MIT License,
  for maintained .NET bindings to Direct3D 12, DXGI, and DXC.
- [DirectX Shader Compiler](https://github.com/microsoft/DirectXShaderCompiler),
  distributed by Microsoft under its repository license.
- [AMD Radeon GPU Analyzer](https://github.com/GPUOpen-Tools/radeon_gpu_analyzer),
  MIT License, is an optional separately downloaded CLI used to produce static
  evidence. No RGA binary or source is redistributed by this repository.
- [FFmpeg](https://ffmpeg.org/legal.html) is an optional external CLI used to
  encode showcase PNG frames as GIF/MP4. No FFmpeg binary or source is
  redistributed by this repository.

The project intentionally does not reimplement a compiler, Direct3D binding,
vendor ISA analyzer, or general-purpose autotuning search framework. The RGA
adapter invokes an external installation and parses its documented output.
