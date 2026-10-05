"""Analyze complete, source-frame-associated scan sweeps without inventing application timings."""
import argparse
import csv
import hashlib
import json
import math
from pathlib import Path
import statistics
from analyze_fluid_benchmark import percentile
from compare_fluid_benchmark import NAMES

def process(folder):
    folder = Path(folder)
    if (folder.parent/'invalid-attempt.json').is_file(): raise ValueError('Execution is explicitly marked invalid')
    run = json.loads((folder/'sweep.json').read_text(encoding='utf-8'))
    gate = json.loads((folder/'correctness.json').read_text(encoding='utf-8'))
    if run['schema'] != 'hlslperf.fluid-scan.sweep.v1' or run['status'] != 'passed' or not all(run[k] for k in
        ('fullOutputValidated','gpuFrameAssociationValidated')) or run['applicationFrameTimeMeasured']:
        raise ValueError('Incomplete or mislabeled isolated sweep')
    if gate['status'] != 'passed' or not gate['fullOutputValidated'] or run['fullSizeValidationPassesPerLength'] != 2:
        raise ValueError('Startup and post-timing full-output checks are required')
    settings = run['settings']
    lengths = settings['scanLengths']
    if len(set(lengths)) != len(lengths) or set(run['validatedLengths']) != set(lengths):
        raise ValueError('Lengths are duplicate or incompletely validated')
    intervals = {r['length']:r for r in run['intervals']}
    if set(intervals) != set(lengths): raise ValueError('Missing source-frame intervals')
    data = {n:dict(frames=set(),complete=[],core=[],adaptation=[]) for n in lengths}
    with (folder/'sweep-observations.csv').open(encoding='utf-8',newline='') as f:
        for row in csv.DictReader(f):
            n, frame, observed = (int(row[k]) for k in ('length','source_frame','observed_frame'))
            if n not in intervals or not intervals[n]['firstMeasuredFrame'] <= frame <= intervals[n]['lastMeasuredFrame'] or observed <= frame:
                raise ValueError('Unassociated or outside-window GPU observation')
            if frame in data[n]['frames']: raise ValueError('Duplicate source-frame observation')
            data[n]['frames'].add(frame)
            if int(row['complete_blocks']) != settings['scanBatch'] or int(row['core_blocks']) != settings['scanBatch']:
                raise ValueError('GPU block count mismatch')
            complete, core, batch = (float(row[k]) for k in ('complete_ms_per_scan','core_ms_per_scan','batch_gpu_ms'))
            if not all(math.isfinite(v) and v > 0 for v in (complete,core,batch)) or core > complete + 1e-9:
                raise ValueError('Invalid nested timing')
            data[n]['complete'].append(complete); data[n]['core'].append(core); data[n]['adaptation'].append(complete-core)
    result = {}
    for n, d in data.items():
        if len(d['frames']) != settings['measureFrames'] or intervals[n]['lastMeasuredFrame']-intervals[n]['firstMeasuredFrame']+1 != settings['measureFrames']:
            raise ValueError('Incomplete measurement window')
        result[n] = {metric:dict(p50Ms=percentile(d[metric],.5),p95Ms=percentile(d[metric],.95),meanMs=statistics.mean(d[metric]))
                     for metric in ('core','complete','adaptation')}
    return run, result

def paired_ratio(baseline, candidate):
    if len(baseline) != 4 or len(candidate) != 4: raise ValueError('This declared comparison requires four paired process repeats')
    logs = [math.log(b/c) for b,c in zip(baseline,candidate)]
    mean = statistics.mean(logs); margin = 3.182446305284263 * statistics.stdev(logs)/math.sqrt(4)
    return dict(baselineOverCandidate=math.exp(mean),pointwise95Interval=[math.exp(mean-margin),math.exp(mean+margin)],
                supportedFaster=math.exp(mean-margin)>1)

def compare(paths):
    groups = {name:{} for name in NAMES.values()}
    identity = None; seen = set()
    for path in map(Path,paths):
        run, data = process(path)
        launcher = json.loads((path/'process.json').read_text(encoding='utf-8'))
        repeat = launcher['repeat']; arm = NAMES[run['settings']['arm']]
        digest = hashlib.sha256((path/'sweep-observations.csv').read_bytes()).hexdigest()
        if digest in seen or repeat in groups[arm]: raise ValueError('Duplicate process evidence')
        seen.add(digest)
        signature = {k:run[k] for k in ('device','api','unity','timingMethod','inputPattern','coreBoundary','nativeTimingBuild','gpuTimestampFrequency')}
        signature.update({k:run['settings'][k] for k in ('scanBatch','warmupFrames','measureFrames','seed')})
        signature['lengths'] = sorted(run['settings']['scanLengths'])
        signature['payloadSha256'] = json.loads(run['provenance'])['payloadSha256']
        if identity is None: identity = signature
        if signature != identity: raise ValueError('Sweep source/configuration mismatch')
        if launcher['arm'] != arm or launcher['exitCode'] != 0: raise ValueError('Wrong or failed process receipt')
        groups[arm][repeat] = dict(run=path.name,observationsSha256=digest,metrics=data)
    if any(set(v) != {1,2,3,4} for v in groups.values()): raise ValueError('Four independent paired repeats per arm are required')
    result = {}
    for n in identity['lengths']:
        cells = {}
        for arm, repeats in groups.items():
            cells[arm] = {}
            for metric in ('core','complete','adaptation'):
                medians = [repeats[r]['metrics'][n][metric]['p50Ms'] for r in range(1,5)]
                cells[arm][metric] = dict(medianOfRunP50Ms=statistics.median(medians),minRunP50Ms=min(medians),maxRunP50Ms=max(medians))
                cells[arm][metric]['meanOfRunMeanMs'] = statistics.mean(repeats[r]['metrics'][n][metric]['meanMs'] for r in range(1,5))
        for arm in NAMES.values():
            if arm == 'original': continue
            cells[arm]['relativeToOriginal'] = {metric:paired_ratio(
                [groups['original'][r]['metrics'][n][metric]['meanMs'] for r in range(1,5)],
                [groups[arm][r]['metrics'][n][metric]['meanMs'] for r in range(1,5)]) for metric in ('core','complete')}
        cells['hlsl-wave-tiled-direct']['relativeToAdapted'] = paired_ratio(
            [groups['hlsl-wave-tiled'][r]['metrics'][n]['complete']['meanMs'] for r in range(1,5)],
            [groups['hlsl-wave-tiled-direct'][r]['metrics'][n]['complete']['meanMs'] for r in range(1,5)])
        cells['hlsl-wave-tiled-direct']['coreRelativeToRts'] = paired_ratio(
            [groups['gpuprefixsums-rts'][r]['metrics'][n]['core']['meanMs'] for r in range(1,5)],
            [groups['hlsl-wave-tiled-direct'][r]['metrics'][n]['core']['meanMs'] for r in range(1,5)])
        result[n] = cells
    return dict(schema='hlslperf.fluid-scan.sweep-cohort.v1',configuration=identity,lengths=result,processes=groups,
                statistic='p50 summarized descriptively; mean equally weights four process means; paired process-mean log-ratio Student-t intervals df=3, pointwise only',
                limitation='fixed regenerated full32 input and hot sequential GPU workload; no cold-cache, application or simultaneous confidence-band claim')

def main():
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('runs',nargs='+',type=Path);p.add_argument('--output',type=Path,required=True)
    a=p.parse_args(); result=compare(a.runs)
    with a.output.open('x',encoding='utf-8') as f: json.dump(result,f,indent=2,allow_nan=False);f.write('\n')
    print(json.dumps({n:{arm:cell['complete']['medianOfRunP50Ms'] for arm,cell in cells.items()} for n,cells in result['lengths'].items()},indent=2))
if __name__=='__main__':main()
