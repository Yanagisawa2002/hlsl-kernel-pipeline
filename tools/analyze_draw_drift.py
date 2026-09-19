"""Descriptive mechanism analysis of diagnostic-only draw-drift runs."""
import argparse,bisect,gzip,json,statistics,math
from pathlib import Path
from collections import Counter
from analyze_crossover_drift import correlation,ranks,trend,quarter_stats
from run_draw_drift import check
FIELDS=['graphicsMHz','smMHz','memoryMHz','powerMilliwatts','utilizationPercent','temperatureC']
def read(p):
 raw=p.read_bytes();return json.loads(gzip.decompress(raw) if p.suffix=='.gz' else raw)
def stats(xs):
 xs=[x for x in xs if x is not None]
 return {'mean':statistics.mean(xs),'min':min(xs),'max':max(xs)} if xs else None
def assoc(x,y):
 pairs=[(a,b) for a,b in zip(x,y) if a is not None and b is not None];x=[a for a,b in pairs];y=[b for a,b in pairs]
 if len(x)<4:return {'n':len(x),'spearman':None,'linearR2':None}
 r=correlation(x,y);return {'n':len(x),'spearman':correlation(ranks(x),ranks(y)),'linearR2':r*r if r is not None else None}
def aggregate(rows):
 return {**{f:stats([r['adapters'][0].get(f) for r in rows]) for f in FIELDS},'pstates':dict(Counter(str(r['adapters'][0].get('pstate')) for r in rows)),'telemetrySamples':len(rows)}
def nearest(rows,t):
 pos=bisect.bisect_left([r['utcMs'] for r in rows],t);choices=rows[max(0,pos-1):pos+1]
 row=min(choices,key=lambda r:abs(r['utcMs']-t))
 return row if abs(row['utcMs']-t)<=150 else None

def metric_quarter(values,unit):
 q=quarter_stats(values)
 return {'unit':unit,'firstMean':q['firstQuarterMeanMs'],'lastMean':q['lastQuarterMeanMs'],'signedChange':q['signedShiftMs'],'absoluteChange':q['absoluteDriftMs'],'relativeChange':q['relativeDrift']}

def analyze(directory):
 runs={};raw={}
 def file(name):
  p=directory/name;return p if p.exists() else directory/(name+'.gz')
 for name in ['short','forward','frozen','reverse']:
  d=read(file(name+'.json'));t=read(file(name+'.telemetry.json'));receipt=read(file(name+'.launch.json'));validation=check(d,t);ss=d['samples'];rows=t['samples'];n=len(ss);raw[name]=d
  for s in ss:
   p=d['viewProxies'][s['viewIndex']];s['projectedArea']=p['sumProjectedQuadAreaPixels'];s['clippedArea']=p['sumClippedProjectedAreaPixels'];s['nsPerPS']=s['gpuDrawMs']*1e6/s['PSInvocations'] if s['PSInvocations'] else None;s['psPerVisible']=s['PSInvocations']/s['visibleCount'] if s['visibleCount'] else None
  blocks=[]
  for start in range(0,n,100):
   group=ss[start:start+100];end=start+len(group);startUtc=group[0]['submissionUtcMs'];endUtc=ss[end]['submissionUtcMs'] if end<n else ss[-1]['availabilityUtcMs']
   if end==n:endUtc=max(d['lastMeasuredUtcMs'],ss[-1]['availabilityUtcMs'])
   telemetry=[r for r in rows if startUtc<=r['utcMs']<endUtc]
   block={'start':start,'end':end-1,'startUtcMs':startUtc,'endUtcMs':endUtc,'drawMeanMs':statistics.mean(s['gpuDrawMs'] for s in group),'drawMedianMs':statistics.median(s['gpuDrawMs'] for s in group),**{k+'Mean':statistics.mean(s[k] for s in group) for k in ['visibleCount','PSInvocations','VSInvocations','CInvocations','CPrimitives','projectedArea','clippedArea','nsPerPS','psPerVisible']},'state':aggregate(telemetry)};blocks.append(block)
  mapped=[nearest(rows,s['submissionUtcMs']) for s in ss]
  associations={k:assoc([s['gpuDrawMs'] for s in ss],[s[k] for s in ss]) for k in ['frameIndex','PSInvocations','visibleCount','projectedArea','clippedArea']}
  for f in ['graphicsMHz','powerMilliwatts']:associations[f]=assoc([s['gpuDrawMs'] for s in ss],[r['adapters'][0][f] if r else None for r in mapped])
  # Block-level state associations avoid presenting reused 100ms sensors as independent frame measurements.
  blockAssoc={f:assoc([b['drawMeanMs'] for b in blocks],[b['state'][f]['mean'] if b['state'][f] else None for b in blocks]) for f in ['graphicsMHz','powerMilliwatts']}
  measured=[r for r in rows if d['firstMeasuredUtcMs']<=r['utcMs']<=ss[-1]['availabilityUtcMs']]
  runs[name]={'diagnosticOnly':True,'eligibleForPerformanceAcceptance':False,'validation':validation,'blocks':blocks,'associations':associations,'blockStateAssociations':blockAssoc,'drawTrend':trend([s['gpuDrawMs'] for s in ss]),'quarter':{k:metric_quarter([s[k] for s in ss],{'gpuDrawMs':'ms','PSInvocations':'invocations','visibleCount':'agents','projectedArea':'pixel squared area','clippedArea':'pixel squared area','nsPerPS':'ns/invocation descriptive'}[k]) for k in ['gpuDrawMs','PSInvocations','visibleCount','projectedArea','clippedArea','nsPerPS']},'constancy':{k:len(set(s[k] for s in ss)) for k in ['viewIndex','visibleCount','PSInvocations','VSInvocations','CInvocations','CPrimitives','projectedArea','clippedArea']},'sanity':{'vsEquals6Visible':all(s['VSInvocations']==6*s['visibleCount'] for s in ss),'cInvocationsEquals2Visible':all(s['CInvocations']==2*s['visibleCount'] for s in ss),'cPrimitivesEquals2Visible':all(s['CPrimitives']==2*s['visibleCount'] for s in ss)},'perFrameDerived':[{'frameIndex':s['frameIndex'],'viewIndex':s['viewIndex'],'psPerVisible':s['psPerVisible'],'nsPerPS':s['nsPerPS'],'projectedArea':s['projectedArea'],'clippedArea':s['clippedArea']} for s in ss],'measuredState':aggregate(measured),'preLaunch':aggregate([r for r in rows if r['utcMs']<receipt['playerLaunchUtcMs']]),'postExit':aggregate([r for r in rows if r['utcMs']>receipt['playerExitUtcMs']]),'cadenceMs':stats([(rows[i]['utcMs']-rows[i-1]['utcMs']) for i in range(1,len(rows))]),'queryDurationMs':stats([r['queryDurationMs'] for r in rows]),'missingFields':dict(Counter(k for r in rows for a in r['adapters'] for k in a['unavailable'])),'missedDeadlines':t['missedDeadlines'],'timeline':{k:d[k] for k in ['diagnosticStartUtcMs','firstMeasuredUtcMs','lastMeasuredUtcMs','finishUtcMs']}}
 fwd=raw['forward'];rev=raw['reverse'];reverse={s['viewIndex']:s for s in rev['samples']};pairs=[]
 for a in fwd['samples']:
  b=reverse[a['viewIndex']];p=fwd['viewProxies'][a['viewIndex']];rp=rev['viewProxies'][b['viewIndex']]
  pairs.append({'viewIndex':a['viewIndex'],'forwardFrame':a['frameIndex'],'reverseFrame':b['frameIndex'],'forwardDrawMs':a['gpuDrawMs'],'reverseDrawMs':b['gpuDrawMs'],'forwardPS':a['PSInvocations'],'reversePS':b['PSInvocations'],'visible':a['visibleCount'],'reverseVisible':b['visibleCount'],'projectedArea':p['sumProjectedQuadAreaPixels'],'reverseProjectedArea':rp['sumProjectedQuadAreaPixels'],'clippedArea':p['sumClippedProjectedAreaPixels'],'reverseClippedArea':rp['sumClippedProjectedAreaPixels'],'idHashEqual':p['visibleIdHash']==rp['visibleIdHash'],'partiallyClipped':p['partiallyClippedAgentCount'],'reversePartiallyClipped':rp['partiallyClippedAgentCount']})
 match={'views':len(pairs),'allSameVisible':all(p['visible']==p['reverseVisible'] for p in pairs),'allSamePS':all(p['forwardPS']==p['reversePS'] for p in pairs),'allSameProxies':all(p['projectedArea']==p['reverseProjectedArea'] and p['clippedArea']==p['reverseClippedArea'] and p['idHashEqual'] and p['partiallyClipped']==p['reversePartiallyClipped'] for p in pairs),'drawAssociation':assoc([p['forwardDrawMs'] for p in pairs],[p['reverseDrawMs'] for p in pairs]),'reverseMinusForwardMs':stats([p['reverseDrawMs']-p['forwardDrawMs'] for p in pairs]),'pairs':pairs}
 return {'diagnosticOnly':True,'eligibleForPerformanceAcceptance':False,'runs':runs,'matchedViews':match,'alignment':'nearest telemetry within 150ms of CPU submission; blocks use submission windows; approximate, not per-frame GPU clocks','noIndependentFrameInference':True}

def main():
 p=argparse.ArgumentParser();p.add_argument('--directory',type=Path,required=True);p.add_argument('--output',type=Path,required=True);a=p.parse_args();assert not a.output.exists();a.output.write_text(json.dumps(analyze(a.directory),indent=2,allow_nan=False),encoding='utf-8')
if __name__=='__main__':main()
