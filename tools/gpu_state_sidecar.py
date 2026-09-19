"""Persistent read-only NVML sidecar. Anonymous aggregate fields; diagnostic only."""
import argparse,ctypes as c,json,time
from pathlib import Path

class Util(c.Structure):_fields_=[('gpu',c.c_uint),('memory',c.c_uint)]
class Nvml:
 def __init__(self):
  self.lib=c.CDLL('nvml.dll');self.lib.nvmlInit_v2.restype=c.c_int
  if self.lib.nvmlInit_v2()!=0:raise RuntimeError('NVML init unavailable')
  count=c.c_uint();self.lib.nvmlDeviceGetCount_v2(c.byref(count));self.handles=[]
  for i in range(count.value):
   h=c.c_void_p();rc=self.lib.nvmlDeviceGetHandleByIndex_v2(c.c_uint(i),c.byref(h))
   if rc==0:self.handles.append(h)
  if not self.handles:raise RuntimeError('No NVML adapters')
 def uint(self,name,h,*args):
  out=c.c_uint()
  try:rc=getattr(self.lib,name)(h,*[c.c_uint(x) for x in args],c.byref(out))
  except AttributeError:return None,'symbol unavailable'
  return (out.value,None) if rc==0 else (None,rc)
 def sample(self):
  start=time.perf_counter();row={'utcMs':time.time()*1000,'monotonicSeconds':start,'adapters':[]}
  for i,h in enumerate(self.handles):
   a={'index':i,'unavailable':{}}
   for key,fn,args in [('graphicsMHz','nvmlDeviceGetClockInfo',(0,)),('smMHz','nvmlDeviceGetClockInfo',(1,)),('memoryMHz','nvmlDeviceGetClockInfo',(2,)),('pstate','nvmlDeviceGetPerformanceState',()),('powerMilliwatts','nvmlDeviceGetPowerUsage',()),('temperatureC','nvmlDeviceGetTemperature',(0,))]:
    value,error=self.uint(fn,h,*args);a[key]=value
    if error is not None:a['unavailable'][key]=error
   util=Util();rc=self.lib.nvmlDeviceGetUtilizationRates(h,c.byref(util));a['utilizationPercent']=util.gpu if rc==0 else None
   if rc:a['unavailable']['utilizationPercent']=rc
   row['adapters'].append(a)
  row['queryDurationMs']=(time.perf_counter()-start)*1000;return row
 def close(self):self.lib.nvmlShutdown()

def main():
 p=argparse.ArgumentParser();p.add_argument('--output',type=Path,required=True);p.add_argument('--ready',type=Path,required=True);p.add_argument('--stop',type=Path,required=True);a=p.parse_args()
 if a.output.exists() or a.ready.exists() or a.stop.exists():raise ValueError('New sidecar paths required')
 result={'diagnosticOnly':True,'eligibleForPerformanceAcceptance':False,'cadenceMs':100,'backend':'persistent ctypes NVML','startUtcMs':time.time()*1000,'samples':[],'missedDeadlines':0,'error':''};nv=None
 try:
  nv=Nvml();deadline=time.perf_counter()
  while not a.stop.exists():
   result['samples'].append(nv.sample())
   if not a.ready.exists():a.ready.write_text('first anonymous observation recorded')
   deadline+=.1;now=time.perf_counter()
   if now>deadline:
    skipped=int((now-deadline)/.1)+1;result['missedDeadlines']+=skipped;deadline+=skipped*.1
   time.sleep(max(0,deadline-time.perf_counter()))
 except Exception as exc:result['error']=type(exc).__name__
 finally:
  if nv:nv.close()
  result['endUtcMs']=time.time()*1000;a.output.write_text(json.dumps(result,indent=2),encoding='utf-8')
 if result['error']:raise SystemExit(2)
if __name__=='__main__':main()
