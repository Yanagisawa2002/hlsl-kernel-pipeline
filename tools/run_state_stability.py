"""Fixed six-position state diagnostic. Quiet block stops; never retries or accepts performance."""
import argparse,hashlib,json,os,subprocess,sys,time,uuid
from pathlib import Path
from run_crossover import snapshot
from check_crossover_timing import quiet_status
from analyze_state_stability import analyze,read,require
ROOT=Path(__file__).resolve().parents[1]
ORDER=['cpu','gpu','gpu','cpu','cpu','gpu']
def write(p,d):p.write_text(json.dumps(d,indent=2),encoding='utf-8')
def quiet_preflight(receipt,record):
 record['before']=[]
 for _ in range(3):record['before'].append(snapshot());time.sleep(1)
 if not quiet_status(record['before']):
  record['status']='blocked: initial GPU utilization';write(receipt,record);return False
 write(receipt,record);return True

def launch(player,calibration,out,position):
 arm=ORDER[position];name=f'{position+1:02d}-{arm}';prefix=out/name;receipt=out/(name+'.launch.json');require(not receipt.exists(),'No overwrite or retry')
 for prior in range(position):require(read(out/(f'{prior+1:02d}-{ORDER[prior]}.assessment.json'))['instrumentationValid'],'prior diagnostic missing')
 result=out/(name+'.json');telemetry=out/(name+'.telemetry.json');ready=out/(name+'.ready');stop=out/(name+'.stop')
 require(not any(p.exists() for p in [result,telemetry,ready,stop]),'No stale outputs')
 runid=str(uuid.uuid4());cmd=[str(player),'-force-d3d12','-screen-fullscreen','0','-screen-width','1280','-screen-height','720','-logFile',str(prefix)+'.log','--mode',arm,'--agents','100000','--density','0.25','--seed','69501203','--warmup-frames','300','--frames','1000','--timestamps','on','--timing-api','native','--run-id',runid,'--calibration',str(calibration),'--output',str(result)]
 record={'diagnosticOnly':True,'eligibleForPerformanceAcceptance':False,'order':ORDER,'position':position+1,'arm':arm,'runId':runid,'command':cmd,'status':'preflight','playerSha256':hashlib.sha256(player.read_bytes()).hexdigest(),'calibrationSha256':hashlib.sha256(calibration.read_bytes()).hexdigest()};write(receipt,record)
 if not quiet_preflight(receipt,record):return False
 startup=subprocess.STARTUPINFO();startup.dwFlags|=subprocess.STARTF_USESHOWWINDOW;startup.wShowWindow=0
 side=subprocess.Popen([sys.executable,str(ROOT/'tools/gpu_state_sidecar.py'),'--output',str(telemetry),'--ready',str(ready),'--stop',str(stop)],startupinfo=startup)
 try:
  deadline=time.monotonic()+20
  while not ready.exists():
   require(side.poll() is None and time.monotonic()<deadline,'sidecar unavailable');time.sleep(.1)
  record['playerLaunchUtcMs']=time.time()*1000
  record['returnCode']=subprocess.run(cmd,startupinfo=startup,timeout=300).returncode
  record['playerExitUtcMs']=time.time()*1000;record['after']=snapshot()
 except Exception as exc:record['status']='failed: '+str(exc);raise
 finally:
  stop.write_text('diagnostic ended');side.wait(timeout=20);write(receipt,record)
 try:
  require(record['returnCode']==0,'Player failed')
  d=read(result);require(d['calibrationSha256']==record['calibrationSha256'],'calibration identity')
  dll=player.parent/(player.stem+'_Data/Plugins/x86_64/CrossoverTimestamp.dll');require(d['pluginSha256']==hashlib.sha256(dll.read_bytes()).hexdigest(),'DLL identity')
  summary=analyze(d,read(telemetry));write(out/(name+'.assessment.json'),summary);record['status']='diagnostic valid; NOT performance acceptance'
 except Exception as exc:record['status']='failed: '+str(exc);raise
 finally:write(receipt,record)
 print(name,record['status'],'frames',summary['framesExecuted'],'candidate',summary['candidate'],flush=True);return True

def main():
 p=argparse.ArgumentParser();p.add_argument('--player',type=Path,required=True);p.add_argument('--calibration',type=Path,required=True);p.add_argument('--output',type=Path,required=True);a=p.parse_args();a.output.mkdir(parents=True,exist_ok=True)
 for position in range(6):
  if not launch(a.player,a.calibration,a.output,position):print('STOP: blocked at position',position+1,flush=True);raise SystemExit(2)
if __name__=='__main__':main()
