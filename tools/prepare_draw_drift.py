"""Generate an isolated diagnostic-only Player from pinned actual benchmark sources."""
import argparse,hashlib,json,shutil,subprocess
from pathlib import Path
from prepare_crossover_project import source_identity
ROOT=Path(__file__).resolve().parents[1]

def patch(s,old,new):
    if s.count(old)!=1:raise ValueError('Patch anchor drift: '+old[:90])
    return s.replace(old,new)

def native_source(s):
    s=patch(s,'#include <cstdint>','#include <cstdint>\n#include <cstring>')
    s=patch(s,'Slot slots[Capacity];','Slot slots[Capacity];\nComPtr<ID3D12QueryHeap> statsHeap; ComPtr<ID3D12Resource> statsReadback; D3D12_QUERY_DATA_PIPELINE_STATISTICS* statsMapped=nullptr;')
    s=patch(s,'ready=0; mapped=nullptr;readback.Reset();heap.Reset();fence.Reset();','ready=0; mapped=nullptr;readback.Reset();heap.Reset();fence.Reset();statsMapped=nullptr;statsReadback.Reset();statsHeap.Reset();')
    s=patch(s,'fence=api->GetFrameFence();', '''D3D12_QUERY_HEAP_DESC sq={D3D12_QUERY_HEAP_TYPE_PIPELINE_STATISTICS,Capacity,0};
  if(FAILED(device->CreateQueryHeap(&sq,IID_PPV_ARGS(&statsHeap)))) {Fail(20);return;}
  desc.Width=Capacity*sizeof(D3D12_QUERY_DATA_PIPELINE_STATISTICS);
  if(FAILED(device->CreateCommittedResource(&hp,D3D12_HEAP_FLAG_NONE,&desc,D3D12_RESOURCE_STATE_COPY_DEST,nullptr,IID_PPV_ARGS(&statsReadback)))) {Fail(21);return;}
  range.End=static_cast<SIZE_T>(desc.Width);
  if(FAILED(statsReadback->Map(0,&range,reinterpret_cast<void**>(&statsMapped)))) {Fail(22);return;}
  fence=api->GetFrameFence();''')
    s=patch(s,'recording.commandList->EndQuery(heap.Get(),D3D12_QUERY_TYPE_TIMESTAMP,base+stage);slot.stage++;', '''unsigned statsIndex=static_cast<unsigned>((id-1)%Capacity);
 if(stage==2)recording.commandList->EndQuery(statsHeap.Get(),D3D12_QUERY_TYPE_PIPELINE_STATISTICS,statsIndex);
 recording.commandList->EndQuery(heap.Get(),D3D12_QUERY_TYPE_TIMESTAMP,base+stage);slot.stage++;
 if(stage==1)recording.commandList->BeginQuery(statsHeap.Get(),D3D12_QUERY_TYPE_PIPELINE_STATISTICS,statsIndex);''')
    s=patch(s,'slot.fence=api->GetNextFrameFenceValue();','recording.commandList->ResolveQueryData(statsHeap.Get(),D3D12_QUERY_TYPE_PIPELINE_STATISTICS,statsIndex,1,statsReadback.Get(),statsIndex*sizeof(D3D12_QUERY_DATA_PIPELINE_STATISTICS));\n  slot.fence=api->GetNextFrameFenceValue();')
    s=patch(s,'lastRead=id;s.state.store(0,std::memory_order_release);return 1;','std::memcpy(output+7,&statsMapped[(id-1)%Capacity],sizeof(D3D12_QUERY_DATA_PIPELINE_STATISTICS));\n lastRead=id;s.state.store(0,std::memory_order_release);return 1;')
    return '// DIAGNOSTIC ONLY: generated from unchanged v4 native source.\n'+s

def managed_source(s):
    s=patch(s,'public int schema = 4, protocolVersion = 4','public bool diagnosticOnly=true,eligibleForPerformanceAcceptance=false;\n        public string trajectory,diagnosticSourceIdentity; public double diagnosticStartUtcMs,firstMeasuredUtcMs,lastMeasuredUtcMs,finishUtcMs; public DrawProxy[] viewProxies;\n        public int schema = 104, protocolVersion = 0')
    s=patch(s,'public int frameIndex, visibleCount,','public int viewIndex; public double submissionUtcMs,availabilityUtcMs;\n        public ulong IAVertices,IAPrimitives,VSInvocations,GSInvocations,GSPrimitives,CInvocations,CPrimitives,PSInvocations,HSInvocations,DSInvocations,CSInvocations;\n        public int frameIndex, visibleCount,')
    s=patch(s,'options = Options.Parse(Environment.GetCommandLineArgs());','options = Options.Parse(Environment.GetCommandLineArgs());\n                if(options.mode!="cpu" || options.agents!=100000 || options.warmup!=300 || (options.frames!=96 && options.frames!=1000) || options.timestamps!="on")throw new ArgumentException("Diagnostic only CPU contract");')
    s=patch(s,'processId = Process.GetCurrentProcess().Id, sourceIdentity = Identity,','diagnosticStartUtcMs=DrawDriftSupport.UtcMs,diagnosticSourceIdentity=Resources.Load<TextAsset>("draw-drift-identity").text.Trim(),\n                    processId = Process.GetCurrentProcess().Id, sourceIdentity = Identity,')
    s=patch(s,'calibration.frames != options.frames','calibration.frames != 1000')
    s=patch(s,'calibration.views.Length != options.frames','calibration.views.Length != 1000')
    s=patch(s,'result.calibrationSha256 = Hash(calibrationBytes);','DrawDriftSupport.Initialize(population,calibration);result.viewProxies=DrawDriftSupport.Proxies;result.trajectory=DrawDriftSupport.Trajectory;result.mode="draw-drift-diagnostic";\n                result.calibrationSha256 = Hash(calibrationBytes);')
    s=patch(s,'visibleCount = calibration.views[f].visible','visibleCount = calibration.views[DrawDriftSupport.ViewIndex(f)].visible,viewIndex=DrawDriftSupport.ViewIndex(f)')
    s=patch(s,'Draw(options.mode, calibration.views[index], nextFrame, sample);','if(sample!=null) {sample.submissionUtcMs=DrawDriftSupport.UtcMs;if(nextFrame==0)result.firstMeasuredUtcMs=sample.submissionUtcMs;if(nextFrame==options.frames-1)result.lastMeasuredUtcMs=sample.submissionUtcMs;}\n                Draw(options.mode, calibration.views[DrawDriftSupport.ViewIndex(index)], nextFrame, sample);')
    s=patch(s,'sample.availabilityUnityFrame=Time.frameCount;','sample.IAVertices=data[7];sample.IAPrimitives=data[8];sample.VSInvocations=data[9];sample.GSInvocations=data[10];sample.GSPrimitives=data[11];sample.CInvocations=data[12];sample.CPrimitives=data[13];sample.PSInvocations=data[14];sample.HSInvocations=data[15];sample.DSInvocations=data[16];sample.CSInvocations=data[17];sample.availabilityUtcMs=DrawDriftSupport.UtcMs;\n                sample.availabilityUnityFrame=Time.frameCount;')
    s=patch(s,'string json=JsonUtility.ToJson(result,true);','result.finishUtcMs=DrawDriftSupport.UtcMs;\n            string json=JsonUtility.ToJson(result,true);')
    return s

def main():
    p=argparse.ArgumentParser();p.add_argument('--project',type=Path,required=True);p.add_argument('--native-output',type=Path,required=True);p.add_argument('--unity',type=Path,required=True);a=p.parse_args()
    if a.project.exists() or a.native_output.exists():raise ValueError('New isolated paths required')
    subprocess.run(['python',str(ROOT/'tools/prepare_crossover_project.py'),'--project',str(a.project)],check=True)
    src=ROOT/'unity/GpuDrivenCrowdBenchmark';target=a.project/'Assets/GpuDrivenCrowdBenchmark'
    (target/'CrossoverBenchmark.cs').write_text(managed_source((src/'CrossoverBenchmark.cs').read_text(encoding='utf-8-sig')),encoding='utf-8')
    backend=(src/'NativeTimingBackend.cs').read_text();backend=patch(backend,'new ulong[7]','new ulong[18]');(target/'NativeTimingBackend.cs').write_text(backend,encoding='utf-8')
    shutil.copy2(ROOT/'unity/DrawDriftDiagnostic/DrawDriftSupport.cs',target/'DrawDriftSupport.cs')
    out=a.native_output;out.mkdir(parents=True);cpp=out/'DrawDriftTimestamp.cpp';cpp.write_text(native_source((src/'Native/CrossoverTimestamp.cpp').read_text()),encoding='utf-8')
    vswhere='C:/Program Files (x86)/Microsoft Visual Studio/Installer/vswhere.exe'
    vs=Path(subprocess.check_output([vswhere,'-latest','-products','*','-requires','Microsoft.VisualStudio.Component.VC.Tools.x86.x64','-property','installationPath'],text=True).strip())
    headers=a.unity/'Data/PluginAPI';cmd=out/'build.cmd';cmd.write_text('@echo off\nset VSLANG=1033\ncall "'+str(vs/'VC/Auxiliary/Build/vcvars64.bat')+'" >nul\nif errorlevel 1 exit /b %errorlevel%\ncl /nologo /utf-8 /std:c++17 /O2 /EHsc /W4 /WX /LD /I"'+str(headers)+'" "'+str(cpp)+'" /link /OUT:CrossoverTimestamp.dll d3d12.lib dxgi.lib\n',encoding='utf-8')
    r=subprocess.run(['cmd','/d','/c',str(cmd)],cwd=out,capture_output=True,text=True,encoding='utf-8',errors='replace');(out/'build.log').write_text(r.stdout+r.stderr,encoding='utf-8');print(r.stdout+r.stderr);r.check_returncode()
    dll=out/'CrossoverTimestamp.dll';dest=a.project/'Assets/Plugins/x86_64/CrossoverTimestamp.dll';dest.parent.mkdir(parents=True);shutil.copy2(dll,dest)
    sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest()
    inputs=[Path(__file__),ROOT/'unity/DrawDriftDiagnostic/DrawDriftSupport.cs',ROOT/'unity/DrawDriftDiagnostic/PROTOCOL.md']
    identity=hashlib.sha256(''.join(sha(p) for p in inputs).encode()).hexdigest();resources=a.project/'Assets/Resources';(resources/'draw-drift-identity.txt').write_text(identity);(resources/'crossover-plugin-identity.txt').write_text(sha(dll))
    receipt={'diagnosticOnly':True,'eligibleForPerformanceAcceptance':False,'baseSourceIdentity':source_identity(ROOT),'diagnosticIdentity':identity,'files':{str(p):sha(p) for p in [*inputs,cpp,dll,target/'CrossoverBenchmark.cs',target/'NativeTimingBackend.cs',target/'BenchmarkModel.cs',target/'Crowd.shader']}}
    (out/'build.json').write_text(json.dumps(receipt,indent=2),encoding='utf-8')
if __name__=='__main__':main()
