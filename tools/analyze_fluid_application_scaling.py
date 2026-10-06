"""Validate a declared four-arm application scaling cohort, including its process plan."""
import argparse
import csv
import hashlib
import json
import math
from pathlib import Path
import statistics
from analyze_fluid_sweep import paired_ratio
from analyze_fluid_benchmark import percentile
from compare_fluid_benchmark import compare, NAMES, METRICS
from run_fluid_scaling import schedule


def load(path):
    return json.loads(Path(path).read_text(encoding='utf-8'))


def associated_samples(folder):
    run = load(folder/'run.json')
    if not run.get('positionFiniteValidated') or not run.get('coreTimingEnabled') or run['gpuTimingMethod'] != 'd3d12_query_frame_fence':
        raise ValueError('Finite full-position validation and native core timing are required')
    frames = {}
    with (folder/'observations.csv').open(encoding='utf-8', newline='') as stream:
        for row in csv.DictReader(stream):
            observed, source = int(row['observed_unity_frame']), int(row['source_unity_frame'])
            metric = row['metric']
            expected_status = 'observed' if metric == 'wall_frame' else 'native_frame_fence_validated'
            if observed <= source or row['status'] != expected_status:
                raise ValueError('Observation is not associated with a completed source frame')
            value = float(row['ms'])
            if not math.isfinite(value) or value <= 0:
                raise ValueError('Invalid application timing')
            frame = frames.setdefault(source, {})
            if metric in frame:
                raise ValueError('Duplicate source-frame metric')
            frame[metric] = value
    first, last = run['firstMeasuredUnityFrame'], run['lastMeasuredUnityFrame']
    if set(frames) != set(range(first, last+1)):
        raise ValueError('Incomplete source-frame window')
    for frame in frames.values():
        if set(frame) != set((*METRICS, 'scan_core')):
            raise ValueError('Incomplete source-frame metric set')
        nested = [frame[k] for k in ('scan_core', 'scan_complete', 'count_sort_complete', 'spatial_hash_complete', 'simulation_complete')]
        if any(a > b+1e-9 for a, b in zip(nested, nested[1:])):
            raise ValueError('Invalid nested application timing')
    return frames


def analyze(root):
    root = Path(root)
    if (root/'invalid-attempt.json').exists():
        raise ValueError('Execution is explicitly marked invalid')
    plan, completion = load(root/'plan.json'), load(root/'completion.json')
    if plan['stage'] != 'application' or plan['recording'] or completion['status'] != 'passed':
        raise ValueError('Completed recording-free application stage is required')
    cases = plan['cases']
    densities = list(dict.fromkeys(c['density'] for c in cases))
    expected = schedule('application', densities, cases[0]['frames'])
    if cases != expected or completion['processes'] != len(expected):
        raise ValueError('Declared four balanced orders are incomplete or changed')
    actual = {p.name for p in root.iterdir() if p.is_dir()}
    if actual != {c['name'] for c in cases}:
        raise ValueError('Missing or undeclared process directories')
    identity = None
    timestamp_frequency = None
    groups = {density:{arm:{} for arm in NAMES.values()} for density in densities}
    for case in cases:
        folder = root/case['name']
        run, receipt = load(folder/'run.json'), load(folder/'process.json')
        frequency = int(run['gpuTimestampFrequency'])
        if frequency <= 0 or (timestamp_frequency is not None and frequency != timestamp_frequency):
            raise ValueError('Invalid or changed graphics-queue timestamp frequency')
        timestamp_frequency = frequency
        if receipt != load(root/(case['name']+'.process.json')):
            raise ValueError('Process receipt copies disagree')
        if any(receipt[k] != v for k,v in case.items()) or receipt['exitCode'] != 0 or receipt['playerSha256'] != plan['playerFilesSha256']['FluidScan.exe']:
            raise ValueError('Failed process or mismatched immutable Player/plan')
        settings = run['settings']
        if NAMES[settings['arm']] != case['arm'] or any(settings[k] != case[v] for k,v in
                (('spawnDensity','density'),('warmupFrames','warmup'),('measureFrames','frames'))):
            raise ValueError('Run does not implement the declared case')
        associated_samples(folder)
        groups[case['density']][case['arm']][case['repeat']] = folder
    results = {}
    hashes = set()
    for density, arms in groups.items():
        cohort = compare([arms[a][r] for a in NAMES.values() for r in range(1,5)])
        common = {k:v for k,v in cohort['configuration'].items() if k not in ('particles','spawnDensity')}
        if identity is None:
            identity = common
        if common != identity:
            raise ValueError('Application configuration/source changed across sizes')
        cells = {}
        for arm, runs in cohort['arms'].items():
            repeats = {}
            for r in range(1,5):
                folder = arms[arm][r]
                sha = hashlib.sha256((folder/'observations.csv').read_bytes()).hexdigest()
                if sha in hashes:
                    raise ValueError('Duplicate evidence across application sizes')
                hashes.add(sha)
                repeats[r] = next(x for x in runs['runs'] if x['run'] == folder.name)
            cells[arm] = dict(metrics={}, processes=repeats)
            for metric in (*METRICS, 'scan_core'):
                cells[arm]['metrics'][metric] = dict(runs['metrics'][metric],
                    meanOfRunMeanMs=statistics.mean(repeats[r]['metrics'][metric]['meanMs'] for r in range(1,5)))
            outside = [[v['scan_complete']-v['scan_core'] for v in associated_samples(arms[arm][r]).values()] for r in range(1,5)]
            cells[arm]['pairedOutsideCore'] = dict(meanOfRunMeanMs=statistics.mean(statistics.mean(v) for v in outside),
                medianOfRunP50Ms=statistics.median(percentile(v,.5) for v in outside))
            cells[arm]['scanFractionOfWallPercent'] = 100*cells[arm]['metrics']['scan_complete']['meanOfRunMeanMs']/cells[arm]['metrics']['wall_frame']['meanOfRunMeanMs']
        for arm in NAMES.values():
            if arm == 'original':
                continue
            cells[arm]['relativeToOriginal'] = {metric:paired_ratio(
                [cells['original']['processes'][r]['metrics'][metric]['meanMs'] for r in range(1,5)],
                [cells[arm]['processes'][r]['metrics'][metric]['meanMs'] for r in range(1,5)]) for metric in (*METRICS,'scan_core')}
        cells['hlsl-wave-tiled-direct']['relativeToAdapted'] = {metric:paired_ratio(
            [cells['hlsl-wave-tiled']['processes'][r]['metrics'][metric]['meanMs'] for r in range(1,5)],
            [cells['hlsl-wave-tiled-direct']['processes'][r]['metrics'][metric]['meanMs'] for r in range(1,5)]) for metric in (*METRICS,'scan_core')}
        results[cohort['configuration']['particles']] = dict(spawnDensity=density,arms=cells)
    return dict(schema='hlslperf.fluid-scan.application-scaling.v1',configuration=identity,particleCounts=results,
        processCount=len(cases), playerFilesSha256=plan['playerFilesSha256'],gpuTimestampFrequency=timestamp_frequency,
        measuredSimulatedSeconds=[identity['warmupFrames']*identity['fixedTimestep'],
            (identity['warmupFrames']+identity['measureFrames'])*identity['fixedTimestep']],
        statistic='equal-weight process means; paired process-mean log-ratio Student-t intervals df=3, pointwise only; p50/p95 descriptive',
        boundary='core includes reset plus full exclusive scan; complete includes any bridges; scan accumulates three calls per simulation frame; wall includes rendering, presentation and harness',
        limitation='four process pairs, one device, one declared fixed simulation window; density changes physical load; unstable equal-key ordering can change trajectories; pointwise intervals are not a simultaneous band or causal attribution')


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('root', type=Path)
    p.add_argument('--output', required=True, type=Path)
    a = p.parse_args()
    result = analyze(a.root)
    with a.output.open('x', encoding='utf-8') as stream:
        json.dump(result, stream, indent=2, allow_nan=False)
        stream.write('\n')
    print(json.dumps({n:{arm:cell['metrics']['wall_frame']['meanOfRunMeanMs'] for arm,cell in row['arms'].items()} for n,row in result['particleCounts'].items()}, indent=2))


if __name__ == '__main__':
    main()
