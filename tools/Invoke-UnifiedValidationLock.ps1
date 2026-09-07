param([Parameter(Mandatory)][scriptblock]$Action)
$ErrorActionPreference='Stop'
$validationMutex=[Threading.Mutex]::new($false,'Local\CodexR9700VNextUnityGpu')
$held=$false
try {
    Write-Output 'Waiting for the shared R9700 / Unity validation lock.'
    try { $held=$validationMutex.WaitOne() }
    catch [Threading.AbandonedMutexException] { $held=$true }
    Write-Output 'Shared validation lock acquired.'
    & $Action
}
finally {
    if ($held) { $validationMutex.ReleaseMutex() }
    $validationMutex.Dispose()
}
