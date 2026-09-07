param(
    [Parameter(Mandatory=$true)][string]$ValidationLockScript,
    [Parameter(Mandatory=$true)][string]$RgaPath,
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [string]$MatrixPath = "$PSScriptRoot/../manifests/evidence/bounded-r9700.json"
)
$ErrorActionPreference = 'Stop'
$evidenceRepo = (Resolve-Path "$PSScriptRoot/..").Path
$evidenceOutput = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $evidenceOutput) { throw 'Choose a new evidence directory; prior attempts are preserved.' }
& $ValidationLockScript -Action {
    dotnet build "$PSScriptRoot/HlslPerf.EvidenceRun/HlslPerf.EvidenceRun.csproj" -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Evidence runner build failed.' }
    dotnet test "$evidenceRepo/tests/HlslPerf.Core.Tests" -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Evidence contract tests failed.' }
    & dotnet "$PSScriptRoot/HlslPerf.EvidenceRun/bin/Release/net10.0/HlslPerf.EvidenceRun.dll" $evidenceRepo $MatrixPath $evidenceOutput $RgaPath
    $evidenceExit = $LASTEXITCODE
    $metadata = [ordered]@{
        finishedUtc = [DateTimeOffset]::UtcNow.ToString('o')
        gitCommit = (& git -C $evidenceRepo rev-parse HEAD)
        gitStatus = (& git -C $evidenceRepo status --porcelain)
        runnerExitCode = $evidenceExit
        displayDrivers = @(Get-CimInstance Win32_VideoController | Select-Object Name, DriverVersion, PNPDeviceID)
        processes = @(Get-Process | Select-Object ProcessName, Id, CPU, WorkingSet64)
        thermalStatus = 'unavailable: no temperature or clock provider installed by this task'
        comparability = 'No cache flush, clock pinning, or external process control; process list is a snapshot, not GPU attribution.'
    }
    $metadata | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath "$evidenceOutput/host-metadata.json" -Encoding utf8
    Get-ChildItem -LiteralPath $evidenceOutput -File -Recurse | ForEach-Object {
        [ordered]@{ path = [IO.Path]::GetRelativePath($evidenceOutput, $_.FullName); sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash; bytes = $_.Length }
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath "$evidenceOutput/artifacts.json" -Encoding utf8
    if ($evidenceExit -ne 0) { throw "Evidence run failed with exit $evidenceExit; raw failure retained at $evidenceOutput" }
}
