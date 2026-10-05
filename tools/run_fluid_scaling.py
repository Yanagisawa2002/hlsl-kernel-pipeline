"""Explicit sequential GPU experiment launcher; fresh outputs and immutable Player identities."""
import argparse
import ctypes
import datetime
import hashlib
import json
from pathlib import Path
import subprocess
import time
from analyze_fluid_benchmark import summarize
from analyze_fluid_sweep import process as sweep_process

ARMS = ('original','hlsl-wave-tiled','gpuprefixsums-rts','hlsl-wave-tiled-direct')
LENGTHS = (32768,65536,131072,262144,410758,524288,1048576,2097152,4194304,8388608,16000000)

def digest(path): return hashlib.sha256(path.read_bytes()).hexdigest()
def schedule(stage, densities, app_frames=600):
    cases=[]
    if stage == 'gate':
        return [dict(name='gate-'+arm,arm=arm,mode='validate-only',repeat=0) for arm in ARMS]
    if stage == 'sweep-pilot':
        return [dict(name='pilot-'+arm,arm=arm,mode='scan-sweep',repeat=0,lengths=[32768,410758,16000000],batch=8,warmup=30,frames=30) for arm in ARMS]
    if stage == 'application-pilot':
        return [dict(name=f'pilot-d{density}-{arm}',arm=arm,mode='benchmark',repeat=0,density=density,warmup=120,frames=120)
                for density in densities for arm in ('original','hlsl-wave-tiled-direct')]
    for repeat in range(1,5):
        order = ARMS[repeat-1:]+ARMS[:repeat-1]
        if stage == 'sweep':
            lengths = list(LENGTHS)
            if repeat == 2: lengths.reverse()
            if repeat == 3: lengths = lengths[4:]+lengths[:4]
            if repeat == 4: lengths = (lengths[4:]+lengths[:4])[::-1]
            cases += [dict(name=f'sweep-r{repeat:02}-{arm}',arm=arm,mode='scan-sweep',repeat=repeat,lengths=lengths,batch=32,warmup=60,frames=120) for arm in order]
        else:
            for density in densities:
                cases += [dict(name=f'app-d{density}-r{repeat:02}-{arm}',arm=arm,mode='benchmark',repeat=repeat,density=density,warmup=120,frames=app_frames) for arm in order]
    return cases

def main():
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('stage',choices=['gate','sweep-pilot','sweep','application-pilot','application'])
    p.add_argument('--player',required=True,type=Path);p.add_argument('--output',required=True,type=Path)
    p.add_argument('--densities',default='60,200,600,1500');p.add_argument('--timeout',type=int,default=600)
    p.add_argument('--app-frames',type=int,default=600,help='Same declared measured window for every application size and arm')
    a=p.parse_args();player=a.player.resolve();root=a.output.resolve();root.mkdir(parents=True,exist_ok=False)
    densities=[int(x) for x in a.densities.split(',')]
    if any(x<=0 for x in densities) or len(set(densities))!=len(densities):raise ValueError('Invalid densities')
    if not 120 <= a.app_frames <= 100000:raise ValueError('Application frames must be at least 120')
    cases=schedule(a.stage,densities,a.app_frames)
    identity={p.relative_to(player.parent).as_posix():digest(p) for p in sorted(player.parent.rglob('*')) if p.is_file()}
    plan=dict(schema='hlslperf.fluid-scan.execution-plan.v1',createdUtc=datetime.datetime.now(datetime.timezone.utc).isoformat(),
              stage=a.stage,playerFilesSha256=identity,cases=cases,recording=False,
              policy='sequential fresh processes; four cyclic arm orders; keep every attempt; fail on incomplete output')
    (root/'plan.json').write_text(json.dumps(plan,indent=2)+'\n',encoding='utf-8')
    # Cooperate with the repository's other local GPU experiments, without touching user applications.
    win=ctypes.WinDLL('kernel32',use_last_error=True)
    win.CreateMutexW.argtypes=[ctypes.c_void_p,ctypes.c_bool,ctypes.c_wchar_p];win.CreateMutexW.restype=ctypes.c_void_p
    win.WaitForSingleObject.argtypes=[ctypes.c_void_p,ctypes.c_uint32];win.WaitForSingleObject.restype=ctypes.c_uint32
    win.ReleaseMutex.argtypes=[ctypes.c_void_p];win.CloseHandle.argtypes=[ctypes.c_void_p]
    mutex=win.CreateMutexW(None,False,'Local\\CodexR9700VNextUnityGpu')
    wait=win.WaitForSingleObject(mutex,0)
    if not mutex or wait not in (0,0x80):raise RuntimeError('Another cooperating GPU experiment owns the device lock')
    try:
        for case in cases:
            if digest(player)!=identity[player.name]:raise RuntimeError('Player changed during execution')
            folder=root/case['name'];log=root/(case['name']+'.log')
            args=[str(player),'-force-d3d12','-screen-fullscreen','0','-screen-width','1920','-screen-height','1080','-popupwindow',
                  '-logFile',str(log),'--fluid-'+case['mode'],'--fluid-arm',case['arm'],'--fluid-output',str(folder),'--fluid-seed','42','--fluid-dt','0.016666667']
            if case['mode']!='validate-only':
                args+=['--fluid-gpu-timing','d3d12-query','--fluid-warmup',str(case['warmup']),'--fluid-frames',str(case['frames'])]
            if 'density' in case:args+=['--fluid-spawn-density',str(case['density'])]
            if 'lengths' in case:args+=['--fluid-lengths',','.join(map(str,case['lengths'])),'--fluid-scan-batch',str(case['batch'])]
            print('START',case['name'],flush=True)
            started=datetime.datetime.now(datetime.timezone.utc).isoformat();tick=time.perf_counter()
            startup=subprocess.STARTUPINFO();startup.dwFlags|=subprocess.STARTF_USESHOWWINDOW;startup.wShowWindow=1
            child=subprocess.Popen(args,startupinfo=startup)
            try:code=child.wait(timeout=a.timeout)
            except subprocess.TimeoutExpired:child.kill();child.wait();code=-1
            receipt=dict(**case,pid=child.pid,command=args,startedUtc=started,endedUtc=datetime.datetime.now(datetime.timezone.utc).isoformat(),
                         processSeconds=time.perf_counter()-tick,exitCode=code,playerSha256=identity[player.name])
            if folder.exists():(folder/'process.json').write_text(json.dumps(receipt,indent=2)+'\n',encoding='utf-8')
            (root/(case['name']+'.process.json')).write_text(json.dumps(receipt,indent=2)+'\n',encoding='utf-8')
            if code!=0:raise RuntimeError(case['name']+' failed; retained '+str(log))
            gate=json.loads((folder/'correctness.json').read_text(encoding='utf-8'))
            if gate['status']!='passed' or not gate['fullOutputValidated']:raise RuntimeError('Correctness failed')
            if case['mode']=='scan-sweep':
                _,result=sweep_process(folder)
                print('DONE',case['name'],{n:{k:round(v['p50Ms'],7) for k,v in cell.items()} for n,cell in result.items()},flush=True)
            elif case['mode']=='benchmark':
                run=json.loads((folder/'run.json').read_text(encoding='utf-8'));result=summarize(folder)
                if not run['positionFiniteValidated'] or not result['gpuDelayValidated'] or any(m['status']!='observed' for m in result['metrics'].values()):
                    raise RuntimeError('Incomplete application evidence')
                (folder/'summary.json').write_text(json.dumps(result,indent=2)+'\n',encoding='utf-8')
                print('DONE',case['name'],'N',run['particles'],{k:round(v['p50Ms'],6) for k,v in result['metrics'].items()},flush=True)
            else:print('DONE',case['name'],gate['scanCases'],gate['sortCases'],gate['spatialOffsetCases'],flush=True)
        final_identity={p.relative_to(player.parent).as_posix():digest(p) for p in sorted(player.parent.rglob('*')) if p.is_file()}
        if final_identity != identity:raise RuntimeError('Player files changed during execution; retain and exclude this stage')
        (root/'completion.json').write_text(json.dumps(dict(status='passed',processes=len(cases),playerFilesVerifiedAtCompletion=True),indent=2)+'\n',encoding='utf-8')
    finally:
        win.ReleaseMutex(mutex);win.CloseHandle(mutex)
if __name__=='__main__':main()
