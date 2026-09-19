"""Create a minimal, isolated Unity project; never reuse the visual demo's Library."""
import argparse
import hashlib
import json
from pathlib import Path
import shutil


def source_identity(root):
    sources = sorted((root / 'unity/GpuDrivenCrowdBenchmark').rglob('*'))
    digest = hashlib.sha256()
    for p in sources:
        if p.suffix in {'.cs', '.compute', '.shader', '.cpp'}:
            digest.update(p.relative_to(root).as_posix().encode())
            digest.update(p.read_bytes().replace(b'\r\n', b'\n'))
    return digest.hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--project', type=Path, required=True)
    parser.add_argument('--native-plugin', type=Path)
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[1]
    project = args.project.resolve()
    if project.exists() and not (project / '.crossover-owned').exists():
        raise SystemExit('Refusing to overwrite a project not created by this tool')
    project.mkdir(parents=True, exist_ok=True)
    (project / '.crossover-owned').touch()
    target = project / 'Assets/GpuDrivenCrowdBenchmark'
    target.mkdir(parents=True, exist_ok=True)
    for p in (root / 'unity/GpuDrivenCrowdBenchmark').rglob('*'):
        if p.suffix in {'.cs', '.compute', '.shader'}:
            dest = target / p.relative_to(root / 'unity/GpuDrivenCrowdBenchmark')
            dest.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(p, dest)
    resources = project / 'Assets/Resources'
    resources.mkdir(exist_ok=True)
    identity = source_identity(root)
    (resources / 'crossover-source-identity.txt').write_text(identity)
    if args.native_plugin:
        plugin=args.native_plugin.resolve()
        receipt=json.loads((plugin.parent/'build.json').read_text(encoding='utf-8'))
        hashes={Path(entry['path']).name:entry['sha256'] for entry in receipt['files']}
        native_source=root/'unity/GpuDrivenCrowdBenchmark/Native/CrossoverTimestamp.cpp'
        if hashes.get('CrossoverTimestamp.cpp')!=hashlib.sha256(native_source.read_bytes()).hexdigest() or hashes.get('CrossoverTimestamp.dll')!=hashlib.sha256(plugin.read_bytes()).hexdigest():
            raise SystemExit('Native DLL/source build receipt mismatch; rebuild the plugin')
        destination=project/'Assets/Plugins/x86_64/CrossoverTimestamp.dll'
        destination.parent.mkdir(parents=True,exist_ok=True);shutil.copyfile(plugin,destination)
        (resources/'crossover-plugin-identity.txt').write_text(hashlib.sha256(plugin.read_bytes()).hexdigest())
    (project / 'Packages').mkdir(exist_ok=True)
    (project / 'Packages/manifest.json').write_text(json.dumps({'dependencies': {'com.unity.modules.imageconversion': '1.0.0', 'com.unity.modules.jsonserialize': '1.0.0'}}))
    (project / 'ProjectSettings').mkdir(exist_ok=True)
    (project / 'ProjectSettings/ProjectVersion.txt').write_text('m_EditorVersion: 6000.3.13f1\n')
    print(json.dumps({'project': str(project), 'sourceIdentity': identity}))


if __name__ == '__main__':
    main()
