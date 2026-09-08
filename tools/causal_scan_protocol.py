"""Freeze/audit the one-cell Scan IO-granularity confirmation with byte-accurate sources."""
import argparse
import itertools
import json
import random
import subprocess
from pathlib import Path
from unified_protocol import analyze,digest

SOURCE_PATHS = ['src/HlslPerf.Workloads/UnifiedWorkloads.cs','src/HlslPerf.Workloads/CausalScanWorkloads.cs',
    'src/HlslPerf.D3D12/D3D12UnifiedOperations.cs','src/HlslPerf.UnifiedBench/UnifiedBenchRunner.cs',
    'src/HlslPerf.UnifiedBench/CausalScanRunner.cs','src/HlslPerf.UnifiedBench/Program.cs',
    'kernels/scan.hlsl','kernels/include/hlslperf/scan_u32.hlsli','third_party/upstream-lock.json',
    'tools/Invoke-UnifiedExperiment.ps1','tools/causal_scan_protocol.py','tools/unified_protocol.py']


def check_bytes(repo, declaration=None):
    sources={p:digest(repo/p) for p in SOURCE_PATHS} if declaration is None else json.loads(declaration.read_text())['sources']
    for p,h in sources.items():
        blob=subprocess.check_output(['git','-C',str(repo),'show','HEAD:'+p])
        import hashlib
        assert digest(repo/p)==hashlib.sha256(blob).hexdigest()==h, p
    return sources


def generate(path,repo):
    assert not path.exists()
    sources=check_bytes(repo)
    d=json.loads((repo/'docs/integration/unified-declaration.json').read_text())
    arms=['internal-scan-single','internal-scan-single-vector-io','gps-reduce-then-scan']
    permutations=list(itertools.permutations(arms)); schedules=[]
    for p in range(1,6):
        orders=permutations*2;random.Random(609081+p*97).shuffle(orders)
        schedules.append(dict(index=p,seeds=[2909087+p*7907],orders=orders))
    cell=dict(id='scan-8mi-keys-uniform-1slots',workload='scan',count=8388608,pattern='uniform',pairs=False,slots=1,arms=arms,processes=schedules)
    d.update(cells=[cell],processOrder=[dict(cell=cell['id'],process=p) for p in range(1,6)],sources=sources,
        scope='One fixed Scan vector4 IO candidate; no Radix; no parameter search or retries. Diagnostic samples are excluded.',
        candidate=dict(arm=arms[1],vectorWidth=4,groupSize=256,waveSize=32,elementsPerThread=16,persistentGroups=256,
            extraLogicalBytes=0,extraLdsBytes=0,limitations='Lane spacing remains 64 bytes. Compiler scheduling/register allocation may change. DXIL is not device ISA or DRAM counters.'),
        rationale='Correct full outputs and matching DXIL demonstrate sixteen scalar IO calls become four vector calls on full blocks while local-scan semantics remain. Three retained diagnostics show lower candidate point estimates with high variability. This single confirmation tests the frozen full-operation contract, not a pure hardware cost decomposition.')
    path.write_text(json.dumps(d,indent=2)+'\n',encoding='utf-8');print(json.dumps(dict(path=str(path),sha256=digest(path),cells=1,processes=5)))


if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__);s=p.add_subparsers(dest='command',required=True)
    g=s.add_parser('generate');g.add_argument('path',type=Path);g.add_argument('--repo',type=Path,default=Path(__file__).resolve().parents[1])
    v=s.add_parser('verify-bytes');v.add_argument('declaration',type=Path);v.add_argument('--repo',type=Path,default=Path(__file__).resolve().parents[1])
    a=s.add_parser('analyze');a.add_argument('declaration',type=Path);a.add_argument('raw',type=Path);a.add_argument('output',type=Path)
    args=p.parse_args()
    if args.command=='generate':generate(args.path,args.repo)
    elif args.command=='verify-bytes':print(json.dumps(dict(passed=True,sources=check_bytes(args.repo,args.declaration))))
    else:analyze(args.declaration,args.raw,args.output,expected_cells=1)
