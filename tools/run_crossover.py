"""Serial fresh-player runner. Validation and timing failures stop, never disappear."""
import argparse
import hashlib
import itertools
import json
import os
from pathlib import Path
import subprocess
import time
import uuid

from analyze_crossover import drift
from check_crossover_timing import diagnostic_status, validate_pilot_v2, quiet_status
from prepare_crossover_project import source_identity

COUNTS = [100000, 250000, 500000, 1000000, 2000000, 4000000]
DENSITIES = [.05, .25, .75]


def balanced_schedule(conditions, pairs=6):
    if pairs < 6 or pairs % 2: raise ValueError('use an even pair count >=6')
    for pair in range(pairs):
        order = conditions if pair % 2 == 0 else list(reversed(conditions))
        for n, density in order:
            for mode in (('cpu', 'gpu') if pair % 2 == 0 else ('gpu', 'cpu')):
                yield pair, n, density, mode


def snapshot():
    try:
        result = subprocess.run(['nvidia-smi', '--query-gpu=name,driver_version,temperature.gpu,utilization.gpu', '--format=csv,noheader,nounits'], capture_output=True, text=True, check=True)
        rows = [line.split(', ') for line in result.stdout.strip().splitlines()]
        return {'time': time.time(), 'adapters': [{'name': r[0], 'driver': r[1], 'temperature': float(r[2]), 'utilization': float(r[3])} for r in rows]}
    except (OSError, subprocess.SubprocessError, ValueError) as exc:
        return {'time': time.time(), 'error': str(exc)}


def launch(player, directory, stage, n, density, pair=0, frames=1000, warmup=300, mode=None, launch_mode="batchmode", timing_api="legacy", gpu_area="unchanged"):
    tag = f'{n}-{density:g}'; mode = mode or stage
    name = f'{stage}-{tag}' if stage in ('calibrate', 'validation') else f'{stage}-{tag}-pair{pair}-{mode}'
    output = directory / (name + '.json')
    receipt_path = directory / (name + '.launch.json')
    if output.exists() or receipt_path.exists(): raise ValueError('Refusing silent overwrite/retry: ' + name)
    command = [str(player), '-force-d3d12', '-screen-fullscreen', '0', '-screen-width', '1280', '-screen-height', '720',
               '-logFile', str(directory / (name + '.log')), '--mode', mode, '--agents', str(n), '--density', str(density),
               '--seed', '69501203', '--frames', str(frames), '--warmup-frames', str(warmup), '--output', str(output),
               '--run-id', str(uuid.uuid4()), '--pair-id', f'{tag}-pair{pair}']
    if launch_mode == 'batchmode': command.insert(1, '-batchmode')
    command += ['--timing-api',timing_api,'--gpu-profiler-area',gpu_area]
    if stage != 'calibrate': command += ['--calibration', str(directory / f'calibrate-{tag}.json')]
    assembly = player.parent / (player.stem + '_Data/Managed/Assembly-CSharp.dll')
    receipt = {'protocolVersion':2, 'command': command, 'stage': stage, 'playerSha256': hashlib.sha256(player.read_bytes()).hexdigest(),
               'assemblySha256': hashlib.sha256(assembly.read_bytes()).hexdigest(),
               'before': [], 'after': None, 'returnCode': None, 'status': 'started'}
    for _ in range(3 if stage in ('pilot','pilot-pairs') else 1):
        receipt['before'].append(snapshot())
        if stage in ('pilot','pilot-pairs'): time.sleep(1)
    receipt_path.write_text(json.dumps(receipt, indent=2))
    if stage in ('pilot','pilot-pairs') and not quiet_status(receipt['before']):
        receipt['status'] = 'blocked: background GPU activity'; receipt_path.write_text(json.dumps(receipt, indent=2))
        raise RuntimeError(receipt['status'])
    startup = None
    if os.name == 'nt':
        startup = subprocess.STARTUPINFO(); startup.dwFlags |= subprocess.STARTF_USESHOWWINDOW; startup.wShowWindow = 0
    try:
        run = subprocess.run(command, startupinfo=startup, timeout=900, capture_output=True, text=True)
        receipt['returnCode'] = run.returncode
        receipt['status'] = 'exited'
    except subprocess.TimeoutExpired:
        receipt['status'] = 'timeout: process terminated by runner'
    receipt['after'] = snapshot(); receipt_path.write_text(json.dumps(receipt, indent=2))
    if receipt['returnCode'] != 0 or not output.exists(): raise RuntimeError(f'{name} failed; see raw output/launch/log')
    result = json.loads(output.read_text(encoding='utf-8-sig'))
    if result.get('sourceIdentity') != source_identity(Path(__file__).resolve().parents[1]):
        raise RuntimeError('Player/source identity mismatch; rebuild and use a new evidence directory')
    if stage in ('pilot', 'pilot-pairs'):
        correctness = json.loads((directory / f'validation-{tag}.json').read_text())
        pacing = validate_pilot_v2(result, correctness)
        receipt['pacing'] = pacing
        receipt_path.write_text(json.dumps(receipt,indent=2))
        if stage in ('pilot','pilot-pairs'):
            before = receipt['before'][-1]; after = receipt['after']
            if 'error' in after or any(abs(a['temperature'] - b['temperature']) > 10 for a,b in zip(after['adapters'], before['adapters'])):
                raise RuntimeError('temperature/identity gate failed; retain run, investigate')
            for metric in ('cpuTotalMs', 'gpuRangeMs'):
                if drift([s[metric] for s in result['samples']]) > .15: raise RuntimeError('drift gate failed: ' + metric)
    print(name, 'complete', flush=True)
    return result


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--player', type=Path, required=True); p.add_argument('--output', type=Path, required=True)
    p.add_argument('--stage', choices=['calibrate', 'validation', 'pilot', 'pilot-pairs', 'measure'], required=True)
    p.add_argument('--agents', type=int, nargs='+', default=COUNTS); p.add_argument('--densities', type=float, nargs='+', default=DENSITIES)
    p.add_argument('--frames', type=int, default=1000); p.add_argument('--warmup', type=int, default=300); p.add_argument('--pairs', type=int, default=6)
    p.add_argument('--timing-diagnostic',type=Path)
    p.add_argument('--launch-mode',choices=['normal','batchmode'],default='batchmode')
    a = p.parse_args(); a.output = a.output.resolve(); a.player = a.player.resolve(); a.output.mkdir(parents=True, exist_ok=True)
    conditions = list(itertools.product(a.agents, a.densities))
    if a.stage in ('pilot','pilot-pairs'): raise ValueError('Use run_integrated_crossover.py for the selected native v4 benchmark; legacy pilot gates are archived')
    if a.stage == 'measure': raise ValueError('Formal matrix disabled at protocol-v2 checkpoint; separate explicit authorization required')
    timing_api='legacy'; gpu_area='unchanged'
    if a.stage in ('pilot','pilot-pairs'):
        if conditions!=[(100000,.25)] or a.frames!=1000 or a.warmup!=300: raise ValueError('Only the authorized 100k/25%, 300+1000 checkpoint pilot is allowed')
        if a.timing_diagnostic is None: raise ValueError('Require a passing raw --timing-diagnostic result')
        diagnostic=json.loads(a.timing_diagnostic.read_text())
        if not diagnostic_status(diagnostic)['passed']: raise ValueError('Diagnostic timing/attribution gate failed')
        if diagnostic['sourceIdentity']!=source_identity(Path(__file__).resolve().parents[1]): raise ValueError('Diagnostic source identity differs from current binary sources')
        if diagnostic['batchmode']!=(a.launch_mode=='batchmode'): raise ValueError('Unvalidated launch mode')
        timing_api=diagnostic['timingApi']; gpu_area=diagnostic.get('gpuProfilerAreaRequest','unchanged')
        prerequisite=json.loads((a.output/'validation-100000-0.25.json').read_text())
        if prerequisite.get('sourceIdentity')!=diagnostic['sourceIdentity'] or not prerequisite.get('completed') or not prerequisite.get('correctnessPassed'):
            raise ValueError('Current-binary correctness prerequisite missing')
        if len(prerequisite.get('checks',[]))!=10 or any(not all(c.get(k) for k in ('setEqual','imageEqual','nonEmptyImage')) for c in prerequisite['checks']):
            raise ValueError('Correctness prerequisite incomplete')
    if a.stage=='pilot-pairs':
        for mode in ('cpu','gpu'):
            pilot=json.loads((a.output/f'pilot-100000-0.25-pair0-{mode}.json').read_text())
            correctness=json.loads((a.output/'validation-100000-0.25.json').read_text())
            validate_pilot_v2(pilot,correctness)
            for metric in ('cpuTotalMs','gpuRangeMs'):
                if drift([s[metric] for s in pilot['samples']])>.15: raise ValueError('Pilot drift unresolved')
        schedule=[(pair,100000,.25,mode) for pair in range(3) for mode in (('cpu','gpu') if pair%2==0 else ('gpu','cpu'))]
    elif a.stage=='pilot': schedule=[(0,100000,.25,mode) for mode in ('cpu','gpu')]
    else: schedule=[(0,n,d,None) for n,d in conditions]
    for pair,n,d,mode in schedule: launch(a.player,a.output,a.stage,n,d,pair,a.frames,a.warmup,mode,a.launch_mode,timing_api,gpu_area)



if __name__ == '__main__': main()
