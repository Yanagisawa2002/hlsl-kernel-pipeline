"""Verify the fixed confirmation's source/binary/oracle/operation receipts."""
import argparse
import json
from pathlib import Path
from unified_protocol import digest


def audit(repo, root, output):
    build=json.loads((root/'formal-build-01/build.json').read_text())
    binary=Path(build['binaryLock']); lock=json.loads(binary.read_text())
    runtime=Path(build['runtime']).parent
    assert lock['sourceSha']==build['sourceSha']
    for f in lock['files']: assert digest(runtime/f['path'])==f['sha256']
    declaration=repo/'docs/integration/focused-cost-declaration.json'
    d=json.loads(declaration.read_text())
    for path, expected in d['sources'].items(): assert digest(repo/path)==expected, path
    totals=dict(processes=0,armProcesses=0,outputChecks=0,measuredBatches=0,measuredOperations=0,warmupOperations=0)
    shaders={}; source_files={}; devices=set(); receipts=[]
    for item in d['processOrder']:
        folder=root/'formal-01'/f"{item['cell']}-p{item['process']}"
        p=json.loads((folder/'process.json').read_text()); r=json.loads(Path(str(folder)+'.execution.json').read_text())
        assert p['completed'] and not p['errors'] and not p['developmentOnly'] and not r['developmentOnly']
        assert r['sourceSha']==build['sourceSha'] and r['binaryLockSha256']==digest(binary) and r['exitCode']==0
        assert all(s=='measured_correct' for s in p['statuses'].values())
        assert r['pid']==p['pid'] and p['declarationSha256']==digest(declaration)==r['declarationSha256']
        receipts.append(dict(pid=p['pid'],sourceSha=r['sourceSha'],command=r['command'],rawSha256=digest(folder/'process.json')))
        devices.add(json.dumps(p['device'],sort_keys=True))
        totals['processes']+=1; totals['armProcesses']+=len(p['statuses'])
        for f in r['binaries']: assert digest(Path(f['path']))==f['sha256']
        for compilation in p['compilation']:
            shader=compilation['shader']; sid=shader['id']; defines=shader['defines']
            assert defines.get('HLSLPERF_SCAN_DIAGNOSTIC_COUNTERS','0')=='0'
            assert defines.get('HLSLPERF_RADIX_RANK_BALLOT','0')==('1' if sid.startswith('internal-radix-8-ballot/') else '0')
            identity=(compilation['identitySha256'],compilation['dxilSha256'])
            assert shaders.get(sid,identity)==identity; shaders[sid]=identity
            for f in compilation['files']:
                assert digest(Path(f['path']))==f['sha256'];source_files[f['path']]=f['sha256']
        for check in p['checks']:
            fixture=p['fixtures'][check['slot']]
            for v in check['verification']:
                assert v['passed'] and v['actualSha256']==v['expectedSha256']
                assert v['expectedSha256'] in [fixture['expectedKeysSha256'],fixture.get('expectedPayloadsSha256')]
                assert v['poisonByte']==(165 if v['attempt']==1 else 90)
                totals['outputChecks']+=1
        for o in p['observations']:
            assert o['status']=='measured';t=o['timing']
            assert t['timestampMarkers']==92 and t['repetitions']==18 and t['residentSlots']==1
            totals['measuredBatches']+=1;totals['measuredOperations']+=t['repetitions']
        totals['warmupOperations']+=sum(w['timing']['repetitions'] for w in p['warmup'])
    assert totals==dict(processes=10,armProcesses=25,outputChecks=160,measuredBatches=600,measuredOperations=10800,warmupOperations=1200)
    assert len(devices)==1
    c=json.loads((root/'candidate-correctness-01/correctness.json').read_text())
    assert c['completed'] and c['testsPassed'] and not c['errors'] and len(c['results'])==90
    assert all(v['passed'] and v['actualSha256']==v['expectedSha256'] for r in c['results'] for v in r['verification'])
    result=dict(schema='hlslperf.focused-provenance-audit.v1',audited=True,measurementSourceSha=build['sourceSha'],
        declarationSha256=digest(declaration),binaryLockSha256=digest(binary),totals=totals,candidateCorrectnessCases=90,
        candidateCorrectnessOutputChecks=sum(len(r['verification']) for r in c['results']),
        device=json.loads(next(iter(devices))),shaderIdentities=shaders,shaderSourceFiles=source_files,receipts=receipts)
    assert not output.exists();output.write_text(json.dumps(result,indent=2)+'\n')
    print(json.dumps(totals))


if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('repo',type=Path);p.add_argument('root',type=Path);p.add_argument('output',type=Path)
    a=p.parse_args();audit(a.repo,a.root,a.output)
