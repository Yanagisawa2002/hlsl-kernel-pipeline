"""Offline prospective cyclic-work stability assessment; never formal acceptance."""
import argparse,bisect,gzip,json,math,statistics
from collections import Counter
from pathlib import Path
from check_crossover_timing import percentile

def require(ok,msg):
 if not ok:raise ValueError(msg)
def read(p):
 raw=p.read_bytes();return json.loads(gzip.decompress(raw) if p.suffix=='.gz' else raw)
def index(g):
 require(isinstance(g,int) and g>=0,'invalid global frame');return g//1000,g%1000

def ratio_summary(previous,current):
 require(len(previous)==len(current)==1000,'need complete matched cycle')
 ratios=[]
 for a,b in zip(previous,current):
  require(isinstance(a,(int,float)) and isinstance(b,(int,float)) and math.isfinite(a) and math.isfinite(b) and a>0 and b>0,'invalid positive timing')
  ratios.append(b/a)
 return {'median':statistics.median(ratios),'p10':percentile(ratios,.1),'p90':percentile(ratios,.9),'p95':percentile(ratios,.95)}
def stable(r):return .95<=r['median']<=1.05 and r['p10']>=.90 and r['p90']<=1.10

def settling(transitions,cycle_ends):
 # transitions[0] compares cycle0->1; three transitions require four full cycles.
 for start in range(len(transitions)-2):
  if all(transitions[start:start+3]):
   return {'candidateSettlingCycle':start,'candidateSettlingFrame':(start+1)*1000,'candidateSettlingTimeSeconds':cycle_ends[start],
           'confirmationCycle':start+3,'confirmationTimeSeconds':cycle_ends[start+3],
           'laterUnstableTransitions':[i+1 for i in range(start+3,len(transitions)) if not transitions[i]]}
 return None

def histogram(states):
 states=[s for s in states if s is not None];counts=Counter(str(s) for s in states)
 return {'counts':dict(counts),'probabilities':{k:v/len(states) for k,v in counts.items()}} if states else None

def tv(a,b):
 if a is None or b is None:return None
 return .5*sum(abs(a['probabilities'].get(k,0)-b['probabilities'].get(k,0)) for k in set(a['probabilities'])|set(b['probabilities']))
def stats(xs):
 xs=[x for x in xs if x is not None]
 return {'min':min(xs),'median':statistics.median(xs),'mean':statistics.mean(xs),'max':max(xs)} if xs else None

def aligned(rows,start,end):
 require(end>=start,'bad interval');return [r for r in rows if start<=r['utcMs']<end]
def nearest(rows,when):
 if not rows:return None
 j=bisect.bisect_left([r['utcMs'] for r in rows],when);xs=rows[max(0,j-1):j+1];v=min(xs,key=lambda x:abs(x['utcMs']-when));return v if abs(v['utcMs']-when)<=150 else None

def boundary(last,passed,first):
 if not all(isinstance(x,(int,float)) and math.isfinite(x) and x>0 for x in [last,passed,first]):return {'available':False}
 require(passed>=last and first>=last,'boundary timestamp order')
 delta=first-passed
 return {'available':True,'warmupDrainMs':passed-last,'postWarmupIdleGapMs':delta if delta>=0 else None,
         'signedFirstAfterFenceObservationMs':delta,'submissionGapMs':first-last,
         'meaning':'passive fence observation bound; no enforced pause; negative delta means frame300 submitted before observed completion'}

def analyze(d,t):
 require(d.get('schema')==105 and d.get('diagnosticOnly') is True and d.get('eligibleForPerformanceAcceptance') is False,'not state diagnostic')
 require(d['completed'] and not d['error'],'incomplete')
 s=d['samples'];n=d['framesExecuted'];require(n==len(s)==d['timestampSubmitted']==d['timestampResolved'] and n>300,'incomplete IDs')
 require(d['timestampInvalid']==0 and d['maxRingOccupancy']<32 and d['batchFenceCompleted'],'native invalid')
 require(d['graphicsApi']=='Direct3D12' and d['renderingThreadingMode']=='MultiThreaded' and not d['profilerEnabled'],'runtime contract')
 require(30<=d['elapsedSeconds']<35 and n<2000000,'duration/cap failure')
 for i,x in enumerate(s):
  c,v=index(i);require((x['cycleIndex'],x['viewId'],x['globalFrameIndex'])==(c,v,i),'trajectory indexing')
  require(x['submissionId']==x['resolvedSubmissionId']==i+1 and x['timestampRingSlot']==i%32,'ID/slot failure')
  require(0<x['gpuTimestampT0']<=x['gpuTimestampT1']<=x['gpuTimestampT2'] and x['gpuTimestampFrequency']==d['timestampFrequency']>0,'ticks/frequency')
  require(0<x['requiredFence']<=x['completedFence']<2**64-1,'fence')
  for field in ['gpuDrawMs','gpuRangeMs']+(['gpuCullMs'] if d['arm']=='gpu' else []):require(math.isfinite(x[field]) and x[field]>0,'invalid timing')
  if d['arm']=='cpu':require(x['gpuCullMs'] is None,'CPU cull null')
  else:require(math.isclose(x['gpuCullMs']+x['gpuDrawMs'],x['gpuRangeMs'],rel_tol=1e-7,abs_tol=1e-9),'stage units')
  for key,a,b in [('gpuDrawMs','gpuTimestampT1','gpuTimestampT2'),('gpuRangeMs','gpuTimestampT0','gpuTimestampT2')]:require(math.isclose(x[key],(x[b]-x[a])*1000/x['gpuTimestampFrequency'],rel_tol=1e-7,abs_tol=1e-9),'tick conversion')
  if i:require(x['submissionUtcMs']>=s[i-1]['submissionUtcMs'],'clock order')
  if i>=1000:require(x['visibleCount']==s[i%1000]['visibleCount'],'view count changed')
 require(t['diagnosticOnly'] and not t['error'] and t['samples'],'sidecar invalid')
 rows=t['samples'];require(rows[0]['utcMs']<d['firstWorkloadTimestamp'] and rows[-1]['utcMs']>d['diagnosticEndTimestamp'],'telemetry coverage')
 require(all(len(r['adapters'])==1 for r in rows),'analysis assumes one observed NVIDIA adapter')
 cycles=[];ends=[];fields=['gpuDrawMs','gpuRangeMs']+(['gpuCullMs'] if d['arm']=='gpu' else [])
 for c in range(n//1000):
  begin=c*1000;end=begin+1000;part=s[begin:end]
  endUtc=s[end]['submissionUtcMs'] if end<n else s[end-1]['availabilityUtcMs']
  endSec=(endUtc-d['firstWorkloadTimestamp'])/1000;ends.append(endSec)
  telemetry=aligned(rows,part[0]['submissionUtcMs'],endUtc)
  state={f:stats([r['adapters'][0].get(f) for r in telemetry]) for f in ['graphicsMHz','memoryMHz','powerMilliwatts','utilizationPercent','temperatureC']};state['pstate']=histogram([r['adapters'][0].get('pstate') for r in telemetry]);state['samples']=len(telemetry)
  entry={'cycleIndex':c,'endElapsedSeconds':endSec,'timing':{k:stats([x[k] for x in part]) for k in fields},'state':state,'transition':None}
  if c:
   prev=s[begin-1000:begin];ratios={k:ratio_summary([x[k] for x in prev],[x[k] for x in part]) for k in fields}
   ps=cycles[-1]['state'];clock={f:(state[f]['median']/ps[f]['median'] if state[f] and ps[f] and ps[f]['median']>0 else None) for f in ['graphicsMHz','memoryMHz']}
   entry['transition']={'matchedViewRatios':ratios,'drawCostStable':stable(ratios['gpuDrawMs']),'clockMedianRatios':clock,'graphicsMedianChangeWithin10Percent':abs(clock['graphicsMHz']-1)<=.1 if clock['graphicsMHz'] is not None else None,'pstateTotalVariation':tv(ps['pstate'],state['pstate'])}
  cycles.append(entry)
 transitions=[c['transition']['drawCostStable'] for c in cycles[1:]];candidate=settling(transitions,ends)
 sustained=None
 for start in range(max(0,len(transitions)-2)):
  if all(transitions[start:]):
   sustained=settling(transitions[start:],ends[start:])
   if sustained:
    sustained['candidateSettlingCycle']+=start;sustained['candidateSettlingFrame']+=start*1000;sustained['confirmationCycle']+=start
   break
 near=nearest(rows,s[300]['submissionUtcMs'])
 return {'diagnosticOnly':True,'eligibleForPerformanceAcceptance':False,'arm':d['arm'],'runId':d['runId'],'processId':d['processId'],'instrumentationValid':True,'elapsedSeconds':d['elapsedSeconds'],'framesExecuted':n,'cyclesCompleted':n//1000,'incompleteFinalCycleFrames':n%1000,'timestampResolved':n,'maxRingOccupancy':d['maxRingOccupancy'],'candidate':candidate,'sustainedCandidate':sustained,'frame300':{'elapsedSeconds':s[300]['elapsedSeconds'],'telemetry':near,'recentDrawMs':stats([x['gpuDrawMs'] for x in s[250:301]]),'beforeCandidate':s[300]['elapsedSeconds']<candidate['candidateSettlingTimeSeconds'] if candidate else None},'boundary':boundary(d['warmupLastSubmissionTimestamp'],d['warmupFencePassedTimestamp'],d['firstMeasuredSubmissionTimestamp']),'cycles':cycles}

def main():
 p=argparse.ArgumentParser();p.add_argument('--result',type=Path,required=True);p.add_argument('--telemetry',type=Path,required=True);p.add_argument('--output',type=Path,required=True);a=p.parse_args();require(not a.output.exists(),'no overwrite');a.output.write_text(json.dumps(analyze(read(a.result),read(a.telemetry)),indent=2,allow_nan=False),encoding='utf-8')
if __name__=='__main__':main()
