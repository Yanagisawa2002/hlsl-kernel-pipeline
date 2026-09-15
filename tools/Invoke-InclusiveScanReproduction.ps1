param(
    [string]$Repository = (Split-Path $PSScriptRoot -Parent),
    [string]$DotNet = 'dotnet',
    [string]$MSBuild = 'C:/Program Files (x86)/Microsoft Visual Studio/2022/BuildTools/MSBuild/Current/Bin/MSBuild.exe',
    [string]$Adapter = 'NVIDIA GeForce RTX 4090',
    [string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath($Repository)
if (!(Test-Path -LiteralPath (Join-Path $repositoryRoot 'HlslKernelPipeline.slnx'))) { throw 'Repository must name an HLSL Kernel Pipeline checkout.' }
if (!$OutputDirectory) { $OutputDirectory = '.scratch/inclusive-repro-' + (Get-Date -Format 'yyyyMMdd-HHmmss') }
$evidence = [IO.Path]::GetFullPath($OutputDirectory, $repositoryRoot)
if (Test-Path -LiteralPath $evidence) { throw 'Choose a new output directory; previous evidence is preserved.' }
New-Item -ItemType Directory -Path $evidence | Out-Null
$native = Join-Path $evidence 'native'
$device = Join-Path $evidence 'probe/device.json'
function Invoke-Checked([string]$Name, [scriptblock]$Command) {
    Write-Output ((Get-Date -Format o) + ' ' + $Name)
    & $Command *> (Join-Path $evidence ($Name + '.log'))
    if ($LASTEXITCODE -ne 0) { throw ($Name + ' failed; inspect the preserved log.') }
}
Push-Location $repositoryRoot
try {
    & (Join-Path $repositoryRoot 'tools/Invoke-UnifiedValidationLock.ps1') -Action {
        Invoke-Checked 'native-build' { python tools/prepare_external_benchmarks.py --output $native --programs scan --build --msbuild $MSBuild }
        Invoke-Checked 'cpu-build' { & $DotNet build tests/HlslPerf.Core.Tests/HlslPerf.Core.Tests.csproj -c Release --disable-build-servers }
        Invoke-Checked 'cpu-tests' { & $DotNet test tests/HlslPerf.Core.Tests/HlslPerf.Core.Tests.csproj -c Release --no-build --no-restore }
        Invoke-Checked 'model-tests' { python tests/test_wave_tiled_scan.py }
        Invoke-Checked 'external-tests' { python tools/test_external_contracts.py }
        Invoke-Checked 'analysis-tests' { python tools/test_inclusive_scan_analysis.py }
        $dxc = Join-Path $native 'packages/Microsoft.Direct3D.DXC/build/native/bin/x64/dxc.exe'
        Invoke-Checked 'compiler-contracts' { python tools/check_wave_tiled_scan.py --dxc $dxc --output (Join-Path $evidence 'compiler') }
        Invoke-Checked 'compiler-controls' { python tools/check_inclusive_control.py --dxc $dxc --output (Join-Path $evidence 'controls') }
        Invoke-Checked 'gpu-build' { & $DotNet build tools/HlslPerf.InclusiveScanValidation -c Release --disable-build-servers }
        Invoke-Checked 'gpu-correctness' { & $DotNet tools/HlslPerf.InclusiveScanValidation/bin/Release/net10.0/HlslPerf.InclusiveScanValidation.dll $repositoryRoot $Adapter (Join-Path $evidence 'managed') }
        Invoke-Checked 'probe' { python tools/run_inclusive_scan.py probe --native $native --adapter $Adapter --output (Join-Path $evidence 'probe') }
        Invoke-Checked 'native-correctness' { python tools/run_inclusive_scan.py validate --native $native --device $device --output (Join-Path $evidence 'validation') }
        Invoke-Checked 'discovery' { python tools/run_inclusive_scan.py discover --native $native --device $device --output (Join-Path $evidence 'discovery') }
        Invoke-Checked 'freeze' { python tools/run_inclusive_scan.py freeze --native $native --device $device --validation (Join-Path $evidence 'validation') --managed (Join-Path $evidence 'managed/correctness.json') --discovery (Join-Path $evidence 'discovery') --output (Join-Path $evidence 'registration') }
        Invoke-Checked 'confirmation' { python tools/run_inclusive_scan.py confirm --native $native --device $device --plan (Join-Path $evidence 'registration/plan.json') --output (Join-Path $evidence 'confirmation') }
        Invoke-Checked 'analysis' { python tools/run_inclusive_scan.py analyze --confirmation (Join-Path $evidence 'confirmation') --output (Join-Path $evidence 'analysis.json') }
    }
}
finally { Pop-Location }
Write-Output ('Evidence: ' + $evidence)
