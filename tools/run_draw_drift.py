"""Single-launch diagnostic runner; never performs acceptance or retries."""
import argparse,json,os,subprocess,sys,time,hashlib,math
from pathlib import Path
ROOT=Path(__file__).resolve().parents[1]
def read(p):return json.loads(p.read_text(encoding='utf-8-sig'))
def check(d,telemetry):
 assert d['diagnosticOnly'] and not d['eligibleForPerformanceAcceptance'] and d['schema']==104
 assert d['completed'] and not d['error'] and d['mode']=='draw-drift-diagnostic'
 n=d['options']['frames'];assert len(d['samples'])==n==d['timestampSubmitted']==d['timestampResolved']
 assert d['timestampInvalid']==0 and d['maxRingOccupancy']<32 and d['graphicsApi']=='Direct3D12'
 assert d['warmupFenceCompleted'] and d['batchFenceCompleted']
 assert not d['profilerEnabled'] and d['renderingThreadingMode']=='MultiThreaded'
 for i,s in enumerate(d['samples']):
  sid=301+i;assert s['submissionId']==s['resolvedSubmissionId']==sid and s['timestampRingSlot']==(sid-1)%32
  assert 0<s['gpuTimestampT0']<=s['gpuTimestampT1']<=s['gpuTimestampT2'] and s['gpuTimestampFrequency']==d['timestampFrequency']>0
  assert 0<s['requiredFence']<=s['completedFence']<2**64-1
  assert s['gpuCullMs'] is None
  assert math.isclose(s['gpuDrawMs'],(s['gpuTimestampT2']-s['gpuTimestampT1'])*1000/s['gpuTimestampFrequency'],rel_tol=1e-7,abs_tol=1e-9)
  assert math.isclose(s['gpuRangeMs'],(s['gpuTimestampT2']-s['gpuTimestampT0'])*1000/s['gpuTimestampFrequency'],rel_tol=1e-7,abs_tol=1e-9)
  assert s['PSInvocations']>0 and s['VSInvocations']>0 and s['CPrimitives']>0
  idx=999-i if d['trajectory']=='reverse' else 500 if d['trajectory']=='frozen' else i
  assert s['viewIndex']==idx and s['visibleCount']==d['viewProxies'][idx]['visibleCount']
 assert telemetry['diagnosticOnly'] and not telemetry['error'] and telemetry['samples']
 assert telemetry['samples'][0]['utcMs']<d['firstMeasuredUtcMs']<d['lastMeasuredUtcMs']<telemetry['endUtcMs']
 assert any(d['firstMeasuredUtcMs']<=s['utcMs']<=d['lastMeasuredUtcMs'] for s in telemetry['samples'])
 return {'diagnosticOnly':True,'eligibleForPerformanceAcceptance':False,'instrumentationValid':True,'resolved':n,'ringMax':d['maxRingOccupancy'],'vsEqualsSixVisible':all(s['VSInvocations']==s['visibleCount']*6 for s in d['samples'])}
def main():
 p=argparse.ArgumentParser();p.add_argument('--player',type=Path,required=True);p.add_argument('--output',type=Path,required=True);p.add_argument('--calibration',type=Path,required=True);p.add_argument('--stage',choices=['short','forward','frozen','reverse'],required=True);a=p.parse_args();out=a.output;out.mkdir(parents=True,exist_ok=True)
 prerequisites={'short':[],'forward':['short'],'frozen':['short','forward'],'reverse':['short','forward','frozen']}
 for stage in prerequisites[a.stage]:assert read(out/(stage+'.assessment.json'))['instrumentationValid']
 prefix=out/a.stage;receipt=Path(str(prefix)+'.launch.json');assert not receipt.exists()
 frames=96 if a.stage=='short' else 1000;trajectory='forward' if a.stage=='short' else a.stage
 result=Path(str(prefix)+'.json');telemetry=Path(str(prefix)+'.telemetry.json');ready=Path(str(prefix)+'.ready');stop=Path(str(prefix)+'.stop')
 cmd=[str(a.player),'-force-d3d12','-screen-fullscreen','0','-screen-width','1280','-screen-height','720','-logFile',str(prefix)+'.log','--mode','cpu','--agents','100000','--density','0.25','--seed','69501203','--warmup-frames','300','--frames',str(frames),'--timestamps','on','--timing-api','native','--calibration',str(a.calibration),'--output',str(result)]
 r={'diagnosticOnly':True,'eligibleForPerformanceAcceptance':False,'stage':a.stage,'trajectory':trajectory,'command':cmd,'status':'starting','calibrationSha256':hashlib.sha256(a.calibration.read_bytes()).hexdigest(),'playerSha256':hashlib.sha256(a.player.read_bytes()).hexdigest()}
 receipt.write_text(json.dumps(r,indent=2));startup=subprocess.STARTUPINFO();startup.dwFlags|=subprocess.STARTF_USESHOWWINDOW;startup.wShowWindow=0
 side=subprocess.Popen([sys.executable,str(ROOT/'tools/gpu_state_sidecar.py'),'--output',str(telemetry),'--ready',str(ready),'--stop',str(stop)],startupinfo=startup)
 try:
  deadline=time.monotonic()+20
  while not ready.exists():
   if side.poll() is not None or time.monotonic()>deadline:raise RuntimeError('Sidecar not ready')
   time.sleep(.1)
  time.sleep(1) # Fixed prelaunch telemetry lead-in, not a retry/quiet gate.
  env=os.environ.copy();env['DRAW_DRIFT_TRAJECTORY']=trajectory
  r['playerLaunchUtcMs']=time.time()*1000
  r['returnCode']=subprocess.run(cmd,env=env,startupinfo=startup,timeout=300).returncode;r['playerExitUtcMs']=time.time()*1000
  time.sleep(.2)
 finally:
  stop.write_text('Player ended / runner cleanup');side.wait(timeout=20)
  receipt.write_text(json.dumps(r,indent=2))
 try:
  assert r['returnCode']==0
  assessment=check(read(result),read(telemetry));Path(str(prefix)+'.assessment.json').write_text(json.dumps(assessment,indent=2));r['status']='diagnostic instrumentation valid; NOT acceptance'
 except Exception as exc:r['status']='diagnostic failed: '+str(exc);raise
 finally:receipt.write_text(json.dumps(r,indent=2))
 print(a.stage,r['status'])
if __name__=='__main__':main()
