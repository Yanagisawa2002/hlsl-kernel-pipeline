param([Parameter(Mandatory)][scriptblock]$Action)
$ErrorActionPreference='Stop'
$validationMutex=[Threading.Mutex]::new($false,'Local\CodexR9700VNextUnityGpu')
$held=$false
try {
    Write-Output ("{0:o} PID {1}: Waiting for the shared R9700 / Unity validation lock." -f [DateTimeOffset]::UtcNow,$PID)
    try { $held=$validationMutex.WaitOne() }
    catch [Threading.AbandonedMutexException] { $held=$true }
    Write-Output ("{0:o} PID {1}: Shared validation lock acquired." -f [DateTimeOffset]::UtcNow,$PID)
    & $Action
}
finally {
    if ($held) {
        $validationMutex.ReleaseMutex()
        Write-Output ("{0:o} PID {1}: Shared validation lock released." -f [DateTimeOffset]::UtcNow,$PID)
    }
    $validationMutex.Dispose()
}
