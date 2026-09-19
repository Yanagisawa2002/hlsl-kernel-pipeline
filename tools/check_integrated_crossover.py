"""Protocol-v4 gates and summaries; no legacy timing or crossover inference."""
import math,statistics
from check_crossover_timing import pacing_status,percentile,quiet_status
from analyze_crossover import drift

def require(ok,message):
    if not ok:raise ValueError(message)

def distribution(xs):
    require(bool(xs) and all(math.isfinite(x) for x in xs),'nonfinite/empty distribution')
    return dict(mean=statistics.mean(xs),median=statistics.median(xs),p10=percentile(xs,.1),p90=percentile(xs,.9),p95=percentile(xs,.95))

def validate(d,quality=False):
    require(d['schema']==4 and d['completed'] and not d['error'],'incomplete v4 process')
    o=d['options'];n=o['frames'];on=o['timestamps']=='on';cpu=d['mode']=='cpu';samples=d['samples']
    require(d['graphicsApi']=='Direct3D12' and d['renderingThreadingMode']=='MultiThreaded' and not d['profilerEnabled'],'runtime contract')
    require(d['timingApi']=='native' and d['nativeTimingVersion']=='4','wrong native backend')
    require(len(samples)==n and d['timestampInvalid']==0 and d['maxRingOccupancy']<=32,'missing sample/ring failure')
    require(d['batchFenceCompleted'] and d['warmupFenceCompleted'],'batch fence incomplete')
    start,end=d['batchStartTicks'],d['firstTrueFencePollTicks'];frequency=d['stopwatchFrequency']
    require(0<start<d['finalSubmissionTicks']<=end and frequency>0,'invalid batch ticks')
    batch=(end-start)*1000/frequency
    require(math.isclose(batch,d['batchCompletionMs'],rel_tol=1e-7) and math.isclose(batch/n,d['batchCompletionMsPerFrame'],rel_tol=1e-7),'batch unit mismatch')
    lower=d['lastFalseFencePollTicks'] or d['finalSubmissionTicks'];require(d['finalSubmissionTicks']<=lower<=end,'bad fence bracket')
    bound=(end-lower)*1000/frequency/n
    require(math.isclose(bound,d['fenceObservationBoundMsPerFrame'],rel_tol=1e-7,abs_tol=1e-9),'bad observation bound')
    require(d['timestampSubmitted']==d['timestampResolved']==(n if on else 0),'partial timestamp triplets')
    for i,s in enumerate(samples):
        require(s['frameIndex']==i and s['submissionUnityFrame']==samples[0]['submissionUnityFrame']+i,'missing submitted frame')
        for k in ['cpuCullAndListMs','cpuUploadMs','cpuSubmitMs','cpuTotalMs']:require(math.isfinite(s[k]) and s[k]>=0,'CPU cost invalid')
        if not on:
            require(s['submissionId']==0 and s['gpuCullMs'] is None and s['gpuDrawMs'] is None and s['gpuRangeMs'] is None,'OFF contains GPU timings');continue
        sid=o['warmup']+i+1
        require(s['submissionId']==s['resolvedSubmissionId']==sid and s['timestampRingSlot']==(sid-1)%32,'ID/slot mismatch')
        t0,t1,t2,f=s['gpuTimestampT0'],s['gpuTimestampT1'],s['gpuTimestampT2'],s['gpuTimestampFrequency']
        require(0<t0<=t1<=t2 and f==d['timestampFrequency'] and f>0,'bad ticks/frequency')
        require(0<s['requiredFence']<=s['completedFence']<2**64-1,'read before fence/device removal')
        require(s['timestampResolveDelayFrames']==s['availabilityUnityFrame']-s['submissionUnityFrame']>0,'bad availability metadata')
        for k,value in [('gpuDrawMs',(t2-t1)*1000/f),('gpuRangeMs',(t2-t0)*1000/f)]:require(math.isclose(s[k],value,rel_tol=1e-7,abs_tol=1e-9),'GPU unit mismatch')
        if cpu:require(s['gpuCullMs'] is None,'CPU GPU cull must be null')
        else:require(math.isclose(s['gpuCullMs'],(t1-t0)*1000/f,rel_tol=1e-7,abs_tol=1e-9) and math.isclose(s['gpuCullMs']+s['gpuDrawMs'],s['gpuRangeMs'],rel_tol=1e-7,abs_tol=1e-9),'GPU stage inconsistency')
    pacing=pacing_status([s['updateIntervalMs'] for s in samples[1:]])
    summary={'batchCompletionMsPerFrame':d['batchCompletionMsPerFrame'],'fenceObservationBoundMsPerFrame':bound,'pacing':pacing,'timestampResolved':d['timestampResolved'],'maxRingOccupancy':d['maxRingOccupancy']}
    for k in ['cpuCullAndListMs','cpuUploadMs','cpuSubmitMs','cpuTotalMs','gpuCullMs','gpuDrawMs','gpuRangeMs','timestampResolveDelayFrames']:
        xs=[s[k] for s in samples if s[k] is not None and (on or not k.startswith(('gpu','timestamp')))];summary[k]=distribution(xs) if xs else None
    if quality:
        require((o['agents'],o['density'],o['seed'],o['warmup'],n)==(100000,.25,69501203,300,1000),'pilot workload mismatch')
        require(pacing['passed'] and d['gc0Collections']==0 and d['vsync']==0 and d['targetFrameRate']==-1,'pacing/GC/settings gate')
        require(bound/d['batchCompletionMsPerFrame']<=.01,'fence quantization exceeds 1 percent')
        require(drift([s['cpuTotalMs'] for s in samples])<=.15,'CPU drift')
        if on:require(drift([s['gpuRangeMs'] for s in samples])<=.15,'GPU drift')
        if cpu and on:
            gap=statistics.mean(s['gpuRangeMs']-s['gpuDrawMs'] for s in samples)
            require(gap<=max(.01,.05*summary['gpuRangeMs']['mean']),'anomalous CPU timestamp-only gap')
    return summary


def validate_placement(chunks,arm):
    def value(c,name):
        return next((int(x['value']) for x in c['children'] if x['name']==name and x['value']),None)
    resolves=[c for c in chunks if c['name'].endswith('::ResolveQueryData') and value(c,'NumQueries')==3 and value(c,'StartIndex')<96]
    require(len(resolves)==1,'expected one native triplet in representative capture')
    resolve=resolves[0];base=value(resolve,'StartIndex')
    queries=[next((c for c in chunks if c['name'].endswith('::EndQuery') and value(c,'Index')==base+i),None) for i in range(3)]
    require(all(queries),'missing native query')
    t0,t1,t2=[c['index'] for c in queries];require(t0<t1<t2<resolve['index'],'query order')
    if arm=='gpu':
        dispatch=[c['index'] for c in chunks if c['name'].endswith('::Dispatch')]
        copy=[c['index'] for c in chunks if c['name'].endswith('::CopyBufferRegion') and value(c,'DstOffset')==4 and value(c,'NumBytes')==4]
        draw=[c['index'] for c in chunks if c['name'].endswith('::ExecuteIndirect')]
        require(any(t0<d<t1 for d in dispatch) and any(t1<c<d<t2 for c in copy for d in draw),'GPU placement mismatch')
    else:
        draw=[c['index'] for c in chunks if c['name'].endswith('::DrawInstanced') and value(c,'VertexCountPerInstance')==6]
        require(any(t1<d<t2 for d in draw) and not any(c['name'].endswith(('::Dispatch','::ExecuteIndirect')) for c in chunks),'CPU placement mismatch')
    return {'passed':True,'T0Chunk':t0,'T1Chunk':t1,'T2Chunk':t2,'resolveChunk':resolve['index'],'queryIndices':[base,base+1,base+2]}
