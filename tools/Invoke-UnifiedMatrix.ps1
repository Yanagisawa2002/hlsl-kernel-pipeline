param(
    [Parameter(Mandatory)][string]$Runtime,
    [Parameter(Mandatory)][string]$OutputRoot,
    [Parameter(Mandatory)][string]$Declaration,
    [Parameter(Mandatory)][string]$BinaryLock,
    [Parameter(Mandatory)][string]$ExpectedSourceSha,
    [Parameter(Mandatory)][string]$SerializedValidationRunner,
    [string]$CoordinationReport
)
$ErrorActionPreference='Stop'
$protocol=Get-Content -Raw -LiteralPath $Declaration | ConvertFrom-Json
if ($protocol.processOrder.Count -ne 120 -or $protocol.cells.Count -ne 24) { throw 'The fixed matrix must contain 120 process launches and 24 cells.' }
$seen=@{}
foreach ($run in $protocol.processOrder) {
    $name=$run.cell+'-p'+$run.process
    if ($seen.ContainsKey($name)) { throw "Duplicate launch $name" }; $seen[$name]=$true
    $output=Join-Path $OutputRoot $name
    # Restarting the wrapper only skips complete, matching receipts; it never retries a failed process.
    if (Test-Path -LiteralPath "$output.execution.json") {
        $receipt=Get-Content -Raw -LiteralPath "$output.execution.json" | ConvertFrom-Json
        if ($receipt.sourceSha -ne $ExpectedSourceSha -or $receipt.exitCode -notin @(0,2) -or $receipt.status -notin @('tests-passed','collection-complete-with-correctness-failures')) { throw "Existing incomplete or incompatible evidence: $output" }
        if ($receipt.binaryLockSha256 -ne (Get-FileHash -LiteralPath $BinaryLock -Algorithm SHA256).Hash.ToLowerInvariant()) { throw "Existing evidence used another binary lock: $output" }
        $record=Get-Content -Raw -LiteralPath (Join-Path $output 'process.json') | ConvertFrom-Json
        if (-not $record.completed -or $record.declarationSha256 -ne (Get-FileHash -LiteralPath $Declaration -Algorithm SHA256).Hash.ToLowerInvariant()) { throw "Existing process cannot be resumed: $output" }
        continue
    }
    & (Join-Path $PSScriptRoot 'Invoke-UnifiedExperiment.ps1') -Mode formal -Runtime $Runtime -OutputDirectory $output -Declaration $Declaration -Cell $run.cell -ProcessIndex $run.process -ExpectedSourceSha $ExpectedSourceSha -BinaryLock $BinaryLock -SerializedValidationRunner $SerializedValidationRunner -CoordinationReport $CoordinationReport
}
