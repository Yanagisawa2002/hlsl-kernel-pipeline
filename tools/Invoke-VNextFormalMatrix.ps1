[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][ValidateSet('Declare', 'RadixScreen', 'RadixCompare', 'Scenarios')][string]$Phase,
    [Parameter(Mandatory = $true)][string]$EvidenceRoot,
    [string]$SerializedValidationRunner,
    [string]$CellFilter = '*'
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$EvidenceRoot = [IO.Path]::GetFullPath($EvidenceRoot)
$cli = Join-Path $repoRoot 'src/HlslPerf.Cli/bin/Release/net10.0/HlslPerf.Cli.dll'
$declarationPath = Join-Path $EvidenceRoot 'declaration.json'

function Write-Json($Object, [string]$Path) {
    $Object | ConvertTo-Json -Depth 80 | Set-Content -LiteralPath $Path
}
function Read-Json([string]$Path) {
    Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json -AsHashtable
}
function Get-Hash([string]$Path) {
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}
function New-Manifest([string]$Template, [string]$Name, [int]$Count, [int]$Slots, [int]$Seed, [int]$Batch) {
    $manifest = Read-Json (Join-Path $repoRoot "manifests/$Template")
    $manifest.name = $Name
    $manifest.kernelPath = [IO.Path]::GetFullPath((Join-Path (Join-Path $repoRoot 'manifests') $manifest.kernelPath))
    $manifest.workItemCount = [Math]::Max(1, $Count)
    $manifest.workload.parameters.elementCount = $Count
    $manifest.workload.parameters.seed = $Seed
    $manifest.correctness = @{ kind = 'cpu-oracle'; seed = $Seed }
    $manifest.measurementProtocol = 'gpu-paired-abba-independent-confirmation-v2'
    $manifest.pairedMeasurement = @{
        calibrationBlocks = 8; confirmationBlocks = 8; orderSeed = 73019 + $Seed
        calibrationSeedStart = $Seed; confirmationSeedStart = $Seed + 5000
        residentSlots = $Slots; maximumAllocationBytesPerArm = 536870912
        maximumBaselineDrift = 0.15
    }
    $manifest.warmupDispatches = 4
    $manifest.minimumWarmupMilliseconds = 25
    $manifest.measurementBatches = 8
    $manifest.dispatchesPerBatch = $Batch
    $manifest.maximumDispatchesPerBatch = $Batch
    $manifest.minimumBatchMilliseconds = 0.25
    $manifest.maximumCoefficientOfVariation = 0.05
    $manifest.minimumRequiredSpeedup = 1.01
    return $manifest
}
function Save-Cell($Manifest, [string]$Kind, [string]$ParentCell = '') {
    $path = Join-Path $EvidenceRoot ($Manifest.name + '.manifest.json')
    Write-Json $Manifest $path
    return @{
        name = $Manifest.name; kind = $Kind; manifestPath = $path
        manifestSha256 = Get-Hash $path; parentCell = $ParentCell
    }
}
function Get-Interference {
    @{
        capturedUtc = [DateTime]::UtcNow.ToString('o')
        policy = 'No application termination, cache clearing, clock control or forced residency. Cooperating tasks serialized only.'
        thermalAndClocks = 'unavailable; neither sampled nor controlled'
        processes = @(Get-Process | Where-Object { $_.ProcessName -match 'Unity|Unreal|Editor|chrome|msedge|Radeon|obs|dwm|Codex' } |
            Select-Object Id, ProcessName, CPU)
    }
}

if ($Phase -eq 'Declare') {
    if (Test-Path -LiteralPath $declarationPath) { throw 'Declaration already exists; use a fresh evidence directory.' }
    if (-not (Test-Path -LiteralPath $cli)) { throw 'Build the integrated Release CLI before declaring binary identity.' }
    New-Item -ItemType Directory -Force -Path $EvidenceRoot | Out-Null
    $cells = @()
    $index = 0
    foreach ($pairs in @($false, $true)) {
        foreach ($shape in @(@(65537, 1), @(262144, 1), @(262144, 3))) {
            $kind = if ($pairs) { 'pairs' } else { 'keys' }
            $template = if ($pairs) { 'radix-wide-pairs.json' } else { 'radix-wide.json' }
            $name = "radix-screen-$kind-n$($shape[0])-slots$($shape[1])"
            $manifest = New-Manifest $template $name $shape[0] $shape[1] (100000 + $index * 10000) 18
            $manifest.workload.parameters.keyPattern = 2
            foreach ($axis in $manifest.axes) {
                if ($axis.name -eq 'HLSLPERF_RADIX_BITS') { $axis.values = @(1) }
            }
            $cells += Save-Cell $manifest 'radix-screen'
            $index++
        }
    }
    foreach ($count in @(4194304, 16777216)) {
        foreach ($slots in @(1, 3)) {
            $name = "scan-n$count-slots$slots"
            $manifest = New-Manifest 'scan.json' $name $count $slots (4000000 + $index * 10000) 36
            $manifest.shaderModel = '6_6'
            $manifest.fixedDefines = @{
                HLSLPERF_GROUP_SIZE = 256; HLSLPERF_ELEMENTS_PER_THREAD = 4
                HLSLPERF_SCAN_OPERATOR = 1; HLSLPERF_VECTOR_WIDTH = 1
            }
            $manifest.axes = @(
                @{ name = 'HLSLPERF_SCAN_BACKEND'; values = @(1, 2, 3) },
                @{ name = 'HLSLPERF_WAVE_SIZE'; values = @(32); when = @{ HLSLPERF_SCAN_BACKEND = @(2, 3) } },
                @{ name = 'HLSLPERF_SINGLE_PASS_ITEMS_SCALE'; values = @(4); when = @{ HLSLPERF_SCAN_BACKEND = @(3) } },
                @{ name = 'HLSLPERF_SINGLE_PASS_GROUPS'; values = @(256); when = @{ HLSLPERF_SCAN_BACKEND = @(3) } }
            )
            $manifest.baselineDefines = @{ HLSLPERF_SCAN_BACKEND = 1 }
            $manifest.constraints = @()
            $cells += Save-Cell $manifest 'scenario'
            $index++
        }
    }
    foreach ($mode in @(0, 1, 2)) {
        $name = "dynamic-n4096-active$mode"
        $manifest = New-Manifest 'dynamic-compaction.json' $name 4096 1 (5000000 + $index * 10000) 128
        $manifest.workload.parameters.activeMode = $mode
        $manifest.workload.parameters.maximumItems = 4096
        $manifest.axes = @(@{ name = 'HLSLPERF_GROUP_SIZE'; values = @(1, 64) })
        $manifest.baselineDefines = @{ HLSLPERF_GROUP_SIZE = 64 }
        $cells += Save-Cell $manifest 'scenario'
        $index++
    }
    $binaries = @(Get-ChildItem -LiteralPath (Split-Path -Parent $cli) -Recurse -File | Where-Object { $_.Extension -in '.dll', '.exe', '.json' } |
        ForEach-Object { @{ path = $_.FullName; sha256 = Get-Hash $_.FullName } })
    Write-Json @{
        schema = 'hlslperf.integration-matrix.v1'; declaredUtc = [DateTime]::UtcNow.ToString('o')
        sourceSha = (& git -C $repoRoot rev-parse HEAD).Trim()
        sourceTree = (& git -C $repoRoot rev-parse 'HEAD^{tree}').Trim()
        sourceStatus = @(& git -C $repoRoot status --porcelain)
        scriptSha256 = Get-Hash $PSCommandPath
        acceptanceSha256 = Get-Hash (Join-Path $repoRoot 'docs/integration/VNEXT_ACCEPTANCE.md')
        binaries = $binaries; cells = $cells
        radixRule = 'Fastest correctness-passing stable calibration binary per cell, then fresh three-family comparison; no confirmation-driven selection.'
        wideConfiguration = @{ group = 128; items = 2; vector = 1; backend = 2; wave = 32; radixBits = @(4, 8) }
    } $declarationPath
    Write-Output "Declared $($cells.Count) initial cells at $declarationPath"
    return
}

$declaration = Read-Json $declarationPath
foreach ($binary in $declaration.binaries) {
    if ((Get-Hash $binary.path) -ne $binary.sha256) { throw "Frozen binary changed: $($binary.path)" }
}
if ((Get-Hash $PSCommandPath) -ne $declaration.scriptSha256) { throw 'Runner changed after declaration.' }
if (-not $SerializedValidationRunner) { throw 'SerializedValidationRunner is required for execution.' }
$SerializedValidationRunner = (Resolve-Path -LiteralPath $SerializedValidationRunner).Path
$cells = @($declaration.cells | Where-Object {
    $_.name -like $CellFilter -and $(if ($Phase -eq 'Scenarios') { $_.kind -eq 'scenario' } else { $_.kind -eq 'radix-screen' })
})
foreach ($cell in $cells) {
    if ((Get-Hash $cell.manifestPath) -ne $cell.manifestSha256) { throw "Frozen manifest changed: $($cell.name)" }
    if ($Phase -eq 'RadixCompare') {
        $screen = Read-Json (Join-Path (Join-Path $EvidenceRoot $cell.name) 'run.json')
        $valid = @($screen.candidates | Where-Object { $_.compiled -and $_.correctness.passed -and $_.stable -and -not $_.error -and $_.timing })
        if ($valid.Count -eq 0) {
            Write-Json @{ name = $cell.name; status = 'no-valid-stable-binary'; reason = 'No three-family performance claim is supported.' } (Join-Path $EvidenceRoot ($cell.name + '.comparison-unavailable.json'))
            continue
        }
        $best = $valid | Sort-Object @{ Expression = { $_.timing.medianMilliseconds } }, @{ Expression = { $_.candidateId } } | Select-Object -First 1
        $manifest = Read-Json $cell.manifestPath
        $manifest.name = $cell.name.Replace('radix-screen-', 'radix-compare-')
        $manifest.pairedMeasurement.calibrationSeedStart += 2000000
        $manifest.pairedMeasurement.confirmationSeedStart += 2000000
        $manifest.pairedMeasurement.orderSeed += 2000000
        $wide = @{ HLSLPERF_GROUP_SIZE = 128; HLSLPERF_ELEMENTS_PER_THREAD = 2; HLSLPERF_VECTOR_WIDTH = 1; HLSLPERF_SCAN_BACKEND = 2; HLSLPERF_WAVE_SIZE = 32 }
        $axes = @(@{ name = 'HLSLPERF_RADIX_BITS'; values = @(1, 4, 8) })
        $binaryRule = @{}
        $wideRule = @{}
        foreach ($name in @('HLSLPERF_SCAN_BACKEND', 'HLSLPERF_GROUP_SIZE', 'HLSLPERF_ELEMENTS_PER_THREAD', 'HLSLPERF_VECTOR_WIDTH', 'HLSLPERF_WAVE_SIZE')) {
            $values = @($wide[$name])
            if ($best.defines.ContainsKey($name)) { $values += $best.defines[$name]; $binaryRule[$name] = @($best.defines[$name]) }
            $axis = @{ name = $name; values = @($values | Sort-Object -Unique) }
            if ($name -eq 'HLSLPERF_WAVE_SIZE') { $axis.when = @{ HLSLPERF_SCAN_BACKEND = @(2) } }
            $axes += $axis
            $wideRule[$name] = @($wide[$name])
        }
        $manifest.axes = $axes
        $manifest.constraints = @(
            @{ if = @{ HLSLPERF_RADIX_BITS = @(1) }; then = $binaryRule },
            @{ if = @{ HLSLPERF_RADIX_BITS = @(4, 8) }; then = $wideRule }
        )
        $manifest.baselineDefines = @{ HLSLPERF_RADIX_BITS = 1 }
        $parent = $cell.name
        $cell = Save-Cell $manifest 'radix-compare' $parent
        Write-Json @{
            declaredUtc = [DateTime]::UtcNow.ToString('o'); cell = $cell
            screeningReportSha256 = Get-Hash (Join-Path (Join-Path $EvidenceRoot $parent) 'run.json')
            binaryCandidateId = $best.candidateId; binaryCalibrationMedian = $best.timing.medianMilliseconds
            confirmationUsedForSelection = $false
        } (Join-Path $EvidenceRoot ($cell.name + '.declaration.json'))
    }
    $output = Join-Path $EvidenceRoot $cell.name
    if (Test-Path -LiteralPath $output) { throw "Evidence already exists for $($cell.name); refusing to overwrite an attempt." }
    New-Item -ItemType Directory -Path $output | Out-Null
    $receipt = @{ name = $cell.name; manifestSha256 = $cell.manifestSha256; sourceSha = $declaration.sourceSha; before = Get-Interference; status = 'running'; exitCode = $null }
    Write-Json $receipt (Join-Path $output 'execution.json')
    & $SerializedValidationRunner -Action {
        $receipt.lockAcquiredUtc = [DateTime]::UtcNow.ToString('o')
        $receipt.before = Get-Interference
        Push-Location $repoRoot
        try {
            & dotnet $cli tune $cell.manifestPath --adapter R9700 --rga off --output $output *> (Join-Path $output 'console.log')
            $receipt.exitCode = $LASTEXITCODE
        }
        finally {
            $receipt.after = Get-Interference
            Pop-Location
        }
    }
    $receipt.status = if (($receipt.exitCode -in @(0, 2)) -and (Test-Path -LiteralPath (Join-Path $output 'run.json'))) { 'recorded' } else { 'execution-failed' }
    Write-Json $receipt (Join-Path $output 'execution.json')
    Write-Output "$($cell.name): $($receipt.status), exit $($receipt.exitCode)"
}
