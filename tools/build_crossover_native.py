"""Build the diagnostic timestamp DLL using installed Unity PluginAPI and MSVC headers."""
import argparse,hashlib,json,subprocess
from pathlib import Path
p=argparse.ArgumentParser();p.add_argument('--unity',type=Path,required=True);p.add_argument('--output',type=Path,required=True);a=p.parse_args()
root=Path(__file__).resolve().parents[1];out=a.output.resolve();out.mkdir(parents=True,exist_ok=True)
vswhere=Path('C:/Program Files (x86)/Microsoft Visual Studio/Installer/vswhere.exe')
vs=Path(subprocess.check_output([str(vswhere),'-latest','-products','*','-requires','Microsoft.VisualStudio.Component.VC.Tools.x86.x64','-property','installationPath'],text=True).strip())
source=root/'unity/GpuDrivenCrowdBenchmark/Native/CrossoverTimestamp.cpp';headers=a.unity.resolve()/'Data/PluginAPI'
command=out/'build.cmd'
command.write_text('@echo off\nset VSLANG=1033\ncall "'+str(vs/'VC/Auxiliary/Build/vcvars64.bat')+'" >nul\nif errorlevel 1 exit /b %errorlevel%\ncl /nologo /utf-8 /std:c++17 /O2 /EHsc /W4 /WX /LD /I"'+str(headers)+'" "'+str(source)+'" /link /OUT:CrossoverTimestamp.dll d3d12.lib dxgi.lib\n',encoding='utf-8')
r=subprocess.run(['cmd','/d','/c',str(command)],cwd=out,capture_output=True,text=True,encoding="utf-8",errors="replace");(out/'build.log').write_text(r.stdout+r.stderr,encoding='utf-8');print(r.stdout+r.stderr);r.check_returncode()
files=[source,out/'CrossoverTimestamp.dll']+[headers/f for f in ['IUnityInterface.h','IUnityGraphics.h','IUnityGraphicsD3D12.h']]
(out/'build.json').write_text(json.dumps({'schema':1,'compiler':'MSVC /O2 /W4 /WX','files':[{'path':str(f),'sha256':hashlib.sha256(f.read_bytes()).hexdigest()} for f in files]},indent=2),encoding='utf-8')
