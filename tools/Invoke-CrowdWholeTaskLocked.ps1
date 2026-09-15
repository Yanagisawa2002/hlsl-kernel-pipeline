param(
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][scriptblock]$Action,
    [ValidateSet('build','correctness','performance')][string]$Stage = 'performance',
    [string]$Coordination = 'D:/CodexWork/whole-task-validation-20260915/coordination'
)
$ErrorActionPreference = 'Stop'
$evidenceRoot = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $evidenceRoot) { throw 'Preserve prior hardware receipts; choose a new output directory.' }
New-Item -ItemType Directory -Path $evidenceRoot | Out-Null
$queue = Get-Content -LiteralPath (Join-Path $Coordination 'queue.json') -Raw | ConvertFrom-Json
$index = [Array]::IndexOf(@($queue.order), 'hlsl')
if ($index -lt 0) { throw 'HLSL is not in the hardware queue.' }
if (Test-Path -LiteralPath (Join-Path $Coordination 'handoff/hlsl.json')) { throw 'Hardware already handed off; new coordination is required.' }
for ($i = 0; $i -lt $index; $i++) {
    $prior = Get-Content -LiteralPath (Join-Path $Coordination ('handoff/' + $queue.order[$i] + '.json')) -Raw | ConvertFrom-Json
    if (!$prior.terminal -or !$prior.hardwareReleased) { throw 'Predecessor has not released hardware.' }
}
$taskMutex = [Threading.Mutex]::new($false, $queue.mutex)
$held = $false
$priorReceipt = $env:HLSLPERF_CROWD_LOCK_RECEIPT
$priorOwner = $env:HLSLPERF_CROWD_LOCK_OWNER
try {
    try { $held = $taskMutex.WaitOne(60000) } catch [Threading.AbandonedMutexException] { $held = $true }
    if (!$held) { throw 'Shared hardware lock busy; bounded wait expired.' }
    $samples = @()
    for ($sample = 0; $sample -lt 2; $sample++) {
        $cpu = (Get-CimInstance Win32_Processor | Measure-Object LoadPercentage -Average).Average
        $gpuText = & nvidia-smi --query-gpu=name,driver_version,utilization.gpu,memory.free,temperature.gpu --format=csv,noheader,nounits
        if ($LASTEXITCODE -ne 0) { throw 'GPU preflight query failed.' }
        $gpu = $gpuText.Split(',').Trim()
        $foreign = @(Get-CimInstance Win32_Process | Where-Object { $_.Name -match '^(Unity|UnityShaderCompiler|dxc|MSBuild|cl|ninja)\.exe$' } | Select-Object ProcessId,Name,ExecutablePath)
        $disk = (Get-Volume -DriveLetter $evidenceRoot.Substring(0,1)).SizeRemaining
        $freeHost = (Get-CimInstance Win32_OperatingSystem).FreePhysicalMemory * 1024
        $samples += [ordered]@{ utc=[DateTimeOffset]::UtcNow.ToString('o'); stage=$Stage; ownerPid=$PID; mutex=$queue.mutex; cpuPercent=$cpu; gpu=$gpu; freeHostBytes=$freeHost; diskFreeBytes=$disk; foreignHeavy=$foreign }
        $samples | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $evidenceRoot 'preflight.json') -Encoding utf8
        $busy = if ($Stage -eq 'performance') { $cpu -gt 25 -or [double]$gpu[2] -gt 15 } else { $cpu -gt 70 }
        if ($busy -or $freeHost -lt 4GB -or [double]$gpu[3] -lt 8192 -or $disk -lt 30GB -or $foreign.Count -gt 0) {
            throw 'Hardware load, free memory, disk, or foreign heavy-process gate failed. Nothing was terminated.'
        }
        Start-Sleep -Milliseconds 500
    }
    Write-Output ('Hardware preflight passed; lock held by PID ' + $PID)
    $env:HLSLPERF_CROWD_LOCK_RECEIPT = Join-Path $evidenceRoot 'preflight.json'
    $env:HLSLPERF_CROWD_LOCK_OWNER = [string]$PID
    & $Action
}
finally {
    $env:HLSLPERF_CROWD_LOCK_RECEIPT = $priorReceipt
    $env:HLSLPERF_CROWD_LOCK_OWNER = $priorOwner
    if ($held) { $taskMutex.ReleaseMutex() }
    $taskMutex.Dispose()
    [ordered]@{ utc=[DateTimeOffset]::UtcNow.ToString('o'); pid=$PID; mutexReleased=$held } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $evidenceRoot 'released.json') -Encoding utf8
}
