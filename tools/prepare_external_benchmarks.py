"""Prepare/link pinned native benchmark adapters. This tool has no execution mode.

Sources, upstream default inputs/validators and native batch methods stay unchanged.
Only our local main, candidate and ABI adapters are compiled alongside them.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import urllib.request
import xml.etree.ElementTree as ET
import zipfile

from verify_external_sources import verify

ROOT = Path(__file__).resolve().parents[1]


def prepare(output, package_cache, build=False, msbuild=None, toolset='v143'):
    verification = verify(ROOT)
    output = output.resolve()
    if output.exists():
        raise ValueError('Choose a new output directory; existing artifacts are never overwritten.')
    if len(str(output)) > 160:
        raise ValueError('Use a shorter native build output path (at most 160 characters).')
    output.mkdir(parents=True)
    dependency_lock = ROOT / 'benchmarks/external/native-dependencies.json'
    dependencies = json.loads(dependency_lock.read_text())
    packages = {}
    for item in dependencies['packages']:
        archive = package_cache / f"{item['id']}.{item['version']}.nupkg"
        if not archive.exists():
            with urllib.request.urlopen(item['url'], timeout=30) as response:
                data = response.read()
        else:
            data = archive.read_bytes()
        if len(data) != item['bytes'] or hashlib.sha256(data).hexdigest() != item['sha256']:
            raise ValueError('Native package hash mismatch: ' + item['id'])
        # Cache verified bytes only; never install/run package build hooks or scripts.
        archive.parent.mkdir(parents=True, exist_ok=True)
        if not archive.exists(): archive.write_bytes(data)
        destination = output / 'packages' / item['id']
        with zipfile.ZipFile(archive) as source:
            for member in source.infolist():
                target = (destination / member.filename).resolve()
                if not target.is_relative_to(destination.resolve()):
                    raise ValueError('Unsafe package archive path')
            source.extractall(destination)
        packages[item['id']] = destination

    native = ROOT / 'benchmarks/external/native'
    projects = []
    ns = 'http://schemas.microsoft.com/developer/msbuild/2003'
    ET.register_namespace('', ns)
    def child(parent, tag, text=None, **attrs):
        element = ET.SubElement(parent, '{' + ns + '}' + tag, attrs)
        element.text = text
        return element
    for name, upstream in [('scan', ROOT / 'third_party/gpu-prefix-sums/GPUPrefixSumsD3D12'),
                           ('sort', ROOT / 'third_party/gpu-sorting/GPUSortingD3D12')]:
        main = native / (name + '-main.cpp')
        if not main.exists(): raise ValueError('Missing adapter: ' + str(main))
        project = ET.Element('{' + ns + '}Project', DefaultTargets='Build')
        group = child(project, 'ItemGroup', Label='ProjectConfigurations')
        config = child(group, 'ProjectConfiguration', Include='Release|x64')
        child(config, 'Configuration', 'Release'); child(config, 'Platform', 'x64')
        globals_ = child(project, 'PropertyGroup', Label='Globals')
        child(globals_, 'WindowsTargetPlatformVersion', '10.0')
        child(project, 'Import', Project='$(VCTargetsPath)\\Microsoft.Cpp.Default.props')
        config = child(project, 'PropertyGroup', Label='Configuration')
        child(config, 'ConfigurationType', 'Application')
        child(config, 'PlatformToolset', toolset)
        child(config, 'UseDebugLibraries', 'false')
        child(project, 'Import', Project='$(VCTargetsPath)\\Microsoft.Cpp.props')
        props = child(project, 'PropertyGroup')
        child(props, 'OutDir', str(output / name) + os.sep)
        child(props, 'IntDir', str(output / (name + '-obj')) + os.sep)
        definitions = child(project, 'ItemDefinitionGroup')
        compiler = child(definitions, 'ClCompile')
        child(compiler, 'LanguageStandard', 'stdcpp17')
        child(compiler, 'PrecompiledHeader', 'NotUsing')
        child(compiler, 'ForcedIncludeFiles', 'algorithm;array;stdexcept')
        child(compiler, 'WarningLevel', 'Level3')
        child(compiler, 'Optimization', 'MaxSpeed')
        child(compiler, 'PreprocessorDefinitions', '_CRT_SECURE_NO_WARNINGS;_SILENCE_EXPERIMENTAL_COROUTINE_DEPRECATION_WARNINGS;NDEBUG;HLSLPERF_REPOSITORY="' + ROOT.as_posix() + '"')
        child(compiler, 'AdditionalIncludeDirectories', ';'.join(str(p) for p in [upstream, native,
            packages['Microsoft.Direct3D.D3D12'] / 'build/native/include',
            packages['Microsoft.Direct3D.DXC'] / 'build/native/include',
            packages['Microsoft.Windows.ImplementationLibrary'] / 'include']))
        link = child(definitions, 'Link')
        child(link, 'AdditionalDependencies', 'dxgi.lib;d3d12.lib;dxcompiler.lib;runtimeobject.lib;windowsapp.lib;%(AdditionalDependencies)')
        child(link, 'AdditionalLibraryDirectories', str(packages['Microsoft.Direct3D.DXC'] / 'build/native/lib/x64'))
        child(link, 'SubSystem', 'Console')
        files = child(project, 'ItemGroup')
        child(files, 'ClCompile', Include=str(main))
        for file in sorted(upstream.glob('*.cpp')):
            if file.stem not in ('pch', upstream.name):
                compiled_file = file
                if file.name == 'FFXParallelSort.cpp':
                    # Preserve the locked original. Its CreateBuffer lengths are bytes,
                    # but these two tables were sized in uint elements. Compile a
                    # separately identified allocation-only patch for the native host.
                    original = file.read_bytes()
                    patched = original
                    for expression in (b'threadBlocks * k_radix,', b'm_numReduceBlocks * k_radix,'):
                        if patched.count(expression) != 1:
                            raise ValueError('Pinned FFX allocation patch no longer matches.')
                        patched = patched.replace(expression, expression[:-1] + b' * sizeof(uint32_t),')
                    compiled_file = output / 'FFXParallelSort.buffer-allocation.cpp'
                    compiled_file.write_bytes(patched)
                    (output / 'ffx-allocation-patch.json').write_text(json.dumps(dict(
                        schema='hlslperf.ffx-byte-allocation-patch.v1', original=str(file),
                        originalSha256=hashlib.sha256(original).hexdigest(), patchedSha256=hashlib.sha256(patched).hexdigest(),
                        change='Multiply sum/reduce table uint element counts by sizeof(uint32_t); shader, generator and timing code unchanged.'), indent=2)+'\n')
                child(files, 'ClCompile', Include=str(compiled_file))
        child(project, 'Import', Project='$(VCTargetsPath)\\Microsoft.Cpp.targets')
        path = output / (name + '.vcxproj')
        ET.indent(project)
        ET.ElementTree(project).write(path, encoding='utf-8', xml_declaration=True)
        projects.append(path)
        if build:
            if not msbuild or not msbuild.is_file(): raise ValueError('Provide an installed MSBuild.exe via --msbuild.')
            with (output / (name + '-build.log')).open('w', encoding='utf-8') as log:
                result = subprocess.run([str(msbuild), str(path), '/t:Build', '/p:Configuration=Release', '/p:Platform=x64', '/nologo', '/v:minimal'], stdout=log, stderr=subprocess.STDOUT)
            if result.returncode: raise RuntimeError('Native compilation failed; inspect ' + str(output / (name + '-build.log')))
            runtime = output / name
            shutil.copytree(upstream / 'Shaders', runtime / 'Shaders')
            for dll in (packages['Microsoft.Direct3D.DXC'] / 'build/native/bin/x64').glob('*.dll'):
                shutil.copy2(dll, runtime / dll.name)
            shutil.copytree(packages['Microsoft.Direct3D.D3D12'] / 'build/native/bin/x64', runtime / 'D3D12')
    report = dict(schema='hlslperf.external-native-preparation.v1', performanceStatus='Unmeasured',
        gpuExecuted=False, benchmarkExecuted=False, buildRequested=build, upstream=verification,
        dependencyLockSha256=hashlib.sha256(dependency_lock.read_bytes()).hexdigest(),
        projects=[str(p) for p in projects],
        localAdapterFiles={p.name:hashlib.sha256(p.read_bytes()).hexdigest() for p in sorted(native.iterdir()) if p.is_file()},
        localBuildPatches={p.name:hashlib.sha256(p.read_bytes()).hexdigest() for p in sorted(output.glob('*allocation*'))},
        binaries={p.relative_to(output).as_posix():hashlib.sha256(p.read_bytes()).hexdigest() for p in sorted(output.rglob('*.exe'))})
    (output / 'preparation.json').write_text(json.dumps(report, indent=2)+'\n')
    return report


if __name__ == '__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', required=True, type=Path)
    parser.add_argument('--package-cache', type=Path, default=ROOT/'.scratch/native-packages')
    parser.add_argument('--build', action='store_true', help='Compile/link only; never launch the produced program.')
    parser.add_argument('--msbuild', type=Path)
    parser.add_argument('--toolset', choices=['v143', 'v145'], default='v143')
    args=parser.parse_args()
    print(json.dumps(prepare(args.output, args.package_cache, args.build, args.msbuild, args.toolset), indent=2))
