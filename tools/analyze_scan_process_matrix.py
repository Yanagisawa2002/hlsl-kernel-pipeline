"""Read-only process-level audit for the predeclared Scan experiment."""
import argparse
import hashlib
import importlib.util
import json
import math
import re
import statistics
import sys
from collections import defaultdict
from datetime import datetime
from pathlib import Path

sys.dont_write_bytecode=True
T4=2.7764451051977987

def read(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))

def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()

def percentile(values,q):
    s=sorted(values); p=(len(s)-1)*q; lo=math.floor(p); hi=math.ceil(p)
    return s[lo]+(s[hi]-s[lo])*(p-lo)

def interval(values,log=False):
    assert len(values)==5, 'Exactly five independent processes are required'
    v=[math.log(x) for x in values] if log else values
    mean=statistics.mean(v); radius=T4*statistics.stdev(v)/math.sqrt(5)
    f=math.exp if log else lambda x:max(0,x)
    return dict(estimate=f(mean),lower95=f(mean-radius),upper95=f(mean+radius),processes=5,df=4,weight='equal process')

def latency(values):
    return dict(meanMs=statistics.mean(values),medianMs=statistics.median(values),p95Ms=percentile(values,.95),p99Ms=percentile(values,.99),maxMs=max(values),samples=len(values))

def graph_hash(root):
    nodes={}
    def visit(p):
        p=p.resolve()
        if p in nodes:return
        text=p.read_bytes().decode('utf-8-sig');nodes[p]=text
        for include in re.findall(r'^\s*#\s*include\s*["<]([^">]+)[">]',text,re.M):visit(p.parent/include.strip())
    visit(root)
    deps=sorted((p.relative_to(root.parent.resolve()).as_posix(),hashlib.sha256(s.encode()).hexdigest()) for p,s in nodes.items())
    return hashlib.sha256(('hlsl-source-graph-v1\n'+''.join(p+'\0'+h+'\n' for p,h in deps)).encode()).hexdigest()

def audit_run(path,entry,declaration,repo,basic_audit):
    import jsonschema
    run=read(path); execution=read(path.parent/'execution.json'); manifest=read(Path(entry['manifestPath']))
    assert execution['status']=='recorded' and execution['exitCode'] in (0,2)
    assert execution['sourceSha']==declaration['sourceSha'] and execution['manifestSha256']==entry['manifestSha256']
    assert datetime.fromisoformat(execution['processStartedUtc'])<=datetime.fromisoformat(run['startedUtc'])
    assert datetime.fromisoformat(execution['processExitedUtc'])>=datetime.fromisoformat(run['finishedUtc'])
    assert datetime.fromisoformat(declaration['declaredUtc'])<datetime.fromisoformat(run['startedUtc'])
    assert sha(Path(entry['manifestPath']))==entry['manifestSha256']==run['manifestSha256']
    assert graph_hash(Path(manifest['kernelPath']))==run['kernelSha256']
    for p,s in [(Path(entry['manifestPath']),'manifest.schema.v3.json'),(path.parent/'checkpoint.json','checkpoint.schema.v2.json')]:
        jsonschema.Draft202012Validator(read(repo/'schemas'/s)).validate(read(p))
    basic=basic_audit(path)
    # Two non-baseline challengers * 8 blocks * 4 positions, then one locked
    # confirmation * 8 * 4. The baseline is measured as the control in each pair.
    ev=run['pairedEvidence'];obs=ev['observations']; options=ev['options']; assert len(obs)==96
    assert options==manifest['pairedMeasurement']
    assert options['calibrationBlocks']==options['confirmationBlocks']==8
    assert options['maximumBaselineDrift']==.15 and ev['maximumCoefficientOfVariation']==.05 and ev['minimumRequiredSpeedup']==1.01
    assert run['device']['adapterName']=='AMD Radeon AI PRO R9700' and run['device']['driverVersion']=='32.0.31041.1004'
    assert len(run['candidates'])==3
    byid={c['candidateId']:c for c in run['candidates']}
    assert {c['defines']['HLSLPERF_SCAN_BACKEND'] for c in byid.values()}=={1,2,3}
    for c in byid.values():
        assert c['compiled'] and c['correctness']['passed'] and c['error'] is None
        d=c['defines'];assert d['HLSLPERF_GROUP_SIZE']==256 and d['HLSLPERF_ELEMENTS_PER_THREAD']==4 and d['HLSLPERF_VECTOR_WIDTH']==1 and d['HLSLPERF_SCAN_OPERATOR']==1
        if d['HLSLPERF_SCAN_BACKEND'] in (2,3):assert d['HLSLPERF_WAVE_SIZE']==32
        if d['HLSLPERF_SCAN_BACKEND']==3:assert d['HLSLPERF_SINGLE_PASS_ITEMS_SCALE']==4 and d['HLSLPERF_SINGLE_PASS_GROUPS']==256
    expected_slots=[]
    for phase,ids in [('calibration',[cid for cid in byid if cid!=run['baselineCandidateId']]),('confirmation',[ev['selectedAfterCalibration']])]:
        for block in range(8):
            position=0
            for cid in sorted(ids,key=lambda cid:hashlib.sha256(f"{options['orderSeed']}/{phase}/{block}/{cid}".encode()).hexdigest()):
                abba=int(hashlib.sha256(f"order/{options['orderSeed']}/{phase}/{block}/{cid}".encode()).hexdigest()[:2],16)%2==0
                for offset in range(4):
                    baseline=(offset in (0,3))==abba
                    expected_slots.append(dict(phase=phase,block=block,position=position,challengerId=cid,candidateId=run['baselineCandidateId'] if baseline else cid,isBaseline=baseline,order='ABBA' if abba else 'BAAB',orderSeed=options['orderSeed'],inputSeed=options[phase+'SeedStart']+block*options['residentSlots']))
                    position+=1
    assert [r['slot'] for r in obs]==expected_slots, 'Observed schedule differs from declaration'
    blocks=defaultdict(list); seedsets=defaultdict(set);digestsets=defaultdict(set); batch=[]
    binary={str(Path(v['path']).resolve()).lower():v['sha256'] for v in declaration['binaries']}
    for row in obs:
        s=row['slot'];r=row['result'];sc=row['scenario']
        assert r['measuredDispatchesPerBatch']==36 and r['error'] is None
        assert r['samplesMilliseconds'][0]*36>=.25
        blocks[(s['phase'],s['challengerId'],s['block'])].append(row)
        for slot in sc['slots']:
            seedsets[s['phase']].add(slot['inputSeed']);digestsets[s['phase']].add(slot['inputSha256'])
        for module in sc['nativeCompilerBinaries']:
            assert binary[str(Path(module['path']).resolve()).lower()]==module['sha256']
        batch.append(dict(phase=s['phase'],block=s['block'],position=s['position'],candidate=s['candidateId'],isBaseline=s['isBaseline'],repeats=36,totalGpuMs=r['samplesMilliseconds'][0]*36,amortizedPlanMs=r['samplesMilliseconds'][0]))
    comparisons=[]
    for phase,c in [('calibration',c) for c in ev['calibration']]+[('confirmation',ev['confirmation'])]:
        a=[];b=[];g=[];local=[]
        for k in range(8):
            rows=blocks[(phase,c['candidateId'],k)]
            aa=[r['result']['samplesMilliseconds'][0] for r in rows if r['slot']['isBaseline']]
            bb=[r['result']['samplesMilliseconds'][0] for r in rows if not r['slot']['isBaseline']]
            a+=aa;b+=bb;g.append(math.sqrt(aa[0]*aa[1]));local.append(max(aa)/min(aa)-1)
        drift=max(max(local),max(g)/min(g)-1)
        acv=statistics.stdev(a)/statistics.mean(a);bcv=statistics.stdev(b)/statistics.mean(b)
        assert math.isclose(drift,c['baselineDrift'],abs_tol=1e-12)
        numerical_ok=(drift<=.15 and max(acv,bcv)<=.05)
        if c['candidateId']!=run['baselineCandidateId']:
            numerical_ok &= percentile(b,.95)<=percentile(a,.95) and c['lower95Speedup']>=1.01
        assert not c['passed'] or numerical_ok
        comparisons.append(dict(phase=phase,candidate=c['candidateId'],backend=byid[c['candidateId']]['defines']['HLSLPERF_SCAN_BACKEND'],geometricSpeedup=c['geometricMeanSpeedup'],lower95=c['lower95Speedup'],upper95=c['upper95Speedup'],drift=drift,baselineCv=acv,candidateCv=bcv,stableTiming=drift<=.15 and max(acv,bcv)<=.05,passed=c['passed'],rejections=c['rejections'],baselineLatency=latency(a),candidateLatency=latency(b)))
    confirmation=comparisons[-1]
    # Replay between-phase drift and pooled calibration baseline CV as well.
    ca=[r['result']['samplesMilliseconds'][0] for r in obs if r['slot']['phase']=='calibration' and r['slot']['isBaseline']]
    co=[r['result']['samplesMilliseconds'][0] for r in obs if r['slot']['phase']=='confirmation' and r['slot']['isBaseline']]
    between=abs(statistics.median(co)/statistics.median(ca)-1); calibration_cv=statistics.stdev(ca)/statistics.mean(ca)
    if between>.15 or calibration_cv>.05:assert not ev['deployable']
    costs=[]
    for cid,c in byid.items():
        sc=next(r['scenario'] for r in obs if r['slot']['candidateId']==cid)
        costs.append(dict(candidate=cid,backend=c['defines']['HLSLPERF_SCAN_BACKEND'],logicalBytesPerSlot=sc['slots'][0]['logicalBytes'],logicalBytesPerArm=sc['logicalBufferBytes'],committedBytesPerArm=sc['committedAllocationBytes'],passes=sc['slots'][0]['passCount']))
    return dict(name=entry['name'],cell=entry['cell'],round=entry['round'],pid=execution['pid'],processStartedUtc=execution['processStartedUtc'],processExitedUtc=execution['processExitedUtc'],reportSha256=sha(path),sessionId=ev['sessionId'],observations=len(obs),poisonOutputChecks=basic['poisonOutputChecks'],device=run['device'],baseline=run['baselineCandidateId'],selected=ev['selectedAfterCalibration'],selectedBackend=byid[ev['selectedAfterCalibration']]['defines']['HLSLPERF_SCAN_BACKEND'],deployable=ev['deployable'],confirmation=confirmation,comparisons=comparisons,betweenPhaseDrift=between,calibrationBaselineCv=calibration_cv,rejections=ev['rejections'],seedsets={k:sorted(v) for k,v in seedsets.items()},digestsets={k:sorted(v) for k,v in digestsets.items()},costs=costs,batches=batch)

def aggregate(rows):
    assert len(rows)==5 and {r['round'] for r in rows}=={1,2,3,4,5}
    assert all(r['costs']==rows[0]['costs'] for r in rows), 'Fixed candidate costs changed between processes'
    selected={r['selected'] for r in rows}; same=len(selected)==1
    ci=interval([r['confirmation']['geometricSpeedup'] for r in rows],True) if same else None
    accepted=same and rows[0]['selectedBackend']!=1 and all(r['deployable'] for r in rows) and ci['lower95']>=1.01
    tails={}
    if same:
        for arm in ('baselineLatency','candidateLatency'):
            tails[arm]={metric:interval([r['confirmation'][arm][metric] for r in rows]) for metric in ('meanMs','medianMs','p95Ms','p99Ms','maxMs')}
    calibration=[]
    for backend in (2,3):
        c=[next(c for c in r['comparisons'] if c['phase']=='calibration' and c['backend']==backend) for r in rows]
        calibration.append(dict(backend=backend,exploratory=True,speedup=interval([v['geometricSpeedup'] for v in c],True),passes=sum(v['passed'] for v in c),latency={arm:{metric:interval([v[arm][metric] for v in c]) for metric in ('medianMs','p95Ms','p99Ms')} for arm in ('baselineLatency','candidateLatency')}))
    return dict(cell=rows[0]['cell'],processes=5,selectedBackends=[r['selectedBackend'] for r in rows],sameSelection=same,individualGatesPassed=sum(r['deployable'] for r in rows),speedup=ci,accepted=accepted,status='confirmed_gain' if accepted else 'baseline_retained' if same and rows[0]['selectedBackend']==1 and all(r['deployable'] for r in rows) else 'inconclusive',confirmationLatency=tails,calibration=calibration,allConfirmationTimingStable=all(r['confirmation']['stableTiming'] for r in rows),costs=rows[0]['costs'])

def main():
    parser=argparse.ArgumentParser();parser.add_argument('repo',type=Path);parser.add_argument('root',type=Path);parser.add_argument('output',type=Path);parser.add_argument('--partial',action='store_true')
    args=parser.parse_args();repo=args.repo.resolve();root=args.root.resolve();d=read(root/'declaration.json')
    spec=importlib.util.spec_from_file_location('basic_audit',repo/'tools/validate_vnext_evidence.py');module=importlib.util.module_from_spec(spec);spec.loader.exec_module(module)
    for f in d['binaries']+d['sources']:assert sha(Path(f['path']))==f['sha256'],f['path']
    assert sha(Path(d['protocolPath']))==d['protocolSha256']
    rows=[];failures=[];pending=[];sessions=set();identities=set();allseeds=set();alldigests=set();groups=defaultdict(list)
    previous_exit=None
    for entry in d['schedule']:
        path=root/entry['name']/'run.json';receipt=path.parent/'execution.json'
        if not path.exists() or not receipt.exists() or read(receipt)['status'] in ('queued','running'):
            pending.append(entry['name']);continue
        try:
            row=audit_run(path,entry,d,repo,module.audit)
            if previous_exit is not None:assert datetime.fromisoformat(row['processStartedUtc'])>=previous_exit, 'Native processes overlapped or violated declared order'
            previous_exit=datetime.fromisoformat(row['processExitedUtc'])
            identity=(row['pid'],row['processStartedUtc'])
            assert identity not in identities and row['sessionId'] not in sessions
            identities.add(identity);sessions.add(row['sessionId'])
            seeds=set(v for values in row['seedsets'].values() for v in values)
            digests=set(v for values in row['digestsets'].values() for v in values)
            assert not allseeds.intersection(seeds) and not alldigests.intersection(digests)
            allseeds.update(seeds);alldigests.update(digests)
            rows.append(row);groups[row['cell']].append(row)
        except Exception as error:failures.append(dict(name=entry['name'],error=repr(error)))
    cells=[aggregate(sorted(groups[c['name']],key=lambda r:r['round'])) for c in d['cells'] if len(groups[c['name']])==5]
    refinement=[]
    for slots in (1,3):
        ordered=[next((c for c in cells if c['cell']==f'scan-{mi}Mi-slots{slots}'),None) for mi in (4,8,12,16)]
        for lo,hi,mid in zip(ordered,ordered[1:],(6,10,14)):
            if lo and hi and lo['sameSelection'] and hi['sameSelection'] and lo['selectedBackends'][0]==hi['selectedBackends'][0]!=1 and lo['allConfirmationTimingStable'] and lo['speedup']['upper95']<=1.01 and hi['accepted']:
                refinement.append(dict(midpointMi=mid,slots=slots,lowerCell=lo['cell'],upperCell=hi['cell']))
    full=len(rows)==40 and not failures and not pending and len(cells)==8
    result=dict(schema='hlslperf.scan-process-audit.v1',passed=not failures and (full or args.partial),complete=full,measurementSha=d['sourceSha'],baselineSha=d['baselineSha'],declarationSha256=sha(root/'declaration.json'),processes=len(rows),observations=sum(r['observations'] for r in rows),poisonOutputChecks=sum(r['poisonOutputChecks'] for r in rows),failures=failures,pending=pending,cells=cells,refinementTrigger=refinement,processResults=rows)
    args.output.parent.mkdir(parents=True,exist_ok=True);args.output.write_text(json.dumps(result,indent=2),encoding='utf-8')
    print(json.dumps({k:v for k,v in result.items() if k not in ('processResults','cells','pending')},indent=2))
    print(json.dumps([{k:c[k] for k in ('cell','status','selectedBackends','individualGatesPassed','speedup')} for c in cells],indent=2))
    return 0 if result['passed'] else 1

if __name__=='__main__':sys.exit(main())
