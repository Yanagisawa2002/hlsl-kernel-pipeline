"""Normal/batch A/B API-availability probes; not timing-quality pilots."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess
from run_crossover import snapshot
from check_crossover_timing import diagnostic_status, pacing_status


def main():
    p=argparse.ArgumentParser(description=__doc__); p.add_argument('--player',type=Path,required=True); p.add_argument('--output',type=Path,required=True)
    p.add_argument('--api',choices=['legacy','profiler-recorder'],required=True)
    p.add_argument('--gpu-profiler-area',choices=['unchanged','enabled'],default='unchanged')
    a=p.parse_args(); player=a.player.resolve(); out=a.output.resolve(); out.mkdir(parents=True,exist_ok=True)
    for batch in (False,True):
        name=a.api+('-batch' if batch else '-normal'); result=out/(name+'.json'); receipt=out/(name+'.launch.json')
        if result.exists() or receipt.exists(): raise ValueError('No overwrite/retry: '+name)
        command=[str(player),'-force-d3d12','-screen-fullscreen','0','-screen-width','1280','-screen-height','720','-logFile',str(out/(name+'.log')),
                 '--mode','timing-diagnostic','--timing-api',a.api,'--gpu-profiler-area',a.gpu_profiler_area,'--output',str(result)]
        if batch: command.insert(1,'-batchmode')
        record={'protocolVersion':2,'command':command,'scope':'availability/attribution only, not quiet performance evidence',
                'assemblySha256':hashlib.sha256((player.parent/(player.stem+'_Data/Managed/Assembly-CSharp.dll')).read_bytes()).hexdigest(),
                'before':snapshot(),'after':None,'returnCode':None}
        receipt.write_text(json.dumps(record,indent=2))
        startup=None
        if os.name=='nt':
            startup=subprocess.STARTUPINFO(); startup.dwFlags|=subprocess.STARTF_USESHOWWINDOW; startup.wShowWindow=0
        try: record['returnCode']=subprocess.run(command,startupinfo=startup,timeout=120).returncode
        except subprocess.TimeoutExpired: record['error']='process timeout, killed by runner'
        record['after']=snapshot(); receipt.write_text(json.dumps(record,indent=2))
        if record['returnCode']!=0 or not result.exists(): raise RuntimeError(name+' failed to complete; inspect retained logs')
        d=json.loads(result.read_text()); status=diagnostic_status(d)
        status['pacingDiagnosticOnly']=pacing_status([r['updateIntervalMs'] for r in d['observations'][1:] if r])
        (out/(name+'.assessment.json')).write_text(json.dumps(status,indent=2))
        print(name,json.dumps(status),flush=True)


if __name__=='__main__': main()
