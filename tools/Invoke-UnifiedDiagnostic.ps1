param(
    [Parameter(Mandatory)][string]$Runtime,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][string]$SerializedValidationRunner,
    [string]$CoordinationReport,
    [ValidateSet('scan','radix')][string]$Workload = 'scan',
    [int]$Count = 6145,
    [string]$Pattern = 'uniform',
    [switch]$Pairs,
    [string]$Implementation = 'all',
    [switch]$DebugLayer
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$source = (& git -C $repo rev-parse HEAD).Trim()
if (& git -C $repo status --porcelain) { throw 'Commit the diagnostic source before native execution.' }
$output = [IO.Path]::GetFullPath($OutputDirectory)
$receiptPath = "$output.execution.json"
if ((Test-Path -LiteralPath $output) -or (Test-Path -LiteralPath $receiptPath)) { throw 'Never overwrite a diagnostic attempt.' }
[IO.Directory]::CreateDirectory((Split-Path -Parent $output)) | Out-Null
$arguments = @([IO.Path]::GetFullPath($Runtime), 'diagnose', $repo, $output, $Workload, "$Count", $Pattern, "$($Pairs.IsPresent)", $Implementation, "$($DebugLayer.IsPresent)")
$receipt = [ordered]@{ schema='hlslperf.unified-native-execution.v1'; developmentOnly=$true; sourceSha=$source; command=@('dotnet')+$arguments; queuedUtc=[DateTime]::UtcNow.ToString('o'); status='queued'; output=$output; wrapperPid=$PID; pid=$null }
function Save-State {
    [IO.File]::WriteAllText($receiptPath, ($receipt | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
    if ($CoordinationReport) {
        $state = Get-Content -Raw -LiteralPath $CoordinationReport | ConvertFrom-Json -AsHashtable
        $state.updatedUtc=[DateTime]::UtcNow.ToString('o')
        $state.phase='native-diagnostic-'+$receipt.status
        $state.currentProcess=if ($receipt.status -eq 'running') { @{ pid=$receipt.pid; output=$output; command=$receipt.command; startedUtc=$receipt.startedUtc } } else { $null }
        $state.rawPaths=@($state.rawPaths)+@($output) | Select-Object -Unique
        $state.commands=@($state.commands)+@(($receipt.command -join ' ')) | Select-Object -Unique
        [IO.File]::WriteAllText($CoordinationReport, ($state | ConvertTo-Json -Depth 10), [Text.UTF8Encoding]::new($false))
    }
}
Save-State
& $SerializedValidationRunner -Action {
    $receipt.binaries=@(Get-ChildItem -LiteralPath (Split-Path -Parent $Runtime) -Recurse -File | ForEach-Object {
        @{ path=$_.FullName; sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
    })
    $quoted=@($arguments | ForEach-Object { '"'+$_.Replace('"','\"')+'"' })
    $native=Start-Process -FilePath 'dotnet' -ArgumentList $quoted -WindowStyle Hidden -PassThru -RedirectStandardOutput "$output.stdout.log" -RedirectStandardError "$output.stderr.log"
    $receipt.pid=$native.Id; $receipt.startedUtc=$native.StartTime.ToUniversalTime().ToString('o'); $receipt.status='running'; Save-State
    $native.WaitForExit()
    $receipt.exitedUtc=[DateTime]::UtcNow.ToString('o'); $receipt.exitCode=$native.ExitCode; $receipt.status='recorded'; Save-State
    Get-Content -LiteralPath "$output.stdout.log"
    Get-Content -LiteralPath "$output.stderr.log"
    if ($native.ExitCode -ne 0) { throw "Diagnostic native process exited $($native.ExitCode); original evidence retained." }
}
