"""Bounded, recording-free fluid frame confirmation using the frozen Unity Player."""
import argparse
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import subprocess
import time

from analyze_fluid_application_scaling import associated_samples
from analyze_fluid_benchmark import summarize
from run_ultra_scan import GIB, check_resources, gpu_snapshot, host_snapshot, read, save, sha, shared_lock

ROOT = Path(__file__).resolve().parents[1]
ARMS = ('original', 'hlsl-wave-tiled-direct')
SIZES = {1500: 1024000, 23000: 15761198}
POLICY = dict(warmupFrames=120, measuredFrames=120, pairedRounds=6, interProcessRestSeconds=10,
              maximumParticles=16000000, forecastBytesPerParticle=256, forecastFixedBytes=256<<20,
              minimumHostPhysicalBytes=4*GIB, minimumHostCommitBytes=4*GIB,
              minimumGpuFreeBytes=8*GIB, maximumGpuUsedBytes=12*GIB, maximumGpuTemperatureC=80,
              processTimeoutSeconds=180, maximumSimulationGpuMs=1000, maximumWallFrameMs=2000,
              practicalWallReductionPercent=1.0, recording=False, changeSystemSettings=False)


def now(): return datetime.now(timezone.utc).isoformat()


def schedule(stage):
    cases = []
    for density, count in SIZES.items():
        for repeat in range(1, 7 if stage == 'confirm' else 2):
            order = ARMS if repeat % 2 else ARMS[::-1]
            for position, arm in enumerate(order, 1):
                cases.append(dict(name=f'd{density}-r{repeat:02}-{arm}', density=density, particles=count,
                                  arm=arm, repeat=repeat, position=position))
    return cases


def resources(host, gpu, particles=0):
    check_resources(host, gpu)
    if particles:
        if particles not in SIZES.values(): raise ValueError('Only the two previously validated fluid loads are allowed.')
        extra = particles*POLICY['forecastBytesPerParticle']+POLICY['forecastFixedBytes']
        if host['availablePhysical'] < 4*GIB+extra: raise RuntimeError('Fluid physical-memory allocation forecast rejected.')
        if host['availableCommit'] < 4*GIB+extra: raise RuntimeError('Fluid commit-memory allocation forecast rejected.')
        if gpu['freeBytes'] < 8*GIB+extra or gpu['usedBytes']+extra > 12*GIB:
            raise RuntimeError('Fluid GPU allocation forecast rejected.')


def identity(player):
    sources = ('run_fluid_frame_confirmation.py', 'run_ultra_scan.py', 'run_inclusive_scan.py',
               'analyze_fluid_application_scaling.py', 'analyze_fluid_benchmark.py',
               'analyze_fluid_sweep.py', 'compare_fluid_benchmark.py', 'run_fluid_scaling.py')
    return dict(playerFilesSha256={p.relative_to(player.parent).as_posix():sha(p) for p in sorted(player.parent.rglob('*')) if p.is_file()},
                runnerFilesSha256={name:sha(ROOT/'tools'/name) for name in sources},
                sourceBase=subprocess.check_output(['git','rev-parse','HEAD'],cwd=ROOT,text=True).strip())


def validate_run(folder, case):
    run = read(folder/'run.json'); summary = summarize(folder); frames = associated_samples(folder)
    settings = run['settings']
    if (run['particles'] != case['particles'] or settings['spawnDensity'] != case['density']
            or settings['arm'] != (0 if case['arm'] == 'original' else 3)
            or settings['warmupFrames'] != 120 or settings['measureFrames'] != 120
            or settings['seed'] != 42 or not settings['nativeGpuTiming']
            or run['width'] != 1920 or run['height'] != 1080 or run['api'] != 'Direct3D12'
            or run['device'] != 'NVIDIA GeForce RTX 4090'
            or not summary['gpuDelayValidated'] or len(frames) != 120
            or any(m['status'] != 'observed' for m in summary['metrics'].values())):
        raise ValueError('Run differs from the declared application contract.')
    if any(f['simulation_complete'] > POLICY['maximumSimulationGpuMs'] or f['wall_frame'] > POLICY['maximumWallFrameMs'] for f in frames.values()):
        raise ValueError('Application timing exceeded the declared stop threshold.')
    return run, summary


def execute(player, output, case, fixed):
    folder = output/case['name']; log = output/(case['name']+'.log')
    args = [str(player), '-force-d3d12', '-screen-fullscreen','0','-screen-width','1920','-screen-height','1080',
            '-popupwindow','-logFile',str(log),'--fluid-benchmark','--fluid-arm',case['arm'],
            '--fluid-output',str(folder),'--fluid-seed','42','--fluid-dt','0.016666667',
            '--fluid-gpu-timing','d3d12-query','--fluid-warmup','120','--fluid-frames','120',
            '--fluid-spawn-density',str(case['density'])]
    host, gpu = host_snapshot(), gpu_snapshot('NVIDIA GeForce RTX 4090')
    row = dict(**case, command=args, startedUtc=now(), telemetry=[dict(utc=now(),phase='before',host=host,gpu=gpu)],
               playerSha256=fixed['playerFilesSha256'][player.name])
    receipt = output/(case['name']+'.process.json'); save(receipt,row)
    try: resources(host,gpu,case['particles'])
    except BaseException as error:
        row.update(preflightRejected=True,stopReason=repr(error),endedUtc=now()); save(receipt,row); raise
    print('START',case['name'],flush=True)
    startup=subprocess.STARTUPINFO();startup.dwFlags|=subprocess.STARTF_USESHOWWINDOW;startup.wShowWindow=1
    child=subprocess.Popen(args,startupinfo=startup,creationflags=subprocess.CREATE_NO_WINDOW)
    row['pid']=child.pid;save(receipt,row);tick=time.monotonic();last_gpu=tick
    try:
        while child.poll() is None:
            if time.monotonic()-tick > POLICY['processTimeoutSeconds']:raise RuntimeError('Own fluid child exceeded its 180-second deadline.')
            host=host_snapshot()
            if time.monotonic()-last_gpu >= 2:
                gpu=gpu_snapshot('NVIDIA GeForce RTX 4090');last_gpu=time.monotonic()
            # Retain the rejecting point as well as accepted samples on future runs.
            row['telemetry'].append(dict(utc=now(),phase='running',host=host,gpu=gpu))
            resources(host,gpu);time.sleep(.5)
    except BaseException as error:
        row['stopReason']=repr(error)
        if child.poll() is None:child.kill()
        raise
    finally:
        row.update(exitCode=child.wait(timeout=10),endedUtc=now(),processSeconds=time.monotonic()-tick)
        if log.exists():row['logSha256']=sha(log)
        save(receipt,row)
    if row['exitCode']:raise RuntimeError('Retained failed fluid process: '+case['name'])
    host,gpu=host_snapshot(),gpu_snapshot('NVIDIA GeForce RTX 4090');resources(host,gpu)
    row['telemetry'].append(dict(utc=now(),phase='after',host=host,gpu=gpu))
    run,summary=validate_run(folder,case)
    row['evidenceSha256']={name:sha(folder/name) for name in ('run.json','correctness.json','observations.csv')}
    save(folder/'summary.json',summary);save(receipt,row);save(folder/'process.json',row)
    print('DONE',case['name'],'wallMeanMs',summary['metrics']['wall_frame']['meanMs'],flush=True)


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('stage',choices=('pilot','confirm'));parser.add_argument('--player',type=Path,required=True)
    parser.add_argument('--output',type=Path,required=True);parser.add_argument('--reference-plan',type=Path,required=True)
    parser.add_argument('--pilot',type=Path)
    args=parser.parse_args();player=args.player.resolve();output=args.output.resolve()
    if output.exists():raise FileExistsError('Preserve earlier attempts: '+str(output))
    with shared_lock():
        resources(host_snapshot(),gpu_snapshot('NVIDIA GeForce RTX 4090'))
        fixed=identity(player);reference=read(args.reference_plan)
        if fixed['playerFilesSha256'] != reference['playerFilesSha256']:raise ValueError('Player differs from the frozen successful application cohort.')
        prerequisite=None
        if args.stage=='confirm':
            if args.pilot is None:raise ValueError('A fresh pilot is required.')
            prior=read(args.pilot/'plan.json');done=read(args.pilot/'completion.json')
            if prior['identity']!=fixed or prior['stage']!='pilot' or prior['policy']!=POLICY or not done['complete'] or done['planSha256']!=sha(args.pilot/'plan.json'):
                raise ValueError('Pilot source/policy/completion differs.')
            prerequisite=dict(planSha256=sha(args.pilot/'plan.json'),completionSha256=sha(args.pilot/'completion.json'))
        output.mkdir(parents=True)
        plan=dict(schema='hlslperf.fluid-frame.plan.v1',registeredUtc=now(),stage=args.stage,identity=fixed,
                  cases=schedule(args.stage),policy=POLICY,referencePlanSha256=sha(args.reference_plan),pilot=prerequisite,
                  primaryEndpoint='wall_frame equal-weight process mean; six paired rounds per size; pointwise nominal 95% log-ratio Student-t df5; practical threshold 1% reduction',
                  boundary='exclusive scans (three/frame), full simulation GPU time and rendered/presented wall frame; no recording; fixed simulated 2..4-second window')
        save(output/'plan.json',plan)
        try:
            for index,case in enumerate(plan['cases']):
                if identity(player)!=fixed:raise ValueError('Player or measured runner dependencies changed.')
                execute(player,output,case,fixed)
                save(output/'progress.json',dict(lastCompleted=case['name'],utc=now()))
                if index+1<len(plan['cases']):time.sleep(10)
            if identity(player)!=fixed:raise ValueError('Player/source changed at completion.')
            save(output/'completion.json',dict(complete=True,processes=len(plan['cases']),planSha256=sha(output/'plan.json'),finishedUtc=now()))
        except BaseException as error:
            save(output/'failure.json',dict(complete=False,error=repr(error),utc=now()));raise
    print('COMPLETE',args.stage,len(plan['cases']),flush=True)


if __name__=='__main__':main()
