"""Compile-only radix validation. Never creates a device, dispatches, or measures work."""
import argparse
import hashlib
import json
import math
import re
import subprocess
from pathlib import Path


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def compile_radix(repo, dxc, output):
    output.mkdir(parents=True, exist_ok=True)
    results = []
    configurations = [(4, 0, 1), (4, 1, 1), (8, 0, 1), (8, 1, 1),
                      (1, 0, 0), (1, 1, 0), (4, 0, 0), (4, 1, 0),
                      (8, 0, 0), (8, 1, 0), (8, 0, 2), (8, 1, 2)]
    for bits, pairs, mode in configurations:
        tiled, ballot = mode == 1, mode == 2
        entries = ['EmptyRadix', 'SplitRadixPairs', 'PackRadixPairs'] if pairs else ['EmptyRadix']
        entries += ['ProduceZeroFlags', 'ScatterRadixBit'] if bits == 1 else ['BuildRadixHistogram']
        entries += (['PrefixRadixHistogramTiles', 'PrefixRadixHistogramBins', 'ScatterRadixTile'] +
                    (['ScatterRadixTileToPairs'] if pairs else [])) if tiled else (
                        ['BlockScanPass', 'AddScanOffsets'] + (['ScatterRadixDigit'] if bits != 1 else []))
        for entry in entries:
            stem = output / f'radix-{bits}-pairs{pairs}-mode{mode}-{entry}'
            args = [str(dxc), '-T', 'cs_6_6', '-E', entry, '-HV', '2018', '-O3', '-Ges', '-WX']
            defines = {'HLSLPERF_GROUP_SIZE': 128, 'HLSLPERF_ELEMENTS_PER_THREAD': 2 if ballot else 4,
                       'HLSLPERF_SCAN_BACKEND': 2, 'HLSLPERF_SCAN_OPERATOR': 1,
                       'HLSLPERF_VECTOR_WIDTH': 1, 'HLSLPERF_WAVE_SIZE': 32,
                       'HLSLPERF_RADIX_BITS': bits, 'HLSLPERF_RADIX_PAIRS': pairs,
                       'HLSLPERF_RADIX_TILE': int(tiled), 'HLSLPERF_RADIX_RANK_BALLOT': int(ballot)}
            for name, value in defines.items():
                args.extend(['-D', f'{name}={value}'])
            args += ['-Fo', str(stem.with_suffix('.dxil')), '-Fc', str(stem.with_suffix('.asm')),
                     str(repo / 'kernels/radix-sort.hlsl')]
            subprocess.run(args, check=True, capture_output=True, text=True)
            assembly = stem.with_suffix('.asm').read_text(encoding='utf-8')
            # Fail on unrecognized declarations instead of undercounting them.
            shared_bytes = 0
            for line in assembly.splitlines():
                if not line.startswith('@') or 'addrspace(3) global' not in line:
                    continue
                match = re.search(r'addrspace\(3\) global ((?:\[\d+ x )*i32\]*)(?: |,)', line)
                if not match:
                    raise ValueError(f'Unrecognized shared allocation: {line}')
                shared_bytes += math.prod(int(size) for size in re.findall(r'\[(\d+) x ', match[1])) * 4
            if shared_bytes > 32768:
                raise ValueError(f'LDS exceeds 32 KiB: {stem.name} = {shared_bytes}')
            results.append(dict(entry=entry, defines=defines, sharedBytes=shared_bytes,
                                dxilSha256=sha(stem.with_suffix('.dxil')),
                                assemblySha256=sha(stem.with_suffix('.asm'))))
    receipt = dict(status='Unmeasured', validation='DXC compilation and static LDS only',
                   gpuExecuted=False, compilerPath=str(dxc), compilerSha256=sha(dxc),
                   sources={name: sha(repo / name) for name in [
                       'kernels/radix-sort.hlsl', 'kernels/include/hlslperf/scan_u32.hlsli']},
                   options=['cs_6_6', 'HLSL2018', 'O3', 'Ges', 'WX'], shaders=results)
    for dll in ['dxcompiler.dll', 'dxil.dll']:
        path = dxc.parent / dll
        if path.is_file():
            receipt.setdefault('compilerLibraries', {})[dll] = sha(path)
    (output / 'compile-receipt.json').write_text(json.dumps(receipt, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(dict(passed=True, shaders=len(results), maxSharedBytes=max(r['sharedBytes'] for r in results),
                         status='Unmeasured', gpuExecuted=False)))


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dxc', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    try:
        compile_radix(Path(__file__).resolve().parents[1], args.dxc.resolve(), args.output.resolve())
    except subprocess.CalledProcessError as error:
        raise SystemExit(error.stderr or error.stdout or str(error))
