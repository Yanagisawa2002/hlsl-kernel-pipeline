"""Bounded, sequential ultra-scale native scans; preserve every attempt and fail closed."""
import argparse
from contextlib import contextmanager
import ctypes
from ctypes import wintypes
from datetime import datetime, timezone
import json
import math
from pathlib import Path
import shutil
import statistics
import subprocess
import time

from run_inclusive_scan import identity, save, sha

ROOT = Path(__file__).resolve().parents[1]
COUNTS = (1 << 26, 1 << 27, 3 << 26, 1 << 28)
ARMS = ('rts', 'tile-fused')
OPERATIONS = ('inclusive', 'exclusive')
GIB = 1 << 30
T5 = 2.570581835636314
BASE_POLICY = dict(minimumHostPhysicalBytes=4*GIB, minimumHostCommitBytes=4*GIB,
              minimumGpuFreeBytes=8*GIB, maximumGpuUsedBytes=12*GIB,
              maximumGpuTemperatureC=80, maximumCount=1 << 28,
              processTimeoutSeconds=60, maximumSingleGpuMs=100,
              measuredIterations=100, warmupIterations=8,
              changeTdr=False, changePowerOrClocks=False, recording=False)
POLICY = dict(**BASE_POLICY, interProcessRestSeconds=5)


def now(): return datetime.now(timezone.utc).isoformat()
def read(path): return json.loads(Path(path).read_text(encoding='utf-8-sig'))


def counts(text):
    result = tuple(int(x) for x in text.split(','))
    if not result or tuple(sorted(set(result))) != result or any(n < 1 << 26 or n > 1 << 28 or n % 4 for n in result):
        raise ValueError('Counts must ascend, be unique/4-aligned, and remain in 2^26..2^28.')
    return result


def extra_bytes(n): return n * 12 + (64 << 20) if n else 0


def check_resources(host, gpu, n=0):
    extra = extra_bytes(n)
    if host['availablePhysical'] < POLICY['minimumHostPhysicalBytes'] + extra:
        raise RuntimeError('Stop: host physical reserve would fall below 4 GiB.')
    if host['availableCommit'] < POLICY['minimumHostCommitBytes'] + extra:
        raise RuntimeError('Stop: host commit reserve would fall below 4 GiB.')
    if gpu['freeBytes'] < POLICY['minimumGpuFreeBytes'] + extra:
        raise RuntimeError('Stop: GPU free reserve would fall below 8 GiB.')
    if gpu['usedBytes'] + extra > POLICY['maximumGpuUsedBytes']:
        raise RuntimeError('Stop: projected total GPU use exceeds 12 GiB.')
    if gpu['temperatureC'] >= POLICY['maximumGpuTemperatureC']:
        raise RuntimeError('Stop: GPU temperature reached 80 C.')


class MemoryStatus(ctypes.Structure):
    _fields_ = [('length', wintypes.DWORD), ('load', wintypes.DWORD)] + [
        (name, ctypes.c_uint64) for name in ('totalPhysical', 'availablePhysical', 'totalCommit', 'availableCommit',
                                           'totalVirtual', 'availableVirtual', 'availableExtendedVirtual')]


def host_snapshot():
    kernel = ctypes.WinDLL('kernel32', use_last_error=True)
    kernel.GlobalMemoryStatusEx.argtypes = [ctypes.POINTER(MemoryStatus)]
    kernel.GlobalMemoryStatusEx.restype = wintypes.BOOL
    status = MemoryStatus(); status.length = ctypes.sizeof(status)
    if not kernel.GlobalMemoryStatusEx(ctypes.byref(status)): raise ctypes.WinError(ctypes.get_last_error())
    return {name: int(getattr(status, name)) for name, _ in MemoryStatus._fields_ if name != 'length'}


def gpu_snapshot(adapter):
    command = ['nvidia-smi', '--query-gpu=name,memory.total,memory.used,memory.free,temperature.gpu,power.draw,utilization.gpu,driver_version',
               '--format=csv,noheader,nounits']
    output = subprocess.check_output(command, text=True, timeout=5, creationflags=subprocess.CREATE_NO_WINDOW)
    rows = [line.split(',') for line in output.strip().splitlines()]
    row = next((r for r in rows if r[0].strip() == adapter), None)
    if row is None or len(row) != 8: raise RuntimeError('Missing exact NVIDIA adapter telemetry.')
    return dict(adapter=row[0].strip(), totalBytes=int(row[1])*1048576, usedBytes=int(row[2])*1048576,
                freeBytes=int(row[3])*1048576, temperatureC=int(row[4]), powerW=float(row[5]),
                utilizationPercent=int(row[6]), driver=row[7].strip())


@contextmanager
def shared_lock():
    kernel = ctypes.WinDLL('kernel32', use_last_error=True)
    kernel.CreateMutexW.argtypes = [ctypes.c_void_p, wintypes.BOOL, wintypes.LPCWSTR]
    kernel.CreateMutexW.restype = wintypes.HANDLE
    kernel.WaitForSingleObject.argtypes = [wintypes.HANDLE, wintypes.DWORD]
    kernel.WaitForSingleObject.restype = wintypes.DWORD
    kernel.ReleaseMutex.argtypes = [wintypes.HANDLE]
    kernel.CloseHandle.argtypes = [wintypes.HANDLE]
    mutex = kernel.CreateMutexW(None, False, 'Local\\CodexR9700VNextUnityGpu')
    if not mutex: raise ctypes.WinError(ctypes.get_last_error())
    held = False
    try:
        result = kernel.WaitForSingleObject(mutex, 0)
        if result == 0x80: raise RuntimeError('GPU mutex was abandoned; stop for inspection.')
        if result != 0: raise RuntimeError('Another cooperating GPU experiment owns the lock.')
        held = True
        yield
    finally:
        if held: kernel.ReleaseMutex(mutex)
        kernel.CloseHandle(mutex)


def schedule(stage, sizes):
    cases = []
    if stage == 'gate':
        return [dict(tag=f'gate-{op}-{arm}', count=sizes[0], operation=op, arm=arm, repeat=0, position=pos,
                     mode='ultra-gate') for op in OPERATIONS for pos, arm in enumerate(ARMS, 1)]
    for n in sizes:
        for repeat in range(1, 7 if stage == 'confirm' else 2):
            ops = OPERATIONS if repeat % 2 else OPERATIONS[::-1]
            order = ARMS if repeat % 2 else ARMS[::-1]
            for op in ops:
                for pos, arm in enumerate(order, 1):
                    cases.append(dict(tag=f'n{n}-r{repeat:02}-{op}-{arm}', count=n, operation=op,
                                      arm=arm, repeat=repeat, position=pos, mode='ultra-batch'))
    return cases


def parse_log(text, case, device):
    if 'RUNTIME_FAILED' in text or 'TEST FAILED' in text: raise ValueError('Native process reported failure.')
    events = [json.loads(line[7:]) for line in text.splitlines() if line.startswith('HPJSON ')]
    observed = [e for e in events if e['kind'] == 'device']
    if observed != [device]: raise ValueError('Device/driver identity differs.')
    arms = [e for e in events if e['kind'] == 'ultraArm']
    expected_arm = dict(kind='ultraArm', backend=case['arm'], count=case['count'], operation=case['operation'])
    if arms != [expected_arm]: raise ValueError('Wrong backend, size or semantics.')
    checks = [e for e in events if e['kind'] == 'ultraValidation']
    phases = ['before'] if case['mode'] == 'ultra-gate' else ['before', 'after']
    if checks != [dict(kind='ultraValidation', phase=phase, count=case['count'], operation=case['operation'], passed=True) for phase in phases]:
        raise ValueError('Full-size correctness gate incomplete.')
    memory = [e for e in events if e['kind'] == 'memory']
    if not any(e['phase'] == 'allocated' and 0 < e['usage'] <= POLICY['maximumGpuUsedBytes'] for e in memory):
        raise ValueError('Missing bounded allocated DXGI usage.')
    if case['mode'] == 'ultra-gate':
        strict = [e for e in events if e['kind'] == 'correctness']
        if len(strict) != 27 or any(not e['passed'] or e['repeats'] != 4 for e in strict):
            raise ValueError('Small full32 correctness gate incomplete.')
        return dict(events=events, samplesMs=[])
    batches = [e for e in events if e['kind'] == 'ultraBatch']
    if batches != [dict(kind='ultraBatch', count=case['count'], operation=case['operation'],
                        warmup=8, measured=100, input='upstream-init-one')]:
        raise ValueError('Changed batch/input boundary.')
    samples = [e for e in events if e['kind'] == 'ultraSample']
    if [s['index'] for s in samples] != list(range(100)): raise ValueError('Missing/reordered timing samples.')
    values = [s['milliseconds'] for s in samples]
    if any(not math.isfinite(v) or v <= 0 or v > 100 for v in values): raise ValueError('Invalid timing sample.')
    totals = [e for e in events if e['kind'] == 'ultraTotal']
    if len(totals) != 1 or totals[0]['count'] != case['count'] or totals[0]['operation'] != case['operation'] or totals[0]['measured'] != 100:
        raise ValueError('Missing final total.')
    mean = statistics.mean(values)
    if not math.isclose(mean, totals[0]['meanMs'], rel_tol=1e-9, abs_tol=1e-9): raise ValueError('Sample/total arithmetic differs.')
    ordered = sorted(values)
    p95 = ordered[94] + .05 * (ordered[95] - ordered[94])
    return dict(events=events, samplesMs=values, meanMs=mean, p50Ms=statistics.median(values), p95Ms=p95,
                maxMs=max(values), withinProcessCv=statistics.stdev(values)/mean)


def execute(native, root, case, device, adapter):
    exe = native/'scan/scan.exe'; log = root/(case['tag']+'.log'); receipt = root/(case['tag']+'.json')
    host, gpu = host_snapshot(), gpu_snapshot(adapter)
    command = [str(exe), case['mode'], case['arm'], str(case['count']), str(device['luid']), adapter, case['operation']]
    row = dict(**case, command=command, cwd=str(exe.parent), executableSha256=sha(exe),
               startedUtc=now(), telemetry=[dict(utc=now(), phase='before', host=host, gpu=gpu)])
    save(receipt, row)
    try: check_resources(host, gpu, case['count'])
    except RuntimeError as error:
        row.update(preflightRejected=True,stopReason=str(error),finishedUtc=now())
        save(receipt,row)
        raise
    print('START', case['tag'], flush=True)
    tick = time.monotonic(); last_gpu = tick; stop_reason = None
    with log.open('wb') as stream:
        child = subprocess.Popen(command, cwd=exe.parent, stdout=stream, stderr=subprocess.STDOUT,
                                 creationflags=subprocess.CREATE_NO_WINDOW)
        row['pid'] = child.pid; save(receipt, row)
        try:
            while child.poll() is None:
                if time.monotonic()-tick > POLICY['processTimeoutSeconds']:
                    raise RuntimeError('Own child exceeded 60-second process deadline.')
                host = host_snapshot()
                if time.monotonic()-last_gpu >= 1:
                    gpu = gpu_snapshot(adapter); last_gpu = time.monotonic()
                check_resources(host, gpu)
                row['telemetry'].append(dict(utc=now(), phase='running', host=host, gpu=gpu))
                time.sleep(.25)
        except BaseException as error:
            stop_reason = repr(error)
            if child.poll() is None: child.kill()
        finally:
            row.update(exitCode=child.wait(timeout=10), finishedUtc=now(), processSeconds=time.monotonic()-tick)
            row['logSha256'] = sha(log)
            if stop_reason: row['stopReason'] = stop_reason
            save(receipt, row)
    if stop_reason or row['exitCode']: raise RuntimeError('Stop campaign; retained failed own child: '+str(receipt))
    host, gpu = host_snapshot(), gpu_snapshot(adapter)
    row['telemetry'].append(dict(utc=now(), phase='after', host=host, gpu=gpu)); save(receipt, row)
    check_resources(host, gpu)
    row.update(parse_log(log.read_text(encoding='utf-8', errors='replace'), case, device))
    save(receipt, row)
    print('DONE', case['tag'], 'meanMs', row.get('meanMs'), flush=True)
    return row


def analyze(folder, output, partial=False):
    plan = read(folder/'plan.json')
    completion = read(folder/'completion.json') if (folder/'completion.json').exists() else None
    failure = read(folder/'failure.json') if (folder/'failure.json').exists() else None
    complete = bool(completion and completion['complete'] and completion['planSha256'] == sha(folder/'plan.json'))
    if plan['stage'] != 'confirm' or not complete and not (partial and failure and failure['complete'] is False):
        raise ValueError('Incomplete/changed confirmation plan. Use explicit partial audit only for a retained resource preflight stop.')
    if plan['policy'] not in (BASE_POLICY,POLICY) or plan['schedule'] != schedule('confirm', plan['counts']): raise ValueError('Changed schedule/policy.')
    rows = []; previous = None; pending=[]; rejected=[]; incomplete_tail=False
    for case in plan['schedule']:
        path = folder/(case['tag']+'.json')
        if partial and not complete and not path.exists():
            pending.append(case);incomplete_tail=True;continue
        row = read(path); log = folder/(case['tag']+'.log')
        if partial and not complete and row.get('preflightRejected'):
            if any(row[k]!=v for k,v in case.items()) or 'pid' in row or 'exitCode' in row or log.exists():
                raise ValueError('A rejected preflight unexpectedly launched a process.')
            if len(row['telemetry'])!=1 or row['telemetry'][0]['phase']!='before':raise ValueError('Missing rejection telemetry.')
            point=row['telemetry'][0]
            try:check_resources(point['host'],point['gpu'],case['count'])
            except RuntimeError as error:
                if str(error)!=row['stopReason'] or str(error) not in failure['error']:raise ValueError('Resource stop reason differs.')
            else:raise ValueError('Preflight stop cannot be independently reproduced from telemetry.')
            rejected.append(row);incomplete_tail=True;continue
        if incomplete_tail:raise ValueError('A process ran after the first interrupted scheduled slot.')
        if any(row[k] != v for k, v in case.items()) or row['exitCode'] != 0 or row.get('stopReason'):
            raise ValueError('Failed/mismatched process receipt.')
        if row['executableSha256'] != plan['identity']['runtimeFiles']['scan/scan.exe'] or sha(log) != row['logSha256']:
            raise ValueError('Changed binary/log.')
        parsed = parse_log(log.read_text(encoding='utf-8'), case, plan['device'])
        if any(row[k] != v for k, v in parsed.items()): raise ValueError('Receipt/raw-log mismatch.')
        start, finish = datetime.fromisoformat(row['startedUtc']), datetime.fromisoformat(row['finishedUtc'])
        if finish <= start or previous and start < previous: raise ValueError('Processes overlap.')
        previous = finish
        # Retained telemetry is part of acceptance, not a decorative attachment.
        if not row['telemetry'] or row['telemetry'][0]['phase'] != 'before' or row['telemetry'][-1]['phase'] != 'after':
            raise ValueError('Missing resource telemetry.')
        for point in row['telemetry']:
            check_resources(point['host'], point['gpu'], case['count'] if point['phase'] == 'before' else 0)
        rows.append(row)
    if complete and (len(rows) != completion['processes'] or len(rows) != len(plan['schedule'])): raise ValueError('Missing process.')
    if not complete and (len(rejected)!=1 or not pending or not rows):raise ValueError('Partial audit requires completed processes, one verified resource rejection and an unexecuted schedule tail.')
    cells = []; incomplete_cells=[]
    for n in plan['counts']:
        for op in OPERATIONS:
            current = [r for r in rows if r['count'] == n and r['operation'] == op]
            by = {arm: sorted([r for r in current if r['arm'] == arm], key=lambda r:r['repeat']) for arm in ARMS}
            if any([r['repeat'] for r in values] != list(range(1, 7)) for values in by.values()):
                if complete:raise ValueError('Missing independent round.')
                incomplete_cells.append(dict(count=n,operation=op,completedProcessesByArm={arm:len(by[arm]) for arm in ARMS},status='not-confirmed'))
                continue
            ratios = [a['meanMs']/b['meanMs'] for a,b in zip(by['rts'], by['tile-fused'], strict=True)]
            logs = [math.log(x) for x in ratios]; center = statistics.mean(logs)
            half = T5 * statistics.stdev(logs)/math.sqrt(6)
            ratio, lo, hi = math.exp(center), math.exp(center-half), math.exp(center+half)
            means = {arm:statistics.mean(r['meanMs'] for r in by[arm]) for arm in ARMS}
            cvs = {arm:statistics.stdev(r['meanMs'] for r in by[arm])/means[arm] for arm in ARMS}
            cells.append(dict(count=n, operation=op, processMeanMs=means, ratios=ratios,
                              rtsOverLocalRatio=ratio, ratio95Ci=[lo,hi], timeReductionPercent=100*(1-1/ratio),
                              timeReduction95CiPercent=[100*(1-1/lo),100*(1-1/hi)], processMeanCv=cvs,
                              processP50MeanMs={arm:statistics.mean(r['p50Ms'] for r in by[arm]) for arm in ARMS},
                              processP95MeanMs={arm:statistics.mean(r['p95Ms'] for r in by[arm]) for arm in ARMS},
                              maximumWithinProcessCv={arm:max(r['withinProcessCv'] for r in by[arm]) for arm in ARMS},
                              favorablePointwiseInterval=lo>1, independentRounds=6))
    resources = dict(minimumHostPhysicalBytes=min(p['host']['availablePhysical'] for r in rows for p in r['telemetry']),
                     minimumHostCommitBytes=min(p['host']['availableCommit'] for r in rows for p in r['telemetry']),
                     maximumSampledGpuUsedBytes=max(p['gpu']['usedBytes'] for r in rows for p in r['telemetry']),
                     maximumNativeDxgiUsageBytes=max(e['usage'] for r in rows for e in r['events'] if e['kind']=='memory'),
                     maximumGpuTemperatureC=max(p['gpu']['temperatureC'] for r in rows for p in r['telemetry']))
    result = dict(schema='hlslperf.ultra-scan.analysis.v1', complete=complete, device=plan['device'],
                  processes=len(rows), planSha256=sha(folder/'plan.json'), cells=cells, resourceSummary=resources,
                  incompleteCells=incomplete_cells,rejectedPreflights=rejected,pendingCases=pending,
                  stopReason=failure['error'] if not complete else None,
                  policy=plan['policy'], sourceIdentity=plan['identity'],
                  statistics='Six fresh processes per arm/cell; balanced paired orders; pointwise nominal 95% Student-t df5 log-ratio intervals; no multiplicity adjustment or exclusions.',
                  boundary='Inherited TimeScan complete GPU operation (reset, dependencies, scan); input generation, compile, allocation, validation, CPU submit/fence/readback excluded. Single operations, not a giant queued batch.',
                  scope='Pinned upstream all-one uint32 inputs, RTX 4090, inclusive/exclusive, 2^26..2^28; no application-frame, mixed-input timing, cross-GPU or continuous-range claim.')
    save(output, result)
    import csv
    with output.with_suffix('.csv').open('w', newline='', encoding='utf-8') as stream:
        fields = ['count','operation','rtsMeanMs','localMeanMs','rtsOverLocalRatio','ratio95Lo','ratio95Hi','timeReductionPercent']
        writer=csv.DictWriter(stream,fieldnames=fields);writer.writeheader()
        for cell in cells:
            writer.writerow(dict(count=cell['count'],operation=cell['operation'],rtsMeanMs=cell['processMeanMs']['rts'],
                                 localMeanMs=cell['processMeanMs']['tile-fused'],rtsOverLocalRatio=cell['rtsOverLocalRatio'],
                                 ratio95Lo=cell['ratio95Ci'][0],ratio95Hi=cell['ratio95Ci'][1],timeReductionPercent=cell['timeReductionPercent']))
    print(json.dumps(dict(cells=cells, resourceSummary=resources), indent=2))


def main():
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('stage',choices=['build','probe','gate','pilot','confirm','analyze'])
    p.add_argument('--native',type=Path);p.add_argument('--output',required=True,type=Path)
    p.add_argument('--adapter',default='NVIDIA GeForce RTX 4090');p.add_argument('--device',type=Path)
    p.add_argument('--counts',default=','.join(map(str,COUNTS)));p.add_argument('--evidence',type=Path)
    p.add_argument('--gate',type=Path);p.add_argument('--pilot',type=Path);p.add_argument('--partial',action='store_true')
    p.add_argument('--msbuild',type=Path);p.add_argument('--package-cache',type=Path,default=ROOT/'.scratch/native-packages')
    p.add_argument('--toolset',choices=['v143','v145'],default='v143')
    a=p.parse_args();out=a.output.resolve();sizes=counts(a.counts)
    if out.exists():raise FileExistsError('Preserve existing attempt: '+str(out))
    if a.stage=='analyze':analyze(a.evidence.resolve(),out,a.partial);return
    with shared_lock():
        check_resources(host_snapshot(),gpu_snapshot(a.adapter))
        if a.stage=='build':
            from prepare_external_benchmarks import prepare
            prepare(out,a.package_cache.resolve(),True,a.msbuild.resolve(),a.toolset,('scan',))
            print('Native scan build complete:',out,flush=True);return
        native=a.native.resolve();fixed=identity(native);fixed['ultraRunnerSha256']=sha(Path(__file__));out.mkdir(parents=True)
        if a.stage=='probe':
            exe=native/'scan/scan.exe';log=out/'probe.log'
            with log.open('wb') as stream:
                subprocess.run([str(exe),'probe','rts',str(1<<28),'0',a.adapter],cwd=exe.parent,stdout=stream,
                               stderr=subprocess.STDOUT,check=True,timeout=20,creationflags=subprocess.CREATE_NO_WINDOW)
            events=[json.loads(line[7:]) for line in log.read_text().splitlines() if line.startswith('HPJSON ')]
            device=next(e for e in events if e['kind']=='device');save(out/'device.json',device)
            save(out/'identity.json',fixed);print(json.dumps(device));return
        device=read(a.device);cases=schedule(a.stage,sizes)
        prerequisites={}
        if a.stage=='confirm':
            for name,path in [('gate',a.gate),('pilot',a.pilot)]:
                plan=read(path/'plan.json');completion=read(path/'completion.json')
                if plan['stage']!=name or plan['identity']!=fixed or plan['device']!=device or not completion['complete'] or completion['planSha256']!=sha(path/'plan.json'):
                    raise ValueError('Prerequisite source/device/completion changed: '+name)
                if name=='pilot' and plan['counts']!=list(sizes):raise ValueError('Every scale/semantic/arm needs a completed pilot.')
                prerequisites[name]=dict(planSha256=sha(path/'plan.json'),completionSha256=sha(path/'completion.json'))
        plan=dict(schema='hlslperf.ultra-scan.plan.v1',registeredUtc=now(),stage=a.stage,counts=list(sizes),
                  identity=fixed,device=device,policy=POLICY,schedule=cases,prerequisites=prerequisites,
                  candidate='One unchanged wave32/group256/items16/partition4096/polls4/persistent256 candidate, native inclusive/exclusive; pinned RTS defaults; no tuning.',
                  resourceRule='Forecast 12 bytes/element +64 MiB and retain 4 GiB host physical/commit and 8 GiB GPU free; stop own child and campaign on resource/correctness/device failure; no external process termination or OS setting changes.')
        save(out/'plan.json',plan)
        try:
            for index,case in enumerate(cases):
                current=identity(native);current['ultraRunnerSha256']=sha(Path(__file__))
                if current!=fixed:raise ValueError('Source/runtime changed during campaign.')
                execute(native,out,case,device,a.adapter)
                save(out/'progress.json',dict(lastCompleted=case['tag'],utc=now()))
                if index+1<len(cases):time.sleep(POLICY['interProcessRestSeconds'])
            current=identity(native);current['ultraRunnerSha256']=sha(Path(__file__))
            if current!=fixed:raise ValueError('Source/runtime changed at completion.')
            save(out/'completion.json',dict(complete=True,processes=len(cases),planSha256=sha(out/'plan.json'),finishedUtc=now(),sourceFilesVerified=True))
        except BaseException as error:
            save(out/'failure.json',dict(complete=False,error=repr(error),utc=now()))
            raise
    print('COMPLETE',a.stage,len(cases),'fresh processes',flush=True)


if __name__=='__main__':main()
