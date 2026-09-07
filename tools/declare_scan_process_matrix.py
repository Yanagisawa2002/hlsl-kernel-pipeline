"""Freeze the bounded next-performance Scan experiment before native sampling."""
import argparse
import copy
import hashlib
import json
import subprocess
from datetime import datetime, timezone
from pathlib import Path

REPO = Path(__file__).resolve().parents[1]

def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()

def read(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))

def write(path, value):
    path.write_text(json.dumps(value, indent=2)+'\n', encoding='utf-8')

def git(*args):
    return subprocess.check_output(['git', *args], cwd=REPO, text=True).strip()

def main():
    import jsonschema
    parser=argparse.ArgumentParser()
    parser.add_argument('evidence_root', type=Path)
    args=parser.parse_args(); root=args.evidence_root.resolve()
    if root.exists():
        raise ValueError('Use a new evidence root; never replace a declaration')
    if git('status','--porcelain'):
        raise ValueError('Commit a clean source before declaring')
    cli=REPO/'src/HlslPerf.Cli/bin/Release/net10.0/hlslperf.dll'
    smoke=REPO/'tools/HlslPerf.ScanCorrectness/bin/Release/net10.0/HlslPerf.ScanCorrectness.dll'
    if not cli.exists() or not smoke.exists():
        raise ValueError('Build the frozen CLI and correctness harness first')
    root.mkdir(parents=True)
    template=read(REPO/'manifests/scan.json')
    validator=jsonschema.Draft202012Validator(read(REPO/'schemas/manifest.schema.v3.json'))
    cells=[]; schedule=[]; correctness=[]
    for idx,(mi,slots) in enumerate((m,s) for m in (4,8,12,16) for s in (1,3)):
        cell=f'scan-{mi}Mi-slots{slots}'
        cells.append(dict(name=cell,elements=mi*1048576,slots=slots))
        for repeat in range(1,6):
            seed=60000000+idx*100000+(repeat-1)*10000
            name=f'{cell}-process{repeat}'
            m=copy.deepcopy(template)
            m.update(name=name,kernelPath=str(REPO/'kernels/scan.hlsl'),shaderModel='6_6',
                workItemCount=mi*1048576,warmupDispatches=4,minimumWarmupMilliseconds=25,
                measurementBatches=8,dispatchesPerBatch=36,maximumDispatchesPerBatch=36,
                minimumBatchMilliseconds=0.25,maximumCoefficientOfVariation=0.05,
                minimumRequiredSpeedup=1.01,measurementProtocol='gpu-paired-abba-independent-confirmation-v2',
                fixedDefines=dict(HLSLPERF_GROUP_SIZE=256,HLSLPERF_ELEMENTS_PER_THREAD=4,HLSLPERF_VECTOR_WIDTH=1,HLSLPERF_SCAN_OPERATOR=1),
                baselineDefines=dict(HLSLPERF_SCAN_BACKEND=1),constraints=[],
                axes=[dict(name='HLSLPERF_SCAN_BACKEND',values=[1,2,3]),
                      dict(name='HLSLPERF_WAVE_SIZE',values=[32],when=dict(HLSLPERF_SCAN_BACKEND=[2,3])),
                      dict(name='HLSLPERF_SINGLE_PASS_ITEMS_SCALE',values=[4],when=dict(HLSLPERF_SCAN_BACKEND=[3])),
                      dict(name='HLSLPERF_SINGLE_PASS_GROUPS',values=[256],when=dict(HLSLPERF_SCAN_BACKEND=[3]))],
                correctness=dict(kind='cpu-oracle',seed=seed),
                pairedMeasurement=dict(calibrationBlocks=8,confirmationBlocks=8,orderSeed=seed+73019,
                    calibrationSeedStart=seed,confirmationSeedStart=seed+5000,residentSlots=slots,
                    maximumAllocationBytesPerArm=536870912,maximumBaselineDrift=0.15))
            m['workload']['parameters'].update(elementCount=mi*1048576,seed=seed)
            validator.validate(m)
            path=root/(name+'.manifest.json');write(path,m)
            schedule.append(dict(name=name,cell=cell,round=repeat,manifestPath=str(path),manifestSha256=sha(path)))
            if repeat==1:
                offset={4:3,8:127,12:1,16:4095}[mi]
                correctness.append(dict(name=f'correctness-{mi}Mi-plus{offset}-slots{slots}',
                    manifestPath=str(path),elementCount=mi*1048576+offset,seeds=list(range(90000000+idx*100,90000000+idx*100+slots))))
    schedule.sort(key=lambda c:(c['round'],hashlib.sha256(f"scan-next/613779/{c['round']}/{c['cell']}".encode()).hexdigest()))
    binaries={p for base in [cli.parent,smoke.parent] for p in base.rglob('*') if p.is_file() and p.suffix in ('.dll','.exe','.json')}
    code=[p for p in git('ls-files').splitlines() if p.startswith(('src/','kernels/','schemas/','tools/','unity/')) or p in ('Directory.Build.props','Directory.Packages.props','global.json')]
    declaration=dict(schema='hlslperf.scan-process-matrix.v1',declaredUtc=datetime.now(timezone.utc).isoformat(),
        baselineSha='18c2e19500063b1749a9bb0315e22d3070a47ade',sourceSha=git('rev-parse','HEAD'),sourceTree=git('rev-parse','HEAD^{tree}'),
        cli=str(cli),correctnessHarness=str(smoke),cells=cells,processesPerCell=5,schedule=schedule,correctnessCells=correctness,
        binaries=[dict(path=str(p),sha256=sha(p)) for p in sorted(binaries)],
        sources=[dict(path=str(REPO/p),sha256=sha(REPO/p)) for p in code],
        protocolPath=str(REPO/'docs/integration/SCAN_PERFORMANCE_NEXT.md'),protocolSha256=sha(REPO/'docs/integration/SCAN_PERFORMANCE_NEXT.md'),
        processStatistics=dict(unit='independent native process',weight='equal',confidence=.95,df=4,critical=2.7764451051977987,minimumSpeedup=1.01,requireAllFiveIndividualGates=True),
        radixStatus='conditional; no second-phase GPU work authorized until parent dispatch',refinement='At most one separately declared round under the protocol trigger')
    write(root/'declaration.json',declaration)
    print(json.dumps(dict(declaration=str(root/'declaration.json'),sourceSha=declaration['sourceSha'],processes=len(schedule),correctnessScenarios=24,binaryFiles=len(binaries)),indent=2))

if __name__=='__main__':
    main()
