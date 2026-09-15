"""Compile old and new exclusive paths with one DXC; require identical DXIL. No device creation."""
import argparse
import json
import subprocess
from pathlib import Path
from check_wave_tiled_scan import compile_one, sha256

ROOT = Path(__file__).resolve().parents[1]


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--dxc', required=True, type=Path)
    p.add_argument('--output', required=True, type=Path)
    p.add_argument('--baseline', default='b52d18aff221f3f8822ddb54bc679c9e03932e2a')
    args = p.parse_args()
    output = args.output.resolve(); output.mkdir(parents=True, exist_ok=False)
    files = subprocess.check_output(['git', 'ls-tree', '-r', '--name-only', args.baseline, 'kernels'], cwd=ROOT, text=True).splitlines()
    for name in files:
        if Path(name).suffix not in ('.hlsl', '.hlsli', '.compute'): continue
        destination = output / 'baseline' / name; destination.parent.mkdir(parents=True, exist_ok=True)
        destination.write_bytes(subprocess.check_output(['git', 'show', args.baseline + ':' + name], cwd=ROOT))
    defines = {'HLSLPERF_SCAN_WAVE_TILED': 1, 'HLSLPERF_SCAN_BACKEND': 3, 'HLSLPERF_SCAN_OPERATOR': 1,
        'HLSLPERF_VECTOR_WIDTH': 4, 'HLSLPERF_WAVE_TILED_MAX_POLLS': 4, 'HLSLPERF_GROUP_SIZE': 256,
        'HLSLPERF_WAVE_SIZE': 32, 'HLSLPERF_ELEMENTS_PER_THREAD': 4, 'HLSLPERF_SINGLE_PASS_ITEMS_SCALE': 4}
    records = []
    for source, entry in [('kernels/scan.hlsl', 'SinglePassScanWaveTiled'),
        ('kernels/scan.hlsl', 'ResetWaveTiledState'), ('kernels/compaction.hlsl', 'FusedCompactWaveTiled'),
        ('kernels/compaction.hlsl', 'ResetWaveTiledState')]:
        label = Path(source).stem + '-' + entry
        old = compile_one(args.dxc.resolve(), output, 'old-' + label, str(output/'baseline'/source), entry, defines)
        new = compile_one(args.dxc.resolve(), output, 'new-' + label, source, entry, defines)
        if old['dxilSha256'] != new['dxilSha256']: raise ValueError('Exclusive/control DXIL changed: ' + label)
        records.append({'source': source, 'entry': entry, 'old': old, 'new': new, 'byteIdentical': True})
    (output/'control.json').write_text(json.dumps({'baselineCommit': args.baseline, 'passed': True,
        'compilerSha256': sha256(args.dxc.resolve()), 'checks': records}, indent=2)+'\n')
    print('PASS: four baseline/exclusive/compaction controls have byte-identical DXIL.')


if __name__ == '__main__': main()
