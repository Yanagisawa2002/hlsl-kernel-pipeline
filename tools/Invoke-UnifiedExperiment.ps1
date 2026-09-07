param(
    [Parameter(Mandatory)][string]$Runtime,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][string]$SerializedValidationRunner,
    [Parameter(Mandatory)][ValidateSet('correctness','formal','pilot')][string]$Mode,
    [string]$CoordinationReport,
    [string]$Declaration,
    [string]$Cell,
    [int]$ProcessIndex,
    [string]$ExpectedSourceSha,
    [string]$BinaryLock
)
$ErrorActionPreference='Stop'
$repo=Split-Path -Parent $PSScriptRoot
$source=(& git -C $repo rev-parse HEAD).Trim()
if (& git -C $repo status --porcelain) { throw 'Commit all experiment source before native execution.' }
if ($ExpectedSourceSha -and $source -ne $ExpectedSourceSha) { throw 'Source differs from the frozen build.' }
$output=[IO.Path]::GetFullPath($OutputDirectory)
if ((Test-Path -LiteralPath $output) -or (Test-Path -LiteralPath "$output.execution.json")) { throw 'Evidence path already exists; no automatic retries.' }
[IO.Directory]::CreateDirectory((Split-Path -Parent $output)) | Out-Null
$arguments=@([IO.Path]::GetFullPath($Runtime),$Mode,$repo,$output)
if ($Mode -eq 'pilot') { $arguments+=@([IO.Path]::GetFullPath($Declaration),$Cell,"$ProcessIndex") }
if ($Mode -eq 'formal') {
    if (-not ($Declaration -and $Cell -and $ProcessIndex -ge 1 -and $ProcessIndex -le 5 -and $ExpectedSourceSha -and $BinaryLock)) { throw 'Formal execution requires frozen source, binaries and a complete declaration.' }
    $arguments+=@([IO.Path]::GetFullPath($Declaration),$Cell,"$ProcessIndex")
    $frozen=Get-Content -Raw -LiteralPath $BinaryLock | ConvertFrom-Json
    if ($frozen.sourceSha -ne $ExpectedSourceSha) { throw 'Binary lock belongs to a different source commit.' }
    foreach ($item in $frozen.files) {
        $actual=(Get-FileHash -LiteralPath (Join-Path (Split-Path -Parent $Runtime) $item.path) -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actual -ne $item.sha256) { throw "Frozen binary changed: $($item.path)" }
    }
}
$receipt=[ordered]@{ schema='hlslperf.unified-native-execution.v1'; developmentOnly=($Mode -ne 'formal'); sourceSha=$source; command=@('dotnet')+$arguments; queuedUtc=[DateTime]::UtcNow.ToString('o'); status='queued'; output=$output; wrapperPid=$PID; pid=$null }
function Save-State {
    [IO.File]::WriteAllText("$output.execution.json",($receipt | ConvertTo-Json -Depth 10))
    if ($CoordinationReport) {
        $state=Get-Content -Raw -LiteralPath $CoordinationReport | ConvertFrom-Json -AsHashtable
        $state.updatedUtc=[DateTime]::UtcNow.ToString('o'); $state.phase=$Mode+'-'+$receipt.status
        $state.currentProcess=if ($receipt.status -eq 'running') { @{ pid=$receipt.pid; output=$output; command=$receipt.command; startedUtc=$receipt.startedUtc } } else { $null }
        $state.rawPaths=@((@($state.rawPaths)+@($output)) | Select-Object -Unique)
        $state.commands=@((@($state.commands)+@(($receipt.command -join ' '))) | Select-Object -Unique)
        [IO.File]::WriteAllText($CoordinationReport,($state | ConvertTo-Json -Depth 14))
    }
}
Save-State
& $SerializedValidationRunner -Action {
    $receipt.binaries=@(Get-ChildItem -LiteralPath (Split-Path -Parent $Runtime) -Recurse -File | ForEach-Object { @{ path=$_.FullName; sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() } })
    if ($Mode -eq 'formal') { $receipt.declarationSha256=(Get-FileHash -LiteralPath $Declaration -Algorithm SHA256).Hash.ToLowerInvariant(); $receipt.binaryLockSha256=(Get-FileHash -LiteralPath $BinaryLock -Algorithm SHA256).Hash.ToLowerInvariant() }
    $quoted=@($arguments | ForEach-Object { '"'+$_.Replace('"','\"')+'"' })
    $native=Start-Process -FilePath dotnet -ArgumentList $quoted -WindowStyle Hidden -PassThru -RedirectStandardOutput "$output.stdout.log" -RedirectStandardError "$output.stderr.log"
    $receipt.pid=$native.Id; $receipt.startedUtc=$native.StartTime.ToUniversalTime().ToString('o'); $receipt.status='running'; Save-State
    $native.WaitForExit()
    $receipt.exitedUtc=[DateTime]::UtcNow.ToString('o'); $receipt.exitCode=$native.ExitCode
    $receipt.status=if ($native.ExitCode -eq 0) { 'tests-passed' } elseif ($native.ExitCode -eq 2) { 'collection-complete-with-correctness-failures' } else { 'runtime-failed' }
    Save-State
    Get-Content -LiteralPath "$output.stdout.log" -Tail 12
    Get-Content -LiteralPath "$output.stderr.log"
    if ($native.ExitCode -notin @(0,2)) { throw "Native runtime failed with exit $($native.ExitCode); stop this experiment epoch." }
}
