[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$SerializedValidationRunner,
    [Parameter(Mandatory = $true)][string]$UnityEditor,
    [string]$EvidenceRoot
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $EvidenceRoot) { $EvidenceRoot = Join-Path $repoRoot '.hlslperf/integration-regression' }
$EvidenceRoot = [IO.Path]::GetFullPath($EvidenceRoot)
$SerializedValidationRunner = (Resolve-Path -LiteralPath $SerializedValidationRunner).Path
$UnityEditor = (Resolve-Path -LiteralPath $UnityEditor).Path
New-Item -ItemType Directory -Force -Path $EvidenceRoot | Out-Null
$fixture = Join-Path $EvidenceRoot 'unity-profile-fixture'
foreach ($directory in @('Assets', 'Packages', 'ProjectSettings')) {
    New-Item -ItemType Directory -Force -Path (Join-Path $fixture $directory) | Out-Null
}
$packagePath = (Join-Path $repoRoot 'unity/com.edwinliu.hlslperf-profile').Replace('\', '/')
@{
    dependencies = @{
        'com.edwinliu.hlslperf-profile' = "file:$packagePath"
        'com.unity.test-framework' = '1.4.6'
    }
    testables = @('com.edwinliu.hlslperf-profile')
} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $fixture 'Packages/manifest.json')
$identity = [ordered]@{
    sourceSha = (& git -C $repoRoot rev-parse HEAD).Trim()
    sourceStatus = @(& git -C $repoRoot status --porcelain)
    startedUtc = [DateTime]::UtcNow.ToString('o')
    unityEditor = $UnityEditor
    unitySha256 = (Get-FileHash -LiteralPath $UnityEditor -Algorithm SHA256).Hash.ToLowerInvariant()
    completed = $false
    coreTests = $null
    unityTests = $null
}
$receiptPath = Join-Path $EvidenceRoot 'regression.json'
$identity | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $receiptPath
& $SerializedValidationRunner -Action {
    Push-Location $repoRoot
    try {
        & dotnet build HlslKernelPipeline.slnx -c Release --nologo *> (Join-Path $EvidenceRoot 'build.log')
        if ($LASTEXITCODE -ne 0) { throw 'Release build failed; see build.log.' }
        & dotnet test tests/HlslPerf.Core.Tests/HlslPerf.Core.Tests.csproj -c Release --no-build --nologo --logger 'trx;LogFileName=core.trx' --results-directory $EvidenceRoot *> (Join-Path $EvidenceRoot 'core-tests.log')
        if ($LASTEXITCODE -ne 0) { throw 'Core tests failed; see core-tests.log.' }
        [xml]$coreXml = Get-Content -LiteralPath (Join-Path $EvidenceRoot 'core.trx') -Raw
        $identity.coreTests = @{
            total = [int]$coreXml.TestRun.ResultSummary.Counters.total
            passed = [int]$coreXml.TestRun.ResultSummary.Counters.passed
            failed = [int]$coreXml.TestRun.ResultSummary.Counters.failed
        }
        $unityResult = Join-Path $EvidenceRoot 'unity-tests.xml'
        $unityLog = Join-Path $EvidenceRoot 'unity-tests.log'
        if (Test-Path -LiteralPath $unityResult) { throw 'Use a fresh evidence directory to avoid stale Unity results.' }
        $unityArguments = @('-batchmode', '-nographics', '-projectPath', ('"' + $fixture + '"'),
            '-runTests', '-testPlatform', 'EditMode', '-testResults', ('"' + $unityResult + '"'),
            '-logFile', ('"' + $unityLog + '"'))
        $unityProcess = Start-Process -FilePath $UnityEditor -ArgumentList $unityArguments -PassThru -WindowStyle Hidden
        # Start-Process -Wait can wait on an inherited Windows job containing
        # unrelated Codex tasks. Track this Unity process and its own descendants.
        $unityOwnedIds = [System.Collections.Generic.HashSet[uint32]]::new()
        [void]$unityOwnedIds.Add([uint32]$unityProcess.Id)
        do {
            $unitySnapshot = @(Get-CimInstance Win32_Process)
            do {
                $unityFoundChild = $false
                foreach ($unityChild in $unitySnapshot) {
                    if ($unityOwnedIds.Contains([uint32]$unityChild.ParentProcessId) -and
                        $unityOwnedIds.Add([uint32]$unityChild.ProcessId)) { $unityFoundChild = $true }
                }
            } while ($unityFoundChild)
            $unityLiveChildren = @($unitySnapshot | Where-Object {
                $_.ProcessId -ne $unityProcess.Id -and $unityOwnedIds.Contains([uint32]$_.ProcessId)
            })
            $unityProcess.Refresh()
            if ($unityProcess.HasExited -and $unityLiveChildren.Count -eq 0) { break }
            Start-Sleep -Milliseconds 500
        } while ($true)
        $unityProcess.WaitForExit()
        $unityExitCode = $unityProcess.ExitCode
        if ($unityExitCode -ne 0 -or -not (Test-Path -LiteralPath $unityResult)) {
            throw "Unity tests failed or did not produce results (exit $unityExitCode); see unity-tests.log."
        }
        [xml]$unityXml = Get-Content -LiteralPath $unityResult -Raw
        $identity.unityTests = @{
            total = [int]$unityXml.'test-run'.total
            passed = [int]$unityXml.'test-run'.passed
            failed = [int]$unityXml.'test-run'.failed
            result = [string]$unityXml.'test-run'.result
        }
        if ($identity.unityTests.total -le 0 -or $identity.unityTests.failed -ne 0 -or $identity.unityTests.result -ne 'Passed') {
            throw 'Unity test receipt does not establish a non-empty passing suite.'
        }
        $identity.completed = $true
    }
    finally {
        $identity.finishedUtc = [DateTime]::UtcNow.ToString('o')
        $identity | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $receiptPath
        Pop-Location
    }
}
Get-Content -LiteralPath $receiptPath
