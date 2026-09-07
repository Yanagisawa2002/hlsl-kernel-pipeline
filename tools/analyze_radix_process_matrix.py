"""Independent process audit and immutable Radix entry decision."""
import argparse
import hashlib
import importlib.util
import itertools
import json
import math
import statistics
import sys
from collections import defaultdict
from datetime import datetime,timezone
from pathlib import Path
sys.dont_write_bytecode=True
T4=2.7764451051977987

def read(p):return json.loads(p.read_text(encoding='utf-8-sig'))
def sha(p):return hashlib.sha256(p.read_bytes()).hexdigest()
def write(p,v):p.write_text(json.dumps(v,indent=2),encoding='utf-8')
def percentile(v,q):
    s=sorted(v);p=(len(s)-1)*q;l=math.floor(p);h=math.ceil(p);return s[l]+(s[h]-s[l])*(p-l)
def latency(v):return dict(meanMs=statistics.mean(v),medianMs=statistics.median(v),p95Ms=percentile(v,.95),p99Ms=percentile(v,.99),maxMs=max(v),samples=len(v))
def interval(v,log=False):
    assert len(v)==5
    x=[math.log(z) for z in v] if log else v;m=statistics.mean(x);r=T4*statistics.stdev(x)/math.sqrt(5);f=math.exp if log else lambda z:max(0,z)
    return dict(estimate=f(m),lower95=f(m-r),upper95=f(m+r),processes=5,df=4,weight='equal process')
def expand(m):
    result=[]
    for values in itertools.product(*(a['values'] for a in m['axes'])):
        d=dict(m['fixedDefines'],**dict(zip((a['name'] for a in m['axes']),values)))
        if all(not all(d[k] in v for k,v in r['if'].items()) or all(d[k] in v for k,v in r['then'].items()) for r in m['constraints']):result.append(d)
    return sorted(result,key=lambda d:json.dumps(d,sort_keys=True))

def audit_process(root,entry,d,repo,basic,graph_hash):
    import jsonschema
    folder=root/entry['name'];receipt=read(folder/'execution.json');manifest=read(Path(entry['manifestPath']));path=folder/'run.json'
    assert receipt['sourceSha']==d['sourceSha'] and receipt['manifestSha256']==entry['manifestSha256']
    if entry['stage']=='comparison':assert receipt['entryLedgerSha256']==sha(root/'entry-ledger.json')
    assert sha(Path(entry['manifestPath']))==entry['manifestSha256']
    assert receipt['status'] in ('recorded','execution-failed')
    assert datetime.fromisoformat(receipt['processStartedUtc'])<datetime.fromisoformat(receipt['processExitedUtc'])
    row=dict(name=entry['name'],cell=entry['cell'],stage=entry['stage'],round=entry['round'],pid=receipt['pid'],processStartedUtc=receipt['processStartedUtc'],processExitedUtc=receipt['processExitedUtc'],executionSha256=sha(folder/'execution.json'),supported=False,observations=0,poisonOutputChecks=0,seedsets={},digestsets={},errors=[])
    if not path.exists():
        row['errors']=['No native run report; exit '+str(receipt['exitCode'])];return row
    run=read(path);row['reportSha256']=sha(path)
    assert run['manifestSha256']==entry['manifestSha256'] and graph_hash(Path(manifest['kernelPath']))==run['kernelSha256']
    assert datetime.fromisoformat(d['declaredUtc'])<datetime.fromisoformat(run['startedUtc'])
    assert datetime.fromisoformat(receipt['processStartedUtc'])<=datetime.fromisoformat(run['startedUtc']) and datetime.fromisoformat(receipt['processExitedUtc'])>=datetime.fromisoformat(run['finishedUtc'])
    assert run['device']['adapterName']=='AMD Radeon AI PRO R9700' and run['device']['driverVersion']=='32.0.31041.1004'
    jsonschema.Draft202012Validator(read(repo/'schemas/manifest.schema.v3.json')).validate(manifest)
    jsonschema.Draft202012Validator(read(repo/'schemas/checkpoint.schema.v2.json')).validate(read(folder/'checkpoint.json'))
    ev=run['pairedEvidence'];obs=ev['observations'];byid={c['candidateId']:c for c in run['candidates']}
    assert sorted([c['defines'] for c in byid.values()],key=lambda x:json.dumps(x,sort_keys=True))==expand(manifest)
    assert ev['options']==manifest['pairedMeasurement'] and ev['maximumCoefficientOfVariation']==.05 and ev['minimumRequiredSpeedup']==1.01
    expected_count=32*(max(1,len(byid)-1)+1);assert len(obs)==expected_count
    row.update(observations=len(obs),sessionId=ev['sessionId'],device=run['device'],baseline=run['baselineCandidateId'],selected=ev['selectedAfterCalibration'],selectedBits=byid[ev['selectedAfterCalibration']]['defines']['HLSLPERF_RADIX_BITS'],deployable=ev['deployable'],rejections=ev['rejections'])
    failed=[r for r in obs if not r['result']['compiled'] or not (r['result'].get('correctness') or {}).get('passed') or r['result']['error'] or not r['scenario']]
    if failed:
        row['errors']=sorted({r['result'].get('error') or r['result'].get('compilerDiagnostics') or 'Correctness/scenario failure' for r in failed})
        assert not ev['deployable'];return row
    common=basic(path);row['poisonOutputChecks']=common['poisonOutputChecks']
    expected=[];options=ev['options']
    ids=[cid for cid in byid if cid!=run['baselineCandidateId']] or [run['baselineCandidateId']]
    for phase,cids in [('calibration',ids),('confirmation',[ev['selectedAfterCalibration']])]:
        for block in range(8):
            pos=0
            for cid in sorted(cids,key=lambda x:hashlib.sha256(f"{options['orderSeed']}/{phase}/{block}/{x}".encode()).hexdigest()):
                abba=int(hashlib.sha256(f"order/{options['orderSeed']}/{phase}/{block}/{cid}".encode()).hexdigest()[:2],16)%2==0
                for offset in range(4):
                    isbase=(offset in (0,3))==abba
                    expected.append(dict(phase=phase,block=block,position=pos,challengerId=cid,candidateId=run['baselineCandidateId'] if isbase else cid,isBaseline=isbase,order='ABBA' if abba else 'BAAB',orderSeed=options['orderSeed'],inputSeed=options[phase+'SeedStart']+block*options['residentSlots']));pos+=1
    assert [r['slot'] for r in obs]==expected
    blocks=defaultdict(list);seeds=defaultdict(set);digests=defaultdict(set);batches=[]
    binaries={str(Path(f['path']).resolve()).lower():f['sha256'] for f in d['binaries']}
    for r in obs:
        slot=r['slot'];result=r['result'];scenario=r['scenario'];assert result['measuredDispatchesPerBatch']==18 and result['samplesMilliseconds'][0]*18>=.25
        blocks[(slot['phase'],slot['challengerId'],slot['block'])].append(r)
        for s in scenario['slots']:
            seeds[slot['phase']].add(s['inputSeed']);digests[slot['phase']].add(s['inputSha256'])
            assert len(s['expectedOutputs'])==(2 if manifest['fixedDefines']['HLSLPERF_RADIX_PAIRS'] else 1)
        for f in scenario['nativeCompilerBinaries']:assert binaries[str(Path(f['path']).resolve()).lower()]==f['sha256']
        batches.append(dict(phase=slot['phase'],block=slot['block'],position=slot['position'],candidate=slot['candidateId'],isBaseline=slot['isBaseline'],repetitions=18,totalGpuMs=result['samplesMilliseconds'][0]*18,amortizedPlanMs=result['samplesMilliseconds'][0]))
    comparisons=[]
    eligible_calibration=sorted((c for c in ev['calibration'] if c['passed']),key=lambda c:(-c['geometricMeanSpeedup'],c['candidateId']))
    assert ev['selectedAfterCalibration']==(eligible_calibration[0]['candidateId'] if eligible_calibration else run['baselineCandidateId'])
    for paired in blocks.values():
        reference=paired[0]['scenario']
        for observation in paired:
            scenario=observation['scenario']
            for key in ('device','sourceSha256','workloadImplementationSha256','backendAssemblySha256','dynamicExecutorSha256','cachePolicy','timingScope','nativeCompilerBinaries'):
                assert scenario[key]==reference[key]
            assert [(s['slot'],s['inputSeed'],s['expectedOutputs']) for s in scenario['slots']]==[(s['slot'],s['inputSeed'],s['expectedOutputs']) for s in reference['slots']]
        for arm in (False,True):
            assert len({r['inputSha256'] for r in paired if r['slot']['isBaseline']==arm})==1
    for phase,c in [('calibration',c) for c in ev['calibration']]+[('confirmation',ev['confirmation'])]:
        aa=[];bb=[];geos=[];local=[]
        for k in range(8):
            rows=blocks[(phase,c['candidateId'],k)];a=[r['result']['samplesMilliseconds'][0] for r in rows if r['slot']['isBaseline']];b=[r['result']['samplesMilliseconds'][0] for r in rows if not r['slot']['isBaseline']]
            aa+=a;bb+=b;geos.append(math.sqrt(a[0]*a[1]));local.append(max(a)/min(a)-1)
        drift=max(max(local),max(geos)/min(geos)-1);acv=statistics.stdev(aa)/statistics.mean(aa);bcv=statistics.stdev(bb)/statistics.mean(bb)
        assert math.isclose(drift,c['baselineDrift'],abs_tol=1e-12)
        allowed=drift<=.15 and max(acv,bcv)<=.05
        if c['candidateId']!=run['baselineCandidateId']:allowed &= c['lower95Speedup']>=1.01 and percentile(bb,.95)<=percentile(aa,.95)
        assert not c['passed'] or allowed
        comparisons.append(dict(phase=phase,candidate=c['candidateId'],radixBits=byid[c['candidateId']]['defines']['HLSLPERF_RADIX_BITS'],geometricSpeedup=c['geometricMeanSpeedup'],lower95=c['lower95Speedup'],upper95=c['upper95Speedup'],drift=drift,baselineCv=acv,candidateCv=bcv,passed=c['passed'],rejections=c['rejections'],baselineLatency=latency(aa),candidateLatency=latency(bb)))
    ca=[r['result']['samplesMilliseconds'][0] for r in obs if r['slot']['phase']=='calibration' and r['slot']['isBaseline']];co=[r['result']['samplesMilliseconds'][0] for r in obs if r['slot']['phase']=='confirmation' and r['slot']['isBaseline']]
    between=abs(statistics.median(co)/statistics.median(ca)-1);calcv=statistics.stdev(ca)/statistics.mean(ca)
    if between>.15 or calcv>.05:assert not ev['deployable']
    if ev['deployable']:assert ev['confirmation']['passed'] and ev['independentInputs'] and not ev['rejections']
    costs=[]
    n=manifest['workItemCount'];pairs=manifest['fixedDefines']['HLSLPERF_RADIX_PAIRS']
    for cid,c in byid.items():
        assert c['compiled'] and c['correctness']['passed'] and c['error'] is None
        sc=next(r['scenario'] for r in obs if r['slot']['candidateId']==cid);slot=sc['slots'][0];bits=c['defines']['HLSLPERF_RADIX_BITS']
        costs.append(dict(candidate=cid,defines=c['defines'],radixBits=bits,digitPasses=32//bits,dispatchPasses=slot['passCount'],logicalBytesPerSlot=slot['logicalBytes'],scratchBytesPerSlot=slot['logicalBytes']-n*(8 if pairs else 4)-n*(8 if pairs else 4),logicalBytesPerArm=sc['logicalBufferBytes'],committedBytesPerArm=sc['committedAllocationBytes']))
    row.update(supported=True,seedsets={k:sorted(v) for k,v in seeds.items()},digestsets={k:sorted(v) for k,v in digests.items()},comparisons=comparisons,confirmation=comparisons[-1],betweenPhaseDrift=between,calibrationBaselineCv=calcv,candidateSummaryStable=all(c['stable'] for c in byid.values()),costs=costs,batches=batches)
    return row

def preflight_gate(rows):
    assert len(rows)==5 and {r['round'] for r in rows}==set(range(1,6))
    supported=all(r['supported'] for r in rows);medians=[r['confirmation']['baselineLatency']['medianMs'] for r in rows] if supported else []
    cv=statistics.stdev(medians)/statistics.mean(medians) if medians else None;drift=max(medians)/min(medians)-1 if medians else None
    individual=[r['supported'] and r['deployable'] and r['candidateSummaryStable'] and all(c['passed'] for c in r['comparisons']) for r in rows]
    eligible=supported and all(individual) and cv<=.05 and drift<=.15
    reasons=[]
    if not supported:reasons.append('Native compilation/correctness/allocation/execution evidence unavailable or failed')
    if not all(individual):reasons.append('At least one of five baseline calibration/confirmation/stability gates failed')
    if cv is not None and cv>.05:reasons.append('Cross-process baseline median CV exceeded 0.05')
    if drift is not None and drift>.15:reasons.append('Cross-process baseline median range drift exceeded 0.15')
    return dict(cell=rows[0]['cell'],eligible=eligible,status='eligible' if eligible else 'inconclusive' if supported else 'unsupported_or_failed',processes=5,individualPassed=sum(individual),processMedianCv=cv,processMedianDrift=drift,baselineMediansMs=medians,selfControlInterval=interval([r['confirmation']['geometricSpeedup'] for r in rows],True) if supported else None,reasons=reasons)

def comparison_summary(rows):
    assert len(rows)==5 and {r['round'] for r in rows}==set(range(1,6))
    supported=all(r['supported'] for r in rows);same=supported and len({r['selected'] for r in rows})==1
    ci=interval([r['confirmation']['geometricSpeedup'] for r in rows],True) if same else None
    accepted=same and rows[0]['selectedBits']!=1 and all(r['deployable'] for r in rows) and ci['lower95']>=1.01
    tails={arm:{q:interval([r['confirmation'][arm][q] for r in rows]) for q in ('meanMs','medianMs','p95Ms','p99Ms','maxMs')} for arm in ('baselineLatency','candidateLatency')} if same else {}
    return dict(cell=rows[0]['cell'],status='confirmed_gain' if accepted else 'inconclusive' if supported else 'unsupported_or_failed',accepted=accepted,processes=5,selectedBits=[r.get('selectedBits') for r in rows],individualPassed=sum(r.get('deployable',False) and r['supported'] for r in rows),speedup=ci,confirmationLatency=tails,costs=rows[0].get('costs',[]))

def main():
    p=argparse.ArgumentParser();p.add_argument('repo',type=Path);p.add_argument('root',type=Path);p.add_argument('stage',choices=['preflight','comparison']);p.add_argument('output',type=Path);p.add_argument('--partial',action='store_true');p.add_argument('--seal-entry',action='store_true');args=p.parse_args()
    repo=args.repo.resolve();root=args.root.resolve();d=read(root/'declaration.json')
    sys.path.insert(0,str(repo/'tools'))
    from validate_vnext_evidence import audit as basic
    from analyze_scan_process_matrix import graph_hash
    for f in d['sources']+d['binaries']:assert sha(Path(f['path']))==f['sha256']
    assert sha(Path(d['protocolPath']))==d['protocolSha256']
    schedule=d['schedules'][args.stage];not_applicable=[]
    if args.stage=='comparison':
        entry=read(root/'entry-ledger.json');assert entry['auditPassed'] and entry['complete'] and entry['declarationSha256']==sha(root/'declaration.json')
        for f in entry['preflightReports']:assert sha(Path(f['path']))==f['sha256']
        not_applicable=[dict(cell=g['cell'],status='not_applicable',reason=g['reasons'],plannedProcesses=5,executedProcesses=0) for g in entry['cells'] if not g['eligible']]
        for c in not_applicable:assert not any((root/r['name']).exists() for r in schedule if r['cell']==c['cell'])
        schedule=[r for r in schedule if r['cell'] in entry['eligibleCells']]
    rows=[];pending=[];failures=[];seeds=set();digests=set();sessions=set();processes=set();groups=defaultdict(list);previous=None
    if args.stage=='comparison':
        for f in entry['preflightReports']:
            if Path(f['path']).name!='run.json':continue
            prior=read(Path(f['path']))
            for observation in prior['pairedEvidence']['observations']:
                if observation.get('scenario'):
                    for slot in observation['scenario']['slots']:
                        seeds.add(slot['inputSeed']);digests.add(slot['inputSha256'])
    for e in schedule:
        receipt=root/e['name']/'execution.json'
        if not receipt.exists() or read(receipt)['status'] in ('queued','running'):pending.append(e['name']);continue
        try:
            row=audit_process(root,e,d,repo,basic,graph_hash)
            identity=(row['pid'],row['processStartedUtc']);assert identity not in processes;processes.add(identity)
            if previous:assert datetime.fromisoformat(row['processStartedUtc'])>=previous
            previous=datetime.fromisoformat(row['processExitedUtc'])
            if row.get('sessionId'):assert row['sessionId'] not in sessions;sessions.add(row['sessionId'])
            rs=set(v for vs in row['seedsets'].values() for v in vs);rd=set(v for vs in row['digestsets'].values() for v in vs)
            assert not rs.intersection(seeds) and not rd.intersection(digests);seeds.update(rs);digests.update(rd)
            rows.append(row);groups[row['cell']].append(row)
        except Exception as error:failures.append(dict(name=e['name'],error=repr(error)))
    cells=[(preflight_gate if args.stage=='preflight' else comparison_summary)(sorted(groups[c['name']],key=lambda r:r['round'])) for c in d['cells'] if len(groups[c['name']])==5]
    complete=not pending and not failures and len(rows)==len(schedule)
    result=dict(schema='hlslperf.radix-process-audit.v1',stage=args.stage,complete=complete,passed=not failures and (complete or args.partial),sourceSha=d['sourceSha'],declarationSha256=sha(root/'declaration.json'),plannedProcesses=len(schedule),processes=len(rows),observations=sum(r['observations'] for r in rows),poisonOutputChecks=sum(r['poisonOutputChecks'] for r in rows),supportedProcesses=sum(r['supported'] for r in rows),cells=cells,notApplicable=not_applicable,pending=pending,failures=failures,processResults=rows)
    args.output.parent.mkdir(parents=True,exist_ok=True);write(args.output,result)
    if args.seal_entry:
        assert args.stage=='preflight' and complete and len(rows)==80 and len(cells)==16 and not args.partial
        path=root/'entry-ledger.json';assert not path.exists(),'Never overwrite an entry decision'
        evidence=[f for e in schedule for f in (root/e['name']/'execution.json',root/e['name']/'run.json') if f.exists()]
        write(path,dict(createdUtc=datetime.now(timezone.utc).isoformat(),complete=True,auditPassed=True,declarationSha256=sha(root/'declaration.json'),auditPath=str(args.output.resolve()),auditSha256=sha(args.output),auditScriptSha256=sha(Path(__file__)),eligibleCells=[c['cell'] for c in cells if c['eligible']],cells=cells,preflightReports=[dict(path=str(f),sha256=sha(f)) for f in evidence]))
    print(json.dumps({k:v for k,v in result.items() if k not in ('processResults','pending','cells','notApplicable')},indent=2))
    print(json.dumps(cells,indent=2))
    return 0 if result['passed'] else 1

if __name__=='__main__':sys.exit(main())
