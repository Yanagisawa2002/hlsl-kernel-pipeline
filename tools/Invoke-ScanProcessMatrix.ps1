[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][ValidateSet('Correctness', 'Measure')][string]$Phase,
    [Parameter(Mandatory = $true)][string]$EvidenceRoot,
    [Parameter(Mandatory = $true)][string]$SerializedValidationRunner,
    [Parameter(Mandatory = $true)][string]$CoordinationReport
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$EvidenceRoot = (Resolve-Path -LiteralPath $EvidenceRoot).Path
$SerializedValidationRunner = (Resolve-Path -LiteralPath $SerializedValidationRunner).Path
$declarationPath = Join-Path $EvidenceRoot 'declaration.json'
$declaration = Get-Content -LiteralPath $declarationPath -Raw | ConvertFrom-Json -AsHashtable
function Save-Json($Object, [string]$Path) { $Object | ConvertTo-Json -Depth 80 | Set-Content -LiteralPath $Path -Encoding utf8 }
function Hash([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Frozen {
    if ((& git -C $repo rev-parse HEAD).Trim() -ne $declaration.sourceSha) { throw 'Source commit differs from declaration' }
    if (@(& git -C $repo status --porcelain).Count) { throw 'Source checkout is dirty' }
    foreach ($file in @($declaration.binaries) + @($declaration.sources)) {
        if ((Hash $file.path) -ne $file.sha256) { throw "Frozen file changed: $($file.path)" }
    }
    if ((Hash $declaration.protocolPath) -ne $declaration.protocolSha256) { throw 'Protocol changed' }
}
function Update-Status([string]$Stage, $Process, $Output) {
    $report = Get-Content -LiteralPath $CoordinationReport -Raw | ConvertFrom-Json -AsHashtable
    $report.status = 'running'; $report.phase1Status = $Stage
    $report.currentProcess = $Process; $report.currentOutput = $Output
    $report.measurementSha = $declaration.sourceSha; $report.declarationPath = $declarationPath
    $report.rawEvidencePath = $EvidenceRoot; $report.updatedUtc = [DateTime]::UtcNow.ToString('o')
    Save-Json $report $CoordinationReport
}
function Environment-Snapshot {
    @{ utc = [DateTime]::UtcNow.ToString('o'); thermalAndClocks = 'unavailable; not sampled or controlled'
       cacheAndResidency = 'normal WDDM; no eviction, cache flush or residency pinning'
       processes = @(Get-Process | Where-Object { $_.ProcessName -match 'Unity|Unreal|Editor|chrome|msedge|Radeon|obs|dwm|Codex' } | Select-Object Id, ProcessName, CPU) }
}
Frozen
if ($Phase -eq 'Correctness') {
    $output = Join-Path $EvidenceRoot 'nonaligned-correctness'
    if (Test-Path -LiteralPath $output) { throw 'Correctness output exists; inspect it instead of rerunning' }
    Update-Status 'correctness-queued' @{ wrapperPid = $PID } $output
    & $SerializedValidationRunner -Action {
        Frozen
        Update-Status 'correctness-running' @{ wrapperPid = $PID } $output
        & dotnet $declaration.correctnessHarness $declarationPath $output *> (Join-Path $EvidenceRoot 'correctness-console.log')
        if ($LASTEXITCODE -ne 0) { throw 'Nonaligned native correctness failed; evidence retained' }
    }
    Update-Status 'correctness-passed' $null $output
    return
}
$correctness = Get-Content -LiteralPath (Join-Path $EvidenceRoot 'nonaligned-correctness/correctness.json') -Raw | ConvertFrom-Json
if (-not $correctness.passed -or @($correctness.results).Count -ne 24) { throw 'Correctness gate is incomplete' }
foreach ($cell in $declaration.schedule) {
    $output = Join-Path $EvidenceRoot $cell.name
    $receiptPath = Join-Path $output 'execution.json'
    if (Test-Path -LiteralPath $output) {
        if (-not (Test-Path -LiteralPath $receiptPath)) { throw "Incomplete existing output: $output" }
        $old = Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json
        if ($old.status -eq 'recorded' -and $old.manifestSha256 -eq $cell.manifestSha256 -and (Test-Path -LiteralPath (Join-Path $output 'run.json'))) { continue }
        throw "Existing unfinished/failed process must be inspected; no duplicate or automatic retry: $output"
    }
    New-Item -ItemType Directory -Path $output | Out-Null
    $receipt = @{ name=$cell.name; cell=$cell.cell; round=$cell.round; status='queued'; sourceSha=$declaration.sourceSha; manifestSha256=$cell.manifestSha256; wrapperPid=$PID }
    Save-Json $receipt $receiptPath
    Update-Status 'measurement-queued' @{ wrapperPid=$PID; next=$cell.name } $output
    & $SerializedValidationRunner -Action {
        Frozen
        if ((Hash $cell.manifestPath) -ne $cell.manifestSha256) { throw 'Manifest changed after declaration' }
        $receipt.before = Environment-Snapshot
        $receipt.lockAcquiredUtc = [DateTime]::UtcNow.ToString('o')
        $arguments = @('"' + $declaration.cli + '"', 'tune', '"' + $cell.manifestPath + '"', '--adapter', 'R9700', '--rga', 'off', '--output', '"' + $output + '"')
        $native = Start-Process -FilePath 'dotnet' -ArgumentList $arguments -WorkingDirectory $repo -PassThru -WindowStyle Hidden -RedirectStandardOutput (Join-Path $output 'stdout.log') -RedirectStandardError (Join-Path $output 'stderr.log')
        $receipt.pid = $native.Id; $receipt.processStartedUtc = $native.StartTime.ToUniversalTime().ToString('o'); $receipt.status='running'
        Save-Json $receipt $receiptPath
        Update-Status 'measurement-running' @{ wrapperPid=$PID; nativePid=$native.Id; processStartedUtc=$receipt.processStartedUtc; name=$cell.name } $output
        $native.WaitForExit()
        $receipt.exitCode = $native.ExitCode; $receipt.processExitedUtc = [DateTime]::UtcNow.ToString('o')
        $receipt.after = Environment-Snapshot
        $receipt.status = if ($native.ExitCode -in @(0,2) -and (Test-Path -LiteralPath (Join-Path $output 'run.json'))) { 'recorded' } else { 'execution-failed' }
        Save-Json $receipt $receiptPath
    }
    Write-Output "$($cell.name): $($receipt.status), exit $($receipt.exitCode)"
}
Update-Status 'measurement-finished-awaiting-audit' $null $EvidenceRoot
