"""Export the actual opt-in Unity scan shader and dependency identity; no Unity/GPU invocation."""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess

ROOT=Path(__file__).resolve().parents[1]
FILES=['kernels/consumer/ScanWaveTiled.compute','kernels/include/hlslperf/scan_wave_tiled_u32.hlsli','LICENSE.md']

def export(root,output):
    root=Path(root).resolve(); output=Path(output).resolve()
    source=subprocess.check_output(['git','-C',str(root),'rev-parse','HEAD'],text=True).strip()
    # Export exact committed bytes, independent of platform text checkout filters.
    # Refuse local shader changes rather than labelling them with an unrelated commit.
    subprocess.run(['git','-C',str(root),'diff','--exit-code','HEAD','--',*FILES],check=True,stdout=subprocess.DEVNULL)
    data={path:subprocess.check_output(['git','-C',str(root),'show',source+':'+path]) for path in FILES}
    if output.exists(): raise ValueError('Choose a new export directory.')
    files=[dict(path=path,sha256=hashlib.sha256(data[path]).hexdigest()) for path in sorted(data)]
    identity=hashlib.sha256(''.join(f['path']+'\0'+f['sha256']+'\n' for f in files).encode()).hexdigest()
    manifest=dict(schema='hlslperf.unity-scan-consumer.v1', performanceStatus='Unmeasured',
        variantId='hlslperf.scan-wave-tiled.u32.wave32.g256.b4096.v1',
        sourceCommit=source, assetSha256=identity, files=files,
        identityAlgorithm='SHA256(UTF8(concat sorted path + NUL + lowercase file SHA256 + LF))',
        semantic='exclusive-u32-sum-modulo-2^32', kernelAbi='hlslperf.raw-buffer.v1',
        shader='kernels/consumer/ScanWaveTiled.compute', requiredBackend='D3D12', requiredShaderModel='6_6', requiredWaveSize=32,
        inputTarget='Raw', outputTarget='Raw', scratchTarget='Raw', elementStrideBytes=4,
        maximumConsumerCount=256*65535, minimumBufferBytes=4, elementsPerBlock=4096,
        scratchBytes='8 + 12 * ceil(capacity / 4096)',
        constants=['ElementCount','ElementsPerBlock','LogicalBlockCount'],
        passes=[dict(entry='ResetWaveTiledState',groups='1',bindings={'Output0':'scratch'}),
                dict(entry='SinglePassScanWaveTiled',groups='min(ceil(count / 4096), 256)',
                     bindings={'Input0':'input','Output0':'output','Output1':'scratch'})],
        empty='Host skips both dispatches; input/output sentinel bytes are unchanged.',
        ownership='Caller preallocates distinct buffers; resets each invocation; same queue, ordered UAV dependencies; disposal after completion.',
        verification='DXC compilation and CPU model only; Unity import and GPU correctness/performance unvalidated.')
    output.mkdir(parents=True)
    for path,bytes_ in data.items():
        target=output/path; target.parent.mkdir(parents=True,exist_ok=True); target.write_bytes(bytes_)
    (output/'consumer-manifest.json').write_text(json.dumps(manifest,indent=2)+'\n')
    return manifest

if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('--repo',type=Path,default=ROOT)
    parser.add_argument('--output',required=True,type=Path); args=parser.parse_args()
    print(json.dumps(export(args.repo,args.output),indent=2))
