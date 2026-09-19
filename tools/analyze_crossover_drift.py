"""Offline drift forensics and candidate-policy sensitivity, NEVER an acceptance checker."""
import argparse
import gzip
import hashlib
import json
import math
from pathlib import Path
import statistics

from analyze_crossover import drift
from check_crossover_timing import percentile
from check_integrated_crossover import validate

METRICS = ('gpuRangeMs', 'gpuDrawMs', 'cpuTotalMs', 'cpuCullAndListMs',
           'cpuUploadMs', 'cpuSubmitMs', 'updateIntervalMs')


def checked(values):
    xs = list(values)
    if len(xs) < 4 or any(isinstance(x, bool) or not isinstance(x, (int, float))
                          or not math.isfinite(x) or x < 0 for x in xs):
        raise ValueError('need >=4 finite nonnegative numeric samples')
    return xs


def quarter_stats(values, architecture_ms=None):
    xs = checked(values)
    if architecture_ms is not None and (not math.isfinite(architecture_ms) or architecture_ms <= 0):
        raise ValueError('architecture scale must be finite and positive')
    q = len(xs) // 4
    first, last = statistics.mean(xs[:q]), statistics.mean(xs[-q:])
    absolute = abs(last-first)
    relative = drift(xs)  # Exact existing v4 definition, including its zero behavior.
    return {'firstQuarterMeanMs': first, 'lastQuarterMeanMs': last,
            'signedShiftMs': last-first, 'absoluteDriftMs': absolute,
            'relativeDrift': relative if math.isfinite(relative) else None,
            'relativeStatus': 'infinite: zero first/nonzero last' if not math.isfinite(relative) else 'finite',
            'architectureFraction': absolute/architecture_ms if architecture_ms is not None else None}


def relative_exceeds(d, limit=.15):
    return d['relativeDrift'] is None or d['relativeDrift'] > limit


def candidates(values, architecture_ms, primary_values=None):
    """Hypothetical criteria only. No formal status is set or changed.

    A: relative >15% AND absolute >0.01ms.
    B: relative >15% AND architecture fraction >1%.
    C: keep primary CPU >15%; stage fraction >1% fails regardless of relative.
    """
    d = quarter_stats(values, architecture_ms)
    primary = quarter_stats(primary_values) if primary_values is not None else None
    relative = relative_exceeds(d)
    return {'diagnostic': d, 'v4StageCriterionTriggers': relative,
            'AStageTriggers': relative and d['absoluteDriftMs'] > .01,
            'BStageTriggers': None if architecture_ms is None else relative and d['architectureFraction'] > .01,
            'CStageTriggers': None if architecture_ms is None else d['architectureFraction'] > .01,
            'primaryCpuCriterionTriggers': None if primary is None else relative_exceeds(primary),
            'diagnosticOnly': True}


def distribution(values):
    xs = checked(values)
    mean, std = statistics.mean(xs), statistics.pstdev(xs)
    return {'min': min(xs), 'p10': percentile(xs,.1), 'median': statistics.median(xs),
            'mean': mean, 'p90': percentile(xs,.9), 'p95': percentile(xs,.95),
            'p99': percentile(xs,.99), 'max': max(xs), 'stddev': std,
            'CV': std/mean if mean else 0.0}


def ranks(values):
    order = sorted(range(len(values)), key=values.__getitem__)
    result = [0.0]*len(values)
    i = 0
    while i < len(order):
        j = i+1
        while j < len(order) and values[order[j]] == values[order[i]]:
            j += 1
        for idx in order[i:j]:
            result[idx] = (i+j-1)/2
        i = j
    return result


def correlation(a,b):
    am,bm = statistics.mean(a),statistics.mean(b)
    numerator = sum((x-am)*(y-bm) for x,y in zip(a,b))
    denom = math.sqrt(sum((x-am)**2 for x in a)*sum((y-bm)**2 for y in b))
    return numerator/denom if denom else None


def trend(values):
    xs = checked(values)
    n = len(xs); center = (n-1)/2; mean = statistics.mean(xs)
    slope = sum((i-center)*(v-mean) for i,v in enumerate(xs))/sum((i-center)**2 for i in range(n))
    corr = correlation(list(range(n)),xs)
    return {'slopeMsPerFrame':slope,'fittedEndToEndShiftMs':slope*(n-1),
            'linearR2':corr*corr if corr is not None else None,
            'spearmanFrameRank':correlation(list(range(n)),ranks(xs)),
            'firstHalfMeanMs':statistics.mean(xs[:n//2]),'secondHalfMeanMs':statistics.mean(xs[n//2:])}


def read(path):
    raw=path.read_bytes()
    return json.loads(gzip.decompress(raw) if path.suffix=='.gz' else raw)


def integrity(d):
    samples=d['samples'];warmup=d['options']['warmup'];n=len(samples)
    sid=[s['submissionId'] for s in samples];rid=[s['resolvedSubmissionId'] for s in samples]
    expected=list(range(warmup+1,warmup+n+1))
    return {'samples':n,'submitted':d['timestampSubmitted'],'resolved':d['timestampResolved'],
            'invalid':d['timestampInvalid'],'maxRingOccupancy':d['maxRingOccupancy'],
            'timestampFrequencyUnique':sorted({s['gpuTimestampFrequency'] for s in samples}),
            'submissionSequenceValid':sid==expected,'resolvedSequenceValid':rid==expected,
            'duplicateSubmissionIds':len(sid)-len(set(sid)),'duplicateResolvedIds':len(rid)-len(set(rid)),
            'ringSlotFailures':sum(s['timestampRingSlot']!=(s['submissionId']-1)%32 for s in samples),
            'orderingFailures':sum(not 0<s['gpuTimestampT0']<=s['gpuTimestampT1']<=s['gpuTimestampT2'] for s in samples),
            'fenceFailures':sum(not 0<s['requiredFence']<=s['completedFence']<2**64-1 for s in samples),
            'timestampFrequencyMismatch':sum(s['gpuTimestampFrequency']!=d['timestampFrequency'] for s in samples),
            'crossSubmissionBackwardTicks':sum(samples[i]['gpuTimestampT0']<samples[i-1]['gpuTimestampT2'] for i in range(1,n)),
            'duplicateRawTriplets':n-len({(s['gpuTimestampT0'],s['gpuTimestampT1'],s['gpuTimestampT2']) for s in samples}),
            'cpuCullAllNull':all(s['gpuCullMs'] is None for s in samples)}


def analyze(root):
    evidence=root/'docs/evidence'
    target=evidence/'gpu-quiet-window-20260919/overhead-cpu-0-on.json.gz'
    d=read(target);validate(d,False)
    assert len(d['samples'])==d['timestampSubmitted']==d['timestampResolved']==1000
    assert d['timestampInvalid']==0 and d['maxRingOccupancy']==3
    try:
        validate(d,True)
    except ValueError as exc:
        rejection=str(exc)
    else:
        raise ValueError('expected preserved v4 quality rejection')
    assert rejection=='GPU drift'
    xs={k:[s[k] for s in d['samples']] for k in METRICS}
    batch=d['batchCompletionMsPerFrame']
    # Gap from integer tick differences avoids subtracting two rounded durations.
    gap=[(s['gpuTimestampT1']-s['gpuTimestampT0'])*1000/s['gpuTimestampFrequency'] for s in d['samples']]
    blocks=[]
    for start in range(0,1000,100):
        part=d['samples'][start:start+100]
        blocks.append({'startFrame':start,'endFrame':start+99,
                       **{k+'Mean':statistics.mean(s[k] for s in part) for k in
                          ('gpuRangeMs','gpuDrawMs','cpuTotalMs','updateIntervalMs','visibleCount')},
                       'gpuRangeMsMedian':statistics.median(s['gpuRangeMs'] for s in part)})
    paths={'v4_cpu96':evidence/'gpu-timing-integrated-v4-20260919/runs/integration-cpu.json',
           'v4_gpu96':evidence/'gpu-timing-integrated-v4-20260919/runs/integration-gpu.json',
           'v4_cpu_off1000':evidence/'gpu-timing-quality-20260919/overhead-cpu-0-off.json',
           'v4_cpu_on1000_rejected':target,
           'v3_native_normal':evidence/'gpu-timing-v3-20260919/native-final/normal.json',
           'v3_native_batch':evidence/'gpu-timing-v3-20260919/native-final/batch.json'}
    comparisons={}
    for label,path in paths.items():
        old=read(path);ss=old['samples'];gpu=[s['gpuRangeMs'] for s in ss];primary=[s['cpuTotalMs'] for s in ss] if 'cpuTotalMs' in ss[0] else None
        comparisons[label]={'path':path.relative_to(root).as_posix(),'n':len(ss),
                            'architectureMs':old.get('batchCompletionMsPerFrame'),
                            'primaryDrift':quarter_stats(primary) if primary is not None else None,
                            'stageCandidates':candidates(gpu,old.get('batchCompletionMsPerFrame'),primary) if all(x is not None for x in gpu) else None,
                            'repetitionsUnique':sorted({s.get('repetitions',1) for s in ss}),
                            'notAcceptance':True}
    shift=quarter_stats(xs['gpuRangeMs'],batch)
    return {'kind':'offline forensics only; not acceptance; no hardware launch',
            'preservedStatus':'REJECTED UNDER V4','rejection':rejection,
            'sourceIdentity':d['sourceIdentity'],'pluginSha256':d['pluginSha256'],
            'rawSha256':hashlib.sha256(gzip.decompress(target.read_bytes())).hexdigest(),
            'integrity':integrity(d),'quarterDrifts':{k:quarter_stats(v,batch) for k,v in xs.items()},
            'distributions':{k:distribution(xs[k]) for k in ('gpuRangeMs','gpuDrawMs','cpuTotalMs','updateIntervalMs')},
            'trends':{k:trend(v) for k,v in xs.items()},'blocks':blocks,
            'gap':{'distribution':distribution(gap),'quarterDrift':quarter_stats(gap,batch),
                   'nonzeroSamples':sum(x!=0 for x in gap)},
            'architectureScale':{'batchCompletionMsPerFrame':batch,'meanCpuTotalMs':statistics.mean(xs['cpuTotalMs']),
                                 'absoluteGpuShiftMs':shift['absoluteDriftMs'],
                                 'fractionOfBatch':shift['architectureFraction'],
                                 'fractionOfMeanCpu':shift['absoluteDriftMs']/statistics.mean(xs['cpuTotalMs'])},
            'candidateComparisons':comparisons,
            'inputsSha256':{path.relative_to(root).as_posix():hashlib.sha256(path.read_bytes()).hexdigest() for path in paths.values()}}


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output',required=True,type=Path)
    args=parser.parse_args()
    if args.output.exists():parser.error('refusing overwrite')
    result=analyze(Path(__file__).resolve().parents[1])
    args.output.parent.mkdir(parents=True,exist_ok=True)
    args.output.write_text(json.dumps(result,indent=2,allow_nan=False)+'\n',encoding='utf-8')


if __name__=='__main__':main()
