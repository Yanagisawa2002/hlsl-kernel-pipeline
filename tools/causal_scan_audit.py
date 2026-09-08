"""Audit fixed Scan diagnostics, actual DXIL, receipts and complete formal counts."""
import argparse
import collections
import json
import math
import re
import statistics as stats
from pathlib import Path
from causal_scan_protocol import check_bytes
from unified_protocol import digest


def run(repo,root,output):
    declaration=repo/'docs/integration/causal-scan-declaration-v2.json';d=json.loads(declaration.read_text())
    check_bytes(repo,declaration)
    original=json.loads((repo/'docs/integration/causal-scan-declaration.json').read_text())
    assert {k:v for k,v in d.items() if k!='sources'}=={k:v for k,v in original.items() if k!='sources'}
    changed=[k for k,v in d['sources'].items() if original['sources'][k]!=v]
    assert changed==['tools/Invoke-UnifiedExperiment.ps1']
    c=json.loads((root/'correctness-01/diagnostic.json').read_text())
    assert c['completed'] and not c['errors'] and c['deviceRemovalStatus']=='00000000'
    assert len(c['results'])==71 and sum(len(r['verification']) for r in c['results'])==142
    assert all(v['passed'] and v['actualSha256']==v['expectedSha256'] for r in c['results'] for v in r['verification'])
    compiled={x['shader']['id']:x['dxilSha256'] for x in c['compilation']};ir=[]
    for e in c['shaderEvidence']:
        assert e['dxilSha256']==compiled[e['shaderId']]==digest(Path(e['dxilPath']))
        assert e['disassemblySha256']==digest(Path(e['disassemblyPath']))
        text=Path(e['disassemblyPath']).read_text();ops=collections.Counter();masks=collections.Counter()
        for line in text.splitlines():
            if 'call' not in line or '@dx.op.' not in line:continue
            name=re.search(r'@dx\.op\.([^ (]+)',line).group(1);ops[name]+=1
            if name.startswith('rawBuffer'):
                mask=re.search(r', i8 (\d+), i32 \d+\)',line).group(1);masks[name+'/mask'+mask]+=1
        ir.append(dict(shader=e['shaderId'],dxilSha256=e['dxilSha256'],staticCallSites=dict(ops),staticIoMasks=dict(masks),
            sharedGlobals=[line for line in text.splitlines() if line.startswith('@') and 'addrspace(3)' in line]))
    scalar=next(x for x in ir if x['shader']=='internal-scan-single/keys/SinglePassScan')
    vector=next(x for x in ir if x['shader']=='internal-scan-single-vector-io/keys/SinglePassScan')
    assert scalar['sharedGlobals']==vector['sharedGlobals']
    for op in ['wavePrefixOp.i32','waveActiveOp.i32','atomicBinOp.i32','atomicCompareExchange.i32','barrier']:
        assert scalar['staticCallSites'][op]==vector['staticCallSites'][op]
    assert compiled['internal-scan-single/keys/ResetSinglePassState']==compiled['internal-scan-single-vector-io/keys/ResetSinglePassState']
    diagnostics=[]
    for index in range(1,4):
        p=json.loads((root/f'diagnostics/process-{index}/diagnostic.json').read_text())
        r=json.loads((root/f'diagnostics/process-{index}.execution.json').read_text())
        assert p['completed'] and not p['errors'] and p['pid']==r['pid'] and r['exitCode']==0
        assert all(v['passed'] for row in p['results'] if 'verification' in row for v in row['verification'])
        for arm in d['cells'][0]['arms']:
            ts=[x['timing'] for x in p['results'] if x['phase']=='whole-timing' and x['arm']==arm]
            assert len(ts)==12 and all(t['repetitions']==18 and t['timestampMarkers']==92 for t in ts)
            times=[t['gpuTotalMilliseconds']/18 for t in ts]
            diagnostics.append(dict(process=index,arm=arm,meanMs=stats.mean(times),cv=stats.stdev(times)/stats.mean(times)))
    build=json.loads((root/'formal-build-02/build.json').read_text());lock=Path(build['binaryLock']);binary=json.loads(lock.read_text())
    assert build['sourceSha']==binary['sourceSha']
    for f in binary['files']:assert digest(Path(build['runtime']).parent/f['path'])==f['sha256']
    totals=collections.Counter();pids=set();devices=set()
    for index in range(1,6):
        folder=root/f'formal-02/scan-8mi-keys-uniform-1slots-p{index}'
        p=json.loads((folder/'process.json').read_text());r=json.loads(Path(str(folder)+'.execution.json').read_text())
        assert p['completed'] and not p['errors'] and not p['developmentOnly'] and not r['developmentOnly']
        assert r['sourceSha']==build['sourceSha'] and r['exitCode']==0 and p['pid']==r['pid']
        assert r['declarationSha256']==p['declarationSha256']==digest(declaration) and r['binaryLockSha256']==digest(lock)
        assert p['deviceRemovalStatus']=='00000000'
        pids.add((p['pid'],r['startedUtc']));devices.add(json.dumps(p['device'],sort_keys=True))
        for f in r['binaries']:assert digest(Path(f['path']))==f['sha256']
        for x in p['compilation']:
            assert x['dxilSha256']==compiled[x['shader']['id']]
            assert x['shader']['defines'].get('HLSLPERF_SCAN_DIAGNOSTIC_COUNTERS','0')=='0'
            for f in x['files']:assert digest(Path(f['path']))==f['sha256']
        for check in p['checks']:
            expected=p['fixtures'][check['slot']]['expectedKeysSha256']
            for v in check['verification']:
                assert v['passed'] and v['expectedSha256']==v['actualSha256']==expected
                assert v['poisonByte']==(165 if v['attempt']==1 else 90)
                totals['fullOutputChecks']+=1
        for o in p['observations']:
            assert o['status']=='measured' and o['timing']['timestampMarkers']==92 and o['timing']['repetitions']==18
            totals['measuredBatches']+=1;totals['measuredOperations']+=18
        totals['warmupOperations']+=sum(x['timing']['repetitions'] for x in p['warmup'])
    assert len(pids)==5 and len(devices)==1 and dict(totals)==dict(fullOutputChecks=60,measuredBatches=360,measuredOperations=6480,warmupOperations=720)
    failed=json.loads((root/'formal-01/scan-8mi-keys-uniform-1slots-p1.execution.json').read_text())
    assert failed['exitCode']==1 and not (root/'formal-01/scan-8mi-keys-uniform-1slots-p1/process.json').exists()
    result=dict(schema='hlslperf.causal-scan-audit.v1',audited=True,measurementSha=build['sourceSha'],declarationSha256=digest(declaration),
        binaryLockSha256=digest(lock),formalTotals=dict(totals),independentProcesses=5,device=json.loads(next(iter(devices))),
        correctnessCases=68,mechanismCases=3,correctnessOutputChecks=142,diagnosticSummaries=diagnostics,dxilInventory=ir,
        sameDxilAcrossDiagnosticsAndFormal=True,failedFormalLaunchHasNoMeasurements=True,v2ChangedSourcePaths=changed,
        limitations=['DXIL static sites include tails, not runtime path counts or hardware ISA. Full 8Mi blocks take four vector IO sites.',
            'Extra logical/LDS bytes zero; register allocation and scheduling unmeasured; neighboring-lane address stride remains64 bytes.',
            'Development timings are separate from formal statistical inference; all stability failures remain.'])
    assert not output.exists();output.write_text(json.dumps(result,indent=2)+'\n');print(json.dumps(dict(audited=True,formalTotals=dict(totals))))


if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('repo',type=Path);p.add_argument('root',type=Path);p.add_argument('output',type=Path)
    a=p.parse_args();run(a.repo,a.root,a.output)
