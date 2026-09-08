param(
    [Parameter(Mandatory)][string]$Repository,
    [Parameter(Mandatory)][string]$NewOutputRoot,
    [switch]$IncludeDevelopmentDiagnostics
)
$ErrorActionPreference='Stop'
$destination=[IO.Path]::GetFullPath($NewOutputRoot)
if (Test-Path -LiteralPath $destination) { throw 'Use a new reproduction directory.' }
Push-Location $Repository
try {
    python tools/causal_scan_protocol.py verify-bytes docs/integration/causal-scan-declaration-v2.json
    if ($LASTEXITCODE -ne 0) { throw 'Frozen source bytes differ from checkout or Git blob.' }
    ./tools/Build-UnifiedBenchmark.ps1 -OutputDirectory (Join-Path $destination 'build')
    $b=Get-Content -Raw (Join-Path $destination 'build/build.json') | ConvertFrom-Json
    ./tools/Invoke-UnifiedExperiment.ps1 -Mode causal-diagnostic -Runtime $b.runtime `
      -OutputDirectory (Join-Path $destination 'correctness') -ProcessIndex 0 `
      -SerializedValidationRunner $b.serializedValidationRunner
    if ($IncludeDevelopmentDiagnostics) {
        foreach ($index in 1..3) {
            ./tools/Invoke-UnifiedExperiment.ps1 -Mode causal-diagnostic -Runtime $b.runtime `
              -OutputDirectory (Join-Path $destination ('diagnostics/process-'+$index)) -ProcessIndex $index `
              -SerializedValidationRunner $b.serializedValidationRunner
        }
    }
    foreach ($index in 1..5) {
        ./tools/Invoke-UnifiedExperiment.ps1 -Mode causal-formal -Runtime $b.runtime `
          -OutputDirectory (Join-Path $destination ('formal/scan-8mi-keys-uniform-1slots-p'+$index)) `
          -Declaration docs/integration/causal-scan-declaration-v2.json -Cell scan-8mi-keys-uniform-1slots `
          -ProcessIndex $index -ExpectedSourceSha $b.sourceSha -BinaryLock $b.binaryLock `
          -SerializedValidationRunner $b.serializedValidationRunner
    }
    python tools/causal_scan_protocol.py analyze docs/integration/causal-scan-declaration-v2.json `
      (Join-Path $destination 'formal') (Join-Path $destination 'audit')
    if ($LASTEXITCODE -ne 0) { throw 'Formal audit failed.' }
}
finally { Pop-Location }
