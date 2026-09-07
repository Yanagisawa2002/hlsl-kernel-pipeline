[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][ValidateSet('preflight', 'comparison')][string]$Phase,
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
    $report.status = 'running'; $report.phase1Status = 'complete'; $report.radixStatus = 'running'; $report.phase = 'radix-' + $Stage
    $report.currentProcess = $Process; $report.currentOutput = $Output
    $report.measurementSha = $declaration.sourceSha; $report.radixMeasurementSha = $declaration.sourceSha; $report.declarationPath = $declarationPath; $report.radixDeclarationPath = $declarationPath
    $report.rawEvidencePath = $EvidenceRoot; $report.radixRawEvidencePath = $EvidenceRoot; $report.updatedUtc = [DateTime]::UtcNow.ToString('o')
    Save-Json $report $CoordinationReport
}
function Environment-Snapshot {
    @{ utc = [DateTime]::UtcNow.ToString('o'); thermalAndClocks = 'unavailable; not sampled or controlled'
       cacheAndResidency = 'normal WDDM; no eviction, cache flush or residency pinning'
       processes = @(Get-Process | Where-Object { $_.ProcessName -match 'Unity|Unreal|Editor|chrome|msedge|Radeon|obs|dwm|Codex' } | Select-Object Id, ProcessName, CPU) }
}
Frozen
$schedule = @($declaration.schedules[$Phase])
$entryLedgerSha256 = $null
if ($Phase -eq 'comparison') {
    $entryPath = Join-Path $EvidenceRoot 'entry-ledger.json'
    $entry = Get-Content -LiteralPath $entryPath -Raw | ConvertFrom-Json -AsHashtable
    $entryLedgerSha256 = Hash $entryPath
    if (-not $entry.complete -or -not $entry.auditPassed -or $entry.declarationSha256 -ne (Hash $declarationPath)) { throw 'Incomplete or mismatched entry ledger' }
    foreach ($file in $entry.preflightReports) { if ((Hash $file.path) -ne $file.sha256) { throw 'Preflight evidence changed after entry decision' } }
    $schedule = @($schedule | Where-Object { $_.cell -in $entry.eligibleCells })
}
foreach ($cell in $schedule) {

    $output = Join-Path $EvidenceRoot $cell.name
    $receiptPath = Join-Path $output 'execution.json'
    if (Test-Path -LiteralPath $output) {
        if (-not (Test-Path -LiteralPath $receiptPath)) { throw "Incomplete existing output: $output" }
        $old = Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json
        if ($old.status -eq 'recorded' -and $old.manifestSha256 -eq $cell.manifestSha256 -and (Test-Path -LiteralPath (Join-Path $output 'run.json'))) { continue }
        throw "Existing unfinished/failed process must be inspected; no duplicate or automatic retry: $output"
    }
    New-Item -ItemType Directory -Path $output | Out-Null
    $receipt = @{ name=$cell.name; cell=$cell.cell; round=$cell.round; status='queued'; stage=$Phase; entryLedgerSha256=$entryLedgerSha256; sourceSha=$declaration.sourceSha; manifestSha256=$cell.manifestSha256; wrapperPid=$PID }
    Save-Json $receipt $receiptPath
    Update-Status ($Phase + '-queued') @{ wrapperPid=$PID; next=$cell.name } $output
    & $SerializedValidationRunner -Action {
        Frozen
        if ($Phase -eq 'comparison' -and (Hash $entryPath) -ne $entryLedgerSha256) { throw 'Entry ledger changed during comparison' }
        if ((Hash $cell.manifestPath) -ne $cell.manifestSha256) { throw 'Manifest changed after declaration' }
        $receipt.before = Environment-Snapshot
        $receipt.lockAcquiredUtc = [DateTime]::UtcNow.ToString('o')
        $arguments = @('"' + $declaration.cli + '"', 'tune', '"' + $cell.manifestPath + '"', '--adapter', 'R9700', '--rga', 'off', '--output', '"' + $output + '"')
        $native = Start-Process -FilePath 'dotnet' -ArgumentList $arguments -WorkingDirectory $repo -PassThru -WindowStyle Hidden -RedirectStandardOutput (Join-Path $output 'stdout.log') -RedirectStandardError (Join-Path $output 'stderr.log')
        $receipt.pid = $native.Id; $receipt.processStartedUtc = $native.StartTime.ToUniversalTime().ToString('o'); $receipt.status='running'
        Save-Json $receipt $receiptPath
        Update-Status ($Phase + '-running') @{ wrapperPid=$PID; nativePid=$native.Id; processStartedUtc=$receipt.processStartedUtc; name=$cell.name } $output
        $native.WaitForExit()
        $receipt.exitCode = $native.ExitCode; $receipt.processExitedUtc = [DateTime]::UtcNow.ToString('o')
        $receipt.after = Environment-Snapshot
        $receipt.status = if ($native.ExitCode -in @(0,2) -and (Test-Path -LiteralPath (Join-Path $output 'run.json'))) { 'recorded' } else { 'execution-failed' }
        Save-Json $receipt $receiptPath
    }
    Write-Output "$($cell.name): $($receipt.status), exit $($receipt.exitCode)"
}
Update-Status ($Phase + '-finished-awaiting-audit') $null $EvidenceRoot
