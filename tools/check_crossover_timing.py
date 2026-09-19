"""v2 diagnostic attribution and timing-quality gates; no crossover inference."""
import math
import statistics


def diagnostic_status(data):
    rows = [r for r in data.get('observations', []) if r]
    if not data.get('completed') or data.get('error') or data.get('markerValid') != [True]*3 or data.get('recorderValid') != [True]*3 or len(rows)!=112:
        return {'passed': False, 'reason': 'incomplete/invalid markers', 'nonzero': [0,0,0]}
    first=data['firstSubmissionUnityFrame']
    if any(r['availabilityUnityFrame']!=first+i for i,r in enumerate(rows)):
        return {'passed':False,'reason':'non-contiguous observation frames','nonzero':[0,0,0]}
    submitted = {r['availabilityUnityFrame']: r['submittedRepetitions'] for r in rows if r['submittedRepetitions']}
    observed = {r['availabilityUnityFrame']: r for r in rows}
    nonzero = [sum(r['gpuNs'][k] > 0 and r['blocks'][k] > 0 for r in rows) for k in range(3)]
    matches = {}
    # Other lags diagnose phase mistakes; only documented lag 3 is eligible without amendment.
    for lag in range(1, 9):
        matches[str(lag)] = [sum(f + lag in observed and observed[f + lag]['blocks'][k] == count and observed[f + lag]['gpuNs'][k] > 0 for f,count in submitted.items()) for k in range(3)]
    passed = len(submitted) == 96 and matches['3'] == [96]*3
    return {'passed': passed, 'reason': 'documented three-frame mapping verified' if passed else 'zero/missing/misattributed GPU samples',
            'submittedFrames':len(submitted), 'nonzero':nonzero, 'matchesByDelay':matches,
            'batchmode':data.get('batchmode'), 'timingApi':data.get('timingApi')}


def percentile(values, fraction):
    values = sorted(values); position = fraction * (len(values)-1); lo = int(position); hi = min(lo+1,len(values)-1)
    return values[lo] + (values[hi]-values[lo]) * (position-lo)


def pacing_status(intervals):
    if len(intervals) < 10 or any(not math.isfinite(v) or v <= 0 for v in intervals):
        raise ValueError('invalid/incomplete update intervals')
    mean = statistics.mean(intervals); cv = statistics.pstdev(intervals)/mean
    median = statistics.median(intervals)
    # Integer refresh rates cover more than the three illustrative frequencies.
    rates = range(30,361)
    fractions = [(sum(abs(v-1000/r) <= .03*(1000/r) for v in intervals)/len(intervals), r) for r in rates]
    fraction, rate = max(fractions)
    clustered = fraction >= .80 or (median > 2 and cv < .02)
    return {'passed':not clustered, 'p10Ms':percentile(intervals,.1),'medianMs':median,'p90Ms':percentile(intervals,.9),
            'p95Ms':percentile(intervals,.95),'cv':cv,'strongestRefreshHz':rate,'clusterFraction':fraction,
            'reason':'pacing-like concentration: investigate before interpretation' if clustered else 'no preregistered pacing concentration detected'}


def quiet_status(snapshots):
    return len(snapshots) == 3 and all('error' not in s and s.get('adapters') and all(a['utilization'] <= 5 for a in s['adapters']) for s in snapshots)


def validate_pilot_v2(result, correctness):
    if result.get('schema') != 2 or result.get('protocolVersion') != 2 or not result.get('completed') or result.get('error'):
        raise ValueError('incomplete/failed v2 pilot')
    if not correctness.get('completed') or not correctness.get('correctnessPassed'):
        raise ValueError('correctness prerequisite missing')
    for field in ('sourceIdentity','calibrationSha256','adapter','driver','graphicsApi','unityVersion'):
        if not result.get(field) or result[field] != correctness.get(field): raise ValueError('identity mismatch: '+field)
    if len(correctness.get('checks',[]))!=10 or any(not all(c.get(k) for k in ('setEqual','imageEqual','nonEmptyImage')) or c['cpuCount']!=c['gpuCount'] for c in correctness['checks']):
        raise ValueError('incorrect validation checks')
    o=result['options']; n=o['frames']
    if (o['agents'],o['density'],o['seed'],o['warmup'],n)!=(100000,.25,69501203,300,1000): raise ValueError('checkpoint pilot contract mismatch')
    for field in ('agents','density','seed','frames'):
        if correctness['options'][field]!=o[field]: raise ValueError('correctness contract mismatch')
    if not all(result.get(k) for k in ('supportsGraphicsFence','warmupFenceCompleted','batchFenceCompleted')):
        raise ValueError('batch fence incomplete')
    ticks=[result[k] for k in ('batchStartTicks','finalSubmissionTicks','firstTrueFencePollTicks')]
    if not 0<ticks[0]<ticks[1]<=ticks[2] or result.get('stopwatchFrequency',0)<=0: raise ValueError('invalid completion boundaries')
    batch=(ticks[2]-ticks[0])*1000/result['stopwatchFrequency']
    if not math.isclose(batch,result['batchCompletionMs'],rel_tol=1e-8) or not math.isclose(batch/n,result['batchCompletionMsPerFrame'],rel_tol=1e-8): raise ValueError('inconsistent batch cost')
    bound_start=result['lastFalseFencePollTicks'] or result['finalSubmissionTicks']
    bound=(ticks[2]-bound_start)*1000/result['stopwatchFrequency']/n
    if not ticks[1]<=bound_start<=ticks[2] or not math.isclose(bound,result['fenceObservationBoundMsPerFrame'],rel_tol=1e-8): raise ValueError('invalid observation bound')
    samples=result.get('samples',[])
    if len(samples)!=n or result.get('gpuMappedFrames')!=n: raise ValueError('incomplete samples')
    for i,s in enumerate(samples):
        if not 0<=s['visibleCount']<=o['agents']: raise ValueError('invalid visible count')
        if s['frameIndex']!=i or s['submissionUnityFrame']!=result['firstMeasuredUnityFrame']+i or s['availabilityUnityFrame']!=s['submissionUnityFrame']+3: raise ValueError('frame attribution mismatch')
        fields=['cpuCullAndListMs','cpuUploadMs','cpuSubmitMs','cpuTotalMs','gpuDrawMs','gpuRangeMs']
        if result['mode']=='gpu': fields.append('gpuCullMs')
        for field in fields:
            if not math.isfinite(s[field]) or s[field]<0 or (field.startswith('gpu') and s[field]==0): raise ValueError('missing timing: '+field)
    if result.get('gc0Collections')!=0 or result.get('vsync')!=0 or result.get('targetFrameRate')!=-1: raise ValueError('GC/pacing settings gate')
    if not math.isclose(statistics.mean(s['visibleCount'] for s in samples)/o['agents'],result['actualVisibilityMean'],abs_tol=1e-8): raise ValueError('inconsistent visibility mean')
    # First interval spans the warmup-to-measurement transition; exclude this single boundary interval.
    pacing=pacing_status([s['updateIntervalMs'] for s in samples[1:]])
    if not pacing['passed']: raise ValueError('pacing gate: '+str(pacing))
    return pacing
