"""Frozen, balanced three-arm native scan comparison. Hardware commands require the shared lock."""
import argparse
import csv
import hashlib
import itertools
import json
import math
import re
import statistics
import subprocess
from datetime import datetime, timezone
from pathlib import Path

from verify_external_sources import verify

ROOT = Path(__file__).resolve().parents[1]
ARMS = ('tile', 'tile-fused', 'rts')
COUNT = 1 << 28
T5 = 2.570581835636314


def now(): return datetime.now(timezone.utc).isoformat()
def read(path): return json.loads(Path(path).read_text(encoding='utf-8-sig'))
def save(path, data): Path(path).write_text(json.dumps(data, indent=2) + '\n', encoding='utf-8')
def sha(path):
    with Path(path).open('rb') as stream: return hashlib.file_digest(stream, 'sha256').hexdigest()


def identity(native):
    prep = read(native / 'preparation.json')
    for name, digest in prep['localAdapterFiles'].items():
        if sha(ROOT / 'benchmarks/external/native' / name) != digest:
            raise ValueError('Adapter changed since build: ' + name)
    if sha(native / 'scan/scan.exe') != prep['binaries']['scan/scan.exe']:
        raise ValueError('Executable differs from build receipt.')
    return {'sourceCommit': subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=ROOT, text=True).strip(),
        'upstream': verify(ROOT), 'preparationSha256': sha(native / 'preparation.json'),
        'sourceFiles': {p.relative_to(ROOT).as_posix(): sha(p) for folder in ['kernels', 'benchmarks/external/native', 'src/HlslPerf.Workloads', 'tools/HlslPerf.InclusiveScanValidation']
            for p in sorted((ROOT / folder).rglob('*')) if p.is_file() and p.suffix in ('.cs', '.hlsl', '.hlsli', '.h', '.cpp', '.compute')
            and 'obj' not in p.parts and 'bin' not in p.parts},
        'runnerSha256': sha(Path(__file__)),
        'runtimeFiles': {p.relative_to(native).as_posix(): sha(p) for p in sorted((native / 'scan').rglob('*'))
            if p.is_file() and p.suffix.lower() not in ('.pdb', '.ilk', '.lib', '.exp')}}


def execute(native, output, tag, mode, arm, device, export=None):
    exe = native / 'scan/scan.exe'
    command = [str(exe), mode, arm, str(COUNT), str(device['luid']), device['adapter']]
    if export is not None: command.append(str(export))
    log = output / (tag + '.log')
    status = {'tag': tag, 'mode': mode, 'arm': arm, 'count': COUNT, 'command': command,
        'cwd': str(exe.parent), 'startedUtc': now(), 'executableSha256': sha(exe)}
    save(output / (tag + '.status.json'), status)
    with log.open('wb') as stream:
        child = subprocess.Popen(command, cwd=exe.parent, stdout=stream, stderr=subprocess.STDOUT,
            creationflags=subprocess.CREATE_NO_WINDOW)
        status['pid'] = child.pid
        save(output / (tag + '.status.json'), status)
        try: status['exitCode'] = child.wait(timeout=1200)
        except subprocess.TimeoutExpired:
            # Terminate only this runner's own timed-out child, never another task.
            child.kill(); child.wait(); status.update(exitCode=child.returncode, timeout=True)
    status['finishedUtc'] = now()
    status['logSha256'] = sha(log)
    text = log.read_text(encoding='utf-8', errors='replace')
    status['events'] = [json.loads(line[7:]) for line in text.splitlines() if line.startswith('HPJSON ')]
    status['totalsSeconds'] = [float(s) for s in re.findall(r'Total time elapsed: ([0-9.eE+-]+)', text)]
    save(output / (tag + '.status.json'), status)
    print(tag, 'exit', status['exitCode'], 'totals', status['totalsSeconds'], flush=True)
    if status['exitCode'] != 0 or 'RUNTIME_FAILED' in text or 'TEST FAILED' in text:
        raise RuntimeError('Preserve failed process: ' + str(log))
    devices = [event for event in status['events'] if event['kind'] == 'device']
    if len(devices) != 1 or any(devices[0][key] != value for key, value in device.items()
        if not (mode == 'probe' and key == 'luid' and value == 0)):
        raise ValueError('Device identity mismatch.')
    if mode not in ('probe', 'export-input'):
        if not any(e['kind'] == 'fullSizeValidation' and e['count'] == COUNT and e['passed'] for e in status['events']):
            raise ValueError('Original full-size validator did not pass.')
    if mode == 'test-all':
        if '6160/6160 ALL TESTS PASSED' not in text: raise ValueError('Pinned TestAll count did not pass.')
        checks = [e for e in status['events'] if e['kind'] == 'correctness']
        if len(checks) != 27 or any(not e['passed'] or e['repeats'] != 4 for e in checks):
            raise ValueError('Strict native validation incomplete.')
    if mode == 'batch-only':
        if len(status['totalsSeconds']) != 1 or status['totalsSeconds'][0] <= 0:
            raise ValueError('Expected one complete timing batch.')
        if f'Size: {COUNT}' not in text or 'Test size: 100' not in text: raise ValueError('Native batch changed.')
        status['meanMs'] = status['totalsSeconds'][0] * 10  # seconds * 1000 / 100 measured iterations
        save(output / (tag + '.status.json'), status)
    return status


def analyze(folder, output):
    plan = read(folder / 'preregistered-plan.json')
    confirmation = read(folder / 'confirmation.json')
    rows = confirmation['results']
    if not confirmation['complete'] or len(rows) != 18 or confirmation['planSha256'] != sha(folder / 'preregistered-plan.json'):
        raise ValueError('Incomplete or changed confirmation.')
    previous = None
    for row, job in zip(rows, plan['schedule'], strict=True):
        if any(row[key] != value for key, value in job.items()): raise ValueError('Schedule differs.')
        if sha(folder / (row['tag'] + '.log')) != row['logSha256']: raise ValueError('Raw log changed.')
        raw = read(folder / (row['tag'] + '.status.json'))
        if any(raw[key] != value for key, value in row.items() if key not in job): raise ValueError('Process receipt differs.')
        if row['exitCode'] or row['executableSha256'] != plan['identity']['runtimeFiles']['scan/scan.exe']:
            raise ValueError('Process or executable failure.')
        text = (folder / (row['tag'] + '.log')).read_text(encoding='utf-8', errors='replace')
        totals = [float(s) for s in re.findall(r'Total time elapsed: ([0-9.eE+-]+)', text)]
        events = [json.loads(line[7:]) for line in text.splitlines() if line.startswith('HPJSON ')]
        if totals != row['totalsSeconds'] or events != row['events'] or len(totals) != 1 or row['meanMs'] != totals[0] * 10:
            raise ValueError('Raw timing/event arithmetic differs.')
        sizes = re.findall(r'^Size: (\d+)', text, re.MULTILINE)
        iterations = re.findall(r'^Test size: (\d+)', text, re.MULTILINE)
        throughput = re.findall(r'32-bit elements: ([0-9.eE+-]+) keys/sec', text)
        if sizes != [str(COUNT)] or iterations != ['100'] or len(throughput) != 1 or not math.isclose(float(throughput[0]), COUNT * 100 / totals[0], rel_tol=1e-5):
            raise ValueError('Raw native size/iteration/throughput mismatch.')
        if [e for e in events if e['kind'] == 'device'] != [plan['device']]: raise ValueError('Wrong adapter/driver.')
        if not any(e['kind'] == 'fullSizeValidation' and e['count'] == COUNT and e['passed'] for e in events):
            raise ValueError('Full-size correctness missing.')
        if not any(e['kind'] == 'arm' and e['count'] == COUNT and e['backend'] == row['arm'] and e['batchSize'] == 100 and e['warmupIterations'] == 1 for e in events):
            raise ValueError('Batch contract changed.')
        start, finish = datetime.fromisoformat(row['startedUtc']), datetime.fromisoformat(row['finishedUtc'])
        if finish <= start or previous is not None and start < previous: raise ValueError('Overlapping processes.')
        previous = finish
    means = {}
    for arm in ARMS:
        values = [row['meanMs'] for row in rows if row['arm'] == arm]
        mean = statistics.mean(values); half = T5 * statistics.stdev(values) / math.sqrt(6)
        means[arm] = {'processMeanMs': values, 'arithmeticMeanMs': mean, 'mean95CiMs': [mean-half, mean+half],
            'gigaElementsPerSecond': COUNT / mean / 1e6}
    comparisons = []
    for baseline, candidate in [('tile', 'tile-fused'), ('rts', 'tile-fused'), ('rts', 'tile')]:
        pairs = []
        for repeat in range(1, 7):
            arm_rows = {r['arm']: r for r in rows if r['round'] == repeat}
            a, b = arm_rows[baseline]['meanMs'], arm_rows[candidate]['meanMs']
            pairs.append({'round': repeat, 'baselineMs': a, 'candidateMs': b, 'ratio': a/b, 'differenceMs': a-b})
        logs = [math.log(p['ratio']) for p in pairs]
        mean = statistics.mean(logs); half = T5 * statistics.stdev(logs) / math.sqrt(6)
        ratio, lo, hi = math.exp(mean), math.exp(mean-half), math.exp(mean+half)
        diff = [p['differenceMs'] for p in pairs]; dm = statistics.mean(diff); dh = T5 * statistics.stdev(diff)/math.sqrt(6)
        comparisons.append({'baseline': baseline, 'candidate': candidate, 'pairs': pairs,
            'geometricMeanRatio': ratio, 'ratio95Ci': [lo, hi], 'pairedDifferenceMs': dm, 'difference95CiMs': [dm-dh, dm+dh],
            'timeReductionPercent': 100*(1-1/ratio), 'timeReduction95CiPercent': [100*(1-1/lo), 100*(1-1/hi)]})
    save(output, {'schema': 'hlslperf.inclusive-scan-analysis.v1', 'complete': True,
        'count': COUNT, 'processes': len(rows), 'independentRounds': 6, 'iterationsPerProcess': 100,
        'device': plan['device'], 'means': means, 'comparisons': comparisons,
        'planSha256': sha(folder / 'preregistered-plan.json'), 'rows': rows})
    with output.with_suffix('.csv').open('w', newline='', encoding='utf-8') as stream:
        fields = ['round', 'position', 'arm', 'meanMs', 'pid', 'startedUtc', 'finishedUtc', 'logSha256']
        writer = csv.DictWriter(stream, fieldnames=fields); writer.writeheader()
        writer.writerows({field: row[field] for field in fields} for row in rows)
    print(json.dumps({'means': means, 'comparisons': comparisons}, indent=2))


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('mode', choices=['probe', 'validate', 'discover', 'freeze', 'confirm', 'analyze'])
    p.add_argument('--native', type=Path)
    p.add_argument('--output', required=True, type=Path)
    p.add_argument('--adapter', default='NVIDIA GeForce RTX 4090')
    p.add_argument('--device', type=Path)
    p.add_argument('--validation', type=Path)
    p.add_argument('--managed', type=Path)
    p.add_argument('--discovery', type=Path)
    p.add_argument('--plan', type=Path)
    p.add_argument('--confirmation', type=Path)
    args = p.parse_args()
    output = args.output.resolve()
    if output.exists(): raise FileExistsError('Preserve existing evidence: ' + str(output))
    if args.mode == 'analyze': analyze(args.confirmation.resolve(), output); return
    native = args.native.resolve()
    output.mkdir(parents=True)
    current = identity(native)
    save(output / 'identity.json', current)
    if args.mode == 'probe':
        result = execute(native, output, 'probe', 'probe', 'rts', {'adapter': args.adapter, 'luid': 0})
        save(output / 'device.json', next(e for e in result['events'] if e['kind'] == 'device'))
        return
    device = read(args.device)
    if args.mode in ('validate', 'discover'):
        results = []
        for arm in ARMS:
            results.append(execute(native, output, arm, 'test-all' if args.mode == 'validate' else 'batch-only', arm, device))
            save(output / (args.mode + '.json'), {'complete': len(results) == 3, 'identity': current, 'results': results})
        if args.mode == 'validate':
            export = output / 'upstream-init-one-u32.bin'
            execute(native, output, 'input-export', 'export-input', 'rts', device, export)
            expected = hashlib.sha256(); ones = b'\x01\x00\x00\x00' * (1 << 20)
            for _ in range(COUNT // (1 << 20)): expected.update(ones)
            digest = sha(export)
            if digest != expected.hexdigest() or export.stat().st_size != COUNT * 4: raise ValueError('Actual GPU-generated input differs.')
            save(output / 'input.json', {'count': COUNT, 'bytes': COUNT*4, 'sha256': digest,
                'path': str(export), 'source': 'Full GPU readback of unchanged upstream InitOne; independently checked against all-one uint32 bytes.'})
    elif args.mode == 'freeze':
        validation, managed = read(args.validation / 'validate.json'), read(args.managed)
        if not validation['complete'] or validation['identity'] != current or not managed['allPassed']:
            raise ValueError('Correctness/source gate failed.')
        discovery = read(args.discovery / 'discover.json')
        if not discovery['complete'] or discovery['identity'] != current:
            raise ValueError('Separate exploratory processes are missing or differ.')
        for binary in managed['binaries']:
            if sha(binary['location']) != binary['sha256']: raise ValueError('Managed validation binary changed.')
        if not managed['results'] or any(not r['passed'] for r in managed['results']):
            raise ValueError('Incomplete managed correctness.')
        if managed['device']['adapterName'] != device['adapter'] or managed['device']['driverVersion'] != device['driver']:
            raise ValueError('Managed/native device differs.')
        schedule = [{'tag': f'round{r}-pos{pos}-{arm}', 'round': r, 'position': pos, 'arm': arm}
            for r, order in enumerate(itertools.permutations(ARMS), 1) for pos, arm in enumerate(order, 1)]
        plan = {'schema': 'hlslperf.inclusive-three-arm-plan.v1', 'registeredUtc': now(), 'identity': current, 'device': device,
            'native': str(native), 'managedCorrectnessSha256': sha(args.managed),
            'discoverySha256': sha(args.discovery / 'discover.json'),
            'nativeCorrectnessSha256': sha(args.validation / 'validate.json'), 'input': read(args.validation / 'input.json'),
            'count': COUNT, 'additionalPerformanceSizes': [], 'measuredIterationsPerProcess': 100, 'excludedWarmupIterations': 1,
            'schedule': schedule, 'candidate': 'One fixed candidate: wave32/group256/items16/partition4096/polls4/persistent256; native inclusive prefix only. No parameter search.',
            'preBatch': 'One unchanged upstream full-size inclusive validation, then BatchTimingInclusiveInitOne(count,100), whose first iteration is excluded.',
            'boundary': 'Unchanged upstream GPU TimeScan timestamps surround full operation hook: reset, barriers, scan, legacy AddInput when applicable. Excludes generation/allocation/compile/readback/CPU recording/submission/fence wait; not application end-to-end latency.',
            'analysis': 'Six independent rounds, all six arm permutations. Every process mean = upstream totalSeconds/100. Two-sided nominal 95% Student-t intervals with df5 on within-round log ratios and paired differences. No inner-iteration pseudoreplication, no exclusions or best-run selection.',
            'interference': 'Shared Local\\CodexR9700VNextUnityGpu lock, BabelStream priority; one native child at a time; no concurrent managed builds or benchmarks. User apps/clocks uncontrolled.'}
        save(output / 'plan.json', plan)
    elif args.mode == 'confirm':
        plan = read(args.plan)
        if plan['identity'] != current or plan['device'] != device: raise ValueError('Frozen identity changed.')
        save(output / 'preregistered-plan.json', plan)
        results = []
        for job in plan['schedule']:
            if identity(native) != current: raise ValueError('Sources/runtime changed during confirmation.')
            result = execute(native, output, job['tag'], 'batch-only', job['arm'], device)
            results.append({**result, **job})
            save(output / 'confirmation.json', {'complete': len(results) == 18, 'planSha256': sha(output / 'preregistered-plan.json'), 'results': results})


if __name__ == '__main__': main()
