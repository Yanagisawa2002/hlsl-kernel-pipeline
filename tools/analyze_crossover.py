"""Strict process-level resource-cost analysis; never infer latency from unlike metrics."""
import argparse
import csv
import json
import math
from pathlib import Path
import random
import statistics

METRICS = ('cpuCullAndListMs', 'cpuUploadMs', 'cpuSubmitMs', 'cpuTotalMs', 'gpuCullMs', 'gpuDrawMs', 'gpuRangeMs')


def validate_result(r, correctness):
    if r.get('schema') != 1 or not r.get('completed') or r.get('error'):
        raise ValueError('incomplete/failed result')
    if r.get('mode') not in ('cpu', 'gpu') or not r.get('runId') or not r.get('pairId'):
        raise ValueError('missing arm/run/pair identity')
    if not correctness.get('completed') or not correctness.get('correctnessPassed') or len(correctness.get('checks', [])) != 10:
        raise ValueError('missing correctness gate')
    for key in ('sourceIdentity', 'calibrationSha256', 'adapter', 'driver', 'graphicsApi', 'unityVersion'):
        if not r.get(key) or r[key] != correctness.get(key):
            raise ValueError('identity mismatch: ' + key)
    for check in correctness['checks']:
        if not all(check.get(k) for k in ('setEqual', 'imageEqual', 'nonEmptyImage')) or check['cpuCount'] != check['gpuCount']:
            raise ValueError('correctness failure')
    o = r['options']
    for key in ('agents', 'seed', 'density', 'frames'):
        if o.get(key) != correctness.get('options', {}).get(key):
            raise ValueError('workload mismatch: ' + key)
    if not r.get('gpuTimingStatus', '').startswith('complete:'):
        raise ValueError('GPU timing gate incomplete')
    expected_frames = [i * (o['frames'] - 1) // 9 for i in range(10)]
    if [c['frameIndex'] for c in correctness['checks']] != expected_frames:
        raise ValueError('incorrect validation frames')
    if len(r.get('samples', [])) != o['frames']:
        raise ValueError('incomplete sample count')
    for i, sample in enumerate(r['samples']):
        if sample['frameIndex'] != i or not 0 <= sample['visibleCount'] <= o['agents']:
            raise ValueError('invalid frame/count')
        for key in METRICS:
            value = sample.get(key)
            if r['mode'] == 'cpu' and key == 'gpuCullMs' and value == -1:
                continue
            if not isinstance(value, (float, int)) or not math.isfinite(value) or value < 0:
                raise ValueError('missing/invalid timing: ' + key)
    ratio = statistics.mean(s['visibleCount'] for s in r['samples']) / o['agents']
    if not math.isclose(ratio, r['actualVisibilityMean'], rel_tol=0, abs_tol=1e-8):
        raise ValueError('inconsistent actual visibility')
    if r.get('vsync') != 0 or r.get('targetFrameRate') != -1 or r.get('gc0Collections') != 0:
        raise ValueError('pacing/GC gate requires review')
    return r


def drift(values):
    quarter = max(1, len(values) // 4)
    first = statistics.mean(values[:quarter]); last = statistics.mean(values[-quarter:])
    return abs(last / first - 1) if first else (0 if not last else math.inf)


def paired_ratio(cpu, gpu, draws=10000):
    if len(cpu) != len(gpu) or len(cpu) < 5 or any(x <= 0 for x in cpu + gpu):
        raise ValueError('need >=5 positive matched independent process pairs')
    rng = random.Random(69501203); n = len(cpu); ratios = []
    for _ in range(draws):
        indices = [rng.randrange(n) for _ in range(n)]
        ratios.append(sum(cpu[i] for i in indices) / sum(gpu[i] for i in indices))
    ratios.sort()
    return {'ratio': statistics.mean(cpu) / statistics.mean(gpu), 'low': ratios[int(draws * .025)], 'high': ratios[int(draws * .975)]}


def aggregate(records):
    groups = {}; seen = set(); identities = set()
    for r in records:
        if r['runId'] in seen: raise ValueError('duplicate process/run input')
        seen.add(r['runId'])
        identities.add(tuple(r[k] for k in ('sourceIdentity', 'adapter', 'driver', 'graphicsApi', 'unityVersion', 'buildConfiguration', 'width', 'height')))
        o = r['options']; key = (o['agents'], o['density'], r['calibrationSha256'])
        groups.setdefault(key, {}).setdefault(r['pairId'], {})
        if r['mode'] in groups[key][r['pairId']]: raise ValueError('duplicate arm in pair')
        groups[key][r['pairId']][r['mode']] = r
    if len(identities) > 1: raise ValueError('mixed hardware/build/source/resolution')
    rows = []
    for (agents, density, calibration), pairs in sorted(groups.items()):
        if any(set(p) != {'cpu', 'gpu'} for p in pairs.values()): raise ValueError('unmatched pair')
        for metric in METRICS:
            if metric == 'gpuCullMs': arms = ['gpu']
            elif metric in ('cpuCullAndListMs', 'cpuUploadMs'): arms = ['cpu']
            else: arms = ['cpu', 'gpu']
            values = {}
            for mode in arms:
                runs = [p[mode] for p in pairs.values()]
                values[mode] = [statistics.mean(s[metric] for s in r['samples']) for r in runs]
                r = runs[0]
                rows.append({'agents': agents, 'targetVisibility': density, 'actualVisibility': r['actualVisibilityMean'],
                    'visibleCount': agents * r['actualVisibilityMean'], 'metric': metric, 'arm': mode,
                    'processes': len(runs), 'meanMs': statistics.mean(values[mode]),
                    'medianProcessMeanMs': statistics.median(values[mode]), 'minProcessMeanMs': min(values[mode]),
                    'maxProcessMeanMs': max(values[mode]), 'processMeansMs': values[mode], 'calibrationSha256': calibration})
            # CPU work relief is comparable as CPU cost, but is not an architecture speedup.
            if metric == 'cpuTotalMs' and len(pairs) >= 5:
                interval = paired_ratio(values['cpu'], values['gpu'])
                for row in rows[-2:]: row['cpuResourceRatioOnly'] = interval
    return {'schema': 1, 'architectureCrossover': 'not established: no eligible end-to-end metric', 'rows': rows}


def export(summary, out):
    out.mkdir(parents=True, exist_ok=True)
    (out / 'summary.json').write_text(json.dumps(summary, indent=2), encoding='utf-8')
    fields = ['agents', 'targetVisibility', 'actualVisibility', 'visibleCount', 'metric', 'arm', 'processes', 'meanMs', 'medianProcessMeanMs', 'minProcessMeanMs', 'maxProcessMeanMs']
    with (out / 'summary.csv').open('w', newline='', encoding='utf-8') as f:
        writer = csv.DictWriter(f, fields, extrasaction='ignore'); writer.writeheader(); writer.writerows(summary['rows'])
    lines = ['# Resource costs (not architecture latency)', '', summary['architectureCrossover'], '', '| N | Target | Actual | Metric | Arm | Processes | Mean ms |', '|---:|---:|---:|---|---|---:|---:|']
    for row in summary['rows']:
        lines.append(f"| {row['agents']} | {row['targetVisibility']:.0%} | {row['actualVisibility']:.2%} | {row['metric']} | {row['arm']} | {row['processes']} | {row['meanMs']:.3f} |")
    (out / 'summary.md').write_text('\n'.join(lines) + '\n', encoding='utf-8')


def plot_resources(summary, out):
    """Plot only eligible same-resource costs. No architecture-ratio plot is fabricated."""
    import matplotlib
    matplotlib.use('Agg')
    import matplotlib.pyplot as plt
    for density in sorted({r['targetVisibility'] for r in summary['rows']}):
        for axis, label, prefix in [('agents', 'Total agent count', 'A'), ('visibleCount', 'Mean visible count (actual)', 'C')]:
            fig, panels = plt.subplots(1, 2, figsize=(11, 4), constrained_layout=True)
            for ax, metric, title in zip(panels, ['cpuTotalMs', 'gpuDrawMs'], ['CPU main-thread benchmark cost', 'GPU draw range only']):
                for arm in ('cpu', 'gpu'):
                    rows = sorted([r for r in summary['rows'] if r['targetVisibility'] == density and r['metric'] == metric and r['arm'] == arm], key=lambda r:r[axis])
                    ax.plot([r[axis] for r in rows], [r['meanMs'] for r in rows], 'o-', label=arm.upper())
                    for r in rows:
                        ax.scatter([r[axis]] * len(r['processMeansMs']), r['processMeansMs'], s=12, alpha=.35)
                ax.set(xlabel=label, ylabel='ms', title=title); ax.legend(); ax.grid(alpha=.25)
            fig.suptitle(f'Target visibility {density:.0%}; process means; resource costs, not end-to-end latency')
            fig.savefig(out / f'{prefix}-resource-cost-{density:g}.png', dpi=160); plt.close(fig)
    (out / 'plot-B-omitted.txt').write_text('Architecture CPU/GPU ratio omitted: no validated comparable critical-path metric. CPU and GPU milliseconds are different resources.\n')


def main():
    p = argparse.ArgumentParser(description=__doc__); p.add_argument('directory', type=Path); p.add_argument('--output', required=True, type=Path)
    p.add_argument('--plots', action='store_true', help='Requires matplotlib; outputs resource-only plots A/C, omits ineligible B')
    args = p.parse_args(); records = []
    for path in sorted(args.directory.glob('measure-*.json')):
        if path.name.endswith('.launch.json'): continue
        r = json.loads(path.read_text()); o = r['options']; tag = f"{o['agents']}-{o['density']:g}"
        receipt = json.loads(path.with_suffix('.launch.json').read_text())
        if receipt.get('returnCode') != 0 or receipt.get('status') != 'exited':
            raise ValueError('failed process receipt: ' + path.name)
        before, after = receipt['before'], receipt['after']
        if len(before) != 3 or any('error' in s or any(a['utilization'] > 5 for a in s['adapters']) for s in before) or 'error' in after:
            raise ValueError('background/metadata gate failed: ' + path.name)
        if len(before[-1]['adapters']) != len(after['adapters']) or any(
                a['name'] != b['name'] or a['driver'] != b['driver'] or abs(a['temperature'] - b['temperature']) > 10
                for a,b in zip(before[-1]['adapters'], after['adapters'])):
            raise ValueError('hardware/thermal gate failed: ' + path.name)
        correctness = json.loads((args.directory / f'validation-{tag}.json').read_text())
        validate_result(r, correctness)
        for metric in ('cpuTotalMs', 'gpuRangeMs'):
            if drift([s[metric] for s in r['samples']]) > .15: raise ValueError(f'drift gate failed: {path.name} {metric}')
        records.append(r)
    if not records: raise ValueError('No primary measurement files; pilots are deliberately excluded')
    if any(r['options']['frames'] != 1000 or r['options']['warmup'] != 300 for r in records):
        raise ValueError('Primary schedule mismatch')
    counts = {}
    for r in records:
        key = (r['options']['agents'], r['options']['density'], r['mode'])
        counts[key] = counts.get(key, 0) + 1
    if any(n < 6 for n in counts.values()): raise ValueError('Need six independent processes per condition/arm')
    summary = aggregate(records); export(summary, args.output)
    if args.plots: plot_resources(summary, args.output)


if __name__ == '__main__': main()
