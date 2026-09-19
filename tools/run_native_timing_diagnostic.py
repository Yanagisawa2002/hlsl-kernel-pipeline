"""Two fresh native timestamp availability processes, no performance inference."""
import argparse,hashlib,json,os,subprocess
from pathlib import Path
from run_crossover import snapshot
from prepare_crossover_project import source_identity

def require(condition):
    if not condition: raise ValueError("Native timing identity/fence/timestamp gate failed")

def validate(d):
    require(d['completed'] and not d['error'] and d['submitted']==d['resolved']==96)
    require(d['graphicsApi']=='Direct3D12' and d['ringCapacity']==32)
    require(d['renderingThreadingMode'] in ('MultiThreaded','SingleThreaded') and not d['profilerEnabled'])
    require(len(d['samples'])==96 and len({s['frequency'] for s in d['samples']})==1)
    for i,s in enumerate(d['samples']):
        require(s['submissionUnityFrame']==d['samples'][0]['submissionUnityFrame']+i)
        require(s['id']==i+1 and 0<s['t0']<s['t1']<s['t2'] and s['frequency']>0)
        require(s['completedFence']>=s['requiredFence']>0 and s['availabilityUnityFrame']>s['submissionUnityFrame'])
        for field,ticks in [('gpuCullMs',s['t1']-s['t0']),('gpuDrawMs',s['t2']-s['t1']),('gpuRangeMs',s['t2']-s['t0'])]:
            require(abs(s[field]-ticks*1000/s['frequency'])<1e-7)
    return {'passed':True,'resolved':96,'frequency':d['samples'][0]['frequency'],'minDelayFrames':min(s['availabilityUnityFrame']-s['submissionUnityFrame'] for s in d['samples']),'maxDelayFrames':max(s['availabilityUnityFrame']-s['submissionUnityFrame'] for s in d['samples'])}

def main():
    p=argparse.ArgumentParser();p.add_argument('--player',type=Path,required=True);p.add_argument('--output',type=Path,required=True);a=p.parse_args()
    out=a.output.resolve();out.mkdir(parents=True,exist_ok=True);player=a.player.resolve()
    for mode in ['normal','batch']:
        result=out/(mode+'.json');receipt=out/(mode+'.launch.json')
        if result.exists() or receipt.exists():raise ValueError('No overwrite/retry')
        command=[str(player),'-force-d3d12','-screen-fullscreen','0','-logFile',str(out/(mode+'.log')),'--mode','native-diagnostic','--output',str(result)]
        if mode=='batch':command.insert(1,'-batchmode')
        record={'command':command,'before':snapshot(),'scope':'availability/ID/fence only, not performance','assemblySha256':hashlib.sha256((player.parent/(player.stem+'_Data/Managed/Assembly-CSharp.dll')).read_bytes()).hexdigest()}
        receipt.write_text(json.dumps(record,indent=2),encoding='utf-8')
        startup=None
        if os.name=='nt':startup=subprocess.STARTUPINFO();startup.dwFlags|=subprocess.STARTF_USESHOWWINDOW;startup.wShowWindow=0
        try:record['returnCode']=subprocess.run(command,startupinfo=startup,timeout=120).returncode
        except subprocess.TimeoutExpired:record['returnCode']=None;record['error']='timeout, own child terminated'
        record['after']=snapshot();receipt.write_text(json.dumps(record,indent=2),encoding='utf-8')
        if record['returnCode']!=0:raise RuntimeError('Native process failed; retained evidence')
        data=json.loads(result.read_text(encoding='utf-8'))
        require(data['sourceIdentity']==source_identity(Path(__file__).resolve().parents[1]))
        plugin=player.parent/(player.stem+'_Data/Plugins/x86_64/CrossoverTimestamp.dll')
        require(data['pluginSha256']==hashlib.sha256(plugin.read_bytes()).hexdigest())
        assessment=validate(data);(out/(mode+'.assessment.json')).write_text(json.dumps(assessment,indent=2),encoding='utf-8');print(mode,assessment,flush=True)
if __name__=='__main__':main()
