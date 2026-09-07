"""Generate the fixed external comparison declaration or audit/analyze its process evidence."""
import argparse
import csv
import hashlib
import itertools
import json
import math
import random
import statistics as stats
from pathlib import Path

SCAN = ['internal-scan-baseline', 'internal-scan-single', 'gps-reduce-then-scan', 'gps-decoupled-fallback']
RADIX = ['internal-radix-1', 'internal-radix-8', 'amd-parallel-sort']


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def generate(path, repo):
    assert not path.exists(), 'Never overwrite a declaration.'
    cells = []
    specs = [('scan', n, 'uniform', False, slots) for n in [4, 8, 12, 16] for slots in [1, 3]]
    specs += [('radix', n, pattern, pairs, slots) for n in [1, 4] for pairs in [False, True]
              for pattern in ['uniform', 'duplicate'] for slots in [1, 3]]
    for index, (workload, mi, pattern, pairs, slots) in enumerate(specs):
        arms = SCAN if workload == 'scan' else RADIX
        permutations = list(itertools.permutations(arms))
        # A permutation and its reverse form each block. Scan covers all 24 permutations once.
        # Radix covers all six permutations four times across the 24 half-blocks.
        orders = [p for p in permutations if p < p[::-1]] if workload == 'scan' else permutations * 2
        assert len(orders) == 12
        processes = []
        for process in range(1, 6):
            shuffled = orders.copy(); random.Random(911 + index * 181 + process * 37).shuffle(shuffled)
            processes.append(dict(index=process, seeds=[1109007 + index * 1009 + process * 7919 + slot * 104729 for slot in range(slots)], orders=shuffled))
        cells.append(dict(id=f'{workload}-{mi}mi-{"pairs" if pairs else "keys"}-{pattern}-{slots}slots',
                          workload=workload, count=mi * 1048576, pattern=pattern, pairs=pairs, slots=slots, arms=arms, processes=processes))
    process_order = []
    for process in range(1, 6):
        order = list(range(24)); random.Random(771701 + process * 631).shuffle(order)
        process_order.extend(dict(cell=cells[index]['id'], process=process) for index in order)
    source_paths = ['src/HlslPerf.Workloads/UnifiedWorkloads.cs', 'src/HlslPerf.D3D12/D3D12UnifiedOperations.cs',
                    'src/HlslPerf.UnifiedBench/UnifiedBenchRunner.cs', 'third_party/upstream-lock.json']
    declaration = dict(schema='hlslperf.unified-declaration.v1', repetitions=18, blocks=12, warmupBatches=8, warmupRepetitions=6,
        processesPerCell=5, cells=cells, processOrder=process_order,
        inputs='Full uint32 deterministic LCG; duplicate keys are modulo seven; original-index payloads; independent resident allocations.',
        semantics={'scan':'exclusive uint32 addition modulo 2^32', 'radix':'stable ascending full32 uint32 keys; canonical AoS pair input and SoA outputs'},
        costs='GPU total includes input restore, scratch initialization, all dispatches/barriers, consumer conversion, and timestamp markers; no empty-marker subtraction. Upload/readback and CPU plan/record/close/submit/wait/reset are separate.',
        correctness='Every arm and resident slot independently runs two full-output poison/oracle checks before timing; correct arms repeat two checks after timing. A failing arm retains all checks and receives explicit not-timed observations. No other arm is filtered.',
        stops='No retries, tuning, data-dependent warmup, early performance stopping or replacement seeds. Any native exception/device loss aborts this process and stops the matrix epoch for diagnosis; do not pool across source/binary changes. Incorrect arms are not timed. Statistical failures remain measured and inconclusive.',
        statistics={'primary':'paired log GPU-total-per-operation ratios, averaged within 12 blocks and then within each independent process',
            'ci':'two-sided 95% Student t interval over five process log ratios, df=4, critical=2.7764451051977987',
            'cv':'sample standard deviation / mean of 24 batch averages in each process and arm',
            'drift':'absolute ratio of final three block mean to first three block mean minus one',
            'tail':'linear interpolated per-operation GPU timestamp p95 and p99 within each process; correlated repeats are not independent replicates',
            'accepted':'both arms correct in all five complete processes; every arm/process CV<=0.05 and drift<=0.15; every process p95 numerator/denominator>=1.01; paired lower95>=1.01',
            'multiplicity':'predeclared pairwise comparisons are individually descriptive, with no familywise error adjustment; no universal winner or automatic policy promotion'},
        controls='Existing R9700/driver/power/user workloads unchanged. Resident slots rotate equally. No global cache flush, fixed clocks, residency pinning or measured hardware cache counters.',
        memory='512 MiB committed allocation cap per slot, plus 75% of available DXGI local budget check; total allocations and slot counts reported.',
        sources={p:digest(repo/p) for p in source_paths})
    path.parent.mkdir(parents=True, exist_ok=True); path.write_text(json.dumps(declaration, indent=2)+'\n', encoding='utf-8')
    print(json.dumps(dict(path=str(path), sha256=digest(path), cells=len(cells), processes=len(process_order)), indent=2))


def quantile(values, fraction):
    values = sorted(values); position = (len(values)-1)*fraction; lo = int(position); hi = min(lo+1, len(values)-1)
    return values[lo]*(hi-position)+values[hi]*(position-lo) if hi != lo else values[lo]


def arm_metrics(process, arm):
    measured = [o for o in process['observations'] if o['arm']==arm and o['status']=='measured']
    if process['statuses'][arm] != 'measured_correct': return None
    assert len(measured)==24
    batches = [o['timing']['gpuTotalMilliseconds']/o['timing']['repetitions'] for o in measured]
    blocks = [stats.mean(batches[2*b:2*b+2]) for b in range(12)]
    operations = [value for o in measured for value in o['timing']['gpuOperationMilliseconds']]
    assert len(operations)==432 and all(value>0 for value in batches+operations)
    return dict(meanMs=stats.mean(batches), cv=stats.stdev(batches)/stats.mean(batches),
                drift=abs(stats.mean(blocks[-3:])/stats.mean(blocks[:3])-1), p50Ms=quantile(operations,.5),
                p95Ms=quantile(operations,.95), p99Ms=quantile(operations,.99), blocks=blocks,
                cpuRecordMs=stats.mean(o['timing']['cpuRecordMilliseconds']/18 for o in measured),
                cpuSubmitMs=stats.mean(o['timing']['submission']['cpuSubmitMilliseconds']/18 for o in measured),
                gpuAlgorithmMs=stats.mean(o['timing']['gpuAlgorithmMilliseconds']/18 for o in measured),
                gpuInputRestoreMs=stats.mean(o['timing']['gpuInputRestoreMilliseconds']/18 for o in measured),
                gpuScratchInitializationMs=stats.mean(o['timing']['gpuScratchInitializationMilliseconds']/18 for o in measured),
                gpuOutputConversionMs=stats.mean(o['timing']['gpuOutputConversionMilliseconds']/18 for o in measured))


def analyze(declaration_path, raw, output, expected_cells=24):
    assert not output.exists(), 'Never overwrite an audit.'
    declaration=json.loads(declaration_path.read_text()); declaration_hash=digest(declaration_path)
    assert len(declaration['cells'])==expected_cells and len(declaration['processOrder'])==expected_cells*5
    rows=[]; comparisons=[]; failures=[]; identities=set(); pids=set(); source_shas=set()
    for cell in declaration['cells']:
        processes=[]; metrics={arm:[] for arm in cell['arms']}
        for index in range(1,6):
            folder=raw/f"{cell['id']}-p{index}"; p=json.loads((folder/'process.json').read_text())
            receipt=json.loads(Path(str(folder)+'.execution.json').read_text())
            assert p['completed'] and not p['errors'] and p['declarationSha256']==declaration_hash
            assert receipt['declarationSha256']==declaration_hash and receipt['exitCode'] in [0,2]
            assert p['pid']==receipt['pid'] and p['processIndex']==index and p['cell']==cell
            process_identity=(p['pid'],receipt['startedUtc'])
            assert process_identity not in pids; pids.add(process_identity); source_shas.add(receipt['sourceSha'])
            assert p['schedule']==cell['processes'][index-1]
            assert p['deviceRemovalStatus']=='00000000'
            identities.add(json.dumps([(b['path'],b['sha256']) for b in p['runtime']['binaries']],sort_keys=True))
            assert len(p['observations'])==12*2*len(cell['arms'])
            for block, order in enumerate(p['schedule']['orders']):
                for half in [0,1]:
                    expected=order if half==0 else order[::-1]
                    observed=[o for o in p['observations'] if o['block']==block and o['half']==half]
                    assert [o['arm'] for o in observed]==expected
                    for o in observed:
                        if o['status']=='measured':
                            assert o['timing']['repetitions']==18 and o['timing']['residentSlots']==cell['slots']
                            assert o['timing']['slotExecutions']==[18//cell['slots']]*cell['slots']
            for arm in cell['arms']:
                m=arm_metrics(p,arm); metrics[arm].append(m)
                checks=[c for c in p['checks'] if c['arm']==arm]
                assert len([c for c in checks if c['phase']=='before'])==cell['slots']
                if m:
                    assert len(checks)==2*cell['slots'] and all(v['passed'] for c in checks for v in c['verification'])
                    rows.append(dict(cell=cell['id'],process=index,arm=arm,**{k:v for k,v in m.items() if k!='blocks'}))
                else: failures.append(dict(cell=cell['id'],process=index,arm=arm,status=p['statuses'][arm],checks=checks))
            processes.append(p)
        for numerator,denominator in itertools.combinations(cell['arms'],2):
            a,b=metrics[numerator],metrics[denominator]
            result=dict(cell=cell['id'],numerator=numerator,denominator=denominator,ratioMeaning='numerator GPU time / denominator GPU time')
            if any(m is None for m in a+b): result.update(status='not_comparable_correctness_failed')
            else:
                logs=[stats.mean(math.log(x/y) for x,y in zip(ma['blocks'],mb['blocks'])) for ma,mb in zip(a,b)]
                mean=stats.mean(logs); margin=2.7764451051977987*stats.stdev(logs)/math.sqrt(5)
                gates=[dict(process=i+1,numeratorCv=ma['cv'],denominatorCv=mb['cv'],numeratorDrift=ma['drift'],
                            denominatorDrift=mb['drift'],p95Ratio=ma['p95Ms']/mb['p95Ms']) for i,(ma,mb) in enumerate(zip(a,b))]
                stable=all(max(g['numeratorCv'],g['denominatorCv'])<=.05 and max(g['numeratorDrift'],g['denominatorDrift'])<=.15 for g in gates)
                tail=all(g['p95Ratio']>=1.01 for g in gates)
                result.update(ratio=math.exp(mean),lower95=math.exp(mean-margin),upper95=math.exp(mean+margin),
                              independentProcesses=5,processLogRatios=logs,gates=gates,
                              status='accepted' if stable and tail and math.exp(mean-margin)>=1.01 else 'inconclusive')
            comparisons.append(result)
    assert len(source_shas)==1 and len(identities)==1 and len(pids)==expected_cells*5
    output.mkdir(parents=True)
    report=dict(schema='hlslperf.unified-audit.v1',audited=True,declarationSha256=declaration_hash,sourceSha=next(iter(source_shas)),
                independentProcesses=len(pids),cells=expected_cells,comparisons=comparisons,correctnessFailures=failures,metrics=rows)
    (output/'audit.json').write_text(json.dumps(report,indent=2)+'\n')
    with (output/'metrics.csv').open('w',newline='') as stream:
        writer=csv.DictWriter(stream,fieldnames=list(rows[0]));writer.writeheader();writer.writerows(rows)
    lines=['# Official external comparison results','',f'Audited {expected_cells} cells and {len(pids)} independent processes from `{next(iter(source_shas))}`.',
           '', 'Ratios are numerator GPU time / denominator GPU time. Values above one favor the denominator. Failed or unstable comparisons remain visible. No default policy changes are implied.',
           '', '| Cell | Numerator | Denominator | Ratio [95% CI] | Decision |','|---|---|---|---|---|']
    for c in comparisons:
        value=f"{c['ratio']:.4f} [{c['lower95']:.4f}, {c['upper95']:.4f}]" if 'ratio' in c else 'N/A'
        lines.append(f"| {c['cell']} | {c['numerator']} | {c['denominator']} | {value} | {c['status']} |")
    lines+=['','The confidence interval uses five process-level paired log ratios. p95/p99 describe correlated within-process operation timestamps; they are not additional independent samples. Detailed CV, drift, cost breakdowns and all correctness checks are in audit.json and metrics.csv.','']
    (output/'report.md').write_text('\n'.join(lines),encoding='utf-8'); print(json.dumps(dict(audited=True,processes=len(pids),output=str(output))))


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__); sub=parser.add_subparsers(dest='command',required=True)
    g=sub.add_parser('generate');g.add_argument('path',type=Path);g.add_argument('--repo',type=Path,default=Path(__file__).resolve().parents[1])
    a=sub.add_parser('analyze');a.add_argument('declaration',type=Path);a.add_argument('raw',type=Path);a.add_argument('output',type=Path)
    args=parser.parse_args()
    if args.command=='generate':generate(args.path,args.repo)
    else:analyze(args.declaration,args.raw,args.output)
