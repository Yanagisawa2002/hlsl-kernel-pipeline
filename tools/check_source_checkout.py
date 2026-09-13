"""Read-only source/short-path preflight; never creates a device or runs a benchmark."""
import argparse
import json
from pathlib import Path
import subprocess

def check(root):
    root=Path(root).resolve()
    paths=subprocess.check_output(['git','-C',str(root),'ls-files','-z']).decode().split('\0')
    long_paths=[p for p in paths if p and len(str(root/p))>=260]
    configured=subprocess.run(['git','-C',str(root),'config','--get','core.longpaths'],capture_output=True,text=True).stdout.strip()
    if long_paths and configured!='true':
        raise ValueError('Checkout has long paths. Re-clone with git -c core.longpaths=true clone <url> C:/src/hlsl, or set repository core.longpaths=true.')
    missing=[p for p in paths if p and not (root/p).is_file()]
    if missing: raise ValueError('Incomplete checkout (possibly path truncation): '+str(missing[:3]))
    return dict(passed=True,root=str(root),trackedFiles=len(paths)-1,longPaths=len(long_paths),gitLongPaths=configured,
                nativeBuildOutput='Use a new path shorter than 160 characters.')

if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__); parser.add_argument('--repo',type=Path,default=Path(__file__).resolve().parents[1])
    print(json.dumps(check(parser.parse_args().repo),indent=2))
