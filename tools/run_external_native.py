"""Serial native validation/confirmation. Invoke under Invoke-UnifiedValidationLock.ps1."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import sys
from datetime import datetime, timezone

ROOT = Path(__file__).resolve().parents[1]
def now(): return datetime.now(timezone.utc).isoformat()
def sha(path): return hashlib.sha256(Path(path).read_bytes()).hexdigest()
def save(path, data): Path(path).write_text(json.dumps(data, indent=2) + '\n', encoding='utf-8')
def read(path): return json.loads(Path(path).read_text(encoding='utf-8-sig'))

def identity(native):
    prep = read(native / 'preparation.json')
    for name, digest in prep['localAdapterFiles'].items():
        if sha(ROOT / 'benchmarks/external/native' / name) != digest:
            raise ValueError('Native adapter changed after preparation: ' + name)
    binaries = {f'{p}/{p}.exe': sha(native / p / (p + '.exe')) for p in ['scan', 'sort']}
    for name, digest in binaries.items():
        if prep['binaries'][name] != digest: raise ValueError('Prepared executable changed.')
    return {'sourceCommit': subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=ROOT, text=True).strip(),
        'preparationSha256': sha(native / 'preparation.json'), 'binaries': binaries,
        'sourceFiles': {str(p.relative_to(ROOT)).replace('\\', '/'): sha(p)
            for base in ['kernels', 'benchmarks/external/native'] for p in sorted((ROOT / base).rglob('*')) if p.is_file()},
        'upstreamLockSha256': sha(ROOT / 'third_party/upstream-lock.json'),
        'runtimeFiles': {str(p.relative_to(native)).replace('\\', '/'): sha(p)
            for arm in ['scan', 'sort'] for p in sorted((native / arm).rglob('*')) if p.is_file() and p.suffix.lower() not in ['.pdb', '.ilk', '.exp', '.lib']}}

def run(native, output, tag, program, arm, count, mode, luid):
    executable = native / program / (program + '.exe')
    command = [str(executable), mode, arm, str(count), str(luid)]
    status = {'tag': tag, 'program': program, 'arm': arm, 'count': count, 'command': command,
        'cwd': str(executable.parent), 'startedUtc': now(), 'executableSha256': sha(executable)}
    log = output / (tag + '.log')
    save(output / (tag + '.status.json'), status)
    try:
        with log.open('wb') as stream:
            child = subprocess.Popen(command, cwd=executable.parent, stdout=stream, stderr=subprocess.STDOUT,
                creationflags=subprocess.CREATE_NO_WINDOW)
            status['pid'] = child.pid
            save(output / (tag + '.status.json'), status)
            try: status['exitCode'] = child.wait(timeout=900)
            except subprocess.TimeoutExpired:
                child.kill(); child.wait()
                status['exitCode'] = child.returncode
                status['timeout'] = True
    finally:
        status['finishedUtc'] = now()
        status['processExited'] = True
        status['logSha256'] = sha(log)
        text = log.read_text(encoding='utf-8', errors='replace')
        status['events'] = [json.loads(line[7:]) for line in text.splitlines() if line.startswith('HPJSON ')]
        status['totalsSeconds'] = [float(v) for v in re.findall(r'Total time elapsed: ([0-9.eE+-]+)', text)]
        save(output / (tag + '.status.json'), status)
    print(tag, 'exit', status['exitCode'], 'totals', status['totalsSeconds'], flush=True)
    if status['exitCode'] or 'RUNTIME_FAILED' in text: raise RuntimeError('Native failure; preserve ' + str(log))
    devices = [e for e in status['events'] if e['kind'] == 'device']
    if len(devices) != 1 or devices[0]['luid'] != luid or devices[0]['driver'] != '32.0.31041.1004':
        raise RuntimeError('Device/driver identity changed.')
    if not any(e['kind'] == 'fullSizeValidation' and e['passed'] for e in status['events']):
        raise RuntimeError('Full-size GPU correctness did not pass.')
    if mode == 'batch-only' and len(status['totalsSeconds']) != 1: raise RuntimeError('Expected exactly one native batch.')
    return status

def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('mode', choices=['validate', 'register', 'confirm'])
    p.add_argument('--native', required=True, type=Path)
    p.add_argument('--output', required=True, type=Path)
    p.add_argument('--managed-validation', type=Path)
    p.add_argument('--native-validation', type=Path)
    p.add_argument('--additional-validation', type=Path)
    p.add_argument('--tile8-only', action='store_true', help='Separate second phase: extra 2^25 correctness or three tile8 sort comparisons.')
    p.add_argument('--plan', type=Path)
    p.add_argument('--luid', type=int, default=76566)
    args = p.parse_args()
    native, output = args.native.resolve(), args.output.resolve()
    if output.exists(): raise FileExistsError('Preserve previous attempts: ' + str(output))
    output.mkdir(parents=True)
    frozen = identity(native)
    save(output / 'identity.json', frozen)
    if args.mode == 'validate':
        managed = read(args.managed_validation / 'correctness.json')
        if not managed['allPassed']: raise ValueError('Managed GPU correctness failed.')
        declaration = [('scan', 'rts', 1 << 28), ('scan', 'tile', 1 << 28),
            ('sort', 'device', 1 << 28), ('sort', 'onesweep', 1 << 28), ('sort', 'ffx', 1 << 25),
            ('sort', 'tile4', 1 << 28), ('sort', 'tile8', 1 << 28), ('sort', 'tile4', 1 << 25)]
        if args.tile8_only: declaration = [('sort', 'tile8', 1 << 25)]
        save(output / 'declaration.json', {'startedUtc': now(), 'managedCorrectnessSha256': sha(args.managed_validation / 'correctness.json'),
            'declaration': declaration, 'native': str(native), 'identity': frozen})
        results = []
        for program, arm, count in declaration:
            results.append(run(native, output, f'{program}-{arm}-{count}', program, arm, count, 'validate-only', args.luid))
            save(output / 'validation.json', {'passed': len(results) == len(declaration), 'identity': frozen, 'results': results})
    elif args.mode == 'register':
        validation = read(args.native_validation / 'validation.json')
        if not validation['passed'] or validation['identity']['binaries'] != frozen['binaries']:
            raise ValueError('Native correctness or binary identity mismatch.')
        groups = [('scan-rts', 'scan', 'rts', 'tile', 1 << 28), ('sort-device', 'sort', 'device', 'tile4', 1 << 28),
            ('sort-onesweep', 'sort', 'onesweep', 'tile4', 1 << 28), ('sort-ffx', 'sort', 'ffx', 'tile4', 1 << 25)]
        if args.tile8_only:
            extra = read(args.additional_validation / 'validation.json')
            if not extra['passed'] or extra['identity']['binaries'] != frozen['binaries'] or not any(
                r['arm'] == 'tile8' and r['count'] == 1 << 25 and r['exitCode'] == 0 for r in extra['results']):
                raise ValueError('Additional tile8 2^25 GPU correctness did not pass.')
            groups = [(group + '-tile8', program, baseline, 'tile8', count) for group, program, baseline, candidate, count in groups if program == 'sort']
        schedule = []
        for group, program, baseline, candidate, count in groups:
            for pair in range(5):
                for arm in ([baseline, candidate] if pair % 2 == 0 else [candidate, baseline]):
                    schedule.append({'tag': f'{group}-pair{pair + 1}-{arm}', 'group': group, 'pair': pair + 1,
                        'program': program, 'arm': arm, 'baseline': baseline, 'candidate': candidate, 'count': count})
        save(output / 'plan.json', {'schema': 'hlslperf.native-confirmation-plan.v1', 'registeredUtc': now(), 'identity': frozen,
            'validationSha256': sha(args.native_validation / 'validation.json'), 'native': str(native), 'luid': args.luid,
            'phase': 2 if args.tile8_only else 1,
            'additionalValidationSha256': sha(args.additional_validation / 'validation.json') if args.tile8_only else None,
            'pairsPerGroup': 5, 'processes': len(schedule), 'batchSize': 100, 'nativeWarmupIterations': 1,
            'preBatch': 'One original full-size validation operation per process, followed by unchanged upstream batch with one excluded iteration.',
            'primaryCandidate': ('tile8; separately requested second phase covering every original sort workload. Phase one is retained unchanged; no workload selection.' if args.tile8_only else
                'tile4; frozen before any formal timing. tile8 receives correctness coverage only; no performance-based selection.'),
            'timingBoundary': 'Upstream GPU timestamp TimeScan/TimeSort: complete recording hook including conversion/reset/barriers. Original input generation, compilation, allocation, upload, readback and CPU submission wait excluded.',
            'input': 'Scan original inclusive InitOne at 2^28. Sort original ENTROPY_PRESET_1, uint32 ascending pairs, batch seed10 plus iteration index; 2^28 Device/OneSweep, 2^25 FFX.',
            'sourceLabel': 'Adapted upstream host. Original pinned generator/validator/batch. FFX host has separately recorded byte-allocation repair.',
            'analysis': 'Each process mean is totalSeconds/100. Five within-pair baseline/candidate ratios, geometric mean, two-sided 95% Student-t interval on log ratios (df=4). Report all raw values and failures, no best-run selection.',
            'interference': 'Shared mutex; one child at a time; no concurrent builds/downloads/benchmarks. User apps and clocks remain uncontrolled.',
            'schedule': schedule})
    else:
        plan = read(args.plan)
        if frozen != plan['identity']: raise ValueError('Source/runtime identity changed after preregistration.')
        save(output / 'preregistered-plan.json', plan)
        results = []
        for job in plan['schedule']:
            result = run(native, output, job['tag'], job['program'], job['arm'], job['count'], 'batch-only', plan['luid'])
            results.append({**job, **result})
            save(output / 'confirmation.json', {'complete': len(results) == len(plan['schedule']), 'planSha256': sha(args.plan), 'results': results})
    return 0

if __name__ == '__main__':
    sys.exit(main())
