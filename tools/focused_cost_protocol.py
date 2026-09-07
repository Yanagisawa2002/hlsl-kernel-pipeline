"""Freeze or audit the two-cell focused-cost confirmation, without parameter search."""
import argparse
import itertools
import json
import random
from pathlib import Path
from unified_protocol import analyze, digest


def generate(path, repo):
    assert not path.exists(), "Never overwrite a frozen protocol."
    # Preserve the original statistical contract and full-operation definitions.
    d = json.loads((repo / "docs/integration/unified-declaration.json").read_text())
    specs = [
        ("radix", 1048576, True, ["internal-radix-8", "internal-radix-8-ballot", "amd-parallel-sort"]),
        ("scan", 8388608, False, ["internal-scan-single", "gps-reduce-then-scan"]),
    ]
    cells = []
    for ci, (workload, count, pairs, arms) in enumerate(specs):
        permutations = list(itertools.permutations(arms))
        orders = permutations * (12 // len(permutations))
        schedules = []
        for p in range(1, 6):
            shuffled = orders.copy()
            random.Random(260908 + ci*1009 + p*79).shuffle(shuffled)
            schedules.append(dict(index=p, seeds=[1709081+ci*104729+p*7919], orders=shuffled))
        cells.append(dict(id=f"{workload}-{count//1048576}mi-{'pairs' if pairs else 'keys'}-uniform-1slots",
            workload=workload, count=count, pattern="uniform", pairs=pairs, slots=1, arms=arms, processes=schedules))
    d['cells'] = cells
    d['processOrder'] = [dict(cell=cells[i]['id'], process=p) for p in range(1,6) for i in ([0,1] if p%2 else [1,0])]
    d['scope'] = 'One fixed ballot local-rank candidate for Radix; Scan has no candidate and repeats the unchanged comparison. No parameter search, retries or automatic defaults.'
    d['candidate'] = dict(arm='internal-radix-8-ballot', groupSize=128, recordsPerThread=2, radixBits=8,
        waveSize=32, extraSharedBytes=288, change='Stable predecessor rank from valid wave bit planes; input loads, histogram, scan, scatter output, conversion and pass count preserved.')
    source_paths = list(d['sources']) + ['src/HlslPerf.Workloads/FocusedCostWorkloads.cs',
        'src/HlslPerf.UnifiedBench/FocusedCostRunner.cs', 'kernels/radix-sort.hlsl',
        'kernels/include/hlslperf/scan_u32.hlsli', 'tools/focused_cost_protocol.py', 'tools/unified_protocol.py']
    d['sources'] = {p:digest(repo/p) for p in source_paths}
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(d, indent=2)+'\n', encoding='utf-8')
    print(json.dumps(dict(path=str(path), sha256=digest(path), cells=2, processes=10)))


if __name__ == '__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    sub=parser.add_subparsers(dest='command', required=True)
    g=sub.add_parser('generate'); g.add_argument('path',type=Path)
    g.add_argument('--repo',type=Path,default=Path(__file__).resolve().parents[1])
    a=sub.add_parser('analyze'); a.add_argument('declaration',type=Path); a.add_argument('raw',type=Path); a.add_argument('output',type=Path)
    args=parser.parse_args()
    if args.command=='generate': generate(args.path,args.repo)
    else: analyze(args.declaration,args.raw,args.output,expected_cells=2)
