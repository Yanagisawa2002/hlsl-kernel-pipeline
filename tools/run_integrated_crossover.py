"""Run only the authorized v4 checkpoint. Never launches the formal matrix."""
import argparse,hashlib,json,os,subprocess,time,uuid
from pathlib import Path
from run_crossover import snapshot
from prepare_crossover_project import source_identity
from check_integrated_crossover import require,validate,quiet_status

def read(p):return json.loads(p.read_text(encoding='utf-8-sig'))
def write(p,d):p.write_text(json.dumps(d,indent=2)+'\n',encoding='utf-8')
def launch(player,out,name,arm,frames=1000,timestamps='on',quality=False,agents=100000,density=.25):
    output=out/(name+'.json');receipt=out/(name+'.launch.json');require(not output.exists() and not receipt.exists(),'no overwrite/retry: '+name)
    calibration=out/f'calibrate-{agents}-{density:g}-{frames}.json'
    command=[str(player),'-force-d3d12','-screen-fullscreen','0','-screen-width','1280','-screen-height','720','-logFile',str(out/(name+'.log')),'--mode',arm,'--agents',str(agents),'--density',str(density),'--seed','69501203','--frames',str(frames),'--warmup-frames','300','--timing-api','native','--timestamps',timestamps,'--run-id',str(uuid.uuid4()),'--output',str(output)]
    if arm!='calibrate':command+=['--calibration',str(calibration)]
    record={'protocolVersion':4,'command':command,'status':'preflight','quality':quality,'before':[]}
    for _ in range(3 if quality else 1):
        record['before'].append(snapshot())
        if quality:time.sleep(1)
    write(receipt,record)
    if quality and not quiet_status(record['before']):
        record['status']='blocked: background GPU activity';write(receipt,record);raise RuntimeError(record['status'])
    startup=None
    if os.name=='nt':startup=subprocess.STARTUPINFO();startup.dwFlags|=subprocess.STARTF_USESHOWWINDOW;startup.wShowWindow=0
    try:
        record['returnCode']=subprocess.run(command,startupinfo=startup,timeout=900).returncode
        record['after']=snapshot();write(receipt,record)
        require(record['returnCode']==0 and output.exists(),'Player failed')
        d=read(output);require(d['sourceIdentity']==source_identity(Path(__file__).resolve().parents[1]),'source identity mismatch')
        if arm=='calibrate':
            record['status']='accepted';write(receipt,record);return d
        require(d['completed'] and not d['error'],'incomplete Player')
        if arm=='validation':require(d['correctnessPassed'] and len(d['checks'])==10 and all(c['setEqual'] and c['imageEqual'] and c['nonEmptyImage'] and c['cpuCount']==c['gpuCount'] for c in d['checks']),'correctness failed')
        else:
            plugin=player.parent/(player.stem+'_Data/Plugins/x86_64/CrossoverTimestamp.dll');require(d['pluginSha256']==hashlib.sha256(plugin.read_bytes()).hexdigest(),'DLL identity mismatch')
            require(d['calibrationSha256']==hashlib.sha256(calibration.read_bytes()).hexdigest(),'calibration mismatch')
            summary=validate(d,quality);write(out/(name+'.summary.json'),summary)
            if quality:
                before=record['before'][-1]['adapters'];after=record['after'].get('adapters',[])
                require(len(before)==len(after)>0 and all(a['name']==b['name'] and a['driver']==b['driver'] and abs(a['temperature']-b['temperature'])<=10 for a,b in zip(before,after)),'thermal/adapter gate')
        record['status']='accepted';write(receipt,record);print(name,'accepted',flush=True);return d
    except Exception as e:
        record['status']='failed: '+str(e);write(receipt,record);raise

def prerequisite(out,name):
    require(read(out/(name+'.launch.json'))['status']=='accepted','prerequisite '+name)
    receipt=read(out/(name+'.launch.json'))
    if receipt.get('quality'):require(quiet_status(receipt['before']),'prerequisite quiet gate')
    d=read(out/(name+'.json'))
    if d.get('mode') in ('cpu','gpu'):validate(d,receipt.get('quality',False))
    require(d['sourceIdentity']==source_identity(Path(__file__).resolve().parents[1]),'stale prerequisite');return d

def main():
    p=argparse.ArgumentParser();p.add_argument('--player',required=True,type=Path);p.add_argument('--output',required=True,type=Path);p.add_argument('--stage',required=True,choices=['prepare','integration','overhead','cpu','gpu','pairs','correctness-matrix']);a=p.parse_args();out=a.output.resolve();out.mkdir(parents=True,exist_ok=True);player=a.player.resolve()
    if a.stage=='prepare':
        for frames in (1000,96):
            launch(player,out,f'calibrate-100000-0.25-{frames}','calibrate',frames)
            launch(player,out,f'validation-{frames}','validation',frames)
        return
    prerequisite(out,'validation-1000')
    if a.stage=='integration':
        for arm in ('cpu','gpu'):launch(player,out,'integration-'+arm,arm,96)
        return
    for arm in ('cpu','gpu'):prerequisite(out,'integration-'+arm)
    placement=read(out/'placement-assessment.json');require(placement['sourceIdentity']==source_identity(Path(__file__).resolve().parents[1]) and all(placement[arm]['passed'] for arm in ('cpu','gpu')),'placement not accepted')
    if a.stage=='overhead':
        results={}
        for arm in ('cpu','gpu'):
            shifts=[]
            for pair in range(2):
                runs={}
                for switch in (('off','on') if pair==0 else ('on','off')):runs[switch]=launch(player,out,f'overhead-{arm}-{pair}-{switch}',arm,timestamps=switch,quality=True)
                shift=runs['on']['batchCompletionMsPerFrame']/runs['off']['batchCompletionMsPerFrame']-1;shifts.append(shift)
                results[arm]={'pairedRelativeShifts':shifts,'passed':all(abs(x)<=.02 for x in shifts)};write(out/'overhead-assessment.json',results)
                require(abs(shift)<=.02,'material instrumentation shift; STOP')
        return

    for arm in ('cpu','gpu'):
        for pair in range(2):
            for switch in ('off','on'):prerequisite(out,f'overhead-{arm}-{pair}-{switch}')
    overhead=read(out/'overhead-assessment.json');require(all(overhead.get(arm,{}).get('passed') and len(overhead[arm]['pairedRelativeShifts'])==2 for arm in ('cpu','gpu')),'overhead not accepted')
    if a.stage in ('cpu','gpu'):
        if a.stage=='gpu':prerequisite(out,'pilot-cpu')
        launch(player,out,'pilot-'+a.stage,a.stage,quality=True);return
    for arm in ('cpu','gpu'):prerequisite(out,'pilot-'+arm)
    if a.stage=='pairs':
        for pair in range(3):
            for arm in (('cpu','gpu') if pair%2==0 else ('gpu','cpu')):launch(player,out,f'pair-{pair}-{arm}',arm,quality=True)
        return
    for pair in range(3):
        for arm in ('cpu','gpu'):prerequisite(out,f'pair-{pair}-{arm}')
    for n in (100000,250000,500000,1000000,2000000,4000000):
        for density in (.05,.25,.75):
            if (n,density)==(100000,.25):continue
            launch(player,out,f'calibrate-{n}-{density:g}-1000','calibrate',agents=n,density=density)
            launch(player,out,f'validation-{n}-{density:g}','validation',agents=n,density=density)
if __name__=='__main__':main()
