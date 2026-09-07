"""Freeze all preflight and conditional Radix comparison inputs before sampling."""
import argparse
import sys
sys.dont_write_bytecode = True
import copy
import hashlib
from pathlib import Path
from datetime import datetime, timezone
from declare_scan_process_matrix import read, write, sha, git, REPO

def make_manifest(cell,stage,repeat):
    m=copy.deepcopy(read(REPO/'manifests'/('radix-wide-pairs.json' if cell['pairs'] else 'radix-wide.json')))
    seed=(100000000 if stage=='preflight' else 300000000)+cell['index']*1000000+(repeat-1)*10000
    control=dict(HLSLPERF_GROUP_SIZE=128 if cell['pairs'] else 256,HLSLPERF_ELEMENTS_PER_THREAD=4,HLSLPERF_WAVE_SIZE=64 if cell['pairs'] else 32)
    wide=dict(HLSLPERF_GROUP_SIZE=128,HLSLPERF_ELEMENTS_PER_THREAD=2,HLSLPERF_WAVE_SIZE=32)
    bits=[1] if stage=='preflight' else [1,4,8] if cell['include4bit'] else [1,8]
    axes=[dict(name='HLSLPERF_RADIX_BITS',values=bits)]
    for name in control:
        axes.append(dict(name=name,values=sorted({control[name]} if stage=='preflight' else {control[name],wide[name]})))
    constraints=[{'if':{'HLSLPERF_RADIX_BITS':[1]},'then':{k:[v] for k,v in control.items()}}]
    if stage=='comparison':constraints.append({'if':{'HLSLPERF_RADIX_BITS':[b for b in bits if b!=1]},'then':{k:[v] for k,v in wide.items()}})
    m.update(name=f"{stage}-{cell['name']}-process{repeat}",kernelPath=str(REPO/'kernels/radix-sort.hlsl'),kernelAbiVersion='hlslperf.raw-buffer.v2',shaderModel='6_6',workItemCount=cell['elements'],
        measurementProtocol='gpu-paired-abba-independent-confirmation-v2',warmupDispatches=4,minimumWarmupMilliseconds=25,measurementBatches=8,dispatchesPerBatch=18,maximumDispatchesPerBatch=18,minimumBatchMilliseconds=.25,maximumCoefficientOfVariation=.05,minimumRequiredSpeedup=1.01,
        fixedDefines=dict(HLSLPERF_SCAN_OPERATOR=1,HLSLPERF_RADIX_PAIRS=int(cell['pairs']),HLSLPERF_SCAN_BACKEND=2,HLSLPERF_VECTOR_WIDTH=1),baselineDefines=dict(HLSLPERF_RADIX_BITS=1),axes=axes,constraints=constraints,correctness=dict(kind='cpu-oracle',seed=seed),
        pairedMeasurement=dict(calibrationBlocks=8,confirmationBlocks=8,orderSeed=seed+73019,calibrationSeedStart=seed,confirmationSeedStart=seed+5000,residentSlots=cell['slots'],maximumAllocationBytesPerArm=536870912,maximumBaselineDrift=.15))
    m['workload']['parameters'].update(elementCount=cell['elements'],seed=seed,bitCount=32,keyPattern=2 if cell['distribution']=='duplicate' else 1,keyDomain=1)
    return m

def main():
    import jsonschema
    p=argparse.ArgumentParser();p.add_argument('root',type=Path);p.add_argument('--runtime',type=Path,required=True);args=p.parse_args()
    root=args.root.resolve();runtime=args.runtime.resolve()
    if root.exists():raise ValueError('Never overwrite an existing declaration')
    if git('status','--porcelain'):raise ValueError('Commit clean source before declaration')
    if not (runtime/'hlslperf.dll').exists():raise ValueError('Build a separate Radix runtime first')
    root.mkdir(parents=True)
    validator=jsonschema.Draft202012Validator(read(REPO/'schemas/manifest.schema.v3.json'))
    cells=[];schedules={s:[] for s in ('preflight','comparison')}
    for mi in (1,4):
        for pairs in (False,True):
            for distribution in ('uniform','duplicate'):
                for slots in (1,3):
                    cell=dict(index=len(cells),name=f"radix-{mi}Mi-{'pairs' if pairs else 'keys'}-{distribution}-slots{slots}",elements=mi*1048576,pairs=pairs,distribution=distribution,slots=slots,include4bit=mi==4 and distribution=='uniform' and slots==3)
                    cells.append(cell)
                    for stage in schedules:
                        for repeat in range(1,6):
                            m=make_manifest(cell,stage,repeat);validator.validate(m)
                            path=root/(m['name']+'.manifest.json');write(path,m)
                            schedules[stage].append(dict(name=m['name'],stage=stage,cell=cell['name'],round=repeat,manifestPath=str(path),manifestSha256=sha(path)))
    for stage,rows in schedules.items():rows.sort(key=lambda r:(r['round'],hashlib.sha256(f"radix-next/709127/{stage}/{r['round']}/{r['cell']}".encode()).hexdigest()))
    sources=[s for s in git('ls-files').splitlines() if s.startswith(('src/','kernels/','schemas/','tools/','unity/')) or s in ('Directory.Build.props','Directory.Packages.props','global.json')]
    protocol=REPO/'docs/integration/RADIX_PERFORMANCE_NEXT.md'
    d=dict(schema='hlslperf.radix-process-matrix.v1',declaredUtc=datetime.now(timezone.utc).isoformat(),baselineSha='18c2e19500063b1749a9bb0315e22d3070a47ade',phase2StartSha='f787f741becf7a792c5a39ea86c58361c3ca86cb',sourceSha=git('rev-parse','HEAD'),sourceTree=git('rev-parse','HEAD^{tree}'),cli=str(runtime/'hlslperf.dll'),protocolPath=str(protocol),protocolSha256=sha(protocol),cells=cells,schedules=schedules,processesPerCell=5,
        binaries=[dict(path=str(p),sha256=sha(p)) for p in sorted(runtime.rglob('*')) if p.is_file() and p.suffix in ('.dll','.exe','.json')],sources=[dict(path=str(REPO/s),sha256=sha(REPO/s)) for s in sources],
        entryRule=dict(requireFivePassingCalibrationAndConfirmation=True,maximumProcessMedianCv=.05,maximumProcessMedianDrift=.15,replacementControlAllowed=False),maximumProcesses=dict(preflight=80,comparison=80),scanEvidencePreserved=True)
    write(root/'declaration.json',d)
    print(f"Declared 16 cells, 80 preflight and 80 conditional processes at {root}; source {d['sourceSha']}")

if __name__=='__main__':main()
