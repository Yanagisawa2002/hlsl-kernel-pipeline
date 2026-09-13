"""Inspect NuGet bytes without installing/running the package or dispatching shaders."""
import argparse
import hashlib
import json
from pathlib import Path
import zipfile

ROOT=Path(__file__).resolve().parents[1]
PREFIX='contentFiles/any/any/hlslperf/'

def verify(package):
    lock=json.loads((ROOT/'third_party/upstream-lock.json').read_text())
    with zipfile.ZipFile(package) as archive:
        names=archive.namelist()
        if len(names)!=len(set(names)): raise ValueError('Duplicate package entries')
        for source in lock['sources']:
            for file in source['files']:
                data=archive.read(PREFIX+file['localPath'])
                if hashlib.sha256(data).hexdigest()!=file['sha256']:
                    raise ValueError('Packaged upstream bytes differ: '+file['localPath'])
        assets=list((ROOT/'kernels').glob('*.hlsl'))+list((ROOT/'kernels/include/hlslperf').glob('*.hlsli'))+list((ROOT/'kernels/consumer').glob('*.compute'))
        for path in assets:
            if archive.read(PREFIX+path.relative_to(ROOT).as_posix())!=path.read_bytes():
                raise ValueError('Packaged shader missing or changed: '+str(path))
        if archive.read(PREFIX+'third_party/upstream-lock.json')!=(ROOT/'third_party/upstream-lock.json').read_bytes():
            raise ValueError('Source lock missing or changed in package')
        if 'LICENSE.md' not in names: raise ValueError('Project license missing')
    return dict(passed=True,upstreamFiles=sum(len(s['files']) for s in lock['sources']),shaderAssets=len(assets),
                packageSha256=hashlib.sha256(Path(package).read_bytes()).hexdigest())

if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('package',type=Path)
    print(json.dumps(verify(parser.parse_args().package),indent=2))
