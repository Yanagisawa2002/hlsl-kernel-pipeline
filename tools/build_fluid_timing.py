"""Explicit Windows x64 native timestamp plugin build; never invoke Unity or dispatch a GPU."""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess

ROOT = Path(__file__).resolve().parents[1]
def sha(path): return hashlib.sha256(Path(path).read_bytes()).hexdigest()
def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--unity-editor', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    destination = args.output.resolve()
    destination.mkdir(parents=True, exist_ok=False)
    vswhere = Path('C:/Program Files (x86)/Microsoft Visual Studio/Installer/vswhere.exe')
    vs = Path(subprocess.check_output([str(vswhere), '-latest', '-products', '*', '-requires',
        'Microsoft.VisualStudio.Component.VC.Tools.x86.x64', '-property', 'installationPath'], text=True).strip())
    headers = args.unity_editor.resolve() / 'Data/PluginAPI'
    source = ROOT / 'unity/FluidScanBenchmark/Native/FluidGpuTiming.cpp'
    compiler = sorted((vs / 'VC/Tools/MSVC').glob('*/bin/Hostx64/x64/cl.exe'))[-1]
    batch = destination / 'build.cmd'
    # Fixed, quoted paths: generated batch has no user prose or shell-interpolated body.
    for path in (destination, vs, headers, source):
        if any(c in str(path) for c in ('"','\n','\r','%','!')): raise ValueError('Unsupported shell path')
    batch.write_text(f'@echo off\ncall "{vs}/VC/Auxiliary/Build/vcvars64.bat" >nul\n'
        f'if errorlevel 1 exit /b 1\n"{compiler}" /nologo /utf-8 /std:c++17 /O2 /EHsc /W4 /WX /MD /LD '
        f'/I"{headers}" "{source}" /Fe:"{destination}/FluidGpuTiming.dll" '
        f'/Fo:"{destination}/FluidGpuTiming.obj" /link d3d12.lib dxgi.lib\n', encoding='utf-8')
    result = subprocess.run(['cmd', '/c', str(batch)], cwd=destination, capture_output=True, text=True, encoding='utf-8', errors='replace')
    (destination / 'build.log').write_text(result.stdout + result.stderr)
    if result.returncode: raise RuntimeError(result.stdout + result.stderr)
    receipt = dict(schema='hlslperf.fluid-scan.native-build.v1', sourceSha256=sha(source),
        compilerSha256=sha(compiler), dllSha256=sha(destination/'FluidGpuTiming.dll'),
        unityApiHeaders={p.name:sha(p) for p in headers.glob('IUnity*.h')},
        timing='D3D12 timestamp queries with explicit source frame and completed Unity frame fence',
        gpuDispatchExecuted=False)
    (destination / 'FluidGpuTimingBuild.json').write_text(json.dumps(receipt,indent=2)+'\n')
    print(json.dumps(receipt,indent=2))
if __name__ == '__main__': main()
