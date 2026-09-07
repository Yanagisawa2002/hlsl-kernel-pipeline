param(
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$SerializedValidationRunner=(Join-Path $PSScriptRoot 'Invoke-UnifiedValidationLock.ps1')
)
$ErrorActionPreference='Stop'
$repo=Split-Path -Parent $PSScriptRoot
$sourceSha=(& git -C $repo rev-parse HEAD).Trim()
if (& git -C $repo status --porcelain) { throw 'Build the benchmark from a clean committed checkout.' }
$output=[IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'Choose a new build output directory.' }
[IO.Directory]::CreateDirectory($output) | Out-Null
Push-Location $repo
try {
    & $SerializedValidationRunner -Action {
        $verification=python tools/verify_external_sources.py
        if ($LASTEXITCODE -ne 0) { throw 'Official source verification failed.' }
        [IO.File]::WriteAllText((Join-Path $output 'upstream-verification.json'),($verification -join [Environment]::NewLine))
        dotnet build src/HlslPerf.UnifiedBench/HlslPerf.UnifiedBench.csproj -c Release --artifacts-path (Join-Path $output 'artifacts') 2>&1 | Tee-Object -FilePath (Join-Path $output 'build.log')
        if ($LASTEXITCODE -ne 0) { throw 'Release build failed.' }
        dotnet test tests/HlslPerf.Core.Tests/HlslPerf.Core.Tests.csproj -c Release --artifacts-path (Join-Path $output 'test-artifacts') 2>&1 | Tee-Object -FilePath (Join-Path $output 'core-tests.log')
        if ($LASTEXITCODE -ne 0) { throw 'Core tests failed.' }
        python -m unittest discover -s tests -p test_unified_protocol.py 2>&1 | Tee-Object -FilePath (Join-Path $output 'protocol-tests.log')
        if ($LASTEXITCODE -ne 0) { throw 'Protocol tests failed.' }
    }
    if ((& git rev-parse HEAD).Trim() -ne $sourceSha -or (& git status --porcelain)) { throw 'Source changed during the build.' }
    $runtimeRoot=Join-Path $output 'artifacts/bin/HlslPerf.UnifiedBench/release'
    $runtime=Join-Path $runtimeRoot 'hlslperf-unified.dll'
    $binaryPath=Join-Path $output 'binary-lock.json'
    $binaryLock=[ordered]@{schema='hlslperf.unified-binary-lock.v1';sourceSha=$sourceSha;createdUtc=[DateTime]::UtcNow.ToString('o');files=@(
        Get-ChildItem -LiteralPath $runtimeRoot -Recurse -File | Sort-Object FullName | ForEach-Object {
            @{path=[IO.Path]::GetRelativePath($runtimeRoot,$_.FullName);sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}
        }
    )}
    [IO.File]::WriteAllText($binaryPath,($binaryLock | ConvertTo-Json -Depth 10))
    $build=[ordered]@{sourceSha=$sourceSha;runtime=$runtime;binaryLock=$binaryPath;declaration=(Join-Path $repo 'docs/integration/unified-declaration.json');serializedValidationRunner=[IO.Path]::GetFullPath($SerializedValidationRunner)}
    [IO.File]::WriteAllText((Join-Path $output 'build.json'),($build | ConvertTo-Json -Depth 8))
    $build | ConvertTo-Json -Depth 8
}
finally { Pop-Location }
