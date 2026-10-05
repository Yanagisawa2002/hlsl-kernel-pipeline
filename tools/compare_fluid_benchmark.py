"""Compare a balanced repeated fluid cohort; reject incomplete or mismatched inputs."""
import argparse
import hashlib
import json
from pathlib import Path
import statistics
from analyze_fluid_benchmark import summarize

NAMES = {0: 'original', 1: 'hlsl-wave-tiled', 2: 'gpuprefixsums-rts', 3: 'hlsl-wave-tiled-direct'}
METRICS = ('scan_complete','count_sort_complete','spatial_hash_complete','simulation_complete','wall_frame')

def compare(paths):
    groups = {name: [] for name in NAMES.values()}
    identity = None
    seen_paths, seen_observations = set(), set()
    for path in map(Path, paths):
        resolved = path.resolve()
        observation_hash = hashlib.sha256((path/'observations.csv').read_bytes()).hexdigest()
        if resolved in seen_paths or observation_hash in seen_observations:
            raise ValueError('Duplicate process evidence is not an independent repeat')
        seen_paths.add(resolved);seen_observations.add(observation_hash)
        run = json.loads((path/'run.json').read_text())
        settings = run['settings']
        provenance = json.loads(run['provenance'])
        signature = {key:run[key] for key in ('device','api','unity','width','height','particles','foamCapacity','iterationsPerFrame','fixedTimestep','gpuTimingMethod')}
        signature.update({key:settings[key] for key in ('seed','spawnDensity','warmupFrames','measureFrames','nativeGpuTiming')})
        signature['payloadSha256'] = provenance['payloadSha256']
        signature['nativeTimingBuild'] = run['nativeTimingBuild']
        if 'coreTimingEnabled' in run: signature['coreTimingEnabled'] = run['coreTimingEnabled']
        if identity is None: identity = signature
        if signature != identity: raise ValueError('Cohort configuration/source mismatch: ' + str(path))
        summary = summarize(path)
        metrics_to_compare = (*METRICS, 'scan_core') if run.get('coreTimingEnabled') else METRICS
        if not summary['gpuDelayValidated'] or any(summary['metrics'][key]['status'] != 'observed' for key in metrics_to_compare):
            raise ValueError('Complete, associated GPU and wall observations are required: ' + str(path))
        groups[NAMES[settings['arm']]].append(dict(run=path.name,metrics=summary['metrics'],
            observationsSha256=observation_hash))
    if not groups['hlsl-wave-tiled-direct']: del groups['hlsl-wave-tiled-direct']
    if len({len(v) for v in groups.values()}) != 1 or min(len(v) for v in groups.values()) < 3:
        raise ValueError('At least three equally repeated runs per arm are required')
    arms = {}
    for name, runs in groups.items():
        metrics = {}
        for key in metrics_to_compare:
            medians = [r['metrics'][key]['p50Ms'] for r in runs]
            metrics[key] = dict(medianOfRunP50Ms=statistics.median(medians),minRunP50Ms=min(medians),maxRunP50Ms=max(medians),
                medianOfRunP95Ms=statistics.median(r['metrics'][key]['p95Ms'] for r in runs),
                medianOfRunMeanMs=statistics.median(r['metrics'][key]['meanMs'] for r in runs))
        arms[name] = dict(repeats=len(runs),metrics=metrics,runs=runs)
    baseline = arms['original']['metrics']
    for name, arm in arms.items():
        arm['relativeToOriginal'] = {key:dict(millisecondsSaved=baseline[key]['medianOfRunP50Ms']-arm['metrics'][key]['medianOfRunP50Ms'],
            timeChangePercent=100*(arm['metrics'][key]['medianOfRunP50Ms']/baseline[key]['medianOfRunP50Ms']-1)) for key in metrics_to_compare}
    return dict(schema='hlslperf.fluid-scan.cohort.v1',configuration=identity,arms=arms,
        statistic='median of independent per-process p50 values; p95 values are median per-process p95',
        scanBoundary='complete scan including any pack, reset, scan and unpack; direct returns its raw result without bridges; three calls accumulated per simulation frame',
        inference='descriptive cohort only; small whole-frame differences are not a statistical speedup claim')

def main():
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('runs',nargs='+',type=Path);p.add_argument('--output',required=True,type=Path)
    args=p.parse_args(); result=compare(args.runs)
    with args.output.open('x',encoding='utf-8') as f: json.dump(result,f,indent=2,allow_nan=False);f.write('\n')
    print(json.dumps({name:{key:m['medianOfRunP50Ms'] for key,m in arm['metrics'].items()} for name,arm in result['arms'].items()},indent=2))
if __name__=='__main__':main()
