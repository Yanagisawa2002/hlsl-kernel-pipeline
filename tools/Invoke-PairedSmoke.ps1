param(
    [Parameter(Mandatory = $true)][string]$ValidationLockScript,
    [string]$Adapter = 'R9700',
    [string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$taskRepo = Split-Path -Parent $PSScriptRoot
if (!$OutputDirectory) { $OutputDirectory = Join-Path $taskRepo '.hlslperf/paired-smoke' }
$taskOutput = [IO.Path]::GetFullPath($OutputDirectory)
$taskManifest = Join-Path $taskRepo 'manifests/measurement/paired-smoke.json'
$taskCli = Join-Path $taskRepo 'src/HlslPerf.Cli/bin/Release/net10.0/hlslperf.dll'
New-Item -ItemType Directory -Path $taskOutput -Force | Out-Null
function Assert-Run($Run, $Directory) {
    if ($Run.measurementProtocol -ne 'gpu-paired-abba-independent-confirmation-v2' -or
        $Run.pairedEvidence.observations.Count -ne 48 -or !$Run.pairedEvidence.independentInputs) { throw 'Missing paired observations or independent input proof.' }
    foreach ($taskObservation in $Run.pairedEvidence.observations) {
        if (!$taskObservation.result.correctness.passed -or $taskObservation.result.error -or
            $taskObservation.slotVerifications.Count -ne 3) { throw 'Native observation failed correctness/batch coverage.' }
        foreach ($taskVerification in $taskObservation.slotVerifications) {
            if (!$taskVerification.correctness.passed -or $taskVerification.correctness.outputs.Count -lt 2) { throw 'Missing two-poison output verification.' }
        }
    }
    if ((Test-Path (Join-Path $Directory 'profile.json')) -ne $Run.pairedEvidence.deployable) { throw 'Profile emission disagrees with deployment gate.' }
}

& $ValidationLockScript -Action {
    Push-Location $taskRepo
    try {
        dotnet test tests/HlslPerf.Core.Tests/HlslPerf.Core.Tests.csproj -c Release --logger "trx;LogFileName=paired-smoke-tests.trx"
        if ($LASTEXITCODE -ne 0) { throw 'Core tests failed.' }
        dotnet build src/HlslPerf.Cli/HlslPerf.Cli.csproj -c Release
        if ($LASTEXITCODE -ne 0) { throw 'Native CLI build failed.' }
        $taskFirst = Join-Path $taskOutput 'first'
        & dotnet $taskCli tune $taskManifest --output $taskFirst --adapter $Adapter --rga off
        if ($LASTEXITCODE -notin @(0, 2)) { throw "Native smoke exited $LASTEXITCODE" }
        $taskRun = Get-Content (Join-Path $taskFirst 'run.json') -Raw | ConvertFrom-Json
        Assert-Run $taskRun $taskFirst
        $taskReplay = Join-Path $taskOutput 'complete-replay'
        & dotnet $taskCli tune $taskManifest --output $taskReplay --adapter $Adapter --rga off --resume (Join-Path $taskFirst 'checkpoint.json')
        if ($LASTEXITCODE -notin @(0, 2)) { throw 'Complete replay failed.' }
        $taskReplayed = Get-Content (Join-Path $taskReplay 'run.json') -Raw | ConvertFrom-Json
        if ($taskReplayed.pairedEvidence.sessionId -ne $taskRun.pairedEvidence.sessionId -or $taskReplayed.resume.measuredCandidateCount -ne 0) {
            throw 'Complete replay resampled or changed session.'
        }
        $taskPartialPath = Join-Path $taskOutput 'partial-checkpoint.json'
        $taskPartial = Get-Content (Join-Path $taskFirst 'checkpoint.json') -Raw | ConvertFrom-Json
        $taskPartial.completedReport = $null
        $taskPartial.observations = @($taskPartial.observations | Select-Object -First 5)
        $taskPartial.selectedCandidateId = $null
        $taskPartial.selectionLockSha256 = $null
        $taskPartial | ConvertTo-Json -Depth 100 | Set-Content $taskPartialPath
        $taskRestartedPath = Join-Path $taskOutput 'interrupted-restart'
        & dotnet $taskCli tune $taskManifest --output $taskRestartedPath --adapter $Adapter --rga off --resume $taskPartialPath
        if ($LASTEXITCODE -notin @(0, 2)) { throw 'Interrupted restart failed.' }
        $taskRestarted = Get-Content (Join-Path $taskRestartedPath 'run.json') -Raw | ConvertFrom-Json
        Assert-Run $taskRestarted $taskRestartedPath
        if ($taskRestarted.pairedEvidence.sessionId -eq $taskRun.pairedEvidence.sessionId -or
            $taskRestarted.pairedEvidence.historicalAttemptPaths.Count -ne 1) { throw 'Partial attempt was mixed or not archived.' }
        $taskArchived = Get-Content $taskRestarted.pairedEvidence.historicalAttemptPaths[0] -Raw | ConvertFrom-Json
        if ($taskArchived.observations.Count -ne 5) { throw 'Partial observations were not retained.' }
        [ordered]@{
            evidenceKind = 'native-short-smoke-not-formal-performance'; completedUtc = [DateTimeOffset]::UtcNow
            firstSession = $taskRun.pairedEvidence.sessionId; restartedSession = $taskRestarted.pairedEvidence.sessionId
            observationsPerAttempt = 48; residentSlots = 3; independentInputs = $true
            completeReplayWithoutSampling = $true; partialRestartPreservesHistoricalObservations = 5
            firstDeployable = $taskRun.pairedEvidence.deployable; firstRejections = $taskRun.pairedEvidence.rejections
            restartedDeployable = $taskRestarted.pairedEvidence.deployable; restartedRejections = $taskRestarted.pairedEvidence.rejections
            sourceCommit = (git rev-parse HEAD); manifestSha256 = (Get-FileHash $taskManifest -Algorithm SHA256).Hash
        } | ConvertTo-Json -Depth 20 | Set-Content (Join-Path $taskOutput 'smoke-summary.json')
    }
    finally { Pop-Location }
}


