"""Supplement the frozen statistics with source, binary, oracle and accounting checks."""
import argparse
import collections
import datetime as dt
import hashlib
import json
from pathlib import Path


def sha(path): return hashlib.sha256(path.read_bytes()).hexdigest()
def utc(value): return dt.datetime.fromisoformat(value.replace('Z','+00:00'))


def audit(repo, raw, binary_lock_path, output):
    assert not output.exists()
    declaration_path=repo/'docs/integration/unified-declaration.json'
    declaration=json.loads(declaration_path.read_text()); declaration_hash=sha(declaration_path)
    lock=json.loads(binary_lock_path.read_text()); lock_hash=sha(binary_lock_path)
    for relative,expected in declaration['sources'].items(): assert sha(repo/relative)==expected,relative
    runtime=binary_lock_path.parent/'artifacts/bin/HlslPerf.UnifiedBench/release'
    binary_files={str((runtime/item['path']).resolve()):item['sha256'] for item in lock['files']}
    for path,expected in binary_files.items(): assert sha(Path(path))==expected,path
    compiler_identities={}; file_hashes={}; devices=set(); counts=collections.Counter(); times=[]; source_checks=0
    for cell in declaration['cells']:
        for process_index in range(1,6):
            folder=raw/f"{cell['id']}-p{process_index}"
            record=json.loads((folder/'process.json').read_text()); receipt=json.loads(Path(str(folder)+'.execution.json').read_text())
            assert record['completed'] and not record['developmentOnly'] and not record['errors']
            assert receipt['sourceSha']==lock['sourceSha'] and receipt['binaryLockSha256']==lock_hash
            assert receipt['declarationSha256']==record['declarationSha256']==declaration_hash
            assert utc(receipt['startedUtc'])>=utc(lock['createdUtc'])
            times.append((receipt['startedUtc'],receipt['exitedUtc']))
            assert record['deviceRemovalStatus']=='00000000'
            devices.add(json.dumps(record['device'],sort_keys=True))
            assert {str(Path(b['path']).resolve()):b['sha256'] for b in receipt['binaries']}==binary_files
            for compiled in record['compilation']:
                shader=compiled['shader']; identity=compiled['identitySha256']; name=shader['id']
                assert compiler_identities.setdefault(name,identity)==identity,name
                if name.startswith('gps-'):
                    assert shader['hlslVersion']==2021 and shader['shaderModel']=='6_7' and not shader['enableStrictness']
                elif name.startswith('amd/') and '/input-soa-adapter' not in name:
                    assert shader['hlslVersion']==2021 and shader['shaderModel']=='6_6' and not shader['enableStrictness']
                    assert shader['defines']['FFX_HALF']=='0' and shader['defines']['FFX_HLSL_SM']=='66'
                else: assert shader['hlslVersion']==2018 and shader['shaderModel']=='6_6' and shader['enableStrictness']
                for item in compiled['files']:
                    path=item['path']
                    if path not in file_hashes:file_hashes[path]=sha(Path(path))
                    assert file_hashes[path]==item['sha256'];source_checks+=1
                assert len(compiled['dxilSha256'])==64
            assert len({f['inputSha256'] for f in record['fixtures']})==cell['slots']
            for check in record['checks']:
                fixture=record['fixtures'][check['slot']]
                expected={fixture['expectedKeysSha256'],fixture['expectedPayloadsSha256']}-{None}
                verification=check['verification']
                assert len(verification)==2*(2 if cell['pairs'] else 1)
                assert len({(v['resource'],v['attempt']) for v in verification})==len(verification)
                for v in verification:
                    assert v['expectedSha256'] in expected
                    assert (v['attempt'],v['poisonByte']) in [(1,165),(2,90)]
                    assert v['passed']==(v['expectedSha256']==v['actualSha256'])
                    counts['fullOutputChecks']+=1;counts['passingOutputChecks' if v['passed'] else 'failingOutputChecks']+=1
                counts['correctnessOperations']+=2
            for arm,status in record['statuses'].items():
                counts['armProcesses']+=1;counts[status]+=1
                arm_warmup=[w for w in record['warmup'] if w['arm']==arm]
                assert len(arm_warmup)==(8 if status=='measured_correct' else 0)
                for w in arm_warmup:
                    t=w['timing'];assert t['repetitions']==6 and t['slotExecutions']==[6//cell['slots']]*cell['slots']
                    assert t['timestampMarkers']==32;counts['warmupOperations']+=6
            for observation in record['observations']:
                counts['plannedObservations']+=1
                if observation['status']!='measured':
                    assert record['statuses'][observation['arm']]=='correctness_failed'
                    counts['notTimedObservations']+=1;continue
                t=observation['timing'];assert t['repetitions']==18 and t['timestampMarkers']==92
                assert t['slotExecutions']==[18//cell['slots']]*cell['slots']
                assert len(t['gpuOperationMilliseconds'])==18 and min(t['gpuOperationMilliseconds'])>0
                stage_sum=sum(t[key] for key in ['gpuInputRestoreMilliseconds','gpuScratchInitializationMilliseconds','gpuAlgorithmMilliseconds','gpuOutputConversionMilliseconds'])
                assert 0<=stage_sum<=t['gpuTotalMilliseconds']+1e-8
                assert abs(sum(t['gpuOperationMilliseconds'])-stage_sum)<1e-6
                counts['measuredObservations']+=1;counts['measuredOperations']+=18
            counts['independentProcesses']+=1
    assert len(devices)==1 and counts['independentProcesses']==120
    module_probe=json.loads((binary_lock_path.parent/'loaded-native-modules.json').read_text())
    for module in module_probe['modules']:
        if module['name'] in ['dxcompiler.dll','dxil.dll']:
            assert binary_files[str(Path(module['path']).resolve())]==module['sha256']
            assert module['fileVersion']=='1.9.2602.17'
    result=dict(schema='hlslperf.unified-supplemental-audit.v1',passed=True,sourceSha=lock['sourceSha'],
        declarationSha256=declaration_hash,binaryLockSha256=lock_hash,counts=dict(counts),device=json.loads(next(iter(devices))),
        compiledShaderIdentities=compiler_identities,uniqueSourceFilesChecked=len(file_hashes),sourceHashChecks=source_checks,
        nativeModuleProbe=module_probe,firstProcessStartedUtc=min(t[0] for t in times),lastProcessExitedUtc=max(t[1] for t in times),
        note='Source/binary/oracle/accounting audit added after sampling; frozen sampling and statistical rules are unchanged.')
    output.write_text(json.dumps(result,indent=2)+'\n',encoding='utf-8');print(json.dumps(dict(passed=True,counts=dict(counts)),indent=2))


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('raw',type=Path);parser.add_argument('binary_lock',type=Path);parser.add_argument('output',type=Path)
    parser.add_argument('--repo',type=Path,default=Path(__file__).resolve().parents[1]);a=parser.parse_args()
    audit(a.repo.resolve(),a.raw,a.binary_lock,a.output)
